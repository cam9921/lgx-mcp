#if LOGIX_SDK
// ── ProposeRungEditIntegrationTests — integration tests against Echo controller ─
//
// Gated on LOGIX_ACD_PATH environment variable (established in an earlier change as the
// SDK-integration gate convention).  When the variable is absent every test is
// automatically skipped — no failures are reported and the pipeline gate
// (make test / unit tests only) is never broken.
//
// Run integration tests only:
//   LOGIX_ACD_PATH=/path/to/Project.ACD dotnet test --filter Category=Integration
//
// Group 1 goes online with a FactoryTalk Logix Echo controller the way logix_connect does with
// sdkMode="online" (upload to a new project, then go online), and skips when Echo is not reachable:
//   LGX_HW_ECHO_IP       Echo's address (default 192.0.2.30, a documentation address, so the tests skip)
//   LGX_HW_RUNG_PROGRAM  program holding the RLL routine to stage into (default MainProgram)
//   LGX_HW_RUNG_ROUTINE  that RLL routine (default MainRoutine)
// Group 1 stages with target="workstation" only: the rung stays in the server's in-memory image and
// never reaches the controller. One test proves it by uploading again and comparing the routine.
// SDK tests under `dotnet test` also need DOTNET_ROOT_X86 set to the x86 .NET folder (for example
// C:\Program Files (x86)\dotnet): the test host's x64 DOTNET_ROOT otherwise reaches the 32-bit SDK
// adapter and every SDK call times out.
//
// Coverage:
//   1. Staging a valid (NOP) rung on a live Echo session returns ProposeRungEditResult with
//      status "proposed" and a message that says it was staged locally and not sent to the
//      controller; a fresh upload afterwards shows the controller's routine unchanged.
//   2. A fragment that writes a previously-denied tag (Safety_OK, flagged as SafetyTag
//      by the Demo.L5X gate engine) now SUCCEEDS — the gate is advisory only.
//      The scan still runs but no denial is returned; the SDK is called.

using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

using ControllerMode           = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState           = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType                 = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode            = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget     = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode  = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions   = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption      = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent       = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode        = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate  = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── LogixAcdFixture ───────────────────────────────────────────────────────────

/// <summary>
/// Shared xunit collection fixture for Logix Designer SDK integration tests
/// that require a live ACD project open via an Echo controller or Logix
/// Designer installation.
/// </summary>
/// <remarks>
/// The fixture is gated on <c>LOGIX_ACD_PATH</c> — the same environment
/// variable convention established in an earlier change for SDK-backed integration
/// tests.  When the variable is absent the entire collection is skipped.
///
/// <list type="bullet">
///   <item><term>LOGIX_ACD_PATH</term><description>
///     Absolute path to the <c>.ACD</c> project file to open via the
///     Logix Designer SDK.  The file must be accessible on the host and the
///     controller must already be in Test or Run mode.  When this variable is
///     absent, all tests in the <c>LogixAcd</c> collection are skipped.
///   </description></item>
/// </list>
/// </remarks>
public sealed class LogixAcdFixture
{
    /// <summary>
    /// Absolute path to the ACD project, or <see langword="null"/> when
    /// <c>LOGIX_ACD_PATH</c> is not set.
    /// </summary>
    public string? AcdPath { get; }

    /// <summary>
    /// <see langword="true"/> when <c>LOGIX_ACD_PATH</c> is set and SDK
    /// integration tests should execute; <see langword="false"/> otherwise.
    /// </summary>
    public bool IsAvailable => AcdPath is not null;

    /// <summary>
    /// Human-readable message explaining why the test is being skipped, or an
    /// empty string when <see cref="IsAvailable"/> is <see langword="true"/>.
    /// </summary>
    public string SkipReason =>
        IsAvailable
            ? string.Empty
            : "LOGIX_ACD_PATH environment variable is not set. "
              + "Set LOGIX_ACD_PATH to the absolute path of the ACD project "
              + "file on a running Echo controller (or licensed Logix Designer "
              + "host) to run Logix SDK integration tests.";

    /// <summary>
    /// Initialises the fixture by reading <c>LOGIX_ACD_PATH</c> from the
    /// process environment.
    /// </summary>
    public LogixAcdFixture()
    {
        AcdPath = Environment.GetEnvironmentVariable("LOGIX_ACD_PATH");
    }
}

// ── LogixAcdTestCollection ────────────────────────────────────────────────────

/// <summary>
/// Defines the <c>LogixAcd</c> xunit collection and wires up
/// <see cref="LogixAcdFixture"/> as a shared collection-scoped fixture.
/// </summary>
/// <remarks>
/// All Logix SDK integration test classes should be decorated with
/// <c>[Collection("LogixAcd")]</c> so they share the fixture and receive
/// automatic environment-gating (SKIPPED when <c>LOGIX_ACD_PATH</c> is
/// absent).
/// </remarks>
[CollectionDefinition("LogixAcd")]
public sealed class LogixAcdTestCollection : ICollectionFixture<LogixAcdFixture>
{
    // This class is intentionally empty.  Its sole purpose is to serve as an
    // xunit [CollectionDefinition] anchor that associates LogixAcdFixture with
    // the "LogixAcd" collection.
}

// ── ProposeRungEditEchoTests ──────────────────────────────────────────────────

/// <summary>
/// Integration tests for <see cref="ProposeRungEditHandler"/> against a live
/// Echo controller running in Test or Run mode.
///
/// <para>Coverage:</para>
/// <list type="bullet">
///   <item>A valid NOP rung staged (<c>target="workstation"</c>) on a live Echo SDK
///     session returns <see cref="ProposeRungEditResult"/> with <c>status="proposed"</c>
///     and a message that says it was staged locally, not sent to the controller;
///     a fresh upload shows the controller's routine unchanged.</item>
///   <item>A fragment that writes a previously-denied tag (<c>Safety_OK</c>) now
///     succeeds — the gate is advisory only; the scan runs but does not block the
///     SDK call.</item>
/// </list>
///
/// <para>
/// All tests are skipped when <c>LOGIX_ACD_PATH</c> is not set.  Use
/// <see cref="SkippableFactAttribute"/> so the skip propagates correctly to the
/// xunit runner.
/// </para>
/// </summary>
[Collection("LogixAcd")]
[Trait("Category", "Integration")]
public class ProposeRungEditEchoTests : IDisposable
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly LogixAcdFixture _fixture;

    /// <summary>
    /// Path to the Demo.L5X fixture, loaded to derive the <see cref="WriteGatePolicy"/>
    /// for gate-refusal tests without relying on the Echo controller's own project.
    /// </summary>
    private static readonly string DemoFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // ── L5X fragment constants ────────────────────────────────────────────────

    /// <summary>
    /// XPath targeting <c>MainRoutine</c> inside <c>MainProgram</c>, the Demo.L5X routine
    /// the fake-session gate tests (Group 2) propose into.
    /// </summary>
    private const string TargetRoutineXPath =
        "Controller/Programs/Program[@Name='MainProgram']" +
        "/Routines/Routine[@Name='MainRoutine']";

    /// <summary>
    /// A minimal, always-valid L5X fragment containing a single NOP rung.
    /// Used for the happy-path integration test — NOP compiles under any project.
    /// References the shared production constant so the test and the product surface
    /// stay in sync with the same L5X envelope shape.
    /// </summary>
    private const string ValidNopFragment = ProposeRungEditHandler.MinimalNopFragment;

    /// <summary>
    /// An L5X fragment containing a rung that writes <c>Safety_OK</c> via an
    /// <c>OTE</c> coil.  <c>Safety_OK</c> is denied by the Demo.L5X gate policy
    /// (classified <c>SafetyTag</c>), so this fragment must be refused by the
    /// write gate before any SDK call is made.
    /// </summary>
    private const string FragmentWritesSafetyOk =
        "<RSLogix5000Content>" +
          "<Controller>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(Cmd_MotorStart)OTE(Safety_OK);]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the test class and skips the current test when
    /// <c>LOGIX_ACD_PATH</c> is not configured.
    /// </summary>
    /// <param name="fixture">
    /// The collection-scoped fixture injected by xunit.
    /// </param>
    public ProposeRungEditEchoTests(LogixAcdFixture fixture)
    {
        _fixture = fixture;
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a <see cref="WriteGatePolicy"/> from the Demo.L5X fixture.
    /// Used for gate-refusal tests without loading the Echo controller project.
    /// <c>Safety_OK</c> is denied as <c>SafetyTag</c> in this policy.
    /// </summary>
    private static WriteGatePolicy LoadDemoPolicy() =>
        GateEngine.DerivePolicy(L5X.Load(DemoFixturePath), GateConfig.Default);

    /// <summary>
    /// Builds a fake-session-backed <see cref="LogixProjectSession"/> in the
    /// <c>Open</c> state backed by a fresh <see cref="ProposeRungEditRecordingFake"/>.
    /// Used for gate-refusal tests where we need to verify no SDK call was made —
    /// the gate fires before any SDK interaction so the session's real connectivity
    /// is irrelevant.
    /// </summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildFakeSessionAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        // Mark as online and correlated so the handler's pre-flight guard passes.
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    // ── Live Echo session ─────────────────────────────────────────────────────

    /// <summary>Echo's address: <c>LGX_HW_ECHO_IP</c>, as in <see cref="HardwareTests"/>.</summary>
    private static readonly string EchoIp =
        Environment.GetEnvironmentVariable("LGX_HW_ECHO_IP") ?? "192.0.2.30";

    /// <summary>The program and RLL routine on Echo that Group 1 stages a rung into.</summary>
    private static readonly string EchoProgram =
        Environment.GetEnvironmentVariable("LGX_HW_RUNG_PROGRAM") ?? "MainProgram";

    private static readonly string EchoRoutine =
        Environment.GetEnvironmentVariable("LGX_HW_RUNG_ROUTINE") ?? "MainRoutine";

    private static string EchoRoutineXPath =>
        $"Controller/Programs/Program[@Name='{EchoProgram}']/Routines/Routine[@Name='{EchoRoutine}']";

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();

    /// <summary>True when Echo answers on the EtherNet/IP port within 3 seconds.</summary>
    private static async Task<bool> EchoIsReachableAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(EchoIp, 44818, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Goes online with Echo the way <c>logix_connect</c> does with <c>sdkMode="online"</c>:
    /// upload to a new project, then go online. The session is online and correlated, which
    /// the handler requires. Skips when Echo is not reachable or the upload fails.
    /// </summary>
    private async Task<SdkSessionResult> ConnectToEchoAsync()
    {
        Skip.IfNot(await EchoIsReachableAsync(),
            $"Echo at {EchoIp}:44818 is not reachable. Set LGX_HW_ECHO_IP to the Echo controller's address.");
        try
        {
            return await new SdkConnectFactory(_leaseScope.Lease).CreateFromControllerAsync($@"EmulateEthernet\{EchoIp}");
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Upload from Echo at {EchoIp} failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Stages a NOP rung at position 0 of the Echo routine with <c>target="workstation"</c>.
    /// That imports with LeaveEdits: the rung stays in the server's in-memory image. The rung is
    /// typed (<c>rungs</c>), as the MCP tool takes it, so the server builds the envelope for the
    /// routine named; <see cref="ValidNopFragment"/> names MainProgram/MainRoutine, so the SDK
    /// cannot read it against another routine.
    /// </summary>
    private static Task<object> StageNopAsync(SdkSessionResult connected) =>
        ProposeRungEditHandler.ExecuteAsync(
            EchoRoutineXPath, 0, 0, l5xFragment: null,
            ((TypedSdkTagService)connected.Service).Session, policy: null, target: "workstation",
            rungs: [new RungInput("NOP();")]);

    /// <summary>Connects, stages the NOP rung, and disconnects (which discards the image).</summary>
    private async Task<object> StageNopOnEchoAsync()
    {
        var connected = await ConnectToEchoAsync();
        try
        {
            return await StageNopAsync(connected);
        }
        finally
        {
            await connected.Service.DisconnectAsync();
        }
    }

    /// <summary>The successful result, or a failure that quotes the handler's refusal.</summary>
    private static ProposeRungEditResult Staged(object result) =>
        result is ProposeRungEditError refused
            ? throw new Xunit.Sdk.XunitException(
                $"Staging was refused ({refused.Kind}): {refused.Error} Hint: {refused.Hint}")
            : Assert.IsType<ProposeRungEditResult>(result);

    /// <summary>The routine's rungs, as "type: text", in an uploaded project: what the controller holds.</summary>
    private static List<string> RungsOf(L5X project) =>
        project.Content.Serialize()
            .Descendants("Program").Where(p => (string?)p.Attribute("Name") == EchoProgram)
            .Descendants("Routine").Where(r => (string?)r.Attribute("Name") == EchoRoutine)
            .Descendants("Rung")
            .Select(r => $"{(string?)r.Attribute("Type")}: {r.Element("Text")?.Value.Trim()}")
            .ToList();

    // ── Group 1 — a rung staged on a live Echo session ───────────────────────

    /// <summary>
    /// (1a) Staging a valid NOP rung on a live Echo SDK session returns
    /// <see cref="ProposeRungEditResult"/> (not an error): the SDK accepted the
    /// import into the session's in-memory image.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_StagedOnEcho_ReturnsProposedResult()
    {
        Staged(await StageNopOnEchoAsync());
    }

    /// <summary>
    /// (1b) The returned result has <c>status="proposed"</c>, confirming the
    /// handler recognised the SDK call as successful and categorised the outcome
    /// correctly.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_StagedOnEcho_StatusIsProposed()
    {
        var result = Staged(await StageNopOnEchoAsync());
        Assert.Equal("proposed", result.Status);
    }

    /// <summary>
    /// (1c) The result message says the rung was staged locally and NOT sent to the
    /// controller, so an MCP model cannot read the outcome as a controller change.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_StagedOnEcho_MessageStatesNotSentToController()
    {
        var result = Staged(await StageNopOnEchoAsync());

        Assert.Contains("STAGED LOCALLY", result.Message, StringComparison.Ordinal);
        Assert.Contains("NOT SENT TO CONTROLLER", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (1d) Staging leaves the controller unchanged: a fresh upload after the stage
    /// shows the routine's rungs exactly as they were, with no rung pending.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_StagedOnEcho_LeavesTheControllerUnchanged()
    {
        List<string> before;
        var connected = await ConnectToEchoAsync();
        try
        {
            before = RungsOf(connected.TypeProject!);
            Skip.If(before.Count == 0,
                $"{EchoProgram}/{EchoRoutine} has no rungs on Echo. Set LGX_HW_RUNG_PROGRAM and LGX_HW_RUNG_ROUTINE to an RLL routine.");
            Staged(await StageNopAsync(connected));
        }
        finally
        {
            await connected.Service.DisconnectAsync();
        }

        var again = await ConnectToEchoAsync();
        try
        {
            Assert.Equal(before, RungsOf(again.TypeProject!));
        }
        finally
        {
            await again.Service.DisconnectAsync();
        }
    }

    // ── Group 2 — gate is now advisory; scan runs but no denial ─────────────

    /// <summary>
    /// (2a) A fragment that writes <c>Safety_OK</c> (previously denied as
    /// <c>SafetyTag</c> by the Demo.L5X gate policy) now succeeds — the gate
    /// is advisory.  The handler returns <see cref="ProposeRungEditResult"/>
    /// with status="proposed".
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_PolicyDeniedTag_SafetyOk_NowSucceeds()
    {
        // Arrange: derive the Demo.L5X gate policy (Safety_OK → previously denied: SafetyTag).
        var policy = LoadDemoPolicy();

        // Use a fake session so the test does not require a live Echo controller.
        var (session, _) = await BuildFakeSessionAsync();

        // Act: propose a rung that writes Safety_OK.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            routineXPath:   TargetRoutineXPath,
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    FragmentWritesSafetyOk,
            session:        session,
            policy:         policy);

        // Assert: gate is advisory — result is a success, not an error.
        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);
    }

    /// <summary>
    /// (2b) With the gate advisory, a fragment writing <c>Safety_OK</c> produces
    /// a <see cref="ProposeRungEditResult"/> — the scan still runs but does not
    /// generate an error message about the tag.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_PolicyDeniedTag_SafetyOk_ResultIsSuccess_NotError()
    {
        var policy = LoadDemoPolicy();
        var (session, _) = await BuildFakeSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            TargetRoutineXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // Gate is advisory: result is ProposeRungEditResult, never ProposeRungEditError.
        Assert.IsNotType<ProposeRungEditError>(result);
        Assert.IsType<ProposeRungEditResult>(result);
    }

    /// <summary>
    /// (2c) The SDK import IS called when a policy is supplied and the fragment
    /// writes a previously-denied tag — the gate is advisory and does not
    /// block the SDK call.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_PolicyDeniedTag_SafetyOk_SdkCallIsMade()
    {
        var policy = LoadDemoPolicy();
        var (session, fake) = await BuildFakeSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            TargetRoutineXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // The gate is advisory: the SDK import must have been called.
        Assert.NotEmpty(fake.CapturedXPaths);
    }
}
#endif
