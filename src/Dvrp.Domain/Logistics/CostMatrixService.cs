using Dvrp.Domain.Common;
using Dvrp.Domain.Graph;
using Dvrp.Domain.Pathfinding;

namespace Dvrp.Domain.Logistics;

public sealed class CostMatrixService
{
    /// <summary>
    /// Matriz completa do problema original: associa cada ponto ao nó viário mais próximo e
    /// calcula todos os pares entre depósito e clientes.
    /// </summary>
    public CostMatrix BuildMatrix(
        DirectedGraph graph,
        IReadOnlyList<DeliveryPoint> deliveryPoints)
    {
        if (graph.NodeCount == 0)
        {
            throw new ArgumentException("O grafo viario nao pode estar vazio.", nameof(graph));
        }

        if (deliveryPoints == null || deliveryPoints.Count == 0)
        {
            throw new ArgumentException("A lista de pontos logisticos nao pode estar vaiza.", nameof(deliveryPoints));
        }

        LogisticPoint[] points = MapToRoadNetwork(graph, deliveryPoints);
        int[] allIndices = Enumerable.Range(0, points.Length).ToArray();

        return BuildMatrix(graph, points, extraOriginNodes: [], originIndices: allIndices, destinationIndices: allIndices);
    }

    /// <summary>
    /// Caso geral: calcula só as linhas <paramref name="originIndices"/> e, nelas, só as colunas
    /// <paramref name="destinationIndices"/>. Os índices 0..n são os de <paramref name="points"/>;
    /// cada nó de <paramref name="extraOriginNodes"/> recebe o índice seguinte (n+1, n+2, ...).
    /// Entradas não pedidas ficam com <see cref="CostMatrix.NotComputed"/>.
    /// </summary>
    public CostMatrix BuildMatrix(
        DirectedGraph graph,
        IReadOnlyList<LogisticPoint> points,
        IReadOnlyList<int> extraOriginNodes,
        IReadOnlyList<int> originIndices,
        IReadOnlyList<int> destinationIndices)
    {
        if (graph.NodeCount == 0)
        {
            throw new ArgumentException("O grafo viario nao pode estar vazio.", nameof(graph));
        }

        int size = points.Count + extraOriginNodes.Count;
        int RoadNodeOf(int index) =>
            index < points.Count ? points[index].RoadNodeIndex : extraOriginNodes[index - points.Count];
        string Describe(int index) =>
            index < points.Count ? $"Ponto {points[index].Id}" : $"Origem de veiculo (indice {index})";

        float[] travelTimes = new float[size * size];
        float[] distances = new float[size * size];
        Array.Fill(travelTimes, CostMatrix.NotComputed);
        Array.Fill(distances, CostMatrix.NotComputed);

        // Em vez de N*N buscas A* ponto a ponto, roda 1 Dijkstra 1-para-Todos (SSSP) por
        // ponto logistico de ORIGEM — cada busca preenche de uma vez a linha 'i' inteira da
        // matriz — e distribui essas N buscas independentes entre as threads da CPU.
        InvalidOperationException? unreachableFailure = null;

        Parallel.ForEach(originIndices, i =>
        {
            var dijkstraPathfinder = new DijkstraPathfinder();
            SingleSourcePathResult result = dijkstraPathfinder.FindAllPaths(graph, RoadNodeOf(i));

            foreach (int j in destinationIndices)
            {
                int matrixIndex = (i * size) + j;

                if (i == j)
                {
                    travelTimes[matrixIndex] = 0f;
                    distances[matrixIndex] = 0f;
                    continue;
                }

                int targetRoadNode = RoadNodeOf(j);

                if (!result.IsReachable(targetRoadNode))
                {
                    Interlocked.CompareExchange(
                        ref unreachableFailure,
                        new InvalidOperationException(
                            $"Malha viária desconexa: Não foi possível encontrar trajeto entre o {Describe(i)} e o {Describe(j)}."
                        ),
                        null);
                    return;
                }

                travelTimes[matrixIndex] = result.GetTravelTime(targetRoadNode);
                distances[matrixIndex] = result.GetDistance(targetRoadNode);
            }
        });

        if (unreachableFailure != null)
        {
            throw unreachableFailure;
        }

        return new CostMatrix(points, size, travelTimes, distances);
    }

    /// <summary>
    /// Mapeamento φ: associa cada ponto logístico ao nó viário mais próximo.
    /// </summary>
    private LogisticPoint[] MapToRoadNetwork(DirectedGraph graph, IReadOnlyList<DeliveryPoint> deliveryPoints)
    {
        LogisticPoint[] points = new LogisticPoint[deliveryPoints.Count];

        for (int i = 0; i < deliveryPoints.Count; i++)
        {
            DeliveryPoint deliveryPoint = deliveryPoints[i];
            int nearestPoint = FindNearestNode(graph, deliveryPoint.Latitude, deliveryPoint.Longitude);
            points[i] = new LogisticPoint(
                deliveryPoint.Id,
                deliveryPoint.Latitude,
                deliveryPoint.Longitude,
                deliveryPoint.Demand,
                nearestPoint);
        }

        return points;
    }

    private int FindNearestNode(DirectedGraph graph, double latitude, double longitude)
    {
        Node target = new Node(latitude, longitude);
        int nearestIndex = 0;
        float minDistance = float.PositiveInfinity;

        for (int i = 0; i < graph.NodeCount; i++)
        {
            float distance = GeoMath.HaversineDistanceMeters(graph.GetNode(i), target);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }


}
