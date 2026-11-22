using BenchmarkDotNet.Attributes;
using Stigar.Core.Geo;

namespace Stigar.Core.Benchmarks.Summits;

/// <summary>
/// Isolated decode scaling curve. This is the regression that proves the O(n^2) allocation
/// bug in GeoSpatialFunctions.DecodePolyline is gone: allocations must grow linearly.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(2)]
public class DecodePolylineScalingBenchmark
{
    private string _polyline = string.Empty;

    // Small enough to finish in seconds, large enough to expose quadratic allocation growth.
    [Params(500, 2000, 5000)]
    public int RoutePoints { get; set; }

    [GlobalSetup]
    public void Setup()
        => _polyline = LongActivityFixture.Encode(LongActivityFixture.BuildRoute(RoutePoints));

    [Benchmark]
    public int Decode() => GeoSpatialFunctions.DecodePolyline(_polyline).Count();
}
