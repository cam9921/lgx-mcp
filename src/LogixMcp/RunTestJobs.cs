using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Runs <c>logix_run_test</c> calls as server-side jobs, so a long test survives a client that stops waiting for the
/// reply. A run that finishes within the sync budget (<see cref="BudgetEnvVar"/>, default 20 s) answers exactly as a
/// direct call would; a longer one answers <c>{status:"running", runId, ...}</c> at once and keeps running, and
/// <c>logix_run_test_status</c> returns its progress and, once it has finished, its full result. One run at a time:
/// two runs would drive the same emulator's tags against each other.
/// </summary>
public sealed class RunTestJobs
{
    /// <summary>Environment variable holding the sync budget in seconds.</summary>
    public const string BudgetEnvVar = "LGX_RUN_TEST_SYNC_BUDGET_S";

    /// <summary>Sync budget when <see cref="BudgetEnvVar"/> is unset or invalid.</summary>
    public static readonly TimeSpan DefaultSyncBudget = TimeSpan.FromSeconds(20);

    /// <summary>Largest sync budget accepted: clients commonly give up on a tool call after about 60 s.</summary>
    public static readonly TimeSpan MaxSyncBudget = TimeSpan.FromSeconds(40);

    /// <summary>How many finished runs are kept for <c>logix_run_test_status</c>.</summary>
    public const int KeptRuns = 10;

    /// <summary>The process-wide registry.</summary>
    public static RunTestJobs Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<Run> _runs = [];
    private int _nextId;

    private sealed class Run(string id)
    {
        public string Id { get; } = id;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public int Completed;
        public int Total;
        public string Message = "Starting";
        public Task<string> Work = null!;
        public long? FinishedMs;
    }

    // Calls the handler on the reporting thread (Progress<T> would post it), so the state is current at once.
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>Resolves the sync budget from a raw environment value.</summary>
    /// <param name="raw">Seconds, or <c>null</c>.</param>
    /// <returns>The budget: <see cref="DefaultSyncBudget"/> for a missing or invalid value, at most <see cref="MaxSyncBudget"/>.</returns>
    public static TimeSpan ResolveSyncBudget(string? raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0 && double.IsFinite(s)
            ? TimeSpan.FromSeconds(Math.Min(s, MaxSyncBudget.TotalSeconds))
            : DefaultSyncBudget;

    /// <summary>Reads the sync budget from the process environment.</summary>
    /// <returns>The effective sync budget.</returns>
    public static TimeSpan SyncBudgetFromEnvironment() =>
        ResolveSyncBudget(Environment.GetEnvironmentVariable(BudgetEnvVar));

    /// <summary>
    /// Starts <paramref name="work"/> as a job and waits up to <paramref name="budget"/> for it.
    /// </summary>
    /// <param name="work">
    /// The run: receives a progress sink (cases completed, case total, message) and returns the tool payload. It is
    /// never cancelled by the caller going away, so a test always restores what it wrote.
    /// </param>
    /// <param name="budget">How long the caller waits before getting a <c>running</c> payload.</param>
    /// <returns>
    /// The run's payload when it finishes in time; otherwise the <c>running</c> payload. While another run is still
    /// going, a <c>RunTestInProgress</c> error naming it, and nothing is started.
    /// </returns>
    public async Task<string> RunAsync(
        Func<IProgress<(int Completed, int Total, string Message)>, Task<string>> work, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(work);
        Run run;
        lock (_gate)
        {
            if (_runs.FirstOrDefault(r => !r.Work.IsCompleted) is { } busy)
                return JsonSerializer.Serialize(new
                {
                    error = $"logix_run_test refused: run '{busy.Id}' is still running. Nothing was written.",
                    kind  = ErrorKinds.RunTestInProgress,
                    runId = busy.Id,
                    hint  = "Poll logix_run_test_status until that run has finished, then call logix_run_test again.",
                });

            run = new Run($"run-{++_nextId}");
            var progress = new SynchronousProgress<(int Completed, int Total, string Message)>(p =>
            {
                lock (_gate) { run.Completed = p.Completed; run.Total = p.Total; run.Message = p.Message; }
            });
            run.Work = Task.Run(async () =>
            {
                try { return await work(progress).ConfigureAwait(false); }
                finally { lock (_gate) run.FinishedMs = run.Clock.ElapsedMilliseconds; }
            });
            _runs.Add(run);
            if (_runs.Count > KeptRuns && _runs.FirstOrDefault(r => r.Work.IsCompleted) is { } oldest)
                _runs.Remove(oldest);
        }

        if (await Task.WhenAny(run.Work, Task.Delay(budget)).ConfigureAwait(false) == run.Work)
            return await run.Work.ConfigureAwait(false);
        return Status(run);
    }

    /// <summary>
    /// The <c>logix_run_test_status</c> payload for <paramref name="runId"/>, or for the most recent run when it is
    /// <c>null</c>. Read-only: it never starts, cancels or changes a run.
    /// </summary>
    /// <param name="runId">Run id from a <c>running</c> payload; optional.</param>
    /// <returns>The serialized payload.</returns>
    public string GetStatus(string? runId)
    {
        Run? run;
        lock (_gate)
            run = string.IsNullOrWhiteSpace(runId)
                ? _runs.LastOrDefault()
                : _runs.FirstOrDefault(r => string.Equals(r.Id, runId, StringComparison.OrdinalIgnoreCase));

        if (run is null)
            return JsonSerializer.Serialize(new
            {
                error = string.IsNullOrWhiteSpace(runId)
                    ? "No logix_run_test run has been started by this server."
                    : $"No run '{runId}' is known to this server.",
                kind = ErrorKinds.RunNotFound,
                hint = "Runs live in the server process: a restarted server has none. The audit log keeps every " +
                       "step of every run (test_step events).",
            });
        return Status(run);
    }

    private string Status(Run run)
    {
        lock (_gate)
        {
            if (!run.Work.IsCompleted)
                return JsonSerializer.Serialize(new
                {
                    status         = "running",
                    runId          = run.Id,
                    startedAt      = run.StartedAt,
                    elapsedMs      = run.Clock.ElapsedMilliseconds,
                    casesCompleted = run.Completed,
                    casesTotal     = run.Total,
                    message        = run.Message,
                    pollWith       = "logix_run_test_status",
                    note           = "The run keeps going in the server and restores what it writes. Do not call " +
                                     "logix_run_test again until it has finished.",
                });
        }

        var payload = new JsonObject
        {
            ["status"]    = run.Work.IsCompletedSuccessfully ? "finished" : "failed",
            ["runId"]     = run.Id,
            ["startedAt"] = run.StartedAt,
            ["elapsedMs"] = run.FinishedMs,
        };
        if (run.Work.IsCompletedSuccessfully)
            payload["result"] = JsonNode.Parse(run.Work.Result);
        else
        {
            payload["error"] = run.Work.Exception?.GetBaseException().Message ?? "The run was cancelled.";
            payload["kind"]  = "Unknown";
        }
        return payload.ToJsonString();
    }
}
