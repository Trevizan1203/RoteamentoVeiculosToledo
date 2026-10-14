namespace Dvrp.Domain.Logistics;

/// <summary>
/// Matriz de custos (tempo e distância do caminho de menor tempo) indexada por ponto logístico:
/// 0 = depósito, 1..n = clientes (os mesmos índices de <see cref="Points"/>). Na reotimização, a
/// matriz pode ter índices além de n — as origens dos veículos em trânsito —, por isso
/// <see cref="Size"/> pode ser maior que <c>Points.Count</c>.
/// </summary>
public sealed class CostMatrix
{
    /// <summary>
    /// Valor das entradas que não foram calculadas (linha de cliente já atendido, coluna de origem
    /// de veículo). Nunca é custo válido: a malha é fortemente conexa e um destino inalcançável
    /// faz a construção falhar, então todo custo calculado é finito.
    /// </summary>
    public const float NotComputed = float.PositiveInfinity;

    private readonly float[] _travelTimes;
    private readonly float[] _distances;

    public int Size { get; }
    public IReadOnlyList<LogisticPoint> Points { get; }

    public CostMatrix(
        IReadOnlyList<LogisticPoint> points,
        float[] travelTimes,
        float[] distances)
        : this(points, points.Count, travelTimes, distances)
    {
    }

    public CostMatrix(
        IReadOnlyList<LogisticPoint> points,
        int size,
        float[] travelTimes,
        float[] distances)
    {
        Size = size;
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

    public bool IsComputed(int fromIndex, int toIndex) =>
        GetTravelTime(fromIndex, toIndex) != NotComputed;
}
