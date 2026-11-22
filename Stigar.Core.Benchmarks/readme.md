# Summits benchmarks

Benchmarks must be built with release configuration.

```
dotnet run -c Release
```

To run one class or method, pass BenchmarkDotNet filters:

```
dotnet run -c Release -- --filter '*SummitsPipeline*'
dotnet run -c Release -- --filter '*DecodePolylineScaling*'
dotnet run -c Release -- --filter '*3a*'
```

## What each benchmark covers

The CPU path of `SummitsWorker` is split into one benchmark per stage, using the same
deterministic fixture (`Summits/`) so before/after numbers are comparable. The dataset is
deliberately small - 2,000 route points / 1,000 peaks for the pipeline class and 500-5,000 for
the decode curve - so a full run takes about a minute. Raise the constants in
`SummitsPipelineBenchmarks` or the params in `DecodePolylineScalingBenchmark` only when you
specifically want a stress run; the quadratic decode makes large routes take hours.

| Step | Benchmark | Optimization it validates |
| --- | --- | --- |
| 1 | `1 decode`, `DecodePolylineScaling` | Hoisting `ToCharArray()` out of the decode loop; allocations must scale linearly, not quadratically. |
| 2 | `2 route preparation`, `5 full CPU path` | Decoding once and passing the coordinate list through instead of re-decoding the raw polyline. |
| 3 | `3a tile candidates` vs `3b spatial candidates` | Replacing tile-based bulk candidate loading with a route-proximity (ST_DWithin) prefilter. |
| 4 | `4 summit calculation`, `SummitCalculationPeakScalingBenchmark`, legacy `SummitsBenchmarks` | Materializing the decoded polyline once and spatially bucketing route points so the per-peak scan is no longer O(peaks x route points). |
| 5 | not covered | Overpass ingestion and `GetByTiles` need live services; use `Tools/SummitsFetchBenchmark` for those. |

`BASELINE.md` records the pre-fix numbers. Re-run after each change and compare mean time and
allocated bytes. `MemoryDiagnoser` is enabled on every new class because the original failure
was an allocation blow-up, not just slow CPU time.
