using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_run_test</c> as a server-side job: a run within the sync budget answers as a direct call would, a longer
/// one answers <c>running</c> with a run id and keeps going, and <c>logix_run_test_status</c> returns its full result
/// once it has finished. No network: the runs are stand-ins.
/// </summary>
public class RunTestJobsTests
{
    private const string Result = """{"overall":"pass","cases":[{"name":"a","steps":[{"verdict":"pass"}]}]}""";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task RunWithinBudget_ReturnsTheResultUnchanged()
    {
        var jobs = new RunTestJobs();

        var reply = await jobs.RunAsync(_ => Task.FromResult(Result), TimeSpan.FromSeconds(5));

        Assert.Equal(Result, reply);
    }

    [Fact]
    public async Task RunOverBudget_AnswersRunning_ThenStatusReturnsTheFullResult()
    {
        var jobs    = new RunTestJobs();
        var release = new TaskCompletionSource();

        var reply = Parse(await jobs.RunAsync(async progress =>
        {
            progress.Report((1, 3, "Running case 2 of 3: b"));
            await release.Task;
            return Result;
        }, TimeSpan.FromMilliseconds(50)));

        Assert.Equal("running", reply.GetProperty("status").GetString());
        var runId = reply.GetProperty("runId").GetString();
        Assert.Equal(1, reply.GetProperty("casesCompleted").GetInt32());
        Assert.Equal(3, reply.GetProperty("casesTotal").GetInt32());

        var running = Parse(jobs.GetStatus(runId));
        Assert.Equal("running", running.GetProperty("status").GetString());
        Assert.Equal("Running case 2 of 3: b", running.GetProperty("message").GetString());

        release.SetResult();
        await WaitUntilAsync(() => Parse(jobs.GetStatus(runId)).GetProperty("status").GetString() != "running");

        var finished = Parse(jobs.GetStatus(runId));
        Assert.Equal("finished", finished.GetProperty("status").GetString());
        Assert.Equal("pass", finished.GetProperty("result").GetProperty("overall").GetString());
        Assert.Equal("pass", finished.GetProperty("result").GetProperty("cases")[0]
            .GetProperty("steps")[0].GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task SecondRunWhileOneIsGoing_IsRefused_AndNotStarted()
    {
        var jobs    = new RunTestJobs();
        var release = new TaskCompletionSource();
        var first   = Parse(await jobs.RunAsync(async _ => { await release.Task; return Result; },
            TimeSpan.FromMilliseconds(20)));
        var started = false;

        var second = Parse(await jobs.RunAsync(_ => { started = true; return Task.FromResult(Result); },
            TimeSpan.FromSeconds(1)));

        Assert.False(started);
        Assert.Equal(ErrorKinds.RunTestInProgress, second.GetProperty("kind").GetString());
        Assert.Equal(first.GetProperty("runId").GetString(), second.GetProperty("runId").GetString());
        release.SetResult();
    }

    [Fact]
    public async Task StatusWithoutRunId_ReportsTheMostRecentRun()
    {
        var jobs = new RunTestJobs();
        await jobs.RunAsync(_ => Task.FromResult(Result), TimeSpan.FromSeconds(5));

        var status = Parse(jobs.GetStatus(null));

        Assert.Equal("finished", status.GetProperty("status").GetString());
        Assert.Equal("run-1", status.GetProperty("runId").GetString());
    }

    [Fact]
    public void UnknownRun_IsRunNotFound()
    {
        var status = Parse(new RunTestJobs().GetStatus("run-99"));

        Assert.Equal(ErrorKinds.RunNotFound, status.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task RunThatThrows_ReportsFailed()
    {
        var jobs  = new RunTestJobs();
        var reply = Parse(await jobs.RunAsync(async _ =>
        {
            await Task.Delay(100);
            throw new InvalidOperationException("boom");
        }, TimeSpan.FromMilliseconds(10)));

        var runId = reply.GetProperty("runId").GetString();
        await WaitUntilAsync(() => Parse(jobs.GetStatus(runId)).GetProperty("status").GetString() != "running");

        var status = Parse(jobs.GetStatus(runId));
        Assert.Equal("failed", status.GetProperty("status").GetString());
        Assert.Contains("boom", status.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("", 20)]
    [InlineData("abc", 20)]
    [InlineData("-1", 20)]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData("300", 40)]
    public void SyncBudget_DefaultsAndClamps(string? raw, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RunTestJobs.ResolveSyncBudget(raw));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the run to finish");
            await Task.Delay(10);
        }
    }
}
