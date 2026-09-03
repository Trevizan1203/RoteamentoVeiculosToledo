using Dvrp.Domain.Graph;

namespace Dvrp.Domain.Pathfinding;

/// <summary>
/// Dijkstra clássico 1-para-Todos (SSSP): a partir de um único nó de origem, calcula o
/// menor tempo de viagem (e a distância correspondente) até TODOS os demais nós do grafo em
/// uma única busca — sem heurística e sem interrupção antecipada por nó de destino, já que
/// não há um destino único.
///
/// Usado pelo <see cref="Logistics.CostMatrixService"/> para preencher cada LINHA da matriz
/// de custos com uma única busca por ponto logístico, em vez de uma busca A* ponto a ponto
/// por PAR (origem, destino).
/// </summary>
public sealed class DijkstraPathfinder
{
    public SingleSourcePathResult FindAllPaths(DirectedGraph graph, int sourceNodeIndex)
    {
        if (sourceNodeIndex < 0 || sourceNodeIndex >= graph.NodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceNodeIndex), "O índice de nó de origem está fora dos limites do grafo.");
        }

        int nodeCount = graph.NodeCount;

        float[] gScore = new float[nodeCount];
        Array.Fill(gScore, float.PositiveInfinity);

        float[] distanceScore = new float[nodeCount];
        int[] cameFrom = new int[nodeCount];
        Array.Fill(cameFrom, -1);

        // Fila de prioridade Min-Heap nativa do .NET, com prioridade = tempo acumulado (gScore).
        PriorityQueue<int, float> openSet = new();

        gScore[sourceNodeIndex] = 0f;
        distanceScore[sourceNodeIndex] = 0f;
        openSet.Enqueue(sourceNodeIndex, 0f);

        // Roda até a fila esvaziar: não há nó de destino para interromper a busca mais cedo.
        while (openSet.TryDequeue(out int currentNodeIndex, out float currentTravelTime))
        {
            // Entrada obsoleta: este nó já foi desempilhado antes com um custo melhor.
            if (currentTravelTime > gScore[currentNodeIndex])
            {
                continue;
            }

            ReadOnlySpan<Edge> edges = graph.GetOutgoingEdges(currentNodeIndex);

            for (int i = 0; i < edges.Length; i++)
            {
                Edge edge = edges[i];
                int neighborIndex = edge.TargetNodeIndex;

                float tentativeG = currentTravelTime + edge.TravelTimeSeconds;

                if (tentativeG < gScore[neighborIndex])
                {
                    cameFrom[neighborIndex] = currentNodeIndex;
                    gScore[neighborIndex] = tentativeG;
                    distanceScore[neighborIndex] = distanceScore[currentNodeIndex] + edge.DistanceMeters;

                    openSet.Enqueue(neighborIndex, tentativeG);
                }
            }
        }

        return new SingleSourcePathResult(sourceNodeIndex, gScore, distanceScore, cameFrom);
    }
}
