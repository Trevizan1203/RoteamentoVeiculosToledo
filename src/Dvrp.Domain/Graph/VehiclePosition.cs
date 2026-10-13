namespace Dvrp.Domain.Graph;

/// <summary>
/// Onde um veículo está num instante simulado, segundo a sua <see cref="RouteTimeline"/>.
/// </summary>
/// <param name="Latitude">Coordenada interpolada linearmente entre os nós do segmento em curso.</param>
/// <param name="Longitude">Idem, longitude.</param>
/// <param name="SegmentIndex">Segmento em curso (-1 se a rota não tem segmentos).</param>
/// <param name="FractionAlongSegment">Quanto do segmento em curso já foi percorrido (0..1).</param>
/// <param name="ElapsedSeconds">Tempo de percurso decorrido desde a saída do depósito (limitado ao total).</param>
/// <param name="NextNodeIndex">Próximo nó viário que o veículo vai alcançar (depósito, se terminou).</param>
/// <param name="ServedStopCount">Quantas paradas de cliente já foram alcançadas.</param>
/// <param name="IsFinished">true quando o veículo já voltou ao depósito.</param>
public readonly record struct VehiclePosition(
    double Latitude,
    double Longitude,
    int SegmentIndex,
    double FractionAlongSegment,
    double ElapsedSeconds,
    int NextNodeIndex,
    int ServedStopCount,
    bool IsFinished
);
