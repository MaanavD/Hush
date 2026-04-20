// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Hush.Core.Diagnostics;

/// <summary>
/// Lightweight in-process performance profiler for diagnosing CPU bottlenecks.
/// Enable by setting the environment variable <c>HUSH_PROFILE=1</c> before launch,
/// or call <see cref="Enable"/> programmatically.
///
/// Writes periodic snapshot reports to <c>hush-profile.log</c> in the working
/// directory (or the path specified by <c>HUSH_PROFILE_PATH</c>).
/// </summary>
public static class PerformanceProfiler
{
    private static readonly ConcurrentDictionary<string, OperationStats> _operations = new();
    private static readonly Stopwatch _sessionClock = new();
    private static Timer? _reportTimer;
    private static StreamWriter? _logWriter;
    private static bool _enabled;
    private static int _snapshotIndex;
    private static long _lastGen0, _lastGen1, _lastGen2;
    private static long _lastWorkingSet;

    /// <summary>Interval between automatic snapshot reports.</summary>
    private const int ReportIntervalMs = 3000;

    /// <summary>Returns true if the profiler is currently enabled.</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>
    /// Enables the profiler. Call once at app startup. Idempotent.
    /// </summary>
    public static void Enable(string? logPath = null)
    {
        if (_enabled) return;
        _enabled = true;

        logPath ??= Environment.GetEnvironmentVariable("HUSH_PROFILE_PATH")
                     ?? Path.Combine(AppContext.BaseDirectory, "hush-profile.log");

        _logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8)
        {
            AutoFlush = true
        };

        _sessionClock.Start();
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);
        _lastWorkingSet = Environment.WorkingSet;

        _logWriter.WriteLine($"=== Hush Performance Profiler Started at {DateTime.Now:O} ===");
        _logWriter.WriteLine($"Process ID: {Environment.ProcessId}");
        _logWriter.WriteLine($"Processor Count: {Environment.ProcessorCount}");
        _logWriter.WriteLine($"Working Set: {Environment.WorkingSet / 1024.0 / 1024.0:F1} MB");
        _logWriter.WriteLine();

        _reportTimer = new Timer(_ => WriteSnapshot(), null, ReportIntervalMs, ReportIntervalMs);
    }

    /// <summary>
    /// Attempts auto-enable from the <c>HUSH_PROFILE</c> environment variable.
    /// Call once during app bootstrap; no-op if the variable is not set.
    /// </summary>
    public static void TryAutoEnable()
    {
        var env = Environment.GetEnvironmentVariable("HUSH_PROFILE");
        if (env is "1" or "true" or "yes")
            Enable();
    }

    /// <summary>
    /// Begins a timed measurement for the named operation.
    /// Use in a <c>using</c> statement: <c>using var _ = PerformanceProfiler.Measure("OpName");</c>
    /// </summary>
    public static Measurement Measure(string operationName)
    {
        if (!_enabled) return default;
        return new Measurement(operationName, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Records a completed measurement with an optional context value (e.g., string length)
    /// that tracks whether the operation cost is growing with input size.
    /// </summary>
    public static void Record(string operationName, long elapsedTicks, int contextSize = 0)
    {
        if (!_enabled) return;

        var stats = _operations.GetOrAdd(operationName, _ => new OperationStats());
        var elapsedUs = elapsedTicks * 1_000_000L / Stopwatch.Frequency;
        stats.Record(elapsedUs, contextSize);
    }

    /// <summary>
    /// Records a simple counter increment (e.g., number of active tasks, buffer length).
    /// </summary>
    public static void Gauge(string name, long value)
    {
        if (!_enabled) return;
        var stats = _operations.GetOrAdd(name, _ => new OperationStats());
        stats.SetGauge(value);
    }

    /// <summary>Writes the current snapshot to the log immediately.</summary>
    public static void Flush() => WriteSnapshot();

    /// <summary>Stops the profiler and closes the log file.</summary>
    public static void Shutdown()
    {
        if (!_enabled) return;
        _enabled = false;
        _reportTimer?.Dispose();
        _reportTimer = null;
        WriteSnapshot();
        _logWriter?.WriteLine($"\n=== Profiler Stopped at {DateTime.Now:O} ===");
        _logWriter?.Dispose();
        _logWriter = null;
    }

    private static void WriteSnapshot()
    {
        if (_logWriter is null) return;

        var sb = new StringBuilder();
        var now = _sessionClock.Elapsed;
        var idx = Interlocked.Increment(ref _snapshotIndex);

        // GC stats
        long gen0 = GC.CollectionCount(0);
        long gen1 = GC.CollectionCount(1);
        long gen2 = GC.CollectionCount(2);
        long ws = Environment.WorkingSet;
        long managedBytes = GC.GetTotalMemory(forceFullCollection: false);

        // Thread pool
        ThreadPool.GetAvailableThreads(out int availWorker, out int availIO);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIO);
        int busyWorker = maxWorker - availWorker;
        int busyIO = maxIO - availIO;

        sb.AppendLine($"── Snapshot #{idx} at {now.TotalSeconds:F1}s ──────────────────────────");
        sb.AppendLine($"  Memory: WS={ws / 1024.0 / 1024.0:F1}MB  Managed={managedBytes / 1024.0 / 1024.0:F1}MB  ΔWS={((ws - _lastWorkingSet) / 1024.0 / 1024.0):+0.0;-0.0;0.0}MB");
        sb.AppendLine($"  GC: Gen0={gen0}(+{gen0 - _lastGen0})  Gen1={gen1}(+{gen1 - _lastGen1})  Gen2={gen2}(+{gen2 - _lastGen2})");
        sb.AppendLine($"  Threads: pool-workers={busyWorker}/{maxWorker}  pool-io={busyIO}/{maxIO}");

        _lastGen0 = gen0;
        _lastGen1 = gen1;
        _lastGen2 = gen2;
        _lastWorkingSet = ws;

        // Operation stats — sorted by total time descending
        var ops = _operations.ToArray();
        if (ops.Length > 0)
        {
            Array.Sort(ops, (a, b) => b.Value.TotalUs.CompareTo(a.Value.TotalUs));
            sb.AppendLine("  ┌─────────────────────────────────┬────────┬──────────┬──────────┬──────────┬──────────┬───────────┐");
            sb.AppendLine("  │ Operation                       │  Count │   Total  │    Avg   │    Min   │    Max   │ Ctx Trend │");
            sb.AppendLine("  ├─────────────────────────────────┼────────┼──────────┼──────────┼──────────┼──────────┼───────────┤");
            foreach (var (name, stats) in ops)
            {
                var snap = stats.GetSnapshot();
                if (snap.Count == 0 && snap.GaugeValue == 0) continue;

                if (snap.IsGauge)
                {
                    sb.AppendLine($"  │ {name,-33} │ GAUGE  │ {snap.GaugeValue,8} │          │          │          │           │");
                }
                else
                {
                    string FormatUs(long us) => us switch
                    {
                        >= 1_000_000 => $"{us / 1_000_000.0:F1}s",
                        >= 1_000 => $"{us / 1_000.0:F1}ms",
                        _ => $"{us}μs"
                    };

                    var trend = snap.ContextTrend switch
                    {
                        > 0.5 => "↑ GROWING",
                        < -0.5 => "↓ shrink",
                        _ => snap.AvgContextSize > 0 ? $"~{snap.AvgContextSize}" : "—"
                    };

                    sb.AppendLine($"  │ {name,-33} │ {snap.Count,6} │ {FormatUs(snap.TotalUs),8} │ {FormatUs(snap.AvgUs),8} │ {FormatUs(snap.MinUs),8} │ {FormatUs(snap.MaxUs),8} │ {trend,-9} │");
                }
            }
            sb.AppendLine("  └─────────────────────────────────┴────────┴──────────┴──────────┴──────────┴──────────┴───────────┘");
        }

        try { _logWriter.Write(sb.ToString()); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Disposable struct for scoped timing. Zero-alloc when profiling is disabled.
    /// </summary>
    public readonly struct Measurement : IDisposable
    {
        private readonly string? _name;
        private readonly long _startTicks;

        internal Measurement(string name, long startTicks)
        {
            _name = name;
            _startTicks = startTicks;
        }

        public void Dispose()
        {
            if (_name is null) return;
            var elapsed = Stopwatch.GetTimestamp() - _startTicks;
            Record(_name, elapsed);
        }
    }

    /// <summary>
    /// Like <see cref="Measure"/> but also records a context size when disposed.
    /// Use when you want to correlate timing with input size growth.
    /// </summary>
    public static ContextMeasurement MeasureWithContext(string operationName, int contextSize)
    {
        if (!_enabled) return default;
        return new ContextMeasurement(operationName, Stopwatch.GetTimestamp(), contextSize);
    }

    public readonly struct ContextMeasurement : IDisposable
    {
        private readonly string? _name;
        private readonly long _startTicks;
        private readonly int _contextSize;

        internal ContextMeasurement(string name, long startTicks, int contextSize)
        {
            _name = name;
            _startTicks = startTicks;
            _contextSize = contextSize;
        }

        public void Dispose()
        {
            if (_name is null) return;
            var elapsed = Stopwatch.GetTimestamp() - _startTicks;
            Record(_name, elapsed, _contextSize);
        }
    }
}

/// <summary>
/// Thread-safe accumulator for a single named operation's timing data.
/// Tracks count, total, min, max, and a simple linear trend on context size
/// to detect operations that get slower as input grows.
/// </summary>
internal sealed class OperationStats
{
    private long _count;
    private long _totalUs;
    private long _minUs = long.MaxValue;
    private long _maxUs;
    private long _gaugeValue;
    private bool _isGauge;

    // For trend detection: track (contextSize, elapsedUs) in a ring buffer
    private const int TrendWindowSize = 50;
    private readonly (int ctx, long us)[] _trendWindow = new (int, long)[TrendWindowSize];
    private int _trendIndex;
    private int _trendCount;
    private long _totalContextSize;

    private readonly object _lock = new();

    public long TotalUs => Interlocked.Read(ref _totalUs);

    public void Record(long elapsedUs, int contextSize)
    {
        lock (_lock)
        {
            _count++;
            _totalUs += elapsedUs;
            if (elapsedUs < _minUs) _minUs = elapsedUs;
            if (elapsedUs > _maxUs) _maxUs = elapsedUs;
            _totalContextSize += contextSize;

            if (contextSize > 0)
            {
                _trendWindow[_trendIndex % TrendWindowSize] = (contextSize, elapsedUs);
                _trendIndex++;
                if (_trendCount < TrendWindowSize) _trendCount++;
            }
        }
    }

    public void SetGauge(long value)
    {
        _isGauge = true;
        Interlocked.Exchange(ref _gaugeValue, value);
    }

    public StatsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            double trend = 0;
            int avgCtx = _count > 0 ? (int)(_totalContextSize / _count) : 0;

            // Simple trend: compare avg time of first half vs second half of window
            if (_trendCount >= 10)
            {
                int half = _trendCount / 2;
                int start = (_trendIndex - _trendCount + TrendWindowSize) % TrendWindowSize;

                double firstHalfAvg = 0, secondHalfAvg = 0;
                for (int i = 0; i < half; i++)
                    firstHalfAvg += _trendWindow[(start + i) % TrendWindowSize].us;
                firstHalfAvg /= half;

                for (int i = half; i < _trendCount; i++)
                    secondHalfAvg += _trendWindow[(start + i) % TrendWindowSize].us;
                secondHalfAvg /= (_trendCount - half);

                trend = firstHalfAvg > 0
                    ? (secondHalfAvg - firstHalfAvg) / firstHalfAvg
                    : 0;
            }

            return new StatsSnapshot(
                _count, _totalUs,
                _count > 0 ? _totalUs / _count : 0,
                _minUs == long.MaxValue ? 0 : _minUs,
                _maxUs, trend, avgCtx, _isGauge, _gaugeValue);
        }
    }
}

internal readonly record struct StatsSnapshot(
    long Count,
    long TotalUs,
    long AvgUs,
    long MinUs,
    long MaxUs,
    double ContextTrend,
    int AvgContextSize,
    bool IsGauge,
    long GaugeValue);
