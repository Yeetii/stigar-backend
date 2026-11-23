using System.Diagnostics;
using System.Globalization;
using BAMCIS.GeoJSON;
using Microsoft.Extensions.Logging;
using Stigar.Core.Constants;
using Stigar.Core.Geo;
using Stigar.Core.Models;

namespace Stigar.Core.Services.Postgres.Spatial.Ingestion;

/// <summary>
/// Queries Overpass for storage tiles that cover a route and writes the returned features into
/// the PostGIS domain tables. A tile is queried at most once per kind: successful queries are
/// recorded in <see cref="IOsmTileQueryRepository"/> and skipped thereafter, while failed
/// queries are deliberately left unmarked so the next run retries them.
/// </summary>
public sealed class OsmFeatureIngestionService(
    OverpassClient overpassClient,
    IOsmTileQueryRepository tileQueries,
    IPeakRepository peaks,
    IProtectedAreaRepository protectedAreas,
    IAdminBoundaryRepository adminBoundaries,
    ILogger<OsmFeatureIngestionService> logger) : IOsmFeatureIngestionService
{
    // Storage zooms used for the OSM feature domain tables.
    private const int PeakZoom = 11;
    private const int ProtectedAreaZoom = 8;
    private const int AdminBoundaryZoom = 6;

    public Task<OsmIngestionResult> EnsurePeaksAsync(
        IEnumerable<Coordinate> routePoints,
        CancellationToken cancellationToken = default)
        => EnsureAsync(
            FeatureKinds.Peak,
            PeakZoom,
            routePoints,
            (sw, ne, ct) => overpassClient.GetPeaks(sw, ne, ct),
            feature => new StoredFeature(feature, FeatureKinds.Peak, PeakZoom),
            (features, ct) => peaks.BulkUpsert(features, ct),
            cancellationToken);

    public Task<OsmIngestionResult> EnsureProtectedAreasAsync(
        IEnumerable<Coordinate> routePoints,
        CancellationToken cancellationToken = default)
        => EnsureAsync(
            FeatureKinds.ProtectedArea,
            ProtectedAreaZoom,
            routePoints,
            (sw, ne, ct) => overpassClient.GetProtectedAreas(sw, ne, ct),
            feature => new StoredFeature(feature, FeatureKinds.ProtectedArea, ProtectedAreaZoom),
            (features, ct) => protectedAreas.BulkUpsert(features, ct),
            cancellationToken);

    public Task<OsmIngestionResult> EnsureAdminBoundariesAsync(
        IEnumerable<Coordinate> routePoints,
        int adminLevel,
        CancellationToken cancellationToken = default)
        => EnsureAsync(
            FeatureKinds.AdminBoundary,
            AdminBoundaryZoom,
            routePoints,
            (sw, ne, ct) => overpassClient.GetAdminBoundaries(sw, ne, adminLevel, ct),
            feature =>
            {
                var stored = new StoredFeature(feature, FeatureKinds.AdminBoundary, AdminBoundaryZoom);
                // AdminBoundaryRepository reads the level from this property when building its
                // INSERT parameters, so it must be set before the upsert.
                stored.Properties["adminLevel"] = adminLevel.ToString(CultureInfo.InvariantCulture);
                return stored;
            },
            (features, ct) => adminBoundaries.BulkUpsert(features, ct),
            cancellationToken);

    private async Task<OsmIngestionResult> EnsureAsync(
        string kind,
        int zoom,
        IEnumerable<Coordinate> routePoints,
        Func<Coordinate, Coordinate, CancellationToken, Task<IEnumerable<Feature>>> fetch,
        Func<Feature, StoredFeature> toStored,
        Func<IReadOnlyList<StoredFeature>, CancellationToken, Task> upsert,
        CancellationToken cancellationToken)
    {
        var points = (routePoints ?? [])
            .Where(point => point is not null)
            .DistinctBy(point => (point.Lng, point.Lat))
            .ToList();

        // Deduplicate storage tiles so a single call never fetches the same tile twice.
        var tiles = points
            .Select(point => SlippyTileCalculator.WGS84ToTileIndex(point, zoom))
            .Distinct()
            .ToList();

        if (tiles.Count == 0)
            return OsmIngestionResult.Empty;

        var tilesQueried = 0;
        var featuresFetched = 0;
        var featuresUpserted = 0;
        var tilesFailed = 0;
        long overpassFetchMs = 0;
        long upsertMs = 0;
        long tileDbMs = 0;

        static async Task<T> TimeResult<T>(Func<Task<T>> operation, Action<long> addMs)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                return await operation();
            }
            finally
            {
                addMs(stopwatch.ElapsedMilliseconds);
            }
        }

        static async Task TimeVoid(Func<Task> operation, Action<long> addMs)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await operation();
            }
            finally
            {
                addMs(stopwatch.ElapsedMilliseconds);
            }
        }

        foreach (var (x, y) in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await TimeResult(
                        () => tileQueries.HasBeenQueried(kind, zoom, x, y, cancellationToken),
                        ms => tileDbMs += ms))
                    continue;

                var (southWest, northEast) = SlippyTileCalculator.TileIndexToWGS84(x, y, zoom);
                var rawFeatures = (await TimeResult(
                        () => fetch(southWest, northEast, cancellationToken),
                        ms => overpassFetchMs += ms))
                    .ToList();
                featuresFetched += rawFeatures.Count;

                var stored = rawFeatures.Select(toStored).ToList();
                if (stored.Count > 0)
                {
                    await TimeVoid(() => upsert(stored, cancellationToken), ms => upsertMs += ms);
                    featuresUpserted += stored.Count;
                }

                await TimeVoid(
                    () => tileQueries.MarkQueried(kind, zoom, x, y, cancellationToken),
                    ms => tileDbMs += ms);
                tilesQueried++;

                logger.LogDebug(
                    "OSM ingestion stored {Count} {Kind} feature(s) for tile {X},{Y}@{Zoom}",
                    stored.Count,
                    kind,
                    x,
                    y,
                    zoom);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Leave the tile unmarked so the next run retries it.
                tilesFailed++;
                logger.LogWarning(
                    ex,
                    "OSM ingestion failed for {Kind} tile {X},{Y}@{Zoom}; the tile will be retried on the next run",
                    kind,
                    x,
                    y,
                    zoom);
            }
        }

        return new OsmIngestionResult(tilesQueried, featuresFetched, featuresUpserted, tilesFailed)
        {
            OverpassFetchMs = overpassFetchMs,
            UpsertMs = upsertMs,
            TileDbMs = tileDbMs
        };
    }
}
