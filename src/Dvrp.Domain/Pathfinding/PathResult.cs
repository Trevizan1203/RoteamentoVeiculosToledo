namespace Dvrp.Domain.Pathfinding;

public readonly record struct PathResult(
    bool Success,
    float TotalDistanceMeters,
    float TotalTravelTimeSeconds,
    IReadOnlyList<PathSegment> Segments
)
{
    public static PathResult NotFound() =>
        new PathResult(false, 0, 0, Array.Empty<PathSegment>());

    public IReadOnlyList<int> ReconstructPath()
    {
        List<int> nodes = new (Segments.Count + 1);

        for (int i = 0; i < Segments.Count; i++)
        {
            if (i == 0)
            {
                nodes.Add(Segments[i].FromNodeIndex);
            }
            nodes.Add(Segments[i].ToNodeIndex);
        }

        return nodes;
    }
}

public readonly record struct PathSegment(
    int FromNodeIndex,
    int ToNodeIndex,
    float DistanceMeters,
    float TravelTimeSeconds);