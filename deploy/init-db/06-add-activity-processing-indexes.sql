-- The activity-processing poll filters activities on JSONB epoch values cast to bigint and
-- orders the matches by id. Without expression indexes it sequentially scans the whole table
-- on every poll, which eventually outlasts Npgsql's command timeout while reading the result.
-- The expressions must stay in sync with ActivityIdsNeedingProcessingSql in BackendAdminQueries.
CREATE INDEX IF NOT EXISTS idx_activities_summits_epoch
    ON activities (COALESCE((processing_status ->> 'summitedPeaksEpoch')::bigint, 0));

CREATE INDEX IF NOT EXISTS idx_activities_paths_epoch
    ON activities (COALESCE((processing_status ->> 'visitedPathsEpoch')::bigint, 0));

CREATE INDEX IF NOT EXISTS idx_activities_areas_epoch
    ON activities (COALESCE((processing_status ->> 'visitedAreasEpoch')::bigint, 0));
