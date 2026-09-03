namespace Dvrp.Domain.Graph;

public class DirectedGraph
{
    private readonly Node[] _nodes;
    private readonly Edge[][] _adjacencyList;

    public int NodeCount => _nodes.Length;
    public int EdgeCount { get; }

    public float MaxSpeedLimitKmh { get; }

    public DirectedGraph(IReadOnlyList<Node> nodes, IReadOnlyList<IReadOnlyList<Edge>> adjacencyList, float maxSpeedLimitKmh)
    {
        if (nodes.Count != adjacencyList.Count)
        {
            throw new ArgumentException("A quantidade de nós deve corresponder à quantidade de listas de adjacência.", nameof(adjacencyList));
        }

        _nodes = nodes.ToArray();
        _adjacencyList = new Edge[adjacencyList.Count][];

        int edgeCount = 0;
        for (int i = 0; i < adjacencyList.Count; i++)
        {
            _adjacencyList[i] = adjacencyList[i] is Edge[] existingRow
                ? existingRow
                : adjacencyList[i].ToArray();
            edgeCount += _adjacencyList[i].Length;
        }

        EdgeCount = edgeCount;
        MaxSpeedLimitKmh = maxSpeedLimitKmh;
    }

    public Node GetNode(int index) =>
        _nodes[index];
    
    public ReadOnlySpan<Edge> GetOutgoingEdges(int nodeIndex) =>
        _adjacencyList[nodeIndex];

    internal Edge[] GetOutgoingEdgesArray(int nodeIndex) =>
        _adjacencyList[nodeIndex];
}
