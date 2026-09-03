namespace Dvrp.Domain.Graph;

/// <summary>
/// Representa a posição geográfica imutável de um cruzamento ou vértice no mapa.
/// </summary>
public readonly record struct Node(double Latitude, double Longitude);