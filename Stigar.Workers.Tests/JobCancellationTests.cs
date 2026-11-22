using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stigar.Core.Jobs;
using Xunit;

namespace Stigar.Workers.Tests;

/// <summary>
/// Pins the ambient per-job cancellation token used by the two Hangfire targets
/// that have no CancellationToken parameter of their own (recurring dispatcher
/// and the weekly race PMTiles build). Hangfire substitutes the real token only
/// into declared parameters, so these targets read it from the server filter.
/// </summary>
public class JobCancellationTests
{
    [Fact]
    public void Token_OutsideAJob_IsNone()
    {
        Assert.Equal(CancellationToken.None, JobCancellation.Token);
    }

    [Fact]
    public void Begin_SetsAndRestoresTokenIncludingNestedScopes()
    {
        using var source = new CancellationTokenSource();
        var nested = CancellationToken.None;

        using (JobCancellation.Begin(source.Token))
        {
            Assert.Equal(source.Token, JobCancellation.Token);

            using (JobCancellation.Begin(CancellationToken.None))
            {
                nested = JobCancellation.Token;
            }

            Assert.Equal(source.Token, JobCancellation.Token);
        }

        Assert.Equal(CancellationToken.None, nested);
        Assert.Equal(CancellationToken.None, JobCancellation.Token);
    }

    [Fact]
    public void AddJobCancellationServerFilter_RegistersExactlyOneFilter()
    {
        var filters = new JobFilterCollection();

        HangfireInfrastructure.AddJobCancellationServerFilter(filters);
        HangfireInfrastructure.AddJobCancellationServerFilter(filters);

        var cancellationFilters = filters.Select(f => f.Instance)
            .OfType<JobCancellationServerFilterAttribute>().ToList();
        Assert.Single(cancellationFilters);
        Assert.IsAssignableFrom<IServerFilter>(cancellationFilters[0]);
    }

    [Fact]
    public void Filter_ExposesJobShutdownToken_AndClearsItAfterTheJob()
    {
        using var source = new CancellationTokenSource();
        var context = CreatePerformingContext(new StubJobCancellationToken(source.Token));
        var filter = new JobCancellationServerFilterAttribute();

        filter.OnPerforming(context);
        Assert.Equal(source.Token, JobCancellation.Token);

        filter.OnPerformed(new PerformedContext(context, result: null, canceled: false, exception: null));
        Assert.Equal(CancellationToken.None, JobCancellation.Token);
    }

    [Fact]
    public async Task RecurringJobDispatcher_ForwardsAmbientTokenToRecurringJob()
    {
        CancellationToken observed = CancellationToken.None;
        var job = new CapturingRecurringJob(token => observed = token);
        var dispatcher = new RecurringJobDispatcher(
            [job], NullLogger<RecurringJobDispatcher>.Instance);

        using var source = new CancellationTokenSource();
        using (JobCancellation.Begin(source.Token))
        {
            await dispatcher.RunAsync(job.JobId);
        }

        Assert.Equal(source.Token, observed);
    }

    [Fact]
    public async Task RecurringJobDispatcher_OutsideAJob_UsesNoneToken()
    {
        CancellationToken observed = CancellationToken.None;
        var job = new CapturingRecurringJob(token => observed = token);
        var dispatcher = new RecurringJobDispatcher(
            [job], NullLogger<RecurringJobDispatcher>.Instance);

        await dispatcher.RunAsync(job.JobId);

        Assert.Equal(CancellationToken.None, observed);
    }

    [Fact]
    public void Performer_ExposesAmbientTokenToJob_AndClearsItAfterwards()
    {
        using var source = new CancellationTokenSource();
        var filters = new JobFilterCollection();
        HangfireInfrastructure.AddJobCancellationServerFilter(filters);

        var performer = new BackgroundJobPerformer(filters, JobActivator.Current, TaskScheduler.Default);
        var context = CreatePerformingContext(
            new StubJobCancellationToken(source.Token),
            Job.FromExpression(() => ProbeJob.CaptureAmbientToken()));

        performer.Perform(context);

        Assert.Equal(source.Token, ProbeJob.CapturedToken);
        Assert.Equal(CancellationToken.None, JobCancellation.Token);
    }

    private static class ProbeJob
    {
        public static CancellationToken CapturedToken;

        public static void CaptureAmbientToken() => CapturedToken = JobCancellation.Token;
    }

    private static PerformingContext CreatePerformingContext(
        IJobCancellationToken cancellationToken,
        Job? job = null)
    {
        job ??= Job.FromExpression(() => Console.WriteLine("probe"));
        var backgroundJob = new BackgroundJob("probe-id", job, DateTime.UtcNow);
        var performContext = new PerformContext(
            storage: null,
            connection: Mock.Of<IStorageConnection>(),
            backgroundJob: backgroundJob,
            cancellationToken: cancellationToken);
        return new PerformingContext(performContext);
    }

    private sealed class StubJobCancellationToken(CancellationToken shutdownToken) : IJobCancellationToken
    {
        public CancellationToken ShutdownToken => shutdownToken;

        public void ThrowIfCancellationRequested() => shutdownToken.ThrowIfCancellationRequested();
    }

    private sealed class CapturingRecurringJob(Action<CancellationToken> capture) : IRecurringJob
    {
        public string JobId => "capture";

        public string Cron => "0 0 * * *";

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            capture(cancellationToken);
            return Task.CompletedTask;
        }
    }
}
