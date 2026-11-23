using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Stigar.Core.Jobs;
using Xunit;

namespace Stigar.Workers.Tests;

/// <summary>
/// Pins the bridge that forwards worker ILogger output into the current
/// Hangfire.Console job session, and the filter that opens that session for
/// every Hangfire job target.
/// </summary>
public class HangfireConsoleLoggingTests
{
    private sealed class RecordingWriter : IJobConsoleWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_gate) return _lines.ToList(); }
        }

        public void WriteLine(string message)
        {
            lock (_gate) _lines.Add(message);
        }
    }

    private sealed class FailOnceWriter : IJobConsoleWriter
    {
        public int Calls { get; private set; }

        public void WriteLine(string message)
        {
            Calls++;
            if (Calls == 1)
                throw new InvalidOperationException("redis timeout");
        }
    }

    [Fact]
    public void LoggerProvider_DoesNotThrow_WhenConsoleWriteFails()
    {
        var writer = new FailOnceWriter();

        using (JobConsole.Begin(writer))
        {
            var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");
            var exception = Record.Exception(() => logger.LogInformation("diagnostic line"));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void LoggerProvider_PausesConsoleWrites_AfterFailure()
    {
        var writer = new FailOnceWriter();

        using (JobConsole.Begin(writer))
        {
            var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");
            logger.LogInformation("first");
            logger.LogInformation("second");

            // The second line must be skipped while the sink is cooling down.
            Assert.Equal(1, writer.Calls);
        }
    }

    [Fact]
    public void LoggerProvider_ForwardsInformationLine_WhenJobConsoleIsActive()
    {
        var writer = new RecordingWriter();

        using (JobConsole.Begin(writer))
        {
            var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");
            logger.LogInformation("Activity {ActivityId} has {SummitCount} summits", 42, 7);
        }

        var line = Assert.Single(writer.Lines);
        Assert.Equal("Activity 42 has 7 summits", line);
    }

    [Fact]
    public void LoggerProvider_SkipsDebugLines()
    {
        var writer = new RecordingWriter();

        using (JobConsole.Begin(writer))
        {
            var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");
            logger.LogDebug("noise");
        }

        Assert.Empty(writer.Lines);
    }

    [Fact]
    public void LoggerProvider_IsNoOp_OutsideAJob()
    {
        var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");

        var exception = Record.Exception(() => logger.LogError("nothing active"));

        Assert.Null(exception);
    }

    [Fact]
    public void LoggerProvider_AppendsExceptionDetails()
    {
        var writer = new RecordingWriter();

        using (JobConsole.Begin(writer))
        {
            var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");
            logger.LogError(new InvalidOperationException("boom"), "failed");
        }

        var line = Assert.Single(writer.Lines);
        Assert.StartsWith("[error] failed", line);
        Assert.Contains("boom", line);
    }

    [Fact]
    public void LoggerProvider_ChunksLongMessagesBelowConsoleInlineLimit()
    {
        var writer = new RecordingWriter();
        var message = new string('x', 600);

        using (JobConsole.Begin(writer))
        {
            var logger = new HangfireConsoleLoggerProvider().CreateLogger("Test");
            logger.LogInformation("{Message}", message);
        }

        Assert.True(writer.Lines.Count > 1);
        Assert.All(writer.Lines, line => Assert.True(line.Length <= JobConsole.MaxLineLength));
        Assert.Equal(message, string.Concat(writer.Lines));
    }
}
