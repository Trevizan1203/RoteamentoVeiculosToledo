namespace Dvrp.Domain.Graph;

/// <summary>
/// Representa a conexão direcionada para um nó vizinho e o custo de travessia.
/// </summary>
public readonly record struct Edge(
    int TargetNodeIndex,
    float TravelTimeSeconds,
    float DistanceMeters,
    float SpeedLimitKmh
);