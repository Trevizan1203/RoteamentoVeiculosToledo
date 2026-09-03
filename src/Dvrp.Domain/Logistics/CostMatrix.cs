namespace Dvrp.Domain.Logistics;

public sealed class CostMatrix
{
    private readonly float[] _travelTimes;
    private readonly float[] _distances;

    public int Size { get; }
    public IReadOnlyList<LogisticPoint> Points { get; }

    public CostMatrix(
        IReadOnlyList<LogisticPoint> points,
        float[] travelTimes,
        float[] distances)
    {
        Size = points.Count;
        Points = points;
        _travelTimes = travelTimes;
        _distances = distances;
    }

    /// <summary>
    /// Retorna o tempo de viagem (c_ij) em segundos entre o ponto de índice de origem e destino.
    /// </summary>
    public float GetTravelTime(int fromIndex, int toIndex) =>
        _travelTimes[(fromIndex * Size) + toIndex];

    /// <summary>
    /// Retorna a distância física percorrida em metros entre o ponto de índice de origem e destino.
    /// </summary>
    public float GetDistance(int fromIndex, int toIndex) =>
        _distances[(fromIndex * Size) + toIndex];
}