using System.Diagnostics;
using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Reoptimization;

namespace Dvrp.Playground.Runners;

/// <summary>
/// Experimento de reotimização com o evento durante a operação. Para cada seed é gerado um plano
/// inicial; o evento ocorre quando esse plano atinge uma fração de clientes atendidos e as três
/// estratégias (estática, reinício e reaproveitamento) partem do mesmo snapshot e do mesmo grafo
/// penalizado. Com fração 0 o evento acontece antes da partida.
/// </summary>
public static class DynamicReoptimizationExperiment
{
    public const int DeliveryPointCount = 300;
    public const int MinDemand = 5;
    public const int MaxDemand = 25;

    public const int VehicleCapacity = 100;
    // Parâmetros definidos na calibração. O plano inicial é feito antes da operação e pode rodar até
    // convergir; a reotimização precisa responder rápido, então tem um limite menor.
    public const int GaPopulationSize = 100;
    public const int GaInitialMaxGenerations = 5000;
    public const int GaReoptimizationMaxGenerations = 1000;
    public const float GaCrossoverRate = 0.70f;
    public const float GaMutationRate = 0.05f;

    public const double DefaultTrpTopFraction = 0.25;

    public const string StaticStrategy = "estatica";
    public const string RestartStrategy = "reinicio";
    public const string ReuseStrategy = "reaproveitamento";

    /// <summary>Plano inicial de uma seed, com a linha do tempo e o trajeto de cada rota.</summary>
    public sealed record InitialPlan(
        int Seed,
        CostMatrix Matrix,
        int FleetSize,
        int VehicleCapacity,
        GaRunResult Ga,
        IReadOnlyList<RouteTimeline> Timelines,
        IReadOnlyList<RouteGeometry> Geometries);

    /// <param name="TtvTotal">Tempo da operação inteira, somando todos os veículos.</param>
    /// <param name="TtvPos">Tempo dos veículos ativos a partir da âncora até o depósito.</param>
    public readonly record struct StrategyOutcome(
        string Strategy,
        double TtvTotal,
        float TtvPos,
        double DtpTotal,
        float DtpPos,
        double TrMs,
        float Fitness,
        int ExtraRouteCount,
        int? GenerationsRun = null,
        double? TrpPercent = null,
        float? InitialFitness = null);

    public sealed record EventRunResult(
        int Seed,
        double ServedFraction,
        double EventTimeSeconds,
        int PendingCount,
        int ActiveCount,
        bool IsTrivial,
        bool AffectsMatrix,
        IReadOnlyList<StrategyOutcome> Strategies);

    public static InitialPlan BuildInitialPlan(DirectedGraph graph, int seed)
    {
        IReadOnlyList<DeliveryPoint> deliveryPoints = BuildRandomDeliveryPoints(graph, DeliveryPointCount, seed, MinDemand, MaxDemand);
        CostMatrix matrix = new CostMatrixService().BuildMatrix(graph, deliveryPoints);
        int totalDemand = deliveryPoints.Skip(1).Sum(p => p.Demand);
        int fleetSize = (int)Math.Ceiling(totalDemand / (double)VehicleCapacity) + 20;

        return BuildInitialPlan(graph, matrix, fleetSize, VehicleCapacity, seed, GaPopulationSize, GaInitialMaxGenerations);
    }

    public static InitialPlan BuildInitialPlan(
        DirectedGraph graph, CostMatrix matrix, int fleetSize, int vehicleCapacity, int seed, int populationSize, int maxGenerations)
    {
        var engine = new GeneticAlgorithmEngine(new SplitService(), new GeneticOperators(), new Random(seed));
        GaRunResult ga = engine.Run(matrix.Size - 1, matrix, fleetSize, vehicleCapacity, populationSize, maxGenerations,
            GaCrossoverRate, GaMutationRate);

        RouteTimeline[] timelines = ga.Best.Routes.Select(route => RouteTimeline.Build(route, graph, matrix)).ToArray();
        IReadOnlyList<RouteGeometry> geometries = RouteGeometryService.BuildRouteGeometry(ga.Best, graph, matrix);

        return new InitialPlan(seed, matrix, fleetSize, vehicleCapacity, ga, timelines, geometries);
    }

    /// <summary>Penaliza uma fração da área da malha, a partir do canto nordeste.</summary>
    public static RoadEvent BuildRoadEvent(DirectedGraph graph, float penaltyFactor, double eventAreaFraction)
    {
        (double minLat, double maxLat, double minLon, double maxLon) = ComputeBoundingBox(graph);

        return new RoadEvent(
            MinLatitude: maxLat - (eventAreaFraction * (maxLat - minLat)),
            MaxLatitude: maxLat,
            MinLongitude: maxLon - (eventAreaFraction * (maxLon - minLon)),
            MaxLongitude: maxLon,
            PenaltyFactor: penaltyFactor);
    }

    /// <summary>
    /// Instante em que o plano atinge a fração de clientes atendidos. Como o veículo termina a aresta
    /// em que está, o cliente já conta como atendido assim que o veículo começa a aresta que leva até
    /// ele. O instante escolhido fica no meio do intervalo com floor(f·n) atendidos; se houver empate,
    /// usa o próximo intervalo possível.
    /// </summary>
    public static double EventTimeForServedFraction(IReadOnlyList<RouteTimeline> timelines, double servedFraction)
    {
        double[] servedAfter = timelines
            .SelectMany(timeline => timeline.Stops.Select(stop => stop.SegmentEndIndex < 0
                ? double.NegativeInfinity // cliente no mesmo nó do depósito
                : timeline.Segments[stop.SegmentEndIndex].StartTimeSeconds))
            .Order()
            .ToArray();

        for (int served = (int)Math.Floor(servedFraction * servedAfter.Length); served < servedAfter.Length; served++)
        {
            double lower = served == 0 ? 0.0 : Math.Max(0.0, servedAfter[served - 1]);
            double upper = servedAfter[served];
            if (lower < upper)
            {
                return (lower + upper) / 2;
            }
        }

        return timelines.Max(timeline => timeline.TotalTimeSeconds);
    }

    public static EventRunResult RunEventDuringOperation(
        DirectedGraph graph, InitialPlan plan, float penaltyFactor, double eventAreaFraction, double servedFraction,
        int reoptimizationMaxGenerations = GaReoptimizationMaxGenerations)
        => RunEventDuringOperation(
            graph, plan, BuildRoadEvent(graph, penaltyFactor, eventAreaFraction), servedFraction, GaPopulationSize, reoptimizationMaxGenerations);

    public static EventRunResult RunEventDuringOperation(
        DirectedGraph graph, InitialPlan plan, RoadEvent roadEvent, double servedFraction, int populationSize, int maxGenerations)
    {
        double eventTime = servedFraction == 0 ? 0.0 : EventTimeForServedFraction(plan.Timelines, servedFraction);

        // Preparação comum às estratégias dinâmicas; entra no TR das duas.
        Stopwatch preparation = Stopwatch.StartNew();
        DirectedGraph penalizedGraph = new RoadEventApplier().Apply(graph, roadEvent);
        var initial = ReoptimizationSnapshot.Initial(plan.Matrix, plan.FleetSize, plan.VehicleCapacity);
        ReoptimizationSnapshot snapshot = servedFraction == 0
            ? initial
            : ReoptimizationSnapshot.FromExecutingPlan(initial, plan.Timelines, eventTime);
        var context = ReoptimizationContext.Build(snapshot, penalizedGraph, plan.Matrix.Points, new CostMatrixService());
        preparation.Stop();

        if (CountAffectedEdges(graph, penalizedGraph) == 0)
        {
            throw new InvalidOperationException($"O evento da seed {plan.Seed} nao afetou nenhuma aresta.");
        }

        // Fora do TR: indica se o evento mudou algum custo usado pelo problema restante.
        bool affectsMatrix = DiffersFromBaseGraph(context, snapshot, graph, plan.Matrix.Points);

        // A estática não roda o AG. Aqui também saem o trecho já executado e os veículos concluídos,
        // que são iguais nas três estratégias.
        Stopwatch staticClock = Stopwatch.StartNew();
        OperationSplit operation = SplitOperation(plan, snapshot, penalizedGraph, eventTime, servedFraction == 0);
        staticClock.Stop();

        StrategyOutcome staticOutcome = operation.Outcome(
            StaticStrategy, operation.StaticTtvPos, operation.StaticDtpPos, staticClock.Elapsed.TotalMilliseconds,
            fitness: operation.StaticTtvPos, extraRouteCount: 0);

        StrategyOutcome[] strategies;
        bool isTrivial = snapshot.PendingClients.Count < 2;

        if (isTrivial)
        {
            strategies =
            [
                staticOutcome,
                staticOutcome with { Strategy = RestartStrategy },
                staticOutcome with { Strategy = ReuseStrategy },
            ];
        }
        else
        {
            // A população aleatória é criada antes só para registrar o melhor fitness inicial; o
            // resultado é o mesmo de chamar Run sem população.
            Stopwatch restartClock = Stopwatch.StartNew();
            GeneticAlgorithmEngine restartEngine = NewEngine(plan.Seed);
            List<Chromosome> randomPopulation = restartEngine.CreateRandomPopulation(context, populationSize);
            GaRunResult restart = restartEngine.Run(context, populationSize, maxGenerations, GaCrossoverRate, GaMutationRate,
                initialPopulation: randomPopulation);
            restartClock.Stop();

            Stopwatch reuseClock = Stopwatch.StartNew();
            GeneticAlgorithmEngine reuseEngine = NewEngine(plan.Seed);
            List<Chromosome> adapted = reuseEngine.AdaptPopulation(plan.Ga.FinalPopulation, context);
            GaRunResult reuse = reuseEngine.Run(context, populationSize, maxGenerations, GaCrossoverRate, GaMutationRate,
                initialPopulation: adapted);
            reuseClock.Stop();

            if (!IsValidChromosome(restart.Best) || !IsValidChromosome(reuse.Best))
            {
                throw new InvalidOperationException($"Estrategia dinamica da seed {plan.Seed} retornou Chromosome invalido.");
            }

            double trp = PendingPairRetentionPercent(plan.Ga.FinalPopulation, reuse.FinalPopulation, snapshot.PendingClients);

            strategies =
            [
                staticOutcome,
                operation.Outcome(RestartStrategy, restart, preparation.Elapsed.TotalMilliseconds + restartClock.Elapsed.TotalMilliseconds) with
                {
                    InitialFitness = randomPopulation.Min(c => c.Fitness),
                },
                operation.Outcome(ReuseStrategy, reuse, preparation.Elapsed.TotalMilliseconds + reuseClock.Elapsed.TotalMilliseconds) with
                {
                    TrpPercent = trp,
                    InitialFitness = adapted.Min(c => c.Fitness),
                },
            ];
        }

        return new EventRunResult(
            plan.Seed, servedFraction, eventTime,
            snapshot.PendingClients.Count, context.SplitVehicles.Count, isTrivial, affectsMatrix, strategies);
    }

    /// <summary>
    /// Compara a matriz do contexto com a que seria obtida no grafo sem o evento, só nas entradas
    /// calculadas.
    /// </summary>
    private static bool DiffersFromBaseGraph(
        ReoptimizationContext context, ReoptimizationSnapshot snapshot, DirectedGraph baseGraph, IReadOnlyList<LogisticPoint> points)
    {
        CostMatrix penalized = context.Matrix;
        CostMatrix original = ReoptimizationContext.Build(snapshot, baseGraph, points, new CostMatrixService()).Matrix;

        for (int i = 0; i < penalized.Size; i++)
        {
            for (int j = 0; j < penalized.Size; j++)
            {
                if (penalized.IsComputed(i, j) &&
                    (penalized.GetTravelTime(i, j) != original.GetTravelTime(i, j) ||
                     penalized.GetDistance(i, j) != original.GetDistance(i, j)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static GeneticAlgorithmEngine NewEngine(int seed) =>
        new(new SplitService(), new GeneticOperators(), new Random(seed));

    /// <summary>
    /// Divide a operação no instante do evento: o que os veículos ativos já percorreram, o custo dos
    /// veículos concluídos e o restante dos ativos pela estratégia estática.
    /// </summary>
    private sealed record OperationSplit(
        double ActiveExecutedTime,
        double ActiveExecutedDistance,
        double FinishedTime,
        double FinishedDistance,
        float StaticTtvPos,
        float StaticDtpPos)
    {
        public StrategyOutcome Outcome(string strategy, float ttvPos, float dtpPos, double trMs, float fitness, int extraRouteCount) =>
            new(strategy,
                TtvTotal: ActiveExecutedTime + ttvPos + FinishedTime,
                TtvPos: ttvPos,
                DtpTotal: ActiveExecutedDistance + dtpPos + FinishedDistance,
                DtpPos: dtpPos,
                trMs, fitness, extraRouteCount);

        public StrategyOutcome Outcome(string strategy, GaRunResult ga, double trMs) =>
            Outcome(strategy, ga.Best.TotalTravelTimeSeconds, ga.Best.TotalDistanceMeters, trMs, ga.Best.Fitness, ga.Best.ExtraRouteCount)
            with { GenerationsRun = ga.GenerationsRun };
    }

    private static OperationSplit SplitOperation(
        InitialPlan plan, ReoptimizationSnapshot snapshot, DirectedGraph penalizedGraph, double eventTime, bool beforeDeparture)
    {
        HashSet<int> activeIds = snapshot.ActiveVehicles.Select(vehicle => vehicle.VehicleId).ToHashSet();
        double activeTime = 0.0, activeDistance = 0.0, finishedTime = 0.0, finishedDistance = 0.0;
        float staticTtv = 0f, staticDtp = 0f;

        for (int r = 0; r < plan.Timelines.Count; r++)
        {
            RouteTimeline timeline = plan.Timelines[r];
            VehicleRoute route = plan.Ga.Best.Routes[r];
            double anchorTime = beforeDeparture ? 0.0 : timeline.AnchorAfterEvent(eventTime).AnchorTimeSeconds;

            // Segmentos iniciados antes da âncora já foram percorridos.
            int firstRemaining = 0;
            while (firstRemaining < timeline.Segments.Count && timeline.Segments[firstRemaining].StartTimeSeconds < anchorTime)
            {
                firstRemaining++;
            }

            double executedDistance = firstRemaining == 0 ? 0.0 : timeline.Segments[firstRemaining - 1].EndDistanceMeters;

            // A estática segue o trajeto original, com o tempo das arestas do grafo penalizado.
            RouteGeometry remaining = plan.Geometries[r] with { Segments = plan.Geometries[r].Segments.Skip(firstRemaining).ToArray() };
            VehicleRoute remainingCost = RouteGeometryService.RecalculateRouteFromGeometry(
                remaining, penalizedGraph, plan.VehicleCapacity, route.TotalDemand, route.ClientIndices);

            if (activeIds.Contains(route.VehicleIndex))
            {
                activeTime += anchorTime;
                activeDistance += executedDistance;
                staticTtv += remainingCost.TravelTimeSeconds;
                staticDtp += remainingCost.DistanceMeters;
            }
            else
            {
                finishedTime += anchorTime + remainingCost.TravelTimeSeconds;
                finishedDistance += executedDistance + remainingCost.DistanceMeters;
            }
        }

        return new OperationSplit(activeTime, activeDistance, finishedTime, finishedDistance, staticTtv, staticDtp);
    }

    /// <summary>
    /// TRP: percentual dos pares de clientes pendentes que estavam na mesma rota entre os melhores
    /// indivíduos antes do evento e continuam juntos entre os melhores da população final.
    /// </summary>
    private static double PendingPairRetentionPercent(
        IReadOnlyList<Chromosome> before, IReadOnlyList<Chromosome> after, IReadOnlyList<int> pendingClients)
    {
        HashSet<int> pending = pendingClients.ToHashSet();
        HashSet<(int ClientA, int ClientB)> TopPairs(IReadOnlyList<Chromosome> population)
        {
            List<Chromosome> top = population.OrderBy(c => c.Fitness).Take(TopSampleCount(population.Count, DefaultTrpTopFraction)).ToList();
            return ExtractSameRoutePairs(top).Where(pair => pending.Contains(pair.ClientA) && pending.Contains(pair.ClientB)).ToHashSet();
        }

        HashSet<(int ClientA, int ClientB)> oldPairs = TopPairs(before);
        HashSet<(int ClientA, int ClientB)> finalPairs = TopPairs(after);

        return oldPairs.Count > 0 ? 100.0 * oldPairs.Count(finalPairs.Contains) / oldPairs.Count : 0.0;
    }

    /// <summary>Quantidade de indivíduos considerados no TRP (no mínimo 1).</summary>
    internal static int TopSampleCount(int populationSize, double topFraction) =>
        Math.Max(1, (int)Math.Round(populationSize * topFraction, MidpointRounding.AwayFromZero));

    /// <summary>Pares de clientes que aparecem na mesma rota em algum dos cromossomos.</summary>
    internal static HashSet<(int ClientA, int ClientB)> ExtractSameRoutePairs(IReadOnlyList<Chromosome> chromosomes)
    {
        var pairs = new HashSet<(int ClientA, int ClientB)>();

        foreach (Chromosome chromosome in chromosomes)
        {
            foreach (VehicleRoute route in chromosome.Routes)
            {
                IReadOnlyList<int> clients = route.ClientIndices;

                for (int i = 0; i < clients.Count; i++)
                {
                    for (int j = i + 1; j < clients.Count; j++)
                    {
                        int clientA = clients[i];
                        int clientB = clients[j];
                        pairs.Add(clientA < clientB ? (clientA, clientB) : (clientB, clientA));
                    }
                }
            }
        }

        return pairs;
    }

    internal static bool IsValidChromosome(Chromosome? chromosome) =>
        chromosome is not null && !float.IsPositiveInfinity(chromosome.Fitness) && !float.IsNaN(chromosome.Fitness);

    internal static (double MinLat, double MaxLat, double MinLon, double MaxLon) ComputeBoundingBox(DirectedGraph graph)
    {
        double minLat = double.PositiveInfinity, maxLat = double.NegativeInfinity;
        double minLon = double.PositiveInfinity, maxLon = double.NegativeInfinity;

        for (int i = 0; i < graph.NodeCount; i++)
        {
            Node node = graph.GetNode(i);
            minLat = Math.Min(minLat, node.Latitude);
            maxLat = Math.Max(maxLat, node.Latitude);
            minLon = Math.Min(minLon, node.Longitude);
            maxLon = Math.Max(maxLon, node.Longitude);
        }

        return (minLat, maxLat, minLon, maxLon);
    }

    internal static int CountAffectedEdges(DirectedGraph originalGraph, DirectedGraph penalizedGraph)
    {
        int affected = 0;
        for (int i = 0; i < originalGraph.NodeCount; i++)
        {
            ReadOnlySpan<Edge> originalEdges = originalGraph.GetOutgoingEdges(i);
            ReadOnlySpan<Edge> penalizedEdges = penalizedGraph.GetOutgoingEdges(i);

            for (int j = 0; j < originalEdges.Length; j++)
            {
                if (originalEdges[j].TravelTimeSeconds != penalizedEdges[j].TravelTimeSeconds)
                {
                    affected++;
                }
            }
        }

        return affected;
    }

    /// <summary>Sorteia os pontos de entrega entre os nós da malha. O ponto 0 é o depósito.</summary>
    internal static IReadOnlyList<DeliveryPoint> BuildRandomDeliveryPoints(
        DirectedGraph graph, int count, int seed, int minDemand, int maxDemand)
    {
        var random = new Random(seed);
        var points = new DeliveryPoint[count];

        for (int i = 0; i < count; i++)
        {
            Node node = graph.GetNode(random.Next(graph.NodeCount));
            int demand = i == 0 ? 0 : random.Next(minDemand, maxDemand + 1);
            points[i] = new DeliveryPoint(i, node.Latitude, node.Longitude, demand);
        }

        return points;
    }
}
