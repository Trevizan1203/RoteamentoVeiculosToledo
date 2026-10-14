namespace Dvrp.Domain.Optimization;

public sealed class VehicleRoute
{
    public int VehicleIndex { get; }
    public IReadOnlyList<int> ClientIndices { get; }
    public float TravelTimeSeconds { get; }
    public float DistanceMeters { get; }
    public float TotalDemand { get; }
    public bool IsCapacityExceeded { get; }

    /// <summary>Rota extra além da frota disponível, saindo do depósito e paga com penalidade.</summary>
    public bool IsVirtual { get; }

    public VehicleRoute(
        int vehicleIndex,
        IReadOnlyList<int> clientIndices,
        float travelTimeSeconds,
        float distanceMeters,
        float totalDemand,
        float maxCapacity,
        bool isVirtual = false)
    {
        VehicleIndex = vehicleIndex;
        ClientIndices = clientIndices;
        TravelTimeSeconds = travelTimeSeconds;
        DistanceMeters = distanceMeters;
        TotalDemand = totalDemand;
        IsCapacityExceeded = totalDemand > maxCapacity;
        IsVirtual = isVirtual;
    }
}
