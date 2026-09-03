using Dvrp.Domain.Common;
using Dvrp.Domain.Graph;

namespace Dvrp.Domain.Pathfinding;

public sealed class AStarPathfinder : IPathFinder
{
    public PathResult FindPath(DirectedGraph graph, int sourceNodeIndex, int targetNodeIndex)
    {
        // 1. Validações de limites e caso trivial (origem == destino)
        if (sourceNodeIndex < 0 || sourceNodeIndex >= graph.NodeCount ||
            targetNodeIndex < 0 || targetNodeIndex >= graph.NodeCount)
        {
            throw new ArgumentOutOfRangeException("Os índices de nó informados estão fora dos limites do grafo.");
        }

        if (sourceNodeIndex == targetNodeIndex)
        {
            return new PathResult(
                Success: true,
                TotalTravelTimeSeconds: 0,
                TotalDistanceMeters: 0,
                Segments: Array.Empty<PathSegment>()
            );
        }

        int nodeCount = graph.NodeCount;
        Node targetNode = graph.GetNode(targetNodeIndex);

        // 2. Estruturas de controle indexadas diretamente por nó
        float[] gScore = new float[nodeCount];
        Array.Fill(gScore, float.PositiveInfinity);

        float[] distanceScore = new float[nodeCount];
        int[] cameFromNode = new int[nodeCount];
        Array.Fill(cameFromNode, -1);
        Edge?[] cameFromEdge = new Edge?[nodeCount];

        // Fila de prioridade Min-Heap nativa do .NET
        PriorityQueue<int, float> openSet = new();

        // 3. Inicialização da busca a partir da origem
        gScore[sourceNodeIndex] = 0f;
        distanceScore[sourceNodeIndex] = 0f;

        float initialHeuristic = GeoMath.CalculateTimeHeuristic(
            graph.GetNode(sourceNodeIndex),
            targetNode,
            graph.MaxSpeedLimitKmh
        );

        openSet.Enqueue(sourceNodeIndex, initialHeuristic);

        // 4. Laço principal do A*
        while (openSet.TryDequeue(out int currentNodeIndex, out float currentPriority))
        {
            // Se alcançou o destino, reconstrói a rota ótima
            if (currentNodeIndex == targetNodeIndex)
            {
                return ReconstructPath(
                    cameFromNode,
                    cameFromEdge,
                    targetNodeIndex,
                    gScore[targetNodeIndex],
                    distanceScore[targetNodeIndex]
                );
            }

            // Otimização: se o custo desempilhado for pior que o melhor já encontrado para este nó, descarta
            float currentG = gScore[currentNodeIndex];
            if (currentPriority < currentG)
            {
                continue;
            }

            // Consulta os vizinhos em O(1) via ReadOnlySpan sem alocação de Heap
            ReadOnlySpan<Edge> edges = graph.GetOutgoingEdges(currentNodeIndex);

            for (int i = 0; i < edges.Length; i++)
            {
                Edge edge = edges[i];
                
                //No que a aresta leva
                int neighborIndex = edge.TargetNodeIndex;

                //Custo total percorrido + custo desse caminho.
                float tentativeG = currentG + edge.TravelTimeSeconds;

                // Se esse novo trajeto for mais rápido do que qualquer outro já testado para essa esquina
                // (na 1ª vez sempre entra aqui, pois o valor inicial do vizinho é infinito):
                if (tentativeG < gScore[neighborIndex])
                {
                    cameFromNode[neighborIndex] = currentNodeIndex;
                    cameFromEdge[neighborIndex] = edge;
                    gScore[neighborIndex] = tentativeG;
                    distanceScore[neighborIndex] = distanceScore[currentNodeIndex] + edge.DistanceMeters;

                    float h = GeoMath.CalculateTimeHeuristic(
                        graph.GetNode(neighborIndex),
                        targetNode,
                        graph.MaxSpeedLimitKmh
                    );

                    float fScore = tentativeG + h;
                    openSet.Enqueue(neighborIndex, fScore);
                }
            }
        }

        // Se a fila esvaziou e o destino não foi alcançado (ex: vias de mão única sem retorno)
        return PathResult.NotFound();
    }
    
    

    private static PathResult ReconstructPath(
        int[] cameFromNode,
        Edge?[] cameFromEdge,
        int targetNodeIndex,
        float totalTime,
        float totalDistance)
    {
        List<PathSegment> segments = new();
        int current = targetNodeIndex;

        while (cameFromNode[current] != -1)
        {
            Edge edge = cameFromEdge[current]!.Value;
            int previous = cameFromNode[current];

            segments.Add(new PathSegment(
                FromNodeIndex: previous,
                ToNodeIndex: current,
                DistanceMeters: edge.DistanceMeters,
                TravelTimeSeconds: edge.TravelTimeSeconds
            ));

            current = previous;
        }

        segments.Reverse();

        return new PathResult(
            Success: true,
            TotalTravelTimeSeconds: totalTime,
            TotalDistanceMeters: totalDistance,
            Segments: segments
        );
    }
}