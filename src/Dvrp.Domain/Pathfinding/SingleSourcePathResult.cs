namespace Dvrp.Domain.Pathfinding;

public sealed class SingleSourcePathResult(
    int sourceNodeIndex,
    float[] travelTimes,
    float[] distances,
    int[] cameFrom)
{
    public int SourceNodeIndex { get; } = sourceNodeIndex;

    public float GetTravelTime(int targetNodeIndex) => travelTimes[targetNodeIndex];

    public float GetDistance(int targetNodeIndex) => distances[targetNodeIndex];

    public bool IsReachable(int targetNodeIndex) =>
        !float.IsPositiveInfinity(travelTimes[targetNodeIndex]);

    public IReadOnlyList<int> ReconstructPathTo(int targetNodeIndex)
    {
        if (!IsReachable(targetNodeIndex))
        {
            return [];
        }

        List<int> path = [];
        int current = targetNodeIndex;

        while (current != -1)
        {
            path.Add(current);
            current = cameFrom[current];
        }

        path.Reverse();
        return path;
    }
}