using BenchmarkDotNet.Attributes;
using Stigar.Core.Geo.SummitsCalculator;
using Stigar.Core.Models;

namespace Stigar.Core.Benchmarks.Summits;

/// <summary>
/// Isolates the number of peaks fed to FindPointsNearRoute on a fixed 2,000-point route.
/// If the decoded polyline is re-enumerated per peak, allocation must grow linearly with
/// peak count. Typical activities have tens of peaks; the pathological one has thousands.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(2)]
public class SummitCalculationPeakScalingBenchmark
{
    private const int RoutePointCount = 2000;
    private const int GeneratedPeakCount = 2000;
    private const int OnRoutePeakCount = 200;

    private readonly ISummitsCalculator _calculator = new SummitsCalculatorWithBoundingBoxFilter();
    private string _polyline = string.Empty;
    private List<(string Id, Coordinate Coord)> _scatteredPeaks = [];

    [Params(10, 50, 100, 300)]
    public int PeakCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var route = LongActivityFixture.BuildRoute(RoutePointCount);
        _polyline = LongActivityFixture.Encode(route);
        _scatteredPeaks = LongActivityFixture
            .BuildPeaks(GeneratedPeakCount, route)
            .Skip(OnRoutePeakCount)
            .ToList();
    }

    [Benchmark]
    public int FindPointsNearRoute()
        => _calculator
            .FindPointsNearRoute(
                _scatteredPeaks.Take(PeakCount).Select(peak => (peak.Id, peak.Coord)),
                _polyline)
            .Count();
}
