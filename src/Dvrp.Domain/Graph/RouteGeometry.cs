using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Pathfinding;

namespace Dvrp.Domain.Graph;

/// <summary>
/// Traçado físico real, segmento a segmento, de UMA <see cref="VehicleRoute"/> inteira —
/// concatenação, em ordem, dos <see cref="PathSegment"/> retornados pelo
/// <see cref="AStarPathfinder"/> para cada trecho consecutivo da rota (Depósito -> 1º cliente
/// -> ... -> último cliente -> Depósito).
///
/// Capturada sobre o grafo ORIGINAL (sem nenhum <see cref="RoadEvent"/> aplicado), serve de
/// base à estratégia ESTÁTICA de resposta a eventos dinâmicos: ela deve manter o veículo no
/// MESMO trajeto físico original, só ficando mais lento nele — nunca escolher um desvio
/// silenciosamente ao reconsultar uma <see cref="Logistics.CostMatrix"/> penalizada (que
/// recalcularia o caminho de menor tempo do zero via Dijkstra/A*).
/// </summary>
public readonly record struct RouteGeometry(
    int VehicleIndex,
    IReadOnlyList<PathSegment> Segments
);

/// <summary>
/// Constrói e reaproveita a <see cref="RouteGeometry"/> de cada rota de um
/// <see cref="Chromosome"/> — usado pela estratégia ESTÁTICA de resposta a eventos dinâmicos
/// para congelar o trajeto físico ANTES de um <see cref="RoadEvent"/> ser aplicado, e depois
/// recalcular apenas o tempo de viagem relendo pesos de arestas específicas já fixadas — nunca
/// rodando Dijkstra/A* de novo, o que poderia silenciosamente escolher um desvio.
/// </summary>
public static class RouteGeometryService
{
    /// <summary>
    /// Para cada <see cref="VehicleRoute"/> de <paramref name="chromosome"/>, percorre
    /// Depósito -> clientes (na ordem de <c>ClientIndices</c>) -> Depósito usando os
    /// <c>RoadNodeIndex</c> de <paramref name="costMatrix"/>.Points, chama
    /// <see cref="AStarPathfinder.FindPath"/> para cada par consecutivo sobre
    /// <paramref name="graph"/> e concatena os <see cref="PathSegment"/> de cada resultado, em
    /// ordem, na <see cref="RouteGeometry"/> daquela rota.
    /// </summary>
    /// <param name="graph">Deve ser o grafo ORIGINAL, sem nenhum RoadEvent aplicado — a
    /// geometria capturada aqui é o trajeto físico de referência que a estratégia estática
    /// nunca deve abandonar.</param>
    public static IReadOnlyList<RouteGeometry> BuildRouteGeometry(
        Chromosome chromosome,
        DirectedGraph graph,
        CostMatrix costMatrix)
    {
        var pathfinder = new AStarPathfinder();
        var geometries = new List<RouteGeometry>(chromosome.Routes.Count);
        int depotNode = costMatrix.Points[0].RoadNodeIndex;

        foreach (VehicleRoute route in chromosome.Routes)
        {
            var segments = new List<PathSegment>();
            int previousNode = depotNode;

            foreach (int clientIndex in route.ClientIndices)
            {
                int clientNode = costMatrix.Points[clientIndex].RoadNodeIndex;
                segments.AddRange(FindPathOrThrow(pathfinder, graph, previousNode, clientNode).Segments);
                previousNode = clientNode;
            }

            segments.AddRange(FindPathOrThrow(pathfinder, graph, previousNode, depotNode).Segments);

            geometries.Add(new RouteGeometry(route.VehicleIndex, segments));
        }

        return geometries;
    }

    private static PathResult FindPathOrThrow(AStarPathfinder pathfinder, DirectedGraph graph, int fromNode, int toNode)
    {
        PathResult result = pathfinder.FindPath(graph, fromNode, toNode);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Nao foi possivel encontrar trajeto entre os nos {fromNode} e {toNode} no grafo original " +
                "ao construir a RouteGeometry.");
        }

        return result;
    }

    /// <summary>
    /// Recalcula tempo/distância de UMA rota a partir do seu trajeto físico já fixado
    /// (<paramref name="geometry"/>): para cada <see cref="PathSegment"/> armazenado, relê SÓ o
    /// <c>TravelTimeSeconds</c> ATUALIZADO da aresta correspondente (mesmo
    /// FromNodeIndex/ToNodeIndex) em <paramref name="penalizedGraph"/> — nunca roda Dijkstra/A*
    /// de novo, então o trajeto físico nunca muda, só o tempo. O <c>DistanceMeters</c> de cada
    /// segmento é reaproveitado do trajeto original sem releitura, já que
    /// <see cref="RoadEventApplier"/> nunca altera <c>DistanceMeters</c> de aresta.
    ///
    /// Desempate de arestas paralelas (mesmo par FromNodeIndex/ToNodeIndex): usa a PRIMEIRA
    /// encontrada na lista de arestas de saída do nó de origem — critério simples e
    /// determinístico (a mesma aresta, na mesma posição, é sempre escolhida entre execuções),
    /// não arbitrário. Caso raro na malha real usada pelo projeto.
    /// </summary>
    /// <param name="clientIndices">ClientIndices originais da rota — a estratégia estática não
    /// reparticiona, então são repassados sem alteração para o <see cref="VehicleRoute"/>
    /// resultante (não fazem parte de <see cref="RouteGeometry"/>, que só guarda o traçado
    /// físico em nós de grafo, não índices de cliente).</param>
    public static VehicleRoute RecalculateRouteFromGeometry(
        RouteGeometry geometry,
        DirectedGraph penalizedGraph,
        float vehicleCapacity,
        float totalDemand,
        IReadOnlyList<int> clientIndices)
    {
        float totalTime = 0f;
        float totalDistance = 0f;

        foreach (PathSegment segment in geometry.Segments)
        {
            totalTime += FindUpdatedTravelTime(penalizedGraph, segment);
            totalDistance += segment.DistanceMeters;
        }

        return new VehicleRoute(
            vehicleIndex: geometry.VehicleIndex,
            clientIndices: clientIndices,
            travelTimeSeconds: totalTime,
            distanceMeters: totalDistance,
            totalDemand: totalDemand,
            maxCapacity: vehicleCapacity
        );
    }

    private static float FindUpdatedTravelTime(DirectedGraph penalizedGraph, PathSegment segment)
    {
        ReadOnlySpan<Edge> outgoingEdges = penalizedGraph.GetOutgoingEdges(segment.FromNodeIndex);

        for (int i = 0; i < outgoingEdges.Length; i++)
        {
            if (outgoingEdges[i].TargetNodeIndex == segment.ToNodeIndex)
            {
                return outgoingEdges[i].TravelTimeSeconds;
            }
        }

        throw new InvalidOperationException(
            $"Nao foi encontrada aresta {segment.FromNodeIndex} -> {segment.ToNodeIndex} no grafo penalizado " +
            "(topologia inesperadamente diferente do grafo original usado para construir a RouteGeometry).");
    }
}
