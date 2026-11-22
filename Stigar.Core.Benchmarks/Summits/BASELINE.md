# Summits pipeline baseline (pre-optimization)

Measured on Apple M4 Pro, macOS 27.2.1, .NET 10.0.2, BenchmarkDotNet 0.14.0,
`ShortRunJob` with `WarmupCount(1)` / `IterationCount(2)` and `MemoryDiagnoser`.
All numbers use the deterministic synthetic fixture in this folder, so they are directly
comparable after each optimization. Note: with only two iterations these are directional
numbers, not publication-grade statistics; large deltas are what matter.

## Step 1 - DecodePolyline (`DecodePolylineScalingBenchmark`)

| Route points | Mean | Allocated per decode |
| ---: | ---: | ---: |
| 500 | 206.7 us | 5.89 MB |
| 2,000 | 3.26 ms | 92.98 MB |
| 5,000 | 15.8 ms | 579.78 MB |

Allocation grows quadratically with route length. After the `ToCharArray()` fix this column
must grow roughly linearly (a 5k-point decode should allocate a few MB, not 580 MB).

## Steps 2-5 - Summits pipeline (`SummitsPipelineBenchmarks`)

Fixed dataset: 2,000 route points, 1,000 peaks.

| Step | Benchmark | Mean | Allocated per op |
| --- | --- | ---: | ---: |
| 1 | `1 decode` | 3.12 ms | 92.98 MB |
| 2 | `2 route preparation` | 3.35 ms | 93.20 MB |
| 3a | `3a tile candidates` | 25.9 us | 0 B |
| 3b | `3b spatial candidates` | 167.6 us | 0.37 MB |
| 4 | `4 summit calculation` | 889 ms | 27,914 MB |
| 5 | `5 full CPU path` | 880 ms | 28,012 MB |

### Peak-count scaling (`SummitCalculationPeakScalingBenchmark`)

Fixed 2,000-point route, scattered peaks only (no early `break`):

| Peaks | Mean | Allocated |
| ---: | ---: | ---: |
| 10 | 8.97 ms | 278.93 MB |
| 50 | 53.7 ms | 1,671 MB |
| 100 | 112 ms | 3,345 MB |
| 300 | 366 ms | 7,882 MB |

Allocation grows linearly with peak count, confirming that
`SummitsCalculatorWithBoundingBoxFilter` passes the *lazy* `DecodePolyline(...)` enumerable into
`BasicSummitsCalculator`, which walks the whole polyline once per peak. This is a real code defect,
but the important correction is that it is **not** by itself an hour-scale problem for ordinary
activities: with the 66-100 peaks typical of the existing benchmark fixtures, this is ~2-3 GB and
~75-112 ms on a 2,000-point route, so the job completes.

### Key observations

- **The failure needs route length AND peak count together.** Decode allocation grows roughly
  quadratically with route points (5.89 MB at 500, 93 MB at 2k, 580 MB at 5k, ~9.3 GB at 20k),
  and the lazy re-enumeration multiplies that by the number of candidate peaks. A long activity
  with thousands of peaks therefore reaches terabyte-scale allocation; an activity with either a
  short polyline or few peaks completes normally. This is consistent with the observation that
  activities of similar size have succeeded.
- **Steps 1 and 2 are the quadratic decode** in isolation and as the worker does them. Materializing
  the decoded polyline once before the per-peak scan is as important as fixing `DecodePolyline`.
- **Most likely hour-scale driver for a first run: sequential Overpass ingestion.**
  `OsmFeatureIngestionService.EnsureAsync` loops over distinct zoom-11 tiles one at a time and skips
  tiles already recorded in `osm_tile_queries`. Each new tile is a synchronous `GetPeaks` call with a
  400-second Overpass timeout plus mirror retries. A long route crossing many *new* tiles means
  hundreds of serial HTTP calls; the same route after those tiles are cached completes quickly.
  This explains why activity size alone does not predict the hang. It cannot be measured locally
  without live Overpass/PostGIS - use `Tools/SummitsFetchBenchmark` with a tile-cache reset.
- **Step 3a is the current "load every peak in every route tile" strategy.** It is cheap in this
  in-memory proxy because there is no database; in production its cost is the thousands of
  `StoredFeature` -> `Feature` objects materialized by `GetByTiles`, which this harness cannot
  measure without PostGIS. Step 3b is a rough in-memory proxy for a `ST_DWithin` prefilter and
  is slower here, but the production win is avoiding the DB round-trip and object materialization.
- **Ingestion (Overpass) and `GetByTiles` (PostGIS)** need live services; see
  `Tools/SummitsFetchBenchmark` for those integration measurements.

## After the fixes

Changes applied: `DecodePolyline` now takes `ToCharArray()` once; `BasicSummitsCalculator` and
`SummitsCalculatorWithBoundingBoxFilter` materialize the decoded polyline before the per-peak scan.
Same machine/job settings as above. Re-run the suite to regenerate the raw BenchmarkDotNet reports.

### Decode (`DecodePolylineScalingBenchmark`)

| Route points | Before | After | Speedup | Before alloc | After alloc |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 206.7 us | 2.95 us | 70x | 5.89 MB | 18.68 KB |
| 2,000 | 3.26 ms | 11.78 us | 277x | 92.98 MB | 74.4 KB |
| 5,000 | 15.8 ms | 29.2 us | 542x | 579.78 MB | 185.84 KB |

Allocation is now linear in route length, not quadratic.

### Pipeline (`SummitsPipelineBenchmarks`, 2k points / 1k peaks)

| Step | Before | After | Speedup |
| --- | ---: | ---: | ---: |
| `1 decode` | 3.12 ms / 93 MB | 11.67 us / 76 KB | 268x |
| `2 route preparation` | 3.35 ms / 93 MB | 111 us / 325 KB | 30x |
| `3a tile candidates` | 25.9 us | 27.1 us | unchanged (no decode in this stage) |
| `3b spatial candidates` | 167.6 us | 162.6 us | unchanged |
| `4 summit calculation` | 889 ms / 27,914 MB | 9.04 ms / 124 KB | 98x / ~225,000x less memory |
| `5 full CPU path` | 880 ms / 28,012 MB | 9.59 ms / 463 KB | 92x |

### Peak scaling (`SummitCalculationPeakScalingBenchmark`, 2k-point route)

| Peaks | Before | After | Speedup |
| ---: | ---: | ---: | ---: |
| 10 | 8.97 ms / 279 MB | 83.7 us / 107 KB | 107x |
| 50 | 53.7 ms / 1,671 MB | 553 us / 108 KB | 97x |
| 100 | 112 ms / 3,345 MB | 1.11 ms / 109 KB | 101x |
| 300 | 366 ms / 7,882 MB | 2.68 ms / 111 KB | 137x |

Allocation is now flat with respect to peak count - the lazy re-enumeration is gone.

### Notes

- Legacy `SummitsBenchmarks` on this machine: `CompareAllPoints` 405.8 us, `BoundingBoxFilter`
  326.5 us (0.80 ratio vs baseline). `ActivityLengthFilter` shows a 1.36 ratio against
  `CompareAllPoints` because it does extra per-peak work that the quadratic decode previously
  masked; its absolute time (552 us) is still far below the old ~19 ms report.
- These are CPU-only stages. The sequential Overpass ingestion that likely caused the hours-long
  job is unchanged and still needs a live integration measurement.
