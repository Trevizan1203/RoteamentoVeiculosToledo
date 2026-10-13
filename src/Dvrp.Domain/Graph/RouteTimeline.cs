using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Pathfinding;

namespace Dvrp.Domain.Graph;

/// <summary>
/// Um trecho de rua (aresta) da <see cref="RouteTimeline"/>, já posicionado no tempo e no espaço:
/// o veículo sai de <see cref="FromNode"/> no instante <see cref="StartTimeSeconds"/> e chega a
/// <see cref="ToNode"/> no instante <see cref="EndTimeSeconds"/>. Tempos e distâncias são
/// ACUMULADOS desde a saída do depósito (em double, para não derivar somando milhares de floats).
/// </summary>
public readonly record struct TimelineSegment(
    int FromNode,
    int ToNode,
    double StartTimeSeconds,
    double EndTimeSeconds,
    double StartDistanceMeters,
    double EndDistanceMeters)
{
    public double DurationSeconds => EndTimeSeconds - StartTimeSeconds;
}

/// <summary>
/// Uma parada de cliente na <see cref="RouteTimeline"/>: o cliente <see cref="ClientIndex"/>
/// (índice na <see cref="CostMatrix"/>) é atingido no instante <see cref="ArrivalTimeSeconds"/>,
/// ao fim do segmento <see cref="SegmentEndIndex"/>. SegmentEndIndex = -1 quando o cliente é
/// atingido antes do primeiro segmento (cliente no mesmo nó viário do depósito).
/// </summary>
public readonly record struct TimelineStop(
    int ClientIndex,
    double ArrivalTimeSeconds,
    int SegmentEndIndex);

/// <summary>
/// Linha do tempo FÍSICA de UMA <see cref="VehicleRoute"/> já decidida pelo AG: o trajeto rua a
/// rua (Depósito -> clientes na ordem de <c>ClientIndices</c> -> Depósito), com tempo e distância
/// acumulados em cada segmento, consultável por instante simulado.
///
/// É a base do veículo em trânsito: <see cref="PositionAt"/> diz onde o veículo está num instante
/// (visualização no mapa) e <see cref="AnchorAfterEvent"/> diz de onde ele pode ser reotimizado
/// quando ocorre um evento dinâmico (a posição atual vira a nova origem).
///
/// Sem tempo de serviço no cliente: o veículo segue para a próxima perna assim que chega.
/// </summary>
public sealed class RouteTimeline
{
    private readonly DirectedGraph _graph;

    public int VehicleIndex { get; }

    /// <summary>Nó viário do depósito — início e fim de toda rota.</summary>
    public int DepotNodeIndex { get; }

    public IReadOnlyList<TimelineSegment> Segments { get; }
    public IReadOnlyList<TimelineStop> Stops { get; }
    public double TotalTimeSeconds { get; }
    public double TotalDistanceMeters { get; }

    private RouteTimeline(
        DirectedGraph graph,
        int vehicleIndex,
        int depotNodeIndex,
        IReadOnlyList<TimelineSegment> segments,
        IReadOnlyList<TimelineStop> stops,
        double totalTimeSeconds,
        double totalDistanceMeters)
    {
        _graph = graph;
        VehicleIndex = vehicleIndex;
        DepotNodeIndex = depotNodeIndex;
        Segments = segments;
        Stops = stops;
        TotalTimeSeconds = totalTimeSeconds;
        TotalDistanceMeters = totalDistanceMeters;
    }

    /// <summary>
    /// Constrói a linha do tempo de <paramref name="route"/>: para cada perna (Depósito -> 1º
    /// cliente, cliente -> cliente, ..., último cliente -> Depósito) chama o
    /// <see cref="AStarPathfinder"/> entre os <c>RoadNodeIndex</c> dos dois pontos, concatena os
    /// <see cref="PathSegment"/> acumulando tempo/distância e marca o fim de cada perna como a
    /// parada do cliente. A perna final, de volta ao depósito, não gera parada.
    /// </summary>
    /// <param name="graph">Grafo cujos TravelTimeSeconds/DistanceMeters definem os tempos da
    /// linha do tempo (ex.: o ORIGINAL para o plano inicial).</param>
    /// <exception cref="InvalidOperationException">Alguma perna não tem caminho no grafo.</exception>
    public static RouteTimeline Build(VehicleRoute route, DirectedGraph graph, CostMatrix matrix)
    {
        var pathfinder = new AStarPathfinder();
        var segments = new List<TimelineSegment>();
        var stops = new List<TimelineStop>(route.ClientIndices.Count);

        int depotNode = matrix.Points[0].RoadNodeIndex;
        int previousPoint = 0;
        double elapsedTime = 0.0;
        double elapsedDistance = 0.0;

        // 1. Pernas até cada cliente, na ordem da rota. Ao fim de cada perna o veículo está no
        //    cliente: registra a parada com o instante de chegada e o último segmento percorrido.
        //    Perna de comprimento zero (dois pontos no mesmo nó viário) não adiciona segmentos,
        //    então a parada herda o mesmo instante de chegada da anterior.
        foreach (int clientIndex in route.ClientIndices)
        {
            AppendLeg(pathfinder, graph, matrix, route.VehicleIndex, previousPoint, clientIndex,
                segments, ref elapsedTime, ref elapsedDistance);

            stops.Add(new TimelineStop(clientIndex, elapsedTime, segments.Count - 1));
            previousPoint = clientIndex;
        }

        // 2. Perna final de volta ao depósito (sem parada de cliente). Rota sem clientes: perna
        //    depósito -> depósito, que não gera segmento nenhum (linha do tempo vazia, total 0).
        AppendLeg(pathfinder, graph, matrix, route.VehicleIndex, previousPoint, 0,
            segments, ref elapsedTime, ref elapsedDistance);

        return new RouteTimeline(graph, route.VehicleIndex, depotNode, segments, stops, elapsedTime, elapsedDistance);
    }

    /// <summary>
    /// Roda o A* de um ponto logístico a outro e acrescenta os segmentos encontrados ao fim da
    /// linha do tempo, avançando os acumuladores de tempo e distância.
    /// </summary>
    private static void AppendLeg(
        AStarPathfinder pathfinder,
        DirectedGraph graph,
        CostMatrix matrix,
        int vehicleIndex,
        int fromPoint,
        int toPoint,
        List<TimelineSegment> segments,
        ref double elapsedTime,
        ref double elapsedDistance)
    {
        int fromNode = matrix.Points[fromPoint].RoadNodeIndex;
        int toNode = matrix.Points[toPoint].RoadNodeIndex;

        PathResult path = pathfinder.FindPath(graph, fromNode, toNode);

        if (!path.Success)
        {
            throw new InvalidOperationException(
                $"Nao foi possivel construir a RouteTimeline do veiculo {vehicleIndex}: nao ha caminho " +
                $"entre o ponto {fromPoint} (no {fromNode}) e o ponto {toPoint} (no {toNode}) no grafo informado.");
        }

        foreach (PathSegment step in path.Segments)
        {
            double endTime = elapsedTime + step.TravelTimeSeconds;
            double endDistance = elapsedDistance + step.DistanceMeters;

            segments.Add(new TimelineSegment(
                step.FromNodeIndex, step.ToNodeIndex,
                elapsedTime, endTime,
                elapsedDistance, endDistance));

            elapsedTime = endTime;
            elapsedDistance = endDistance;
        }
    }

    /// <summary>
    /// Posição do veículo no instante <paramref name="t"/> (segundos desde a saída do depósito):
    /// localiza o segmento em curso e interpola linearmente a coordenada entre os seus dois nós.
    /// Em <paramref name="t"/> &gt;= <see cref="TotalTimeSeconds"/> o veículo já voltou ao
    /// depósito (<c>IsFinished = true</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> negativo.</exception>
    public VehiclePosition PositionAt(double t)
    {
        ThrowIfNegative(t);

        // 1. Rota concluída: veículo parado no depósito, todas as paradas atendidas.
        if (t >= TotalTimeSeconds)
        {
            Node depot = _graph.GetNode(DepotNodeIndex);

            return new VehiclePosition(
                Latitude: depot.Latitude,
                Longitude: depot.Longitude,
                SegmentIndex: Segments.Count - 1,
                FractionAlongSegment: Segments.Count > 0 ? 1.0 : 0.0,
                ElapsedSeconds: TotalTimeSeconds,
                NextNodeIndex: DepotNodeIndex,
                ServedStopCount: Stops.Count,
                IsFinished: true);
        }

        // 2. Veículo em trânsito: segmento em curso e quanto dele já foi percorrido (0..1).
        int segmentIndex = FindSegmentIndex(t);
        TimelineSegment segment = Segments[segmentIndex];
        double fraction = (t - segment.StartTimeSeconds) / segment.DurationSeconds;

        // 3. Interpolação linear da coordenada entre o nó de origem e o de destino do segmento.
        Node from = _graph.GetNode(segment.FromNode);
        Node to = _graph.GetNode(segment.ToNode);

        return new VehiclePosition(
            Latitude: from.Latitude + (fraction * (to.Latitude - from.Latitude)),
            Longitude: from.Longitude + (fraction * (to.Longitude - from.Longitude)),
            SegmentIndex: segmentIndex,
            FractionAlongSegment: fraction,
            ElapsedSeconds: t,
            NextNodeIndex: segment.ToNode,
            ServedStopCount: CountStopsArrivedBy(t),
            IsFinished: false);
    }

    /// <summary>
    /// Ponto de partida da reotimização para um evento dinâmico ocorrido no instante
    /// <paramref name="t"/>. Regra da proposta: se o evento pega o veículo no meio de uma aresta,
    /// ele CONCLUI essa aresta antes de reotimizar — a âncora é o nó final do segmento em curso,
    /// alcançado no fim desse segmento. Se <paramref name="t"/> cai exatamente sobre um nó, a
    /// âncora é esse próprio nó (sem espera). Cliente conta como atendido se o veículo chega a ele
    /// até o instante da âncora (inclusive o cliente localizado no próprio nó âncora).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> negativo.</exception>
    public EventAnchor AnchorAfterEvent(double t)
    {
        ThrowIfNegative(t);

        // 1. Rota já concluída: nada a reotimizar, âncora no depósito (último nó).
        if (t >= TotalTimeSeconds)
        {
            return new EventAnchor(
                AnchorNodeIndex: DepotNodeIndex,
                AnchorTimeSeconds: TotalTimeSeconds,
                WaitSeconds: 0.0,
                ServedClientIndices: Stops.Select(stop => stop.ClientIndex).ToArray(),
                RemainingClientIndices: Array.Empty<int>(),
                IsFinished: true);
        }

        // 2. Segmento em curso. Exatamente sobre o nó de início: âncora é esse nó, sem espera.
        //    No meio da aresta: o veículo termina a aresta e a âncora é o nó final dela.
        int segmentIndex = FindSegmentIndex(t);
        TimelineSegment segment = Segments[segmentIndex];
        bool isOnNode = t == segment.StartTimeSeconds;

        int anchorNode = isOnNode ? segment.FromNode : segment.ToNode;
        double anchorTime = isOnNode ? segment.StartTimeSeconds : segment.EndTimeSeconds;

        // 3. Paradas têm chegada não decrescente: as atendidas formam um prefixo da rota e as
        //    restantes o sufixo, já na ordem original.
        int servedCount = CountStopsArrivedBy(anchorTime);

        return new EventAnchor(
            AnchorNodeIndex: anchorNode,
            AnchorTimeSeconds: anchorTime,
            WaitSeconds: anchorTime - t,
            ServedClientIndices: Stops.Take(servedCount).Select(stop => stop.ClientIndex).ToArray(),
            RemainingClientIndices: Stops.Skip(servedCount).Select(stop => stop.ClientIndex).ToArray(),
            IsFinished: false);
    }

    /// <summary>
    /// Busca binária pelo segmento em curso no instante <paramref name="t"/> (0 &lt;= t &lt; Total):
    /// o ÚLTIMO segmento com StartTimeSeconds &lt;= t. Por ser o último, nunca cai num segmento de
    /// duração zero (o seguinte começaria no mesmo instante), então sempre Start &lt;= t &lt; End.
    /// </summary>
    private int FindSegmentIndex(double t)
    {
        int low = 0;
        int high = Segments.Count - 1;

        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);

            if (Segments[middle].StartTimeSeconds <= t)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>Quantas paradas já foram alcançadas até o instante <paramref name="t"/> (inclusive).</summary>
    private int CountStopsArrivedBy(double t)
    {
        int count = 0;

        while (count < Stops.Count && Stops[count].ArrivalTimeSeconds <= t)
        {
            count++;
        }

        return count;
    }

    private static void ThrowIfNegative(double t)
    {
        if (t < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(t), t, "O instante simulado nao pode ser negativo.");
        }
    }
}
