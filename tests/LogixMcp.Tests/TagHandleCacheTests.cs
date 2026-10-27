using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="TagHandleCache{THandle}"/>: one open handle per tag name, reused; a handle that loses its
/// connection is reopened (and a reused one retried once); idle and over-capacity handles are closed. Fake handles
/// and a fake clock, no network.
/// </summary>
public class TagHandleCacheTests
{
    private sealed class FakeHandle(string name, int serial)
    {
        public string Name { get; } = name;
        public int Serial { get; } = serial;
        public bool Closed { get; set; }
    }

    private sealed class ConnectionLost() : Exception("connection lost");

    /// <summary>A cache over fake handles, recording every open and close.</summary>
    private sealed class Rig
    {
        public List<FakeHandle> Opened { get; } = [];
        public long Now { get; set; }
        public Func<string, Exception?> OpenFails { get; set; } = _ => null;
        public TagHandleCache<FakeHandle> Cache { get; }

        public Rig(int capacity = 256, TimeSpan? idle = null)
        {
            Cache = new TagHandleCache<FakeHandle>(
                (name, _) =>
                {
                    if (OpenFails(name) is { } error) return Task.FromException<FakeHandle>(error);
                    var handle = new FakeHandle(name, Opened.Count + 1);
                    lock (Opened) Opened.Add(handle);
                    return Task.FromResult(handle);
                },
                handle => handle.Closed = true,
                idle ?? TimeSpan.FromSeconds(60), capacity, () => Now, autoSweep: false);
        }

        public Task<FakeHandle> Use(string name, Func<FakeHandle, Task>? then = null) =>
            Cache.UseAsync(name, async handle =>
            {
                if (then is not null) await then(handle);
                return handle;
            }, ex => ex is ConnectionLost);
    }

    [Fact]
    public async Task SameName_ReusesOneHandle_CaseInsensitively()
    {
        var rig = new Rig();

        var first  = await rig.Use("Master.Sts_Version");
        var second = await rig.Use("Master.Sts_Version");
        var third  = await rig.Use("MASTER.STS_VERSION");

        Assert.Single(rig.Opened);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.False(first.Closed);
    }

    [Fact]
    public async Task DifferentNames_GetTheirOwnHandles()
    {
        var rig = new Rig();

        var a = await rig.Use("A");
        var b = await rig.Use("B");

        Assert.NotSame(a, b);
        Assert.Equal(2, rig.Cache.Count);
    }

    [Fact]
    public async Task ConnectionLost_OnAHandleThatWorked_ReopensAndRetriesOnce()
    {
        var rig = new Rig();
        var first = await rig.Use("A");

        var failed = 0;
        var result = await rig.Use("A", handle =>
            handle == first && failed++ == 0 ? Task.FromException(new ConnectionLost()) : Task.CompletedTask);

        Assert.True(first.Closed);
        Assert.NotSame(first, result);
        Assert.False(result.Closed);
        Assert.Equal(2, rig.Opened.Count);
    }

    [Fact]
    public async Task ConnectionLost_OnAFreshHandle_IsNotRetried()
    {
        var rig = new Rig();

        await Assert.ThrowsAsync<ConnectionLost>(() => rig.Use("A", _ => Task.FromException(new ConnectionLost())));

        Assert.Single(rig.Opened);
        Assert.True(rig.Opened[0].Closed);
        Assert.Equal(0, rig.Cache.Count);
    }

    [Fact]
    public async Task ConnectionLost_Twice_IsRetriedOnlyOnce()
    {
        var rig = new Rig();
        await rig.Use("A");

        await Assert.ThrowsAsync<ConnectionLost>(() => rig.Use("A", _ => Task.FromException(new ConnectionLost())));

        Assert.Equal(2, rig.Opened.Count);
        Assert.All(rig.Opened, h => Assert.True(h.Closed));
    }

    [Fact]
    public async Task OtherErrors_LeaveTheHandleOpen()
    {
        var rig = new Rig();
        var first = await rig.Use("A");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Use("A", _ => Task.FromException(new InvalidOperationException("refused"))));
        var again = await rig.Use("A");

        Assert.Same(first, again);
        Assert.False(first.Closed);
    }

    [Fact]
    public async Task OpenFailure_IsNotKept_AndTheNextUseOpensAgain()
    {
        var rig = new Rig { OpenFails = _ => new ConnectionLost() };

        await Assert.ThrowsAsync<ConnectionLost>(() => rig.Use("A"));
        Assert.Equal(0, rig.Cache.Count);

        rig.OpenFails = _ => null;
        var handle = await rig.Use("A");

        Assert.False(handle.Closed);
        Assert.Equal(1, rig.Cache.Count);
    }

    [Fact]
    public async Task Sweep_ClosesOnlyIdleHandles()
    {
        var rig = new Rig(idle: TimeSpan.FromSeconds(60));
        var old = await rig.Use("Old");
        rig.Now = 50_000;
        var recent = await rig.Use("Recent");

        rig.Now = 70_000; // Old idle 70 s, Recent 20 s
        rig.Cache.Sweep();

        Assert.True(old.Closed);
        Assert.False(recent.Closed);
        Assert.Equal(1, rig.Cache.Count);
        Assert.NotSame(old, await rig.Use("Old"));
    }

    [Fact]
    public async Task OverCapacity_ClosesTheLeastRecentlyUsed()
    {
        var rig = new Rig(capacity: 2);
        var a = await rig.Use("A");
        rig.Now = 1;
        var b = await rig.Use("B");
        rig.Now = 2;
        await rig.Use("A"); // A is now more recent than B
        rig.Now = 3;
        var c = await rig.Use("C");

        Assert.True(b.Closed);
        Assert.False(a.Closed);
        Assert.False(c.Closed);
        Assert.Equal(2, rig.Cache.Count);
    }

    [Fact]
    public async Task Clear_ClosesEverything_AndAHandleInUseWhenItsOperationEnds()
    {
        var rig = new Rig();
        var idle = await rig.Use("Idle");
        var gate = new TaskCompletionSource();
        FakeHandle? busy = null;
        var running = rig.Use("Busy", async h => { busy = h; await gate.Task; });
        while (busy is null) await Task.Delay(1);

        rig.Cache.Clear();

        Assert.True(idle.Closed);
        Assert.False(busy.Closed); // still in use
        gate.SetResult();
        await running;
        Assert.True(busy.Closed);
        Assert.Equal(0, rig.Cache.Count);
    }

    [Fact]
    public async Task SameName_IsUsedByOneCallerAtATime()
    {
        var rig = new Rig();
        var inside = 0;
        var most   = 0;

        async Task Work(FakeHandle _)
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rig.Use("A", Work)));

        Assert.Equal(1, most);
        Assert.Single(rig.Opened);
    }

    [Fact]
    public async Task DifferentNames_RunSideBySide()
    {
        var rig = new Rig();
        var bothIn = new TaskCompletionSource();
        var count  = 0;

        async Task Meet(FakeHandle _)
        {
            if (Interlocked.Increment(ref count) == 2) bothIn.SetResult();
            await bothIn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Task.WhenAll(rig.Use("A", Meet), rig.Use("B", Meet)); // would time out if they were serialised
        Assert.Equal(2, count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
