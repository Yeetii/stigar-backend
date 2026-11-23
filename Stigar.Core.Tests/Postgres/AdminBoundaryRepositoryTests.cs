using System.Globalization;
using BAMCIS.GeoJSON;
using Npgsql;
using NpgsqlTypes;
using Stigar.Core.Models;
using Stigar.Core.Services;
using Stigar.Core.Services.Postgres.Spatial;
using Xunit;

namespace Stigar.Core.Tests.Postgres;

[Collection(PostgisCollection.Name)]
public sealed class AdminBoundaryRepositoryTests(PostgisFixture fixture)
{
    private const string Polygon =
        """{"type":"Polygon","coordinates":[[[7.9,46.8],[8.1,46.8],[8.1,47.0],[7.9,47.0],[7.9,46.8]]]}""";

    [SkippableFact]
    public async Task GetById_ReadsTypedColumnsAndGeometry()
    {
        await fixture.ResetAsync();
        await InsertBoundaryAsync("admin:1", 1234567L, "relation", 4, "Canton", "Q99", 3);

        var boundary = await new AdminBoundaryRepository(fixture.DataSource).GetById("admin:1");

        Assert.NotNull(boundary);
        Assert.IsType<Polygon>(boundary.Geometry);
        Assert.Equal("1234567", boundary.Properties["osmId"]!.ToString());
        Assert.Equal("relation", boundary.Properties["osmType"]!.ToString());
        Assert.Equal("4", boundary.Properties["adminLevel"]!.ToString());
        Assert.Equal("Canton", boundary.Properties["name"]!.ToString());
        Assert.Equal("Q99", boundary.Properties["wikidata"]!.ToString());
        Assert.Equal("3", boundary.Properties["metricsVersion"]!.ToString());
    }

    [SkippableFact]
    public async Task GetByLevel_ReturnsOnlyMatchingBoundaries()
    {
        await fixture.ResetAsync();
        await InsertBoundaryAsync("admin:4", 1L, "relation", 4, "Level 4", null, 0);
        await InsertBoundaryAsync("admin:6", 2L, "relation", 6, "Level 6", null, 0);

        var boundaries = await new AdminBoundaryRepository(fixture.DataSource).GetByLevel(6);

        var boundary = Assert.Single(boundaries);
        Assert.Equal("admin:6", boundary.Id);
        Assert.IsType<Polygon>(boundary.Geometry);
    }

    [SkippableFact]
    public async Task UpdateMetrics_StoresEpochAndClearsQueuedMarker()
    {
        await fixture.ResetAsync();
        await InsertBoundaryAsync("admin:1", 1L, "relation", 4, "Canton", null, 0);
        await fixture.ExecuteAsync("UPDATE admin_boundaries SET metrics_queued_at = NOW() WHERE id = 'admin:1'");

        var repository = new AdminBoundaryRepository(fixture.DataSource);
        await repository.UpdateMetrics("admin:1", new Dictionary<string, object?> { ["population"] = 1000 }, 7);

        var boundary = await repository.GetById("admin:1");
        Assert.NotNull(boundary);
        Assert.Equal("1000", boundary.Properties["population"]!.ToString());
        Assert.IsType<Polygon>(boundary.Geometry);
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT metrics_generation, metrics_queued_at FROM admin_boundaries WHERE id = 'admin:1'");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt64(0));
        Assert.True(await reader.IsDBNullAsync(1));
    }

    [SkippableFact]
    public async Task FindSummariesContainingAnyPoint_ReadsDoubleCentroidColumns()
    {
        await fixture.ResetAsync();
        await InsertBoundaryAsync("admin:1", 1L, "relation", 4, "Canton", null, 0);

        var summaries = await new AdminBoundaryRepository(fixture.DataSource)
            .FindSummariesContainingAnyPoint([new Coordinate(8.0, 46.9)], 4);

        var summary = Assert.Single(summaries);
        Assert.Equal("admin:1", summary.Id);
        Assert.NotNull(summary.Centroid);
        Assert.Equal(8.0, summary.Centroid!.Lng, 1);
        Assert.Equal(46.9, summary.Centroid.Lat, 1);
    }

    [SkippableFact]
    public async Task FindSummariesContainingAnyPoint_UsesSpatialIndexForManyPoints()
    {
        await fixture.ResetAsync();

        var lngs = Enumerable.Range(0, 120).Select(i => 7.9 + (i % 10) * 0.01).ToArray();
        var lats = Enumerable.Range(0, 120).Select(i => 46.8 + (i % 12) * 0.01).ToArray();

        // Realistic level-4 volume: enough rows that scanning all of them for every point
        // is far more expensive than a GiST lookup per point.
        for (var i = 0; i < 300; i++)
        {
            var lng = 7.0 + (i % 30) * 0.05;
            var lat = 46.0 + (i / 30) * 0.05;
            var x = lng.ToString(CultureInfo.InvariantCulture);
            var y = lat.ToString(CultureInfo.InvariantCulture);
            var x2 = (lng + 0.01).ToString(CultureInfo.InvariantCulture);
            var y2 = (lat + 0.01).ToString(CultureInfo.InvariantCulture);
            var geom = $$"""{"type":"Polygon","coordinates":[[[{{x}},{{y}}],[{{x2}},{{y}}],[{{x2}},{{y2}}],[{{x}},{{y2}}],[{{x}},{{y}}]]]}""";
            await InsertBoundaryAsync($"admin:{i}", i + 1, "relation", 4, "Canton", null, 0, geom);
        }
        await fixture.ExecuteAsync("ANALYZE admin_boundaries");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var explain = new NpgsqlCommand(
            "EXPLAIN " + AdminBoundaryRepository.ContainingAnySummarySql, connection, transaction);
        explain.Parameters.Add(new NpgsqlParameter("lngs", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = lngs });
        explain.Parameters.Add(new NpgsqlParameter("lats", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = lats });
        explain.Parameters.AddWithValue("admin_level", 4);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        await transaction.CommitAsync();

        Assert.True(
            plan.Any(line => line.Contains("idx_admin_boundaries_geom", StringComparison.Ordinal)),
            string.Join("\n", plan));
        Assert.False(
            plan.Any(line => line.Contains("st_centroid", StringComparison.Ordinal)),
            "ST_Centroid must run once per boundary after deduplication, not once per point/boundary match: " + string.Join("\n", plan));
    }

    [SkippableFact]
    public async Task BulkUpsert_StoresPropertiesAsJsonb()
    {
        await fixture.ResetAsync();
        var boundary = new StoredFeature
        {
            Id = "admin:bulk",
            FeatureId = "bulk",
            Kind = FeatureKinds.AdminBoundary,
            X = 0,
            Y = 0,
            Zoom = 6,
            Geometry = Geometry.FromJson(Polygon),
            Properties = new Dictionary<string, dynamic>
            {
                ["adminLevel"] = "4",
                ["name"] = "Bulk",
                ["population"] = 12345
            }
        };

        var repository = new AdminBoundaryRepository(fixture.DataSource);
        await repository.BulkUpsert([boundary]);

        var stored = await repository.GetById("admin:bulk");
        Assert.NotNull(stored);
        Assert.Equal("12345", stored!.Properties["population"]!.ToString());
    }

    private Task InsertBoundaryAsync(
        string id,
        long osmId,
        string osmType,
        int adminLevel,
        string name,
        string? wikidata,
        int metricsVersion,
        string? geom = null)
        => fixture.ExecuteAsync(
            """
            INSERT INTO admin_boundaries
                (id, osm_id, osm_type, admin_level, name, wikidata, geom, properties, metrics_version)
            VALUES
                (@id, @osm_id, @osm_type, @admin_level, @name, @wikidata,
                 ST_SetSRID(ST_GeomFromGeoJSON(@geom), 4326), '{}'::jsonb, @metrics_version)
            """,
            ("id", id), ("osm_id", osmId), ("osm_type", osmType),
            ("admin_level", adminLevel), ("name", name), ("wikidata", wikidata),
            ("geom", geom ?? Polygon), ("metrics_version", metricsVersion));
}
