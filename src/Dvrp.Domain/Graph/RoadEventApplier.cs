namespace Dvrp.Domain.Graph;

/// <summary>
/// Aplica um <see cref="RoadEvent"/> sobre um <see cref="DirectedGraph"/>, devolvendo um grafo
/// NOVO com o TravelTimeSeconds das arestas afetadas multiplicado por PenaltyFactor. O grafo
/// original nunca é mutado.
/// </summary>
public sealed class RoadEventApplier
{
    public DirectedGraph Apply(DirectedGraph graph, RoadEvent roadEvent)
    {
        int nodeCount = graph.NodeCount;

        bool[] affectedNodes = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            affectedNodes[i] = IsWithinArea(graph.GetNode(i), roadEvent);
        }

        Node[] nodes = new Node[nodeCount];
        Edge[][] adjacencyList = new Edge[nodeCount][];

        for (int sourceIndex = 0; sourceIndex < nodeCount; sourceIndex++)
        {
            nodes[sourceIndex] = graph.GetNode(sourceIndex);

            Edge[] originalEdges = graph.GetOutgoingEdgesArray(sourceIndex);
            bool sourceAffected = affectedNodes[sourceIndex];

            bool anyEdgeAffected = false;
            for (int i = 0; i < originalEdges.Length; i++)
            {
                if (sourceAffected || affectedNodes[originalEdges[i].TargetNodeIndex])
                {
                    anyEdgeAffected = true;
                    break;
                }
            }

            if (!anyEdgeAffected)
            {
                // Nenhuma aresta desta linha foi afetada: reaproveita o mesmo array (nunca
                // será escrito depois de criado), evitando clonar a maior parte do grafo.
                adjacencyList[sourceIndex] = originalEdges;
                continue;
            }

            Edge[] recalculatedEdges = (Edge[])originalEdges.Clone();
            for (int i = 0; i < recalculatedEdges.Length; i++)
            {
                Edge edge = recalculatedEdges[i];
                if (sourceAffected || affectedNodes[edge.TargetNodeIndex])
                {
                    recalculatedEdges[i] = edge with { TravelTimeSeconds = edge.TravelTimeSeconds * roadEvent.PenaltyFactor };
                }
            }

            adjacencyList[sourceIndex] = recalculatedEdges;
        }

        return new DirectedGraph(nodes, adjacencyList, graph.MaxSpeedLimitKmh);
    }

    private static bool IsWithinArea(Node node, RoadEvent roadEvent) =>
        node.Latitude >= roadEvent.MinLatitude && node.Latitude <= roadEvent.MaxLatitude &&
        node.Longitude >= roadEvent.MinLongitude && node.Longitude <= roadEvent.MaxLongitude;
}
