using BenchmarkDotNet.Attributes;
using Stigar.Core.Geo;
using Stigar.Core.Geo.SummitsCalculator;
using Stigar.Core.Models;

namespace Stigar.Core.Benchmarks.Summits;

/// <summary>
/// One benchmark per CPU stage of the summits worker, on the same synthetic long activity.
/// Re-run after each optimization and compare against BASELINE.md to prove the speedup.
/// Ingestion (Overpass) and GetByTiles (PostGIS) need live infrastructure and are not covered
/// here; see Tools/SummitsFetchBenchmark for those integration measurements.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(2)]
public class SummitsPipelineBenchmarks
{
    // Deliberately modest: the production failure needs tens of thousands of points to appear,
    // but these benchmarks only compare relative cost before/after a change. The isolated
    // DecodePolylineScalingBenchmark covers how cost grows with route length.
    private const int RoutePointCount = 2000;
    private const int PeakCount = 1000;

    private readonly ISummitsCalculator _calculator = new SummitsCalculatorWithBoundingBoxFilter();
    private string _polyline = string.Empty;
    private List<Coordinate> _route = [];
    private List<(string Id, Coordinate Coord)> _peaks = [];
    private HashSet<(int x, int y)> _routeTiles = [];

    [GlobalSetup]
    public void Setup()
    {
        _route = LongActivityFixture.BuildRoute(RoutePointCount);
        _polyline = LongActivityFixture.Encode(_route);
        _peaks = LongActivityFixture.BuildPeaks(PeakCount, _route);
        _routeTiles = _route
            .Select(point => SlippyTileCalculator.WGS84ToTileIndex(point, 11))
            .Distinct()
            .ToHashSet();
    }

    // Step 1: DecodePolyline. Allocation is the metric that matters here.
    [Benchmark(Description = "1 decode")]
    public int Decode() => GeoSpatialFunctions.DecodePolyline(_polyline).Count();

    // Step 2: route preparation done in FetchNearbyPeaks before ingestion/lookup.
    [Benchmark(Description = "2 route preparation")]
    public int RoutePreparation()
    {
        var points = GeoSpatialFunctions.DecodePolyline(_polyline)
            .DistinctBy(point => (point.Lat, point.Lng))
            .ToList();
        return points
            .Select(point => SlippyTileCalculator.WGS84ToTileIndex(point, 11))
            .Distinct()
            .Count();
    }

    // Step 3a: current candidate strategy - every peak in every route tile (GetByTiles).
    [Benchmark(Description = "3a tile candidates")]
    public int TileCandidateSelection()
        => _peaks.Count(peak => _routeTiles.Contains(SlippyTileCalculator.WGS84ToTileIndex(peak.Coord, 11)));

    // Step 3b: proposed candidate strategy - spatial prefilter near the route.
    [Benchmark(Description = "3b spatial candidates")]
    public int SpatialCandidateSelection()
    {
        var grid = new RoutePointGrid(_route);
        return _peaks.Count(peak => grid.IsNear(peak.Coord, 50));
    }

    // Step 4: FindPointsNearRoute with the production calculator (it decodes internally).
    [Benchmark(Description = "4 summit calculation")]
    public int SummitCalculation()
        => _calculator.FindPointsNearRoute(_peaks.Select(peak => (peak.Id, peak.Coord)), _polyline).Count();

    // All CPU stages together, including the two full-polyline decodes the worker does today.
    [Benchmark(Description = "5 full CPU path")]
    public int FullCpuPath()
    {
        var route = GeoSpatialFunctions.DecodePolyline(_polyline)
            .DistinctBy(point => (point.Lat, point.Lng))
            .ToList();
        var tiles = route
            .Select(point => SlippyTileCalculator.WGS84ToTileIndex(point, 11))
            .Distinct()
            .ToHashSet();
        var candidates = _peaks
            .Where(peak => tiles.Contains(SlippyTileCalculator.WGS84ToTileIndex(peak.Coord, 11)))
            .ToList();
        return _calculator
            .FindPointsNearRoute(candidates.Select(peak => (peak.Id, peak.Coord)), _polyline)
            .Count();
    }
}
