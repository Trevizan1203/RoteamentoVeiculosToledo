using Dvrp.Domain.Graph;

namespace Dvrp.Domain.Pathfinding;

public interface IPathFinder
{
    PathResult FindPath(DirectedGraph graph, int sourceNodeIndex, int targetNodeIndex);   
}