using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Stigar.Workers;

/// <summary>
/// Accumulates elapsed time per named phase for a single worker job and emits one
/// consolidated summary line when disposed. This keeps slow phases visible without
/// logging every query or fetch. Phases are reported slowest-first so the likely
/// bottleneck is the first thing on the line.
/// </summary>
internal sealed class JobTimings : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _job;
    private readonly string _activityId;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _elapsedMs = new(StringComparer.Ordinal);
    private bool _disposed;

    public JobTimings(ILogger logger, string job, string activityId)
    {
        _logger = logger;
        _job = job;
        _activityId = activityId;
    }

    public async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await operation();
        }
        finally
        {
            Add(phase, stopwatch.Elapsed);
        }
    }

    public async Task MeasureAsync(string phase, Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await operation();
        }
        finally
        {
            Add(phase, stopwatch.Elapsed);
        }
    }

    public T Measure<T>(string phase, Func<T> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return operation();
        }
        finally
        {
            Add(phase, stopwatch.Elapsed);
        }
    }

    public void Measure(string phase, Action operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            operation();
        }
        finally
        {
            Add(phase, stopwatch.Elapsed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _total.Stop();

        var phases = _elapsedMs
            .OrderByDescending(entry => entry.Value)
            .Select(entry => $"{entry.Key}={entry.Value}ms");

        _logger.LogInformation(
            "{Job} timing for activity {ActivityId}: total={TotalMs}ms; {Phases}",
            _job,
            _activityId,
            _total.ElapsedMilliseconds,
            string.Join(", ", phases));
    }

    private void Add(string phase, TimeSpan elapsed)
    {
        _elapsedMs.TryGetValue(phase, out var prior);
        _elapsedMs[phase] = prior + (long)elapsed.TotalMilliseconds;
    }
}
