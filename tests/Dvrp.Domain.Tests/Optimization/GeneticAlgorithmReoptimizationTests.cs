using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Reoptimization;
using Dvrp.Domain.Tests.Oracles;

namespace Dvrp.Domain.Tests.Optimization;

/// <summary>
/// AG sobre o contexto de reotimização.
///
/// Instância: depósito no nó 0 e dois grupos de 3 clientes (demanda 10, Q = 30), em mão única:
/// 0 -> 1 -> 2 -> 3 -> 0 e 0 -> 4 -> 5 -> 6 -> 0, com 100 s até o primeiro cliente, 50 s entre
/// clientes e 200 s de volta. O plano ótimo é [1, 2, 3] e [4, 5, 6], com os veículos cheios. No
/// evento em t = 90 s cada veículo termina a aresta até o primeiro cliente, então fica com 2
/// clientes e capacidade residual igual à demanda que falta.
/// </summary>
public class GeneticAlgorithmReoptimizationTests(ToledoFixture toledo) : IClassFixture<ToledoFixture>
{
    private const int Seed = 11007;
    private const int FleetSize = 3;
    private const int Capacity = 30;
    private const int PopulationSize = 20;
    private const int MaxGenerations = 200;
    private const double EventTime = 90.0;

    // Posição de cada nó na linha: depósito em 0 e os grupos entre 10 e 20 e entre -10 e -20.
    private static readonly double[] X = [0, 10, 15, 20, -10, -15, -20];
    private static readonly DirectedGraph Graph = BuildClusterGraph();
    private static readonly CostMatrix Matrix = new CostMatrixService().BuildMatrix(Graph, DeliveryPoints());

    // Com o contexto Initial, o AG tem que reproduzir o AG anterior.

    [Fact]
    public void ContextoInitial_ReproduzOAgAnteriorEmToledo()
    {
        AssertSameAsLegacy(toledo.Matrix, toledo.MinVehicles + 20, (int)ToledoFixture.Capacity, populationSize: 100, maxGenerations: 1000);
    }

    [Fact]
    public void ContextoInitial_ReproduzOAgAnteriorNaInstanciaPequena()
    {
        AssertSameAsLegacy(Matrix, FleetSize, Capacity, PopulationSize, MaxGenerations);
    }

    [Fact]
    public void InitializePopulation_ReproduzAPopulacaoAnterior()
    {
        var legacy = new LegacyGeneticAlgorithmEngine(new LegacySplitService(), new GeneticOperators(), new Random(Seed))
            .InitializePopulation(100, toledo.Matrix.Size - 1, toledo.Matrix, toledo.MinVehicles + 20, (int)ToledoFixture.Capacity);
        var current = NewEngine()
            .InitializePopulation(100, toledo.Matrix.Size - 1, toledo.Matrix, toledo.MinVehicles + 20, (int)ToledoFixture.Capacity);

        AssertSamePopulation(legacy, current);
    }

    // Estratégias dinâmicas na instância pequena.

    [Fact]
    public void Adaptacao_PreservaTamanhoEOrdemRelativaDosPendentes()
    {
        (GaRunResult previous, ReoptimizationContext context) = PlanAndEvent();
        int[] pending = context.Snapshot.PendingClients.ToArray();

        List<Chromosome> adapted = NewEngine().AdaptPopulation(previous.FinalPopulation, context);

        Assert.Equal(previous.FinalPopulation.Count, adapted.Count);
        for (int i = 0; i < adapted.Count; i++)
        {
            Assert.Equal(pending, adapted[i].Genes.Order());
            Assert.Equal(previous.FinalPopulation[i].Genes.Where(pending.Contains), adapted[i].Genes);
        }
    }

    [Fact]
    public void MelhorAnteriorAdaptado_ReproduzOPlanoVigenteRestritoAosPendentes()
    {
        (GaRunResult previous, ReoptimizationContext context) = PlanAndEvent();

        Chromosome adaptedBest = NewEngine().AdaptPopulation([previous.Best], context)[0];

        Assert.Equal(0, adaptedBest.ExtraRouteCount);
        Assert.True(float.IsFinite(adaptedBest.Fitness));
        Assert.Equal(context.Snapshot.ActiveVehicles.Count, adaptedBest.Routes.Count);
        foreach (VehicleState vehicle in context.Snapshot.ActiveVehicles)
        {
            VehicleRoute route = Assert.Single(adaptedBest.Routes, r => r.VehicleIndex == vehicle.VehicleId);
            Assert.Equal(vehicle.RemainingPlan, route.ClientIndices);
        }
    }

    [Fact]
    public void Reaproveitamento_NuncaPiorQueOPlanoVigenteRestrito()
    {
        (GaRunResult previous, ReoptimizationContext context) = PlanAndEvent();
        float currentPlanCost = NewEngine().AdaptPopulation([previous.Best], context)[0].Fitness;

        GaRunResult reuse = RunReuse(previous, context);

        Assert.True(reuse.Best.Fitness <= currentPlanCost,
            $"Reaproveitamento terminou em {reuse.Best.Fitness}, pior que o plano vigente ({currentPlanCost}).");
    }

    [Fact]
    public void Reinicio_PopulacaoAleatoriaDosPendentesEDeterministica()
    {
        (_, ReoptimizationContext context) = PlanAndEvent();

        List<Chromosome> first = NewEngine().CreateRandomPopulation(context, PopulationSize);
        List<Chromosome> second = NewEngine().CreateRandomPopulation(context, PopulationSize);

        Assert.Equal(PopulationSize, first.Count);
        Assert.All(first, individual => Assert.Equal(context.Snapshot.PendingClients, individual.Genes.Order()));
        Assert.Equal(first.Select(c => c.Genes), second.Select(c => c.Genes));
    }

    [Fact]
    public void AgPosEvento_AtendeCadaPendenteUmaVezComOsVeiculosAtivos()
    {
        (GaRunResult previous, ReoptimizationContext context) = PlanAndEvent();
        int[] activeIds = context.Snapshot.ActiveVehicles.Select(v => v.VehicleId).ToArray();

        GaRunResult restart = NewEngine().Run(context, PopulationSize, MaxGenerations);
        GaRunResult reuse = RunReuse(previous, context);

        foreach (Chromosome best in new[] { restart.Best, reuse.Best })
        {
            Assert.Equal(0, best.ExtraRouteCount);
            Assert.Equal(context.Snapshot.PendingClients, best.Routes.SelectMany(r => r.ClientIndices).Order());
            Assert.All(best.Routes, route => Assert.Contains(route.VehicleIndex, activeIds));
        }
    }


    private static GeneticAlgorithmEngine NewEngine() =>
        new(new SplitService(), new GeneticOperators(), new Random(Seed));

    private static GaRunResult RunReuse(GaRunResult previous, ReoptimizationContext context)
    {
        GeneticAlgorithmEngine engine = NewEngine();
        List<Chromosome> adapted = engine.AdaptPopulation(previous.FinalPopulation, context);
        return engine.Run(context, PopulationSize, MaxGenerations, initialPopulation: adapted);
    }

    /// <summary>
    /// Gera o plano inicial, aplica um congestionamento no grupo 1, 2, 3 em t = 90 s e monta o
    /// contexto do problema restante.
    /// </summary>
    private static (GaRunResult Previous, ReoptimizationContext Context) PlanAndEvent()
    {
        GaRunResult previous = NewEngine().Run(Matrix.Size - 1, Matrix, FleetSize, Capacity, PopulationSize, MaxGenerations);

        // A instância só funciona se o AG chegar ao plano ótimo.
        Assert.Equal([[1, 2, 3], [4, 5, 6]], previous.Best.Routes.Select(r => r.ClientIndices.ToArray()).OrderBy(c => c[0]));

        RouteTimeline[] timelines = previous.Best.Routes.Select(r => RouteTimeline.Build(r, Graph, Matrix)).ToArray();
        var snapshot = ReoptimizationSnapshot.FromExecutingPlan(
            ReoptimizationSnapshot.Initial(Matrix, FleetSize, Capacity), timelines, EventTime);

        Node clusterNode = NodeAt(2);
        DirectedGraph penalized = new RoadEventApplier().Apply(Graph, new RoadEvent(
            clusterNode.Latitude - 0.001, clusterNode.Latitude + 0.001,
            clusterNode.Longitude - 0.00055, clusterNode.Longitude + 0.00055, PenaltyFactor: 2f));

        return (previous, ReoptimizationContext.Build(snapshot, penalized, Matrix.Points, new CostMatrixService()));
    }

    private static void AssertSameAsLegacy(CostMatrix matrix, int maxVehicles, int capacity, int populationSize, int maxGenerations)
    {
        int clientCount = matrix.Size - 1;
        GaRunResult legacy = new LegacyGeneticAlgorithmEngine(new LegacySplitService(), new GeneticOperators(), new Random(Seed))
            .Run(clientCount, matrix, maxVehicles, capacity, populationSize, maxGenerations);
        GaRunResult current = NewEngine().Run(clientCount, matrix, maxVehicles, capacity, populationSize, maxGenerations);

        Assert.Equal(legacy.GenerationsRun, current.GenerationsRun);
        Assert.Equal(legacy.StopReason, current.StopReason);
        Assert.Equal(legacy.Best.Fitness, current.Best.Fitness);
        Assert.Equal(legacy.Best.TotalTravelTimeSeconds, current.Best.TotalTravelTimeSeconds);
        Assert.Equal(legacy.Best.TotalDistanceMeters, current.Best.TotalDistanceMeters);
        Assert.Equal(legacy.Best.Genes, current.Best.Genes);
        Assert.Equal(
            legacy.Best.Routes.Select(r => r.ClientIndices.ToArray()),
            current.Best.Routes.Select(r => r.ClientIndices.ToArray()));
        AssertSamePopulation(legacy.FinalPopulation, current.FinalPopulation);
    }

    private static void AssertSamePopulation(IReadOnlyList<Chromosome> expected, IReadOnlyList<Chromosome> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Genes, actual[i].Genes);
            Assert.Equal(expected[i].Fitness, actual[i].Fitness);
        }
    }

    private static Node NodeAt(int index) => new(-24.72, -53.74 + (X[index] * 0.0001));

    private static DirectedGraph BuildClusterGraph()
    {
        Edge To(int from, int to) =>
            new(to, (float)(Math.Abs(X[from] - X[to]) * 10), (float)(Math.Abs(X[from] - X[to]) * 100), SpeedLimitKmh: 60f);

        Edge[][] adjacency =
        [
            [To(0, 1), To(0, 4)],
            [To(1, 0), To(1, 2)],
            [To(2, 3)],
            [To(3, 0)],
            [To(4, 0), To(4, 5)],
            [To(5, 6)],
            [To(6, 0)],
        ];

        // Velocidade máxima alta para a heurística do A* não superestimar o tempo.
        return new DirectedGraph(Enumerable.Range(0, X.Length).Select(NodeAt).ToArray(), adjacency, maxSpeedLimitKmh: 360f);
    }

    private static DeliveryPoint[] DeliveryPoints() =>
        Enumerable.Range(0, X.Length)
            .Select(i => new DeliveryPoint(i, NodeAt(i).Latitude, NodeAt(i).Longitude, Demand: i == 0 ? 0 : 10))
            .ToArray();
}
