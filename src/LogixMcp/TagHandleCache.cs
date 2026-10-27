// ── TagHandleCache ────────────────────────────────────────────────────────────

/// <summary>
/// Keeps one open handle per tag name for a session, so a read or write reuses the open handle, and the CIP
/// connection behind it, instead of opening and closing a handle every time. Opening is what made each CIP
/// operation cost 120-140 ms: libplctag closes its connection to the controller when its last tag is disposed.
///
/// <para>
/// Each handle is used by one caller at a time (a per-handle lock), so an operation never sees another
/// operation's buffer; different tags are used side by side. A handle that fails with a connection error is
/// closed; when it had worked before, the operation is retried once on a freshly opened handle. Handles unused
/// for <see cref="DefaultIdle"/> are closed, at most <see cref="DefaultCapacity"/> stay open (the least recently
/// used idle one is closed first), and <see cref="Clear"/> closes them all. The idle timer runs only while a
/// handle is open, so a cache that is no longer used holds nothing.
/// </para>
/// </summary>
/// <typeparam name="THandle">The handle type (a libplctag <c>Tag</c> in production).</typeparam>
public sealed class TagHandleCache<THandle> : IDisposable where THandle : class
{
    /// <summary>How long an unused handle stays open.</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(60);

    /// <summary>The most handles kept open at once.</summary>
    public const int DefaultCapacity = 256;

    private sealed class Entry(string name)
    {
        public string Name { get; } = name;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Task<THandle>? Opening;  // started by the first caller to hold the gate
        public int Users;               // callers holding or waiting for the gate
        public long LastUsed;           // clock milliseconds
        public bool Worked;             // an operation has succeeded on this handle
        public bool Retired;            // no longer in the cache; closed when its last user leaves
        public bool Closed;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, Task<THandle>> _open;
    private readonly Action<THandle> _close;
    private readonly Func<long> _clock;
    private readonly long _idleMs;
    private readonly int _capacity;
    private readonly Timer? _sweeper;

    /// <summary>Creates the cache.</summary>
    /// <param name="open">Opens (creates and initializes) the handle for a tag name.</param>
    /// <param name="close">Closes a handle; exceptions are ignored.</param>
    /// <param name="idle">How long an unused handle stays open; <see cref="DefaultIdle"/> when null.</param>
    /// <param name="capacity">The most handles kept open; <see cref="DefaultCapacity"/> when null.</param>
    /// <param name="clock">Milliseconds now; <see cref="Environment.TickCount64"/> when null. For tests.</param>
    /// <param name="autoSweep">Whether a timer closes idle handles; tests call <see cref="Sweep"/> instead.</param>
    public TagHandleCache(
        Func<string, CancellationToken, Task<THandle>> open,
        Action<THandle> close,
        TimeSpan? idle = null,
        int? capacity = null,
        Func<long>? clock = null,
        bool autoSweep = true)
    {
        _open     = open ?? throw new ArgumentNullException(nameof(open));
        _close    = close ?? throw new ArgumentNullException(nameof(close));
        _idleMs   = (long)(idle ?? DefaultIdle).TotalMilliseconds;
        _capacity = Math.Max(1, capacity ?? DefaultCapacity);
        _clock    = clock ?? (() => Environment.TickCount64);
        if (autoSweep) _sweeper = new Timer(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The number of handles currently in the cache.</summary>
    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>
    /// Runs <paramref name="operation"/> on the open handle for <paramref name="name"/>, opening it first when
    /// needed.
    /// </summary>
    /// <param name="name">The tag name (case-insensitive).</param>
    /// <param name="operation">The read or write to run; it has the handle to itself while it runs.</param>
    /// <param name="isConnectionError">Whether an exception means the connection behind the handle is gone.</param>
    /// <param name="ct">Cancellation token for waiting and opening.</param>
    /// <returns>The operation's result.</returns>
    public async Task<T> UseAsync<T>(
        string name, Func<THandle, Task<T>> operation, Func<Exception, bool> isConnectionError,
        CancellationToken ct = default)
    {
        var retried = false;
        while (true)
        {
            var entry = Acquire(name);
            var holding = false;
            try
            {
                await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
                holding = true;

                // Retired while this caller waited (an error, Clear or the cap): take a fresh handle.
                if (entry.Retired) continue;

                THandle handle;
                try
                {
                    handle = await (entry.Opening ??= _open(name, ct)).ConfigureAwait(false);
                }
                catch
                {
                    // A handle that failed to open is never kept: the next use opens it again.
                    Retire(entry);
                    throw;
                }

                try
                {
                    var result = await operation(handle).ConfigureAwait(false);
                    entry.Worked = true;
                    return result;
                }
                catch (Exception ex) when (isConnectionError(ex))
                {
                    // The connection behind this handle is gone. A handle that had worked before most likely went
                    // stale (the controller or the link restarted), so try once more on a fresh one.
                    var worked = entry.Worked;
                    Retire(entry);
                    if (!worked || retried) throw;
                    retried = true;
                }
            }
            finally
            {
                if (holding) entry.Gate.Release();
                Release(entry);
            }
        }
    }

    /// <summary>Closes every handle; a handle in use is closed when its operation finishes.</summary>
    public void Clear()
    {
        List<Entry> close;
        lock (_lock)
        {
            close = [];
            foreach (var entry in _entries.Values)
            {
                entry.Retired = true;
                if (entry.Users == 0) { entry.Closed = true; close.Add(entry); }
            }
            _entries.Clear();
            StopSweeper();
        }
        CloseAll(close);
    }

    /// <summary>Closes the handles unused for longer than the idle time. Runs on the timer; tests call it.</summary>
    public void Sweep()
    {
        List<Entry> close = [];
        lock (_lock)
        {
            var now = _clock();
            foreach (var entry in _entries.Values.Where(e => e.Users == 0 && now - e.LastUsed >= _idleMs).ToList())
            {
                _entries.Remove(entry.Name);
                entry.Retired = entry.Closed = true;
                close.Add(entry);
            }
            if (_entries.Count == 0) StopSweeper();
        }
        CloseAll(close);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Clear();
        _sweeper?.Dispose();
    }

    // ── Bookkeeping ───────────────────────────────────────────────────────────

    private Entry Acquire(string name)
    {
        List<Entry> close = [];
        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out entry!) || entry.Retired)
            {
                entry = new Entry(name) { LastUsed = _clock() };
                _entries[name] = entry;
                TrimToCapacity(close);
                if (_entries.Count == 1) StartSweeper();
            }
            entry.Users++;
        }
        CloseAll(close);
        return entry;
    }

    private void Release(Entry entry)
    {
        var close = false;
        lock (_lock)
        {
            entry.Users--;
            entry.LastUsed = _clock();
            if (entry.Retired && entry.Users == 0 && !entry.Closed)
                close = entry.Closed = true;
        }
        if (close) CloseHandle(entry);
    }

    private void Retire(Entry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(entry.Name, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.Name);
            entry.Retired = true;
            if (_entries.Count == 0) StopSweeper();
        }
    }

    // Over the cap: close the least recently used handles nobody is using. Called under the lock.
    private void TrimToCapacity(List<Entry> close)
    {
        while (_entries.Count > _capacity)
        {
            var oldest = _entries.Values.Where(e => e.Users == 0).OrderBy(e => e.LastUsed).FirstOrDefault();
            if (oldest is null) return;
            _entries.Remove(oldest.Name);
            oldest.Retired = oldest.Closed = true;
            close.Add(oldest);
        }
    }

    private void StartSweeper()
    {
        var period = Math.Max(1000, _idleMs / 2);
        _sweeper?.Change(period, period);
    }

    private void StopSweeper() => _sweeper?.Change(Timeout.Infinite, Timeout.Infinite);

    private void CloseAll(List<Entry> entries)
    {
        foreach (var entry in entries) CloseHandle(entry);
    }

    private void CloseHandle(Entry entry)
    {
        if (entry.Opening is not { IsCompletedSuccessfully: true } opened) return;
        try { _close(opened.Result); }
        catch (Exception) { /* closing is best effort */ }
    }
}
