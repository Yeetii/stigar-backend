using Microsoft.Extensions.Logging;

namespace Stigar.Workers.Tests;

public class JobTimingsTests
{
    [Fact]
    public async Task Dispose_EmitsOneSummaryWithAllPhasesOrderedSlowestFirst()
    {
        var logger = new CapturingLogger();

        using (var timings = new JobTimings(logger, "TestJob", "act-1"))
        {
            await timings.MeasureAsync("fast", () => Task.CompletedTask);
            await timings.MeasureAsync("slow", () => Task.Delay(40));
            timings.Measure("sync", () => Thread.SpinWait(1000));
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("TestJob timing for activity act-1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("total=", entry.Message, StringComparison.Ordinal);
        Assert.Contains("slow=", entry.Message, StringComparison.Ordinal);
        Assert.Contains("fast=", entry.Message, StringComparison.Ordinal);
        Assert.Contains("sync=", entry.Message, StringComparison.Ordinal);
        Assert.True(
            entry.Message.IndexOf("slow=", StringComparison.Ordinal)
                < entry.Message.IndexOf("fast=", StringComparison.Ordinal),
            "The slowest phase should be listed first.");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var logger = new CapturingLogger();
        var timings = new JobTimings(logger, "TestJob", "act-2");

        timings.Dispose();
        timings.Dispose();

        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task Dispose_EmitsSummaryEvenWhenMeasuredOperationThrows()
    {
        var logger = new CapturingLogger();

        using (var timings = new JobTimings(logger, "TestJob", "act-3"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                timings.MeasureAsync("db", () => Task.FromException(new InvalidOperationException("boom"))));
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("db=", entry.Message, StringComparison.Ordinal);
    }
}
