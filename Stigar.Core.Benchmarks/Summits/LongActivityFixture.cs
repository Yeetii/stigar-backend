using System.Text;
using Stigar.Core.Geo;
using Stigar.Core.Models;

namespace Stigar.Core.Benchmarks.Summits;

/// <summary>
/// Deterministic synthetic stand-in for a long activity. The benchmarks never depend on live
/// Strava, Overpass or PostGIS data, so results are reproducible across machines and runs.
/// </summary>
public static class LongActivityFixture
{
    private const double StartLat = 60.0;
    private const double StartLng = 10.0;
    private const double LngStep = 0.001;
    private const double LatStep = 0.01;
    private const int PointsPerRow = 200;

    /// <summary>Serpentine route: many GPS points in a bounded area, like a real long activity.</summary>
    public static List<Coordinate> BuildRoute(int pointCount)
    {
        var route = new List<Coordinate>(pointCount);
        var lat = StartLat;
        var lng = StartLng;
        for (var i = 0; i < pointCount; i++)
        {
            route.Add(new Coordinate(lng, lat));
            lng += LngStep;
            if ((i + 1) % PointsPerRow == 0)
            {
                lng = StartLng;
                lat += LatStep;
            }
        }
        return route;
    }

    /// <summary>Standard Google encoded polyline at 1e5 precision.</summary>
    public static string Encode(IEnumerable<Coordinate> coordinates)
    {
        var sb = new StringBuilder();
        int lat = 0;
        int lng = 0;
        foreach (var coordinate in coordinates)
        {
            var latInt = (int)Math.Round(coordinate.Lat * 1e5);
            var lngInt = (int)Math.Round(coordinate.Lng * 1e5);
            AppendDelta(sb, latInt - lat);
            AppendDelta(sb, lngInt - lng);
            lat = latInt;
            lng = lngInt;
        }
        return sb.ToString();
    }

    private static void AppendDelta(StringBuilder sb, int delta)
    {
        uint value = delta < 0 ? (uint)~(delta << 1) : (uint)(delta << 1);
        while (value >= 0x20)
        {
            sb.Append((char)((0x20 | (value & 0x1f)) + 63));
            value >>= 5;
        }
        sb.Append((char)(value + 63));
    }

    /// <summary>
    /// Peaks spread over the route area; a tenth sit directly on route points so summit
    /// detection has true positives.
    /// </summary>
    public static List<(string Id, Coordinate Coord)> BuildPeaks(
        int count,
        IReadOnlyList<Coordinate> route,
        int seed = 42)
    {
        var rng = new Random(seed);
        var peaks = new List<(string, Coordinate)>(count);
        var minLat = route.Min(point => point.Lat);
        var maxLat = route.Max(point => point.Lat);
        var minLng = route.Min(point => point.Lng);
        var maxLng = route.Max(point => point.Lng);

        var onRouteCount = Math.Min(count / 10, route.Count);
        for (var i = 0; i < onRouteCount; i++)
            peaks.Add(($"peak-on-route-{i}", route[rng.Next(route.Count)]));

        for (var i = onRouteCount; i < count; i++)
        {
            var lat = minLat - 0.05 + rng.NextDouble() * (maxLat - minLat + 0.1);
            var lng = minLng - 0.05 + rng.NextDouble() * (maxLng - minLng + 0.1);
            peaks.Add(($"peak-{i}", new Coordinate(lng, lat)));
        }
        return peaks;
    }
}

/// <summary>
/// Reference implementation of the spatial candidate prefilter proposed for step 3. It is
/// benchmark-only: production should do this in PostGIS with ST_DWithin and the GIST index.
/// </summary>
public sealed class RoutePointGrid
{
    private const double CellSizeDegrees = 0.001;
    private readonly Dictionary<(int x, int y), List<Coordinate>> _cells = [];

    public RoutePointGrid(IEnumerable<Coordinate> points)
    {
        foreach (var point in points)
        {
            var key = Cell(point);
            if (!_cells.TryGetValue(key, out var bucket))
                _cells[key] = bucket = [];
            bucket.Add(point);
        }
    }

    public bool IsNear(Coordinate point, int maxMeters)
    {
        var (cx, cy) = Cell(point);
        for (var x = cx - 1; x <= cx + 1; x++)
        {
            for (var y = cy - 1; y <= cy + 1; y++)
            {
                if (!_cells.TryGetValue((x, y), out var bucket))
                    continue;
                foreach (var candidate in bucket)
                {
                    if (GeoSpatialFunctions.DistanceTo(point, candidate) <= maxMeters)
                        return true;
                }
            }
        }
        return false;
    }

    private static (int x, int y) Cell(Coordinate point)
        => ((int)Math.Floor(point.Lng / CellSizeDegrees), (int)Math.Floor(point.Lat / CellSizeDegrees));
}
