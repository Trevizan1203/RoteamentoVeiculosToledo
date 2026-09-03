namespace Dvrp.Domain.Logistics;

public readonly record struct DeliveryPoint(
    int Id,
    double Latitude,
    double Longitude,
    int Demand)
{
    public bool IsDepot => Id == 0;
}