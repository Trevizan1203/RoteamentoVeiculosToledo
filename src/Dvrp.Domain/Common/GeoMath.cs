namespace Dvrp.Domain.Common;

using Dvrp.Domain.Graph;

public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000.0;

    /// <summary>
    /// Calcula a distância ortodrômica (em linha reta sobre a superfície da Terra)
    /// entre dois nós utilizando a fórmula de Haversine.
    /// </summary>
    public static float HaversineDistanceMeters(Node origin, Node destination)
    {
        double lat1Rad = double.DegreesToRadians(origin.Latitude);
        double lon1Rad = double.DegreesToRadians(origin.Longitude);
        double lat2Rad = double.DegreesToRadians(destination.Latitude);
        double lon2Rad = double.DegreesToRadians(destination.Longitude);

        double deltaLat = lat2Rad - lat1Rad;
        double deltaLon = lon2Rad - lon1Rad;

        double sinHalfDeltaLat = Math.Sin(deltaLat / 2.0);
        double sinHalfDeltaLon = Math.Sin(deltaLon / 2.0);

        double a = (sinHalfDeltaLat * sinHalfDeltaLat) +
                   Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                   (sinHalfDeltaLon * sinHalfDeltaLon);

        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));

        return (float)(EarthRadiusMeters * c);
    }

    /// <summary>
    /// Calcula a heurística h(n) admissível em segundos para o A*.
    /// h(n) = Distância / Velocidade Máxima da Malha
    /// </summary>
    public static float CalculateTimeHeuristic(Node current, Node target, float maxSpeedLimitKmh)
    {
        if (maxSpeedLimitKmh <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSpeedLimitKmh), "A velocidade máxima do grafo deve ser maior que zero.");
        }

        float distanceMeters = HaversineDistanceMeters(current, target);
        float maxSpeedMetersPerSecond = maxSpeedLimitKmh / 3.6f;

        return distanceMeters / maxSpeedMetersPerSecond;
    }
}