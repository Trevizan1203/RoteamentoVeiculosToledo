namespace Dvrp.Domain.Logistics;

public readonly record struct LogisticPoint(
    int Id,
    double Latitude,
    double Longitude,
    int Demand,
    int RoadNodeIndex
)
{
    public bool IsDepot => Id == 0;
}
