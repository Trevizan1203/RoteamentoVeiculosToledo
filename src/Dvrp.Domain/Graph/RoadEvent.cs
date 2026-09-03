namespace Dvrp.Domain.Graph;

/// <summary>
/// Evento dinâmico de penalização de custo viário (ex.: congestionamento) restrito a uma
/// área geográfica retangular — multiplica o TravelTimeSeconds das arestas afetadas por
/// <see cref="PenaltyFactor"/>, sem alterar distância física ou velocidade limite da via.
/// </summary>
public readonly record struct RoadEvent(
    double MinLatitude,
    double MaxLatitude,
    double MinLongitude,
    double MaxLongitude,
    float PenaltyFactor
);
