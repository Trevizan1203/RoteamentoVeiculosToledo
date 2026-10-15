using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Reoptimization;

namespace Dvrp.Domain.Tests.Optimization;

/// <summary>
/// Split com veículos de origens e capacidades diferentes. Os pontos ficam numa linha e o tempo
/// entre dois deles é a distância entre as posições:
///   depósito em x = 0; clientes 1 (x = 10), 2 (12), 3 (-10) e 4 (-12), com demanda 10;
///   origens 5 (x = 11, perto de 1 e 2) e 6 (x = -11, perto de 3 e 4).
/// Os ids dos veículos (3 e 7) não são sequenciais de propósito, para conferir que a rota usa o id.
/// </summary>
public class MultiOriginSplitTests(ToledoFixture toledo) : IClassFixture<ToledoFixture>
{
    private const int OriginA = 5;
    private const int OriginB = 6;
    private const float Penalty = 5000f;

    private static readonly CostMatrix Line = LineMatrix(positions: [0, 10, 12, -10, -12, 11, -11], clientCount: 4);

    [Fact]
    public void TourConcatenandoOsPlanosRestantes_DevolveCadaPlanoAoSeuVeiculo()
    {
        SplitResult result = Split([1, 2, 3, 4], Vehicle(3, OriginA, 30f), Vehicle(7, OriginB, 30f));

        Assert.Equal(0, result.ExtraRouteCount);
        Assert.Equal([3, 7], result.Routes.Select(route => route.VehicleIndex));
        Assert.Equal([1, 2], result.Routes[0].ClientIndices);
        Assert.Equal([3, 4], result.Routes[1].ClientIndices);
        Assert.Equal([OriginA, OriginB], result.Routes.Select(route => route.OriginIndex));
        Assert.Equal(30f, result.Fitness); // (1 + 2 + 12) + (1 + 2 + 12)
    }

    [Fact]
    public void VeiculoForaDoDepositoSemClientes_PagaAVoltaAoDeposito()
    {
        // Só os clientes 1 e 2 pendentes: o veículo 7 fica sem segmento, mas está em x = -11.
        SplitResult result = Split([1, 2], Vehicle(3, OriginA, 30f), Vehicle(7, OriginB, 30f));

        VehicleRoute idle = Assert.Single(result.Routes, route => route.VehicleIndex == 7);
        Assert.Empty(idle.ClientIndices);
        Assert.Equal(11f, idle.TravelTimeSeconds);
        Assert.Equal(15f + 11f, result.Fitness);
    }

    [Fact]
    public void VeiculoNoDepositoSemClientes_NaoGeraRota()
    {
        SplitResult result = Split([1, 2], Vehicle(3, OriginA, 30f), Vehicle(7, SplitService.DepotIndex, 30f));

        Assert.Equal([3], result.Routes.Select(route => route.VehicleIndex));
        Assert.Equal(15f, result.Fitness);
    }

    [Fact]
    public void CapacidadesResiduaisDiferentes_SaoRespeitadas()
    {
        // O veículo 3 só tem espaço para um cliente; o 7 leva os três seguintes.
        SplitResult result = Split([1, 2, 3, 4], Vehicle(3, OriginA, 10f), Vehicle(7, OriginB, 30f));

        Assert.Equal(0, result.ExtraRouteCount);
        Assert.Equal([1], result.Routes[0].ClientIndices);
        Assert.Equal([2, 3, 4], result.Routes[1].ClientIndices);
        Assert.All(result.Routes, route => Assert.False(route.IsCapacityExceeded));
    }

    [Fact]
    public void TourQueNaoCabeNosVeiculosReais_UsaRotaExtraVirtualComPenalidade()
    {
        SplitResult result = Split([1, 2, 3, 4], Vehicle(3, OriginA, 10f), Vehicle(7, OriginB, 10f));

        // Cada veículo real só leva um cliente; os outros dois cabem numa rota extra (Q = 30).
        VehicleRoute extra = Assert.Single(result.Routes, route => route.IsVirtual);
        Assert.Equal(1, result.ExtraRouteCount);
        Assert.Equal(SplitService.DepotIndex, extra.OriginIndex);
        Assert.True(extra.VehicleIndex < 0);
        Assert.Equal(result.TotalTravelTimeSeconds + Penalty, result.Fitness);
    }

    [Fact]
    public void InstanciaMinima_IgualAForcaBruta()
    {
        var random = new Random(5);

        for (int trial = 0; trial < 200; trial++)
        {
            CostMatrix matrix = RandomMatrix(random);
            int[] tour = Enumerable.Range(1, 4).OrderBy(_ => random.Next()).ToArray();
            SplitVehicle[] vehicles =
            [
                new(VehicleId: 3, OriginA, Capacity: random.Next(0, 4) * 10f),
                new(VehicleId: 7, OriginB, Capacity: random.Next(0, 4) * 10f),
            ];

            SplitResult result = new SplitService().Split(tour, matrix, vehicles, extraRouteCapacity: 20f);
            float expected = BruteForce(matrix, tour, vehicles, extraCapacity: 20f);

            Assert.True(Math.Abs(expected - result.Fitness) < 0.01f,
                $"Tentativa {trial}: força bruta {expected}, DP {result.Fitness}.");
        }
    }

    [Fact]
    public void ContextoValidoEmToledo_CustoFinitoEPlanoRestanteRepresentavel()
    {
        ReoptimizationContext context = ToledoContextAfterEvent(out _);
        var split = new SplitService();
        float extraCapacity = context.Snapshot.VehicleCapacity;

        // Com o tour formado pelos planos restantes, a DP não pode ficar pior que mantê-los.
        int[] planTour = context.Snapshot.ActiveVehicles.SelectMany(v => v.RemainingPlan).ToArray();
        SplitResult fromPlan = split.Split(planTour, context.Matrix, context.SplitVehicles, extraCapacity);

        Assert.Equal(0, fromPlan.ExtraRouteCount);
        Assert.True(fromPlan.Fitness <= KeepPlanCost(context) + 0.01f);

        // Para qualquer ordem dos pendentes o custo tem que ser finito.
        var random = new Random(3);
        for (int t = 0; t < 50; t++)
        {
            int[] tour = planTour.OrderBy(_ => random.Next()).ToArray();
            SplitResult result = split.Split(tour, context.Matrix, context.SplitVehicles, extraCapacity);

            Assert.True(float.IsFinite(result.Fitness));
            Assert.All(result.Routes, route => Assert.True(float.IsFinite(route.TravelTimeSeconds)));
        }
    }

    [Fact]
    public void ContextoEmToledo_VeiculosNaOrdemDasRotasDoPlano()
    {
        ReoptimizationContext context = ToledoContextAfterEvent(out IReadOnlyList<int> activeInPlanOrder);

        Assert.True(activeInPlanOrder.Count > 10, $"Evento deveria pegar a frota em operacao ({activeInPlanOrder.Count} ativos).");
        Assert.Equal(activeInPlanOrder, context.Snapshot.ActiveVehicles.Select(v => v.VehicleId));
        Assert.Equal(activeInPlanOrder, context.SplitVehicles.Select(v => v.VehicleId));
    }


    private static SplitResult Split(int[] tour, params SplitVehicle[] vehicles) =>
        new SplitService().Split(tour, Line, vehicles, extraRouteCapacity: 30f);

    private static SplitVehicle Vehicle(int id, int origin, float capacity) => new(id, origin, capacity);

    /// <summary>
    /// Plano despachado pelo Split clássico num tour aleatório, evento no meio da operação e
    /// contexto no grafo penalizado. Devolve também os ids que deveriam seguir ativos, na ordem do plano.
    /// </summary>
    private ReoptimizationContext ToledoContextAfterEvent(out IReadOnlyList<int> activeInPlanOrder)
    {
        CostMatrix matrix = toledo.Matrix;
        int fleetSize = toledo.MinVehicles + 20;
        var random = new Random(99);

        var plan = new Chromosome(Enumerable.Range(1, matrix.Size - 1).OrderBy(_ => random.Next()).ToArray());
        new SplitService().Evaluate(plan, matrix, fleetSize, ToledoFixture.Capacity);

        RouteTimeline[] timelines = plan.Routes.Select(route => RouteTimeline.Build(route, toledo.Graph, matrix)).ToArray();
        double eventTime = timelines.Select(timeline => timeline.TotalTimeSeconds).Order().ElementAt(timelines.Length / 2) / 2;

        activeInPlanOrder = timelines
            .Where(timeline => timeline.AnchorAfterEvent(eventTime).RemainingClientIndices.Count > 0)
            .Select(timeline => timeline.VehicleIndex)
            .ToArray();

        var initial = ReoptimizationSnapshot.Initial(matrix, fleetSize, ToledoFixture.Capacity);
        var snapshot = ReoptimizationSnapshot.FromExecutingPlan(initial, timelines, eventTime);
        DirectedGraph penalized = new RoadEventApplier().Apply(toledo.Graph, CentralCongestion(toledo.Graph));

        return ReoptimizationContext.Build(snapshot, penalized, matrix.Points, new CostMatrixService());
    }

    private static RoadEvent CentralCongestion(DirectedGraph graph)
    {
        Node[] nodes = Enumerable.Range(0, graph.NodeCount).Select(graph.GetNode).ToArray();
        double minLat = nodes.Min(n => n.Latitude), maxLat = nodes.Max(n => n.Latitude);
        double minLon = nodes.Min(n => n.Longitude), maxLon = nodes.Max(n => n.Longitude);
        double latPad = (maxLat - minLat) / 4, lonPad = (maxLon - minLon) / 4;

        return new RoadEvent(minLat + latPad, maxLat - latPad, minLon + lonPad, maxLon - lonPad, PenaltyFactor: 3f);
    }

    // Cada veículo ativo cumpre o seu plano restante a partir da origem e volta ao depósito.
    private static float KeepPlanCost(ReoptimizationContext context)
    {
        float total = 0f;
        for (int v = 0; v < context.SplitVehicles.Count; v++)
        {
            int previous = context.SplitVehicles[v].OriginIndex;
            foreach (int client in context.Snapshot.ActiveVehicles[v].RemainingPlan)
            {
                total += context.Matrix.GetTravelTime(previous, client);
                previous = client;
            }

            total += context.Matrix.GetTravelTime(previous, SplitService.DepotIndex);
        }

        return total;
    }

    /// <summary>
    /// Enumera todas as partições válidas: um corte para o 1º veículo, um para o 2º, e todas as
    /// composições do sufixo em rotas extras.
    /// </summary>
    private static float BruteForce(CostMatrix matrix, int[] tour, SplitVehicle[] vehicles, float extraCapacity)
    {
        float best = float.PositiveInfinity;

        for (int a = 0; a <= tour.Length; a++)
        {
            for (int b = a; b <= tour.Length; b++)
            {
                float cost = SegmentCost(matrix, tour, 0, a, vehicles[0].OriginIndex, vehicles[0].Capacity)
                           + SegmentCost(matrix, tour, a, b, vehicles[1].OriginIndex, vehicles[1].Capacity)
                           + BestExtras(matrix, tour, b, extraCapacity);
                best = Math.Min(best, cost);
            }
        }

        return best;
    }

    private static float BestExtras(CostMatrix matrix, int[] tour, int start, float capacity)
    {
        if (start == tour.Length)
        {
            return 0f;
        }

        float best = float.PositiveInfinity;
        for (int end = start + 1; end <= tour.Length; end++)
        {
            float segment = SegmentCost(matrix, tour, start, end, SplitService.DepotIndex, capacity);
            best = Math.Min(best, segment + Penalty + BestExtras(matrix, tour, end, capacity));
        }

        return best;
    }

    private static float SegmentCost(CostMatrix matrix, int[] tour, int start, int end, int origin, float capacity)
    {
        float load = tour[start..end].Sum(client => matrix.Points[client].Demand);
        if (load > capacity)
        {
            return float.PositiveInfinity;
        }

        float time = 0f;
        int previous = origin;
        for (int i = start; i < end; i++)
        {
            time += matrix.GetTravelTime(previous, tour[i]);
            previous = tour[i];
        }

        return time + matrix.GetTravelTime(previous, SplitService.DepotIndex);
    }

    private static CostMatrix LineMatrix(double[] positions, int clientCount)
    {
        int size = positions.Length;
        float[] times = new float[size * size];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                times[(i * size) + j] = (float)Math.Abs(positions[i] - positions[j]);
            }
        }

        return new CostMatrix(Points(clientCount, _ => 10), size, times, times);
    }

    // Depósito, 4 clientes e 2 origens com tempos assimétricos aleatórios.
    private static CostMatrix RandomMatrix(Random random)
    {
        const int size = 7;
        float[] times = new float[size * size];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                times[(i * size) + j] = i == j ? 0f : (float)(1 + (random.NextDouble() * 99));
            }
        }

        return new CostMatrix(Points(4, _ => random.Next(1, 3) * 5), size, times, times);
    }

    private static LogisticPoint[] Points(int clientCount, Func<int, int> demandOf) =>
        Enumerable.Range(0, clientCount + 1)
            .Select(i => new LogisticPoint(i, 0, 0, i == 0 ? 0 : demandOf(i), i))
            .ToArray();
}
