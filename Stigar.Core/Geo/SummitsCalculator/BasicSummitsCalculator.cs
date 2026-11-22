using Stigar.Core.Models;

namespace Stigar.Core.Geo.SummitsCalculator;

public class BasicSummitsCalculator : ISummitsCalculator
{
    public IEnumerable<T> FindPointsNearRoute<T>(IEnumerable<(T, Coordinate)> points, string polylineString, int maxIntersectDistance = 50)
    {
        // Materialize once: the per-point scan below re-enumerates this sequence for every peak.
        // A lazy DecodePolyline here would decode the whole route once per candidate peak.
        var polyline = GeoSpatialFunctions.DecodePolyline(polylineString).ToList();
        return FindPointsNearRoute(points, polyline, maxIntersectDistance);
    }

    public IEnumerable<T> FindPointsNearRoute<T>(IEnumerable<(T, Coordinate)> points, IEnumerable<Coordinate> polyline, int maxIntersectDistance = 50)
    {
        foreach (var (obj, location) in points)
        {
            foreach (Coordinate coordinate in polyline)
            {
                var distance = GeoSpatialFunctions.DistanceTo(location, coordinate);
                if (distance <= maxIntersectDistance)
                {
                    yield return obj;
                    break;
                }
            }
        }
    }
}