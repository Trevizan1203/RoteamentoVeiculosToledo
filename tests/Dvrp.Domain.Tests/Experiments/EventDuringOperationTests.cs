using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Reoptimization;
using E = Dvrp.Playground.Runners.DynamicReoptimizationExperiment;

namespace Dvrp.Domain.Tests.Experiments;

/// <summary>
/// Experimento com evento durante a operação numa instância pequena com três grupos de clientes
/// (demanda 10, Q = 30), todos em mão única:
///   A: 0 -> 1 -> 2 -> 3 -> 0   (chegadas 100, 150, 200 s; volta em 400 s)
///   B: 0 -> 4 -> 5 -> 6 -> 0   (chegadas 120, 170, 220 s; volta em 440 s)
///   C: 0 -> 7 -> 0             (chegada 30 s; volta em 60 s)
/// O plano ótimo é [1, 2, 3], [4, 5, 6] e [7]. Em f = 0,5 (t = 50 s) A e B continuam ativos, com
/// âncoras em 100 e 120 s, e C já atendeu o cliente 7 e está voltando, chegando em 60 s.
/// O evento penaliza o grupo A. O nó 8 só se liga ao depósito e não está em nenhum caminho mínimo,
/// então um evento nele muda arestas mas não muda a matriz.
/// </summary>
public class EventDuringOperationTests
{
    private const int Seed = 11007;
    private const int PopulationSize = 50; // com 20 o AG estagna num ótimo local desta instância
    private const int MaxGenerations = 200;

    // Posição na linha: depósito em 0, A entre 10 e 20, B entre -12 e -22, C em 3. O nó 8 não tem
    // cliente e fica ao sul do depósito.
    private static readonly double[] X = [0, 10, 15, 20, -12, -17, -22, 3, 0];

    // Os dois lados somam os mesmos tempos em ordens diferentes, o que pode mudar as últimas casas.
    private const float CostTolerance = 1e-3f;

    private static readonly DirectedGraph Graph = BuildGraph();
    private static readonly E.InitialPlan Plan = BuildPlan();
    private static readonly RoadEvent CongestionOnA = CongestionAround(NodeAt(2));
    private static readonly RoadEvent CongestionOnDeadEnd = CongestionAround(NodeAt(8));

    [Fact]
    public void TtvTotal_EhExecutadoAteAAncoraMaisRestanteMaisConcluidos()
    {
        E.EventRunResult run = Run(servedFraction: 0.5);

        // Âncoras dos ativos: A em 100 s, B em 120 s. C concluído: 60 s, 600 m.
        Assert.Equal(2, run.ActiveCount);
        foreach (E.StrategyOutcome strategy in run.Strategies)
        {
            Assert.Equal(100.0 + 120.0 + strategy.TtvPos + 60.0, strategy.TtvTotal, precision: 3);
            Assert.Equal(1000.0 + 1200.0 + strategy.DtpPos + 600.0, strategy.DtpTotal, precision: 3);
        }
    }

    [Fact]
    public void Estatica_TrechoPelaAreaPenalizadaNaoFicaMaisBarato()
    {
        E.EventRunResult run = Run(servedFraction: 0.5);
        E.StrategyOutcome staticRun = run.Strategies.Single(s => s.Strategy == E.StaticStrategy);

        // O que falta aos ativos no grafo sem evento: A de 100 a 400 s e B de 120 a 440 s.
        double before = (400.0 - 100.0) + (440.0 - 120.0);

        Assert.True(staticRun.TtvPos > before, $"Estatica pos-evento {staticRun.TtvPos} deveria superar {before}.");
    }

    [Fact]
    public void InstanteDoEvento_AtingeAFracaoDeClientesAtendidos()
    {
        // n = 7: f = 0,5 -> floor(3,5) = 3 atendidos (1, 4 e 7).
        Assert.Equal(7 - 3, Run(servedFraction: 0.5).PendingCount);

        // f = 0,25 -> floor(1,75) = 1, mas 1, 4 e 7 passam a contar juntos logo após a partida,
        // então o primeiro instante possível já tem 3 atendidos.
        Assert.Equal(7 - 3, Run(servedFraction: 0.25).PendingCount);
    }

    [Fact]
    public void MenosDeDoisPendentes_CasoTrivialSemAg()
    {
        // f = 0,9 -> 6 atendidos, só o cliente 6 pendente.
        E.EventRunResult run = Run(servedFraction: 0.9);

        Assert.True(run.IsTrivial);
        Assert.Equal(1, run.PendingCount);
        Assert.All(run.Strategies, strategy => Assert.Null(strategy.GenerationsRun));

        E.StrategyOutcome staticRun = run.Strategies[0];
        Assert.All(run.Strategies, strategy => Assert.Equal(staticRun with { Strategy = strategy.Strategy }, strategy));
    }

    [Fact]
    public void FitnessInicial_ReaproveitamentoEntrePlanoVigenteEFinal()
    {
        E.EventRunResult run = Run(servedFraction: 0.5);
        E.StrategyOutcome staticRun = run.Strategies.Single(s => s.Strategy == E.StaticStrategy);
        E.StrategyOutcome reuse = run.Strategies.Single(s => s.Strategy == E.ReuseStrategy);
        E.StrategyOutcome restart = run.Strategies.Single(s => s.Strategy == E.RestartStrategy);

        Assert.Null(staticRun.InitialFitness);
        Assert.NotNull(reuse.InitialFitness);
        Assert.NotNull(restart.InitialFitness);

        // Reaproveitamento: final <= melhor da população adaptada <= plano vigente.
        Assert.True(reuse.Fitness <= reuse.InitialFitness + CostTolerance);
        Assert.True(reuse.InitialFitness <= staticRun.TtvPos + CostTolerance);

        // Reinício: final <= melhor da população aleatória.
        Assert.True(restart.Fitness <= restart.InitialFitness + CostTolerance);
    }

    [Fact]
    public void AfetaMatriz_SoQuandoOEventoMudaAlgumCaminhoMinimo()
    {
        Assert.True(Run(servedFraction: 0.5).AffectsMatrix);
        Assert.False(E.RunEventDuringOperation(Graph, Plan, CongestionOnDeadEnd, 0.5, PopulationSize, MaxGenerations).AffectsMatrix);
    }


    private static E.EventRunResult Run(double servedFraction) =>
        E.RunEventDuringOperation(Graph, Plan, CongestionOnA, servedFraction, PopulationSize, MaxGenerations);

    private static E.InitialPlan BuildPlan()
    {
        DeliveryPoint[] points = Enumerable.Range(0, 8)
            .Select(i => new DeliveryPoint(i, NodeAt(i).Latitude, NodeAt(i).Longitude, Demand: i == 0 ? 0 : 10))
            .ToArray();
        CostMatrix matrix = new CostMatrixService().BuildMatrix(Graph, points);

        E.InitialPlan plan = E.BuildInitialPlan(Graph, matrix, fleetSize: 4, vehicleCapacity: 30, Seed, PopulationSize, MaxGenerations);

        // Pré-condição da instância.
        Assert.Equal([[1, 2, 3], [4, 5, 6], [7]], plan.Ga.Best.Routes.Select(r => r.ClientIndices.ToArray()).OrderBy(c => c[0] == 7 ? 99 : c[0]));
        return plan;
    }

    private static Node NodeAt(int index) => new(index == 8 ? -24.73 : -24.72, -53.74 + (X[index] * 0.0001));

    private static RoadEvent CongestionAround(Node center) =>
        new(center.Latitude - 0.001, center.Latitude + 0.001, center.Longitude - 0.00055, center.Longitude + 0.00055, PenaltyFactor: 2f);

    private static DirectedGraph BuildGraph()
    {
        Edge To(int from, int to) =>
            new(to, (float)(Math.Abs(X[from] - X[to]) * 10), (float)(Math.Abs(X[from] - X[to]) * 100), SpeedLimitKmh: 60f);

        Edge[][] adjacency =
        [
            [To(0, 1), To(0, 4), To(0, 7), new Edge(8, 60f, 600f, SpeedLimitKmh: 60f)],
            [To(1, 0), To(1, 2)],
            [To(2, 3)],
            [To(3, 0)],
            [To(4, 0), To(4, 5)],
            [To(5, 6)],
            [To(6, 0)],
            [To(7, 0)],
            [new Edge(0, 60f, 600f, SpeedLimitKmh: 60f)],
        ];

        // Velocidade máxima alta para a heurística do A* não superestimar o tempo.
        return new DirectedGraph(Enumerable.Range(0, X.Length).Select(NodeAt).ToArray(), adjacency, maxSpeedLimitKmh: 360f);
    }
}
