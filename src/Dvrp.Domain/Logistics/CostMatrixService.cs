using Dvrp.Domain.Common;
using Dvrp.Domain.Graph;
using Dvrp.Domain.Pathfinding;

namespace Dvrp.Domain.Logistics;

public sealed class CostMatrixService
{
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

        int count = deliveryPoints.Count;
        LogisticPoint[] points = new LogisticPoint[count];

        for (int i = 0; i < count; i++)
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

        int totalEntries = count * count;
        float[] travelTimes = new float[totalEntries];
        float[] distances = new float[totalEntries];

        // Em vez de N*N buscas A* ponto a ponto, roda 1 Dijkstra 1-para-Todos (SSSP) por
        // ponto logistico de ORIGEM — cada busca preenche de uma vez a linha 'i' inteira da
        // matriz — e distribui essas N buscas independentes entre as threads da CPU.
        InvalidOperationException? unreachableFailure = null;

        Parallel.For(0, count, i =>
        {
            var dijkstraPathfinder = new DijkstraPathfinder();
            SingleSourcePathResult result = dijkstraPathfinder.FindAllPaths(graph, points[i].RoadNodeIndex);

            for (int j = 0; j < count; j++)
            {
                int matrixIndex = (i * count) + j;

                if (i == j)
                {
                    travelTimes[matrixIndex] = 0f;
                    distances[matrixIndex] = 0f;
                    continue;
                }

                int targetRoadNode = points[j].RoadNodeIndex;

                if (!result.IsReachable(targetRoadNode))
                {
                    Interlocked.CompareExchange(
                        ref unreachableFailure,
                        new InvalidOperationException(
                            $"Malha viária desconexa: Não foi possível encontrar trajeto entre o Ponto {points[i].Id} e o Ponto {points[j].Id}."
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

        return new CostMatrix(points, travelTimes, distances);
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