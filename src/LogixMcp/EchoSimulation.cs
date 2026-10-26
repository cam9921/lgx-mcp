using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// ── EchoSimulationCheck ───────────────────────────────────────────────────────

/// <summary>One check of the Echo simulation proof, with what was observed.</summary>
/// <param name="Name">
/// The check: <c>localAddress</c>, <c>identity</c>, <c>echoService</c> or <c>route</c>.
/// </param>
/// <param name="Passed"><c>true</c> when the check passed.</param>
/// <param name="Evidence">What was observed, or why the check failed or could not be made.</param>
public sealed record EchoSimulationCheck(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("passed")]   bool   Passed,
    [property: JsonPropertyName("evidence")] string Evidence);

// ── EchoSimulationReport ──────────────────────────────────────────────────────

/// <summary>
/// The outcome of the Echo simulation proof made at one <c>logix_connect</c> while
/// <c>LGX_ECHO_SIM_MODE</c> is set: whether simulation mode is active, and the evidence of each check.
/// Reported as <c>echoSimulation</c> in the connect result, by <c>logix_doctor</c> and by <c>logix_ping</c>.
/// </summary>
/// <param name="Active">
/// <c>true</c> only when every check passed. While it is <c>true</c>, the session's capability switches,
/// write gate and confirmation requirements are off (see <see cref="EchoSimulation"/>).
/// </param>
/// <param name="Target">The target as given to <c>logix_connect</c>: <c>gateway,path</c> or the SDK comm path.</param>
/// <param name="Summary">One sentence on what the outcome means.</param>
/// <param name="Checks">Every check, in order, each with its evidence.</param>
/// <param name="ProvenAt">When the proof was made (UTC).</param>
public sealed record EchoSimulationReport(
    [property: JsonPropertyName("active")]   bool                               Active,
    [property: JsonPropertyName("target")]   string                             Target,
    [property: JsonPropertyName("summary")]  string                             Summary,
    [property: JsonPropertyName("checks")]   IReadOnlyList<EchoSimulationCheck> Checks,
    [property: JsonPropertyName("provenAt")] DateTimeOffset                     ProvenAt)
{
    /// <summary>The host the proof resolved (IP or name). Each re-check resolves it again.</summary>
    [JsonIgnore]
    public string Host { get; init; } = string.Empty;

    /// <summary>The environment that made the proof. Re-checks use the same one, so a test's fake is kept.</summary>
    [JsonIgnore]
    internal IEchoSimulationEnvironment? Environment { get; init; }
}

// ── IEchoSimulationEnvironment ────────────────────────────────────────────────

/// <summary>
/// What the Echo simulation proof observes about this machine and the target. Injectable so tests can
/// make each check fail on its own without a network, a service or an emulator.
/// </summary>
public interface IEchoSimulationEnvironment
{
    /// <summary><c>true</c> when <see cref="EchoSimulation.EnvVar"/> is set to <c>1</c>, <c>true</c> or <c>yes</c>.</summary>
    bool OptedIn { get; }

    /// <summary>
    /// The addresses <paramref name="host"/> resolves to. An IP literal resolves to itself without DNS.
    /// </summary>
    /// <exception cref="Exception">Any failure to resolve; the proof treats it as a failed check.</exception>
    IReadOnlyList<IPAddress> Resolve(string host);

    /// <summary>
    /// The name of this PC's network interface that holds <paramref name="address"/> in the
    /// <c>Preferred</c> state, or <c>null</c> when no interface does.
    /// </summary>
    string? OwnPreferredInterface(IPAddress address);

    /// <summary>The state of the named Windows service, as a <see cref="DoctorServiceState"/> name.</summary>
    string GetServiceState(string serviceName);

    /// <summary>The EtherNet/IP ListIdentity reply of the device at <paramref name="address"/>.</summary>
    /// <exception cref="EmulatorIdentityException">The exchange failed.</exception>
    Task<EmulatorIdentityInfo> ReadIdentityAsync(string address, CancellationToken ct);
}

// ── EchoSimulationEnvironment ─────────────────────────────────────────────────

/// <summary>Production <see cref="IEchoSimulationEnvironment"/>: the process environment, DNS, this PC's
/// interfaces, the Windows service manager and a real ListIdentity exchange.</summary>
public sealed class EchoSimulationEnvironment : IEchoSimulationEnvironment
{
    // The doctor already knows how to query a Windows service (and reports Unsupported in a no-SDK build,
    // which fails the proof closed). It is created on first use and never inspects a session.
    private readonly Lazy<DoctorEnvironment> _doctor = new(() => new DoctorEnvironment(null));

    /// <inheritdoc />
    public bool OptedIn => Capabilities.IsTruthy(Environment.GetEnvironmentVariable(EchoSimulation.EnvVar)?.Trim());

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> Resolve(string host) =>
        IPAddress.TryParse(host, out var literal) ? [literal] : Dns.GetHostAddresses(host);

    /// <inheritdoc />
    public string? OwnPreferredInterface(IPAddress address)
    {
        // The address state is only reported on Windows, where Echo runs; elsewhere the check fails closed.
        if (!OperatingSystem.IsWindows()) return null;

        // An address held in any other state (Duplicate, Tentative, Deprecated) is not one this PC answers
        // on: Duplicate in particular means another device on the network holds the same address.
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.Equals(address)
                    && ua.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                    return nic.Name;
        return null;
    }

    /// <inheritdoc />
    public string GetServiceState(string serviceName) => _doctor.Value.GetServiceState(serviceName);

    /// <inheritdoc />
    public Task<EmulatorIdentityInfo> ReadIdentityAsync(string address, CancellationToken ct) =>
        EmulatorIdentity.ReadAsync(address, ct: ct);
}

// ── EchoSimulation ────────────────────────────────────────────────────────────

/// <summary>
/// Echo simulation mode: when the connected controller is proven to be a FactoryTalk Logix Echo emulator
/// on this PC, every lgx-mcp protection is off for the session, so the server can be used as a
/// logic-simulation test tool.
///
/// <para>
/// The capability switches count as on, the write gate is bypassed for every tag, and confirmedByUser /
/// confirm requirements are skipped. Each skipped protection is written to the audit log
/// (<c>echo_simulation_bypass</c>), and every audit record written while the mode is active carries
/// <c>"echo_simulation": true</c>. Echo itself still enforces each tag's External Access.
/// </para>
///
/// <para>
/// The proof fails closed: unless <see cref="EnvVar"/> is set and all four checks pass, nothing changes.
/// <list type="number">
///   <item><description><b>localAddress</b>: every address the target resolves to is an IPv4 address this
///     PC's own interfaces hold in the <c>Preferred</c> state. A real controller can never hold an address
///     this PC owns.</description></item>
///   <item><description><b>identity</b>: EtherNet/IP ListIdentity reports a product name starting
///     <c>Emulate</c>.</description></item>
///   <item><description><b>echoService</b>: the FactoryTalk Logix Echo service is running.</description></item>
///   <item><description><b>route</b>: no hops past the emulated chassis (CIP path exactly <c>1,&lt;slot&gt;</c>;
///     SDK comm path <c>EmulateEthernet\&lt;ip&gt;</c> or <c>AB_ETH-n\&lt;ip&gt;</c>, optionally with
///     <c>\Backplane\&lt;slot&gt;</c>).</description></item>
/// </list>
/// The proof is made again at every <c>logix_connect</c>, and the local-address check is repeated before
/// every protection the mode skips.
/// </para>
/// </summary>
public static class EchoSimulation
{
    /// <summary>The opt-in switch. Without it the proof never runs and behaviour is unchanged.</summary>
    public const string EnvVar = "LGX_ECHO_SIM_MODE";

    /// <summary>The product-name prefix every Echo controller reports in ListIdentity.</summary>
    public const string ProductPrefix = "Emulate";

    /// <summary>The environment used when a caller does not supply one.</summary>
    public static IEchoSimulationEnvironment DefaultEnvironment { get; } = new EchoSimulationEnvironment();

    // CIP: port 1 (the backplane), then the controller's slot. Nothing after it.
    private static readonly Regex CipPath = new(@"^1,\d{1,2}$", RegexOptions.CultureInvariant);

    // SDK: the Echo or Ethernet driver, the controller's IP, and optionally its backplane slot. Nothing after it.
    private static readonly Regex SdkCommPath = new(
        @"^(?:EmulateEthernet|AB_ETH-\d+)\\(?<ip>\d{1,3}(?:\.\d{1,3}){3})(?:\\Backplane\\\d{1,2})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── The proof ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the proof for one <c>logix_connect</c>.
    /// </summary>
    /// <returns>
    /// <c>null</c> when <see cref="EnvVar"/> is not set, or when the connect has no live controller (virtual,
    /// l5x, offline SDK): nothing is checked and nothing changes. Otherwise the report, active or not.
    /// </returns>
    public static async Task<EchoSimulationReport?> ProveAsync(
        string?                     backend,
        string?                     gateway,
        string?                     path,
        string?                     sdkCommPath,
        string?                     sdkMode,
        bool                        useVirtual,
        IEchoSimulationEnvironment? environment = null,
        CancellationToken           ct          = default)
    {
        var env = environment ?? DefaultEnvironment;

        // Without the opt-in switch the proof is never made, so a copied binary behaves exactly as before.
        if (!env.OptedIn) return null;

        // Only a live controller connect has anything to prove; the other backends never reach a controller.
        // Same default as logix_connect: an empty backend means cip.
        var kind = string.IsNullOrWhiteSpace(backend) ? "cip" : backend.Trim().ToLowerInvariant();
        if (useVirtual || kind is "virtual" or "l5x") return null;
        var isSdk = kind == "sdk";
        if (isSdk && !string.Equals(sdkMode?.Trim(), "online", StringComparison.OrdinalIgnoreCase)) return null;
        if (!isSdk && kind != "cip") return null;

        // The route check also says which host the other checks look at.
        var (route, host, target) = isSdk ? CheckSdkRoute(sdkCommPath) : CheckCipRoute(gateway, path);

        var (local, addresses) = CheckLocalAddress(host, env);

        // ListIdentity is only sent to an address this PC owns: a proof that has already failed has no
        // reason to talk to a device elsewhere on the network.
        var identity = local.Passed
            ? await CheckIdentityAsync(addresses, env, ct).ConfigureAwait(false)
            : new EchoSimulationCheck("identity", false,
                "not read, because the target is not one of this PC's own addresses");

        var service = CheckEchoService(env);

        EchoSimulationCheck[] checks = [local, identity, service, route];
        var active = checks.All(c => c.Passed);
        var summary = active
            ? "Echo simulation mode is ACTIVE: the target is proven to be a FactoryTalk Logix Echo emulator on " +
              "this PC, so lgx-mcp's capability switches, write gate and confirmation requirements are off " +
              "for this session. Echo itself still enforces each tag's External Access."
            : "Echo simulation mode is NOT active, so every protection applies. Failed: " +
              string.Join("; ", checks.Where(c => !c.Passed).Select(c => $"{c.Name} ({c.Evidence})")) + ".";

        return new EchoSimulationReport(active, target, summary, checks, DateTimeOffset.UtcNow)
        {
            Host        = host ?? string.Empty,
            Environment = env,
        };
    }

    private static (EchoSimulationCheck Check, string? Host, string Target) CheckCipRoute(string? gateway, string? path)
    {
        var gw     = gateway?.Trim() ?? string.Empty;
        var route  = new string((path ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        var target = route.Length == 0 ? gw : $"{gw},{route}";

        // A gateway that carries its own route ("ip,1,0,...") could hide hops the path check never sees.
        var cut  = gw.IndexOfAny([',', '/', '\\', ' ']);
        var host = cut >= 0 ? gw[..cut] : gw;
        if (cut >= 0)
            return (new EchoSimulationCheck("route", false,
                $"the gateway '{gw}' carries a route of its own; give the controller's IP as gateway and the " +
                "route as path"), host, target);

        if (route.Length == 0)
            return (new EchoSimulationCheck("route", false,
                "no CIP path was given; an Echo controller is reached with path '1,<slot>'"), host, target);

        return CipPath.IsMatch(route)
            ? (new EchoSimulationCheck("route", true,
                $"CIP path '{route}' is backplane port 1 to the controller's slot, with no further hops"), host, target)
            : (new EchoSimulationCheck("route", false,
                $"CIP path '{route}' is not exactly '1,<slot>': further hops could leave the emulated chassis"),
               host, target);
    }

    private static (EchoSimulationCheck Check, string? Host, string Target) CheckSdkRoute(string? commPath)
    {
        var text = commPath?.Trim() ?? string.Empty;
        var m    = SdkCommPath.Match(text);
        if (m.Success)
            return (new EchoSimulationCheck("route", true,
                $"comm path '{text}' reaches the controller with no hops past the emulated chassis"),
                m.Groups["ip"].Value, text);

        // Still name the host so the other checks can report on it.
        return (new EchoSimulationCheck("route", false,
            $"comm path '{text}' is not 'EmulateEthernet\\<ip>' or 'AB_ETH-n\\<ip>' (optionally followed by " +
            "'\\Backplane\\<slot>'): further hops could leave the emulated chassis"),
            DoctorHandler.ParseIpFromCommPath(text), text);
    }

    private static (EchoSimulationCheck Check, IReadOnlyList<IPAddress> Addresses) CheckLocalAddress(
        string? host, IEchoSimulationEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (new EchoSimulationCheck("localAddress", false, "no target address could be found"), []);

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = env.Resolve(host);
        }
        catch (Exception ex)
        {
            return (new EchoSimulationCheck("localAddress", false,
                $"'{host}' could not be resolved: {ex.Message}"), []);
        }

        if (addresses.Count == 0)
            return (new EchoSimulationCheck("localAddress", false, $"'{host}' resolved to no address"), []);

        // Every address must pass: a connection could use any one of them.
        var held = new List<string>();
        foreach (var raw in addresses)
        {
            var address = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return (new EchoSimulationCheck("localAddress", false,
                    $"'{host}' resolves to {address}, which is not an IPv4 address"), []);

            string? nic;
            try
            {
                nic = env.OwnPreferredInterface(address);
            }
            catch (Exception ex)
            {
                return (new EchoSimulationCheck("localAddress", false,
                    $"this PC's interfaces could not be read: {ex.Message}"), []);
            }

            if (nic is null)
                return (new EchoSimulationCheck("localAddress", false,
                    $"{address} is not one of this PC's own IPv4 addresses (in the Preferred state), so the device " +
                    "there may be a real controller"), []);
            held.Add($"{address} on {nic}");
        }

        return (new EchoSimulationCheck("localAddress", true,
            $"'{host}' is this PC's own address ({string.Join(", ", held)}, Preferred); a real controller " +
            "cannot hold it"), addresses);
    }

    private static async Task<EchoSimulationCheck> CheckIdentityAsync(
        IReadOnlyList<IPAddress> addresses, IEchoSimulationEnvironment env, CancellationToken ct)
    {
        var seen = new List<string>();
        foreach (var raw in addresses)
        {
            var address = (raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw).ToString();
            EmulatorIdentityInfo info;
            try
            {
                info = await env.ReadIdentityAsync(address, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new EchoSimulationCheck("identity", false, $"ListIdentity at {address} failed: {ex.Message}");
            }

            if (!info.ProductName.StartsWith(ProductPrefix, StringComparison.Ordinal))
                return new EchoSimulationCheck("identity", false,
                    $"ListIdentity at {address} reports product name '{info.ProductName}', which does not start " +
                    $"with '{ProductPrefix}'");
            seen.Add($"{address} reports '{info.ProductName}' rev {info.Revision}, serial 0x{info.SerialNumber:X8}");
        }

        return new EchoSimulationCheck("identity", true, "ListIdentity: " + string.Join("; ", seen));
    }

    private static EchoSimulationCheck CheckEchoService(IEchoSimulationEnvironment env)
    {
        string state;
        try
        {
            state = env.GetServiceState(DoctorHandler.EchoServiceName);
        }
        catch (Exception ex)
        {
            return new EchoSimulationCheck("echoService", false,
                $"the state of '{DoctorHandler.EchoServiceName}' could not be read: {ex.Message}");
        }

        return state == DoctorServiceState.Running
            ? new EchoSimulationCheck("echoService", true, $"'{DoctorHandler.EchoServiceName}' is Running")
            : new EchoSimulationCheck("echoService", false, $"'{DoctorHandler.EchoServiceName}' is {state}");
    }

    // ── Re-check and bypass ───────────────────────────────────────────────────

    /// <summary>
    /// Repeats the local-address check for an active proof. Cheap (no network traffic for an IP literal),
    /// so it runs before every protection the mode skips.
    /// </summary>
    /// <param name="proof">The active proof.</param>
    /// <param name="evidence">What the re-check observed.</param>
    /// <returns><c>true</c> when the target is still one of this PC's own addresses.</returns>
    public static bool StillLocal(EchoSimulationReport proof, out string evidence)
    {
        var (check, _) = CheckLocalAddress(proof.Host, proof.Environment ?? DefaultEnvironment);
        evidence = check.Evidence;
        return check.Passed;
    }

    /// <summary>
    /// Whether <paramref name="session"/> is in Echo simulation mode, so the protection
    /// <paramref name="protection"/> of <paramref name="tool"/> is skipped. Call it only where that protection
    /// would otherwise refuse, so each skipped protection is audited once.
    /// </summary>
    /// <param name="session">The active session, or <c>null</c>.</param>
    /// <param name="tool">The tool whose protection is skipped, e.g. <c>logix_write</c>.</param>
    /// <param name="protection">The protection skipped: an environment variable, a deny reason, or <c>confirmedByUser</c>.</param>
    /// <param name="subject">The tag or object concerned, when there is one.</param>
    /// <returns>
    /// <c>true</c> when the session holds an active proof and its target is still one of this PC's own addresses.
    /// When the re-check fails the mode is revoked for the rest of the session and <c>false</c> is returned.
    /// </returns>
    public static bool Bypasses(TagSessionService? session, string tool, string protection, string? subject = null)
    {
        if (session?.ActiveEchoSimulation is not { } proof) return false;
        if (!StillLocal(proof, out var evidence))
        {
            session.RevokeEchoSimulation(evidence);
            return false;
        }

        AuditLogger.LogEchoSimulationBypass(tool, protection, proof.Target, subject);
        return true;
    }

    /// <summary>
    /// The connect-time form of <see cref="Bypasses(TagSessionService?, string, string, string?)"/>, for the
    /// protections <c>logix_connect</c> applies before the session exists (the controller download).
    /// </summary>
    /// <param name="proof">The proof made for this connect, or <c>null</c>.</param>
    /// <param name="tool">The tool whose protection is skipped.</param>
    /// <param name="protection">The protection skipped.</param>
    /// <returns><c>true</c> when <paramref name="proof"/> is active and its target is still a local address.</returns>
    public static bool Bypasses(EchoSimulationReport? proof, string tool, string protection)
    {
        if (proof is not { Active: true }) return false;
        if (!StillLocal(proof, out var evidence))
        {
            AuditLogger.LogEchoSimulationRevoked(proof.Target, evidence);
            return false;
        }

        AuditLogger.LogEchoSimulationBypass(tool, protection, proof.Target, null);
        return true;
    }
}
