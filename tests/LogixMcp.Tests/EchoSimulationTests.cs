using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A machine where the Echo proof passes by default: opted in, 192.0.2.30 is this PC's own address,
/// ListIdentity reports an Emulate controller and the Echo service is running. Each test breaks one thing.
/// </summary>
internal sealed class FakeEchoEnvironment : IEchoSimulationEnvironment
{
    public const string EchoIp = "192.0.2.30";

    public bool OptedIn { get; set; } = true;

    public Dictionary<string, IReadOnlyList<IPAddress>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? ResolveError { get; set; }
    public IReadOnlyList<IPAddress> Resolve(string host)
    {
        if (ResolveError is not null) throw ResolveError;
        if (Names.TryGetValue(host, out var addresses)) return addresses;
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        throw new SocketException((int)SocketError.HostNotFound);
    }

    public HashSet<string> OwnAddresses { get; } = [EchoIp];
    public string? OwnPreferredInterface(IPAddress address) =>
        OwnAddresses.Contains(address.ToString()) ? "Ethernet0" : null;

    public string EchoServiceState { get; set; } = DoctorServiceState.Running;
    public Exception? ServiceError { get; set; }
    public string GetServiceState(string serviceName)
    {
        if (ServiceError is not null) throw ServiceError;
        return serviceName == DoctorHandler.EchoServiceName ? EchoServiceState : DoctorServiceState.NotInstalled;
    }

    public string ProductName { get; set; } = "Emulate 5580 Controller";
    public Exception? IdentityError { get; set; }
    public List<string> IdentityReads { get; } = [];
    public Task<EmulatorIdentityInfo> ReadIdentityAsync(string address, CancellationToken ct)
    {
        IdentityReads.Add(address);
        if (IdentityError is not null) throw IdentityError;
        return Task.FromResult(new EmulatorIdentityInfo(ProductName, 38, 11, 1, 14, 0xD9, 0x12345678));
    }
}

/// <summary>
/// Tests for Echo simulation mode (<see cref="EchoSimulation"/>): the proof fails closed (each check failing on
/// its own leaves every protection on), all checks passing turns protections off, and the re-check revokes.
/// </summary>
public class EchoSimulationTests : IDisposable
{
    private const string Ip = FakeEchoEnvironment.EchoIp;
    private readonly string _auditDir;

    public EchoSimulationTests()
    {
        // Keep this class's audit records out of the real log.
        _auditDir = Path.Combine(Path.GetTempPath(), $"echo-sim-tests-{Guid.NewGuid():N}");
        AuditLogger.OverrideLogDir(_auditDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_auditDir, recursive: true); } catch { /* best effort */ }
    }

    private static Task<EchoSimulationReport?> ProveCip(FakeEchoEnvironment env, string gateway = Ip, string path = "1,0") =>
        EchoSimulation.ProveAsync("cip", gateway, path, null, "offline", false, env);

    private static Task<EchoSimulationReport?> ProveSdk(FakeEchoEnvironment env, string commPath) =>
        EchoSimulation.ProveAsync("sdk", null, null, commPath, "online", false, env);

    private static EchoSimulationCheck Check(EchoSimulationReport report, string name) =>
        Assert.Single(report.Checks, c => c.Name == name);

    /// <summary>Asserts the proof is inactive and that exactly <paramref name="failed"/> failed.</summary>
    private static void AssertOnlyFailed(EchoSimulationReport? report, params string[] failed)
    {
        Assert.NotNull(report);
        Assert.False(report.Active);
        Assert.Equal(failed.OrderBy(n => n), report.Checks.Where(c => !c.Passed).Select(c => c.Name).OrderBy(n => n));
        Assert.Contains("NOT active", report.Summary, StringComparison.Ordinal);
    }

    // ── All checks pass ───────────────────────────────────────────────────────

    [Fact]
    public async Task AllChecksPass_Cip_IsActive_WithEvidence()
    {
        var env    = new FakeEchoEnvironment();
        var report = await ProveCip(env);

        Assert.NotNull(report);
        Assert.True(report.Active);
        Assert.Equal(new[] { "localAddress", "identity", "echoService", "route" }, report.Checks.Select(c => c.Name));
        Assert.All(report.Checks, c => Assert.True(c.Passed, c.Evidence));
        Assert.Equal($"{Ip},1,0", report.Target);
        Assert.Contains("Ethernet0", Check(report, "localAddress").Evidence, StringComparison.Ordinal);
        Assert.Contains("Emulate 5580 Controller", Check(report, "identity").Evidence, StringComparison.Ordinal);
        Assert.Contains("ACTIVE", report.Summary, StringComparison.Ordinal);
        Assert.Equal(new[] { Ip }, env.IdentityReads);
    }

    [Theory]
    [InlineData(@"EmulateEthernet\192.0.2.30")]
    [InlineData(@"EmulateEthernet\192.0.2.30\Backplane\0")]
    [InlineData(@"AB_ETH-1\192.0.2.30")]
    [InlineData(@"AB_ETH-2\192.0.2.30\Backplane\3")]
    public async Task AllChecksPass_Sdk_IsActive(string commPath)
    {
        var report = await ProveSdk(new FakeEchoEnvironment(), commPath);

        Assert.NotNull(report);
        Assert.True(report.Active, report.Summary);
        Assert.Equal(commPath, report.Target);
    }

    [Fact]
    public async Task CipPathWithSpaces_IsAccepted()
    {
        var report = await ProveCip(new FakeEchoEnvironment(), path: " 1, 2 ");
        Assert.True(report!.Active, report.Summary);
    }

    [Fact]
    public async Task HostName_ResolvingToOwnAddress_IsActive()
    {
        var env = new FakeEchoEnvironment();
        env.Names["echo-bench"] = [IPAddress.Parse(Ip)];

        var report = await ProveCip(env, gateway: "echo-bench");

        Assert.True(report!.Active, report.Summary);
        Assert.Equal(new[] { Ip }, env.IdentityReads);
    }

    // ── Nothing is proven, nothing changes ────────────────────────────────────

    [Fact]
    public async Task NotOptedIn_ReturnsNull_AndSendsNothing()
    {
        var env = new FakeEchoEnvironment { OptedIn = false };

        Assert.Null(await ProveCip(env));
        Assert.Empty(env.IdentityReads);
    }

    [Theory]
    [InlineData("virtual", false, "offline")]
    [InlineData("cip",     true,  "offline")]
    [InlineData("l5x",     false, "offline")]
    [InlineData("sdk",     false, "offline")]
    public async Task NoLiveController_ReturnsNull(string backend, bool useVirtual, string sdkMode)
    {
        var env = new FakeEchoEnvironment();

        Assert.Null(await EchoSimulation.ProveAsync(
            backend, Ip, "1,0", @"EmulateEthernet\192.0.2.30", sdkMode, useVirtual, env));
        Assert.Empty(env.IdentityReads);
    }

    // ── Each check failing on its own leaves protections on ───────────────────

    [Fact]
    public async Task LocalAddress_NotOwned_Fails_AndNoIdentityIsSent()
    {
        var env = new FakeEchoEnvironment();
        env.OwnAddresses.Clear();

        var report = await ProveCip(env);

        // Identity is not read from a device elsewhere on the network, so it is reported as not passed too.
        AssertOnlyFailed(report, "localAddress", "identity");
        Assert.Contains("not one of this PC's own", Check(report!, "localAddress").Evidence, StringComparison.Ordinal);
        Assert.Empty(env.IdentityReads);
    }

    [Fact]
    public async Task LocalAddress_ResolveFails_Fails()
    {
        var env = new FakeEchoEnvironment { ResolveError = new SocketException((int)SocketError.HostNotFound) };
        AssertOnlyFailed(await ProveCip(env), "localAddress", "identity");
    }

    [Fact]
    public async Task LocalAddress_NameWithOneForeignAddress_Fails()
    {
        var env = new FakeEchoEnvironment();
        env.Names["mixed"] = [IPAddress.Parse(Ip), IPAddress.Parse("198.51.100.40")];

        AssertOnlyFailed(await ProveCip(env, gateway: "mixed"), "localAddress", "identity");
    }

    [Fact]
    public async Task LocalAddress_IPv6_Fails()
    {
        var env = new FakeEchoEnvironment();
        env.Names["v6"] = [IPAddress.IPv6Loopback];

        var report = await ProveCip(env, gateway: "v6");

        AssertOnlyFailed(report, "localAddress", "identity");
        Assert.Contains("not an IPv4", Check(report!, "localAddress").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identity_NotEmulate_Fails()
    {
        var env = new FakeEchoEnvironment { ProductName = "1756-L85E/B LOGIX5585E" };

        var report = await ProveCip(env);

        AssertOnlyFailed(report, "identity");
        Assert.Contains("1756-L85E/B", Check(report!, "identity").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identity_Unreadable_Fails()
    {
        var env = new FakeEchoEnvironment { IdentityError = new EmulatorIdentityException("timed out") };
        AssertOnlyFailed(await ProveCip(env), "identity");
    }

    [Theory]
    [InlineData("Stopped")]
    [InlineData(DoctorServiceState.NotInstalled)]
    [InlineData(DoctorServiceState.Unsupported)]
    public async Task EchoService_NotRunning_Fails(string state)
    {
        var env = new FakeEchoEnvironment { EchoServiceState = state };
        AssertOnlyFailed(await ProveCip(env), "echoService");
    }

    [Fact]
    public async Task EchoService_Unreadable_Fails()
    {
        var env = new FakeEchoEnvironment { ServiceError = new InvalidOperationException("access denied") };
        AssertOnlyFailed(await ProveCip(env), "echoService");
    }

    [Theory]
    [InlineData("")]
    [InlineData("2,0")]
    [InlineData("1,0,2,198.51.100.5,1,0")]
    [InlineData("1,0,1,3")]
    [InlineData("1,100")]
    public async Task Route_CipPathWithHops_Fails(string path)
    {
        AssertOnlyFailed(await ProveCip(new FakeEchoEnvironment(), path: path), "route");
    }

    [Fact]
    public async Task Route_GatewayCarryingARoute_Fails()
    {
        AssertOnlyFailed(await ProveCip(new FakeEchoEnvironment(), gateway: $"{Ip},1,0"), "route");
    }

    [Theory]
    [InlineData(@"AB_ETHIP-1\192.0.2.30")]
    [InlineData(@"EmulateEthernet\192.0.2.30\Backplane\0\A\198.51.100.5")]
    [InlineData(@"EmulateEthernet\192.0.2.30,1,0")]
    [InlineData(@"192.0.2.30")]
    public async Task Route_SdkCommPathWithHopsOrOtherDriver_Fails(string commPath)
    {
        AssertOnlyFailed(await ProveSdk(new FakeEchoEnvironment(), commPath), "route");
    }

    // ── Session state, re-check and bypass ────────────────────────────────────

    private static async Task<(TagSessionService Session, FakeEchoEnvironment Env)> ProvenSession(ITagService service)
    {
        var env     = new FakeEchoEnvironment();
        var session = new TagSessionService();
        session.Attach(service, isVirtual: false, isLiveController: true, controllerHost: Ip);
        session.SetEchoSimulationProof(await ProveCip(env));
        Assert.NotNull(session.ActiveEchoSimulation);
        return (session, env);
    }

    [Fact]
    public void Bypasses_NoProof_IsFalse()
    {
        var session = new TagSessionService();
        session.Attach(Store(), isVirtual: false, isLiveController: true);

        Assert.False(EchoSimulation.Bypasses(session, "logix_write", Capabilities.EnvVar));
        Assert.False(EchoSimulation.Bypasses((TagSessionService?)null, "logix_write", Capabilities.EnvVar));
    }

    [Fact]
    public async Task Bypasses_InactiveProof_IsFalse()
    {
        var session = new TagSessionService();
        session.Attach(Store(), isVirtual: false, isLiveController: true);
        session.SetEchoSimulationProof(await ProveCip(new FakeEchoEnvironment { ProductName = "1756-L85E" }));

        Assert.Null(session.ActiveEchoSimulation);
        Assert.False(EchoSimulation.Bypasses(session, "logix_write", Capabilities.EnvVar));
    }

    [Fact]
    public async Task Bypasses_ActiveProof_IsTrue()
    {
        var (session, _) = await ProvenSession(Store());
        Assert.True(EchoSimulation.Bypasses(session, "logix_write", Capabilities.EnvVar));
    }

    [Fact]
    public async Task Bypasses_RecheckFails_RevokesForTheRestOfTheSession()
    {
        var (session, env) = await ProvenSession(Store());

        env.OwnAddresses.Clear();
        Assert.False(EchoSimulation.Bypasses(session, "logix_write", Capabilities.EnvVar));
        Assert.Null(session.ActiveEchoSimulation);
        Assert.False(session.EchoSimulationProof!.Active);
        Assert.Contains("revoked", session.EchoSimulationProof.Summary, StringComparison.Ordinal);

        // The address coming back does not restore the mode: only a new connect proves it again.
        env.OwnAddresses.Add(Ip);
        Assert.False(EchoSimulation.Bypasses(session, "logix_write", Capabilities.EnvVar));
    }

    [Fact]
    public async Task NewConnect_ClearsTheProof()
    {
        var (session, _) = await ProvenSession(Store());

        session.Attach(Store(), isVirtual: false, isLiveController: true);
        Assert.Null(session.EchoSimulationProof);

        var (session2, _) = await ProvenSession(Store());
        session2.Detach();
        Assert.Null(session2.EchoSimulationProof);
    }

    [Fact]
    public async Task ConnectTimeBypass_UsesTheProofDirectly()
    {
        var env = new FakeEchoEnvironment();
        var proof = await ProveCip(env);

        Assert.True(EchoSimulation.Bypasses(proof, "logix_connect", Capabilities.EnvVarControllerDownload));
        Assert.False(EchoSimulation.Bypasses((EchoSimulationReport?)null, "logix_connect", "confirmedByUser"));

        env.OwnAddresses.Clear();
        Assert.False(EchoSimulation.Bypasses(proof, "logix_connect", "confirmedByUser"));
    }

    // ── logix_write ───────────────────────────────────────────────────────────

    private static VirtualTagService Store() => new(new Dictionary<string, (object, string?)>
    {
        ["Cmd_MotorStart"] = (false, "BOOL"),
        ["Motor_Run"]      = (false, "BOOL"),
        ["Safety_OK"]      = (true,  "BOOL"),
    });

    private static WriteGatePolicy DemoPolicy() =>
        GateEngine.DerivePolicy(L5X.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X")),
            GateConfig.Default);

    [Fact]
    public async Task Write_WithoutProof_IsRefusedAsToday()
    {
        var store   = Store();
        var session = new TagSessionService();
        session.Attach(new GatedTagService(store, DemoPolicy()), isVirtual: false, isLiveController: true);

        var err = Assert.IsType<CapabilityDisabledError>(
            await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session, capabilities: null));
        Assert.Equal("CapabilityDisabled", err.Kind);

        var denied = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session, capabilities: new(LiveWritesEnabled: true)));
        Assert.Equal(TagServiceErrorKind.WriteDenied.ToString(), denied.Kind);
        Assert.Equal(false, (await store.ReadAsync("Motor_Run")).Value);
    }

    [Fact]
    public async Task Write_InSimulationMode_BypassesSwitchAndGate()
    {
        var store        = Store();
        var (session, _) = await ProvenSession(new GatedTagService(store, DemoPolicy()));

        // Motor_Run is written by the Demo program's logic and Safety_OK is a safety tag: both denied by the gate.
        foreach (var tag in new[] { "Motor_Run", "Safety_OK" })
        {
            var ok = Assert.IsType<WriteTagResult>(
                await WriteTagHandler.ExecuteAsync(tag, tag == "Safety_OK" ? "false" : "true", session, capabilities: null));
            Assert.Equal("ok", ok.Status);
        }
        Assert.Equal(true,  (await store.ReadAsync("Motor_Run")).Value);
        Assert.Equal(false, (await store.ReadAsync("Safety_OK")).Value);
    }

    [Fact]
    public async Task Write_InSimulationMode_NeedsNoGate()
    {
        var store        = Store();
        var (session, _) = await ProvenSession(store);

        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session));
        Assert.Equal(true, (await store.ReadAsync("Motor_Run")).Value);
    }

    [Fact]
    public async Task Write_InSimulationMode_SessionCreatedTagIsWritable()
    {
        var store        = Store();
        var (session, _) = await ProvenSession(store);
        session.RecordCreatedTags(["Cmd_MotorStart"], "p-1");

        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session));
    }

    [Fact]
    public async Task Write_AfterRevoke_IsRefusedAgain()
    {
        var store          = Store();
        var (session, env) = await ProvenSession(new GatedTagService(store, DemoPolicy()));
        env.OwnAddresses.Clear();

        Assert.IsType<CapabilityDisabledError>(
            await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session, capabilities: null));
        Assert.Equal(false, (await store.ReadAsync("Motor_Run")).Value);
    }

    [Fact]
    public async Task Write_InSimulationMode_StillRefusesNonFiniteValues()
    {
        var (session, _) = await ProvenSession(new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Speed"] = (0.0, "REAL") }));

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Speed", "NaN", session));
        Assert.Equal(ErrorKinds.InvalidValue, err.Kind);
    }

    // ── logix_gate_report ─────────────────────────────────────────────────────

    [Fact]
    public async Task GateReport_InSimulationMode_SaysNotEnforced()
    {
        var project = new ProjectService();
        project.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));
        var (session, _) = await ProvenSession(new GatedTagService(Store(), DemoPolicy()));

        var result = Assert.IsType<HypotheticalGateResult>(GateReportHandler.Execute(project, session: session));

        Assert.Equal(GateReportHandler.EchoSimulationReason, result.Reason);
        Assert.False(result.Policy.Origin!.EnforcedBySession);
        Assert.NotEmpty(result.Policy.Denies);

        var json = JsonDocument.Parse(GateReportHandler.Serialize(result)).RootElement;
        Assert.Equal("hypothetical", json.GetProperty("policy").GetString());
        Assert.False(json.GetProperty("enforcedBySession").GetBoolean());
        Assert.Contains("NO ENFORCED WRITE GATE", json.GetProperty("disclaimer").GetString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// Audit records of Echo simulation mode: the proof, each skipped protection, the revoke, and the
/// <c>echo_simulation</c> mark on every record written while the mode is active.
/// </summary>
[Collection("AuditLoggerDir")]
public class EchoSimulationAuditTests : IDisposable
{
    private readonly string _dir;

    public EchoSimulationAuditTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"echo-sim-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        AuditLogger.OverrideLogDir(_dir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        AuditLogger.OverrideEchoSimulation(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private List<JsonElement> Records() =>
        File.Exists(Path.Combine(_dir, "audit.jsonl"))
            ? File.ReadAllLines(Path.Combine(_dir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .ToList()
            : [];

    [Fact]
    public async Task ProofBypassAndRevoke_AreAudited()
    {
        var env   = new FakeEchoEnvironment();
        var proof = await EchoSimulation.ProveAsync("cip", FakeEchoEnvironment.EchoIp, "1,0", null, "offline", false, env);
        AuditLogger.LogEchoSimulationProof(proof!);

        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)> { ["X"] = (0, "DINT") }),
            isVirtual: false, isLiveController: true);
        session.SetEchoSimulationProof(proof);

        Assert.True(EchoSimulation.Bypasses(session, "logix_write", "WriteGate", "X"));
        env.OwnAddresses.Clear();
        Assert.False(EchoSimulation.Bypasses(session, "logix_write", "WriteGate", "X"));

        var records = Records();
        var proofRecord = Assert.Single(records, r => r.GetProperty("event").GetString() == "echo_simulation_proof");
        Assert.True(proofRecord.GetProperty("active").GetBoolean());
        Assert.Equal(4, proofRecord.GetProperty("checks").GetArrayLength());

        var bypass = Assert.Single(records, r => r.GetProperty("event").GetString() == "echo_simulation_bypass");
        Assert.Equal("logix_write", bypass.GetProperty("operation").GetString());
        Assert.Equal("WriteGate", bypass.GetProperty("protection").GetString());
        Assert.Equal("X", bypass.GetProperty("subject").GetString());

        Assert.Single(records, r => r.GetProperty("event").GetString() == "echo_simulation_revoked");
    }

    [Fact]
    public void EveryRecord_IsMarked_WhileTheModeIsActive()
    {
        AuditLogger.OverrideEchoSimulation(true);
        AuditLogger.LogCapabilityRefusal("logix_write", Capabilities.EnvVar);
        AuditLogger.OverrideEchoSimulation(false);
        AuditLogger.LogCapabilityRefusal("logix_write", Capabilities.EnvVar);

        var records = Records();
        Assert.Equal(2, records.Count);
        Assert.True(records[0].GetProperty("echo_simulation").GetBoolean());
        Assert.False(records[1].TryGetProperty("echo_simulation", out _));
    }
}
