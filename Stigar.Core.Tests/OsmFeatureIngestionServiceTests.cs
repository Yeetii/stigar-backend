using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stigar.Core.Models;
using Stigar.Core.Services;
using Stigar.Core.Services.Postgres.Spatial;
using Stigar.Core.Services.Postgres.Spatial.Ingestion;

namespace Stigar.Core.Tests;

public class OsmFeatureIngestionServiceTests
{
    [Fact]
    public async Task EnsurePeaksAsync_ExposesFetchUpsertAndTileDbTimings()
    {
        using var httpClient = new HttpClient(new DelayedPeakHandler());
        var overpass = new OverpassClient(httpClient, NullLogger<OverpassClient>.Instance);

        var tileQueries = new Mock<IOsmTileQueryRepository>();
        tileQueries
            .Setup(r => r.HasBeenQueried(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Delay(15); return false; });
        tileQueries
            .Setup(r => r.MarkQueried(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () => await Task.Delay(15));

        var peaks = new Mock<IPeakRepository>();
        peaks
            .Setup(r => r.BulkUpsert(It.IsAny<IEnumerable<StoredFeature>>(), It.IsAny<CancellationToken>()))
            .Returns(async () => await Task.Delay(15));

        var service = new OsmFeatureIngestionService(
            overpass,
            tileQueries.Object,
            peaks.Object,
            Mock.Of<IProtectedAreaRepository>(),
            Mock.Of<IAdminBoundaryRepository>(),
            NullLogger<OsmFeatureIngestionService>.Instance);

        var result = await service.EnsurePeaksAsync([new Coordinate(-120.2, 38.5)]);

        Assert.Equal(1, result.TilesQueried);
        Assert.Equal(1, result.FeaturesFetched);
        Assert.Equal(1, result.FeaturesUpserted);
        Assert.True(result.OverpassFetchMs > 0, "Overpass fetch time should be captured.");
        Assert.True(result.UpsertMs > 0, "Upsert time should be captured.");
        Assert.True(result.TileDbMs > 0, "Tile-query time should be captured.");
    }

    private sealed class DelayedPeakHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken);
            const string json = """
                {"elements":[{"id":1,"lat":38.5,"lon":-120.2,"tags":{"name":"Test Peak","ele":"1000"}}]}
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
