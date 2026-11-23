using Hangfire;
using Moq;
using Stigar.Core.Jobs;
using Stigar.Core.Models;
using Stigar.Core.Services.Postgres;
using Stigar.Core.Services.Postgres.Admin;

namespace Stigar.Workers.Tests;

public class QueueActivityJobsTests
{
    private const long Epoch = 1;

    [Fact]
    public async Task PollAndQueueAsync_LimitsPollToOneThousandActivities()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var activities = new Mock<IActivityRepository>();
        var queries = new Mock<IBackendAdminQueries>();
        var epochs = new Mock<IJobEpochService>();

        epochs.Setup(e => e.GetAsync(It.IsAny<JobEpochType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Epoch);
        queries.Setup(q => q.GetActivityIdsNeedingProcessing(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var poller = new QueueActivityJobs(jobs.Object, activities.Object, queries.Object, epochs.Object);

        await poller.PollAndQueueAsync(CancellationToken.None);

        // One job of each type per activity, so this caps each type at 1000 per poll.
        queries.Verify(q => q.GetActivityIdsNeedingProcessing(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), 1000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ShouldQueueAnyStage_ReturnsTrue_WhenProcessingStatusIsMissing()
    {
        var activity = CreateActivity();

        Assert.True(ShouldQueueAnyStage(activity));
    }

    [Fact]
    public void ShouldQueueAnyStage_ReturnsTrue_WhenNoStagesHaveRunYet()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus();

        Assert.True(ShouldQueueAnyStage(activity));
    }

    [Fact]
    public void ShouldQueueAnyStage_ReturnsTrue_WhenAnyStageHasAlreadyRunButOthersRemain()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus { VisitedAreasEpoch = Epoch };

        Assert.True(ShouldQueueAnyStage(activity));
    }

    [Fact]
    public void ShouldQueueAnyStage_ReturnsFalse_WhenAllStagesHaveRun()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus
        {
            SummitedPeaksEpoch = Epoch,
            VisitedPathsEpoch = Epoch,
            VisitedAreasEpoch = Epoch,
        };

        Assert.False(ShouldQueueAnyStage(activity));
    }

    [Fact]
    public void ShouldQueueAnyStage_ReturnsTrue_WhenCurrentEpochHasBeenBumped()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus
        {
            SummitedPeaksEpoch = Epoch,
            VisitedPathsEpoch = Epoch,
            VisitedAreasEpoch = Epoch,
        };

        Assert.True(ShouldQueueAnyStage(activity, Epoch + 1));
    }

    [Fact]
    public void ShouldQueueSummits_ReturnsFalse_WhenSummitsAlreadyProcessed()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus { SummitedPeaksEpoch = Epoch };

        Assert.False(QueueActivityJobs.ShouldQueueSummits(activity, Epoch));
        Assert.True(QueueActivityJobs.ShouldQueueVisitedPaths(activity, Epoch));
        Assert.True(QueueActivityJobs.ShouldQueueVisitedAreas(activity, Epoch));
    }

    [Fact]
    public void ShouldQueueVisitedPaths_ReturnsFalse_WhenVisitedPathsAlreadyProcessed()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus { VisitedPathsEpoch = Epoch };

        Assert.True(QueueActivityJobs.ShouldQueueSummits(activity, Epoch));
        Assert.False(QueueActivityJobs.ShouldQueueVisitedPaths(activity, Epoch));
        Assert.True(QueueActivityJobs.ShouldQueueVisitedAreas(activity, Epoch));
    }

    [Fact]
    public void ShouldQueueVisitedAreas_ReturnsFalse_WhenVisitedAreasAlreadyProcessed()
    {
        var activity = CreateActivity();
        activity.ProcessingStatus = new ActivityProcessingStatus { VisitedAreasEpoch = Epoch };

        Assert.True(QueueActivityJobs.ShouldQueueSummits(activity, Epoch));
        Assert.True(QueueActivityJobs.ShouldQueueVisitedPaths(activity, Epoch));
        Assert.False(QueueActivityJobs.ShouldQueueVisitedAreas(activity, Epoch));
    }

    private static Activity CreateActivity() => new()
    {
        Id = "activity-1",
        UserId = "user-1",
        Name = "Test Activity",
        SportType = SportTypes.RUN,
        StartDate = DateTime.UtcNow,
        StartDateLocal = DateTime.UtcNow,
    };

    private static bool ShouldQueueAnyStage(Activity activity, long epoch = Epoch)
        => QueueActivityJobs.ShouldQueueSummits(activity, epoch)
            || QueueActivityJobs.ShouldQueueVisitedPaths(activity, epoch)
            || QueueActivityJobs.ShouldQueueVisitedAreas(activity, epoch);
}
