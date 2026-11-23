# Highway features missing from low-zoom tiles (backend handoff)

Status: investigation handoff — backend worker
Date: 2026-11-22
Frontend worktree: `fix/highway-tile-revalidation` (commit `d4844708`), but the client fix does **not** change this symptom.

## Symptom (verbatim from the user)

- "i have some highways that only appear on z12"
- "the highway issue is still there even after wiping the indexed db so the issue must be in the backend"

A specific set of highways is visible when the trails overlay renders z12 tiles, but absent from the lower-zoom tiles (z7–z11) that cover the same area. This persists after clearing the website's IndexedDB `highwaytiles` table and reloading.

## What has been ruled out on the client

- IndexedDB wipe + hard reload produced no change, so the PBF the server returns for the low-zoom tile genuinely lacks the features.
- The service worker passes highway tile requests straight through and never caches them (`website/src/service-worker/index.ts:206-211`).
- The protocol handler's serve-from-IDB fast path and stale-while-revalidate path were both audited (`website/src/lib/components/map/highways-overlay/highways-overlay.ts:267-328`).
- A client fix was shipped (`d4844708`): incomplete cached tiles now always refresh IDB and never call `setTiles`. Full website suite 303/303 passing. It does not affect this symptom.

Do not spend time on client caching for this issue.

## Backend pipeline (verified paths)

1. **Canonical shards are z12.** `Stigar.Core/Services/Shards/FileSystemShardRepository.cs:31-56` auto-builds a missing shard on demand via Overpass and calls `NotifyShardChangedAsync`; `:82-89` does the same for rebuilds, `:119-131` invokes the callback.
2. **Shard writes update ancestor indexes.** `Stigar.Core/Services/Shards/ShardStoreServiceCollectionExtensions.cs:22-48` wires `onShardChanged` to `HighwayZoomIndexService.SyncIndexesForShardAsync`.
3. **Low zoom uses a persisted index.** `HighwayZoomIndexService.cs:23-49` (`GetShardKeysAsync`): `z == 12` → one shard; `z > 12` → parent shard; `z < 12` → read `index/{z}/{x}/{y}.json` or build it. `BuildAndPersistIndexAsync:122-161` enumerates candidate z12 shards, sets `IsComplete = false` when any is missing, and retains a shard only when some owned feature passes `HighwayZoomRules.ShouldKeepFeature(feature, z, profile)`.
4. **Tile build.** `BlobTileService.cs:25-74` (`BuildTileAsync`): load shard features → `FilterByZoom` → `ClipToTileBounds` → `FilterByClippedLength` → `SimplifyByZoom` → encode. Note `sourceWrittenAt` is hardcoded to `null` on this normal path (`:52-54,73`).
5. **Endpoint.** `Stigar.Api/Endpoints/Tiles/GetHighwayTilePbf.cs:41-61`: normal GET calls `BuildTileAsync`; only admin `forceRefresh` calls `RefreshTileAsync`. Incomplete tiles get `Cache-Control: no-store` and `X-Tile-Incomplete: true`.

## Strongest hypothesis — intentional zoom gating, not a defect

`Stigar.Core/Services/Shards/HighwayTileZoomProfile.cs:23-49` (`Default`) sets:

- `LocalPathwaysMinZoom = 12` — `footway`, `pedestrian`, `steps`, `corridor`, `sidewalk`, `crossing`, `link`, `rest_area`, `escape` are invisible below z12.
- `CyclewayMinZoom = 11`, `TrailBackboneMinZoom = 7` (`path`, `track`), `InformalMinZoom = 13`, `SnowmobileMinZoom = 7`.
- `LowVisibilityTrailMinZoom = 9`, `MediumDifficultySacMinZoom = 8`, `HighDifficultySacMinZoom = 9` raise the threshold further for poor visibility / harder SAC scale.
- `MinClippedLengthMeters` by step: 200 (z<8), 125 (z8), 80 (z9), 70 (z10), 30 (z11), 20 (z12), 3 (z13), 0 (z15+). Short lines are dropped even when their class passes.

`HighwayZoomRules.IsVisibleAtZoom` (`:213-234`) and `GetTrailMinimumZoom` (`:236-255`) implement the above. If the affected highways are local pathways, short, poor-visibility, or high-SAC-scale trails, their absence at low zoom is **by design** and no code change is warranted — confirm the tags of one affected OSM way first.

## Secondary suspects if gating is ruled out

1. `HighwayZoomIndexService.SyncIndexesForShardAsync:51-94` never recomputes `IsComplete`. An index built while shards were missing stays `IsComplete = false` forever even after all shards materialize, so low-zoom tiles remain flagged incomplete. (Affects freshness signalling, not necessarily feature content.)
2. Same method, `:60-62`: if the ancestor index blob does not exist, it `continue`s — it never creates an index. An index built before a shard existed does exist, so this is lower priority, but check for indexes that were never built.
3. `shouldInclude` in `SyncIndexesForShardAsync:64` only checks `shard.Owned` against `ShouldKeepFeature`. A shard can be referenced by the index yet contribute no rendered features at that zoom because `FilterByClippedLength` or simplification drops them in `BuildTileAsync`.
4. `ShardFeatureClient.cs:17-19` keeps a 500-entry LRU of deserialized shards. `RebuildShardWithContext:308-330` evicts before rebuilding, but `FileSystemShardRepository.cs:48-58` deliberately retains cached empty shards when Overpass still returns empty.
5. `BuildTileAsync` never populates `sourceWrittenAt`, so normal responses carry no version token (`GetHighwayTilePbf.cs:56-57`). Relevant only if a freshness protocol is needed.

## How to verify

1. Pick one affected highway's `osmId` and the z12 tile where it renders; compute its low-zoom ancestor(s) at the zoom where it disappears.
2. Request both tiles from the API and decode the PBFs; confirm the feature is present at z12 and absent at z10/z11.
3. Dump `index/{z}/{x}/{y}.json` for the ancestor: check `IsComplete`, shard count, and whether the relevant z12 shard is referenced.
4. Inspect the feature's tags (`highway`, `footway`, `snowmobile`, `informal`, `trail_visibility`, `sac_scale`) and evaluate `GetTrailMinimumZoom` plus the step's `MinClippedLengthMeters` at the failing zoom.
5. Check server logs for `Built highway tile z{Z}/{X}/{Y}` — `isComplete`, `shardCount`, `featureCount` — to see whether the shard was selected and how many features survived the pipeline.

## Acceptance

- State clearly whether the missing features are expected from the zoom profile/length rules or are a genuine defect.
- If a defect: fix in `Stigar.Core` with regression tests in `Stigar.Core.Tests` (relevant suites: `HighwayZoomRulesTests.cs`, `ShardStackTests.cs`, `ShardIndexStoreTests.cs`).
- If intended: report which profile values produce the behaviour and whether the product wants those thresholds changed.
