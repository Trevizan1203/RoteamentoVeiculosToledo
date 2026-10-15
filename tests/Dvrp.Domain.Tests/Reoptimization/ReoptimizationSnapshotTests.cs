using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Reoptimization;

namespace Dvrp.Domain.Tests.Reoptimization;

/// <summary>
/// Malha em linha com 5 nós (o 0 é o depósito) e arestas de 60 s nos dois sentidos, com um cliente
/// em cada nó de 1 a 4. No plano padrão:
///   veículo 0, rota [1, 2]: chega ao cliente 1 em 60 s, ao 2 em 120 s e volta em 240 s;
///   veículo 1, rota [4, 3]: chega ao cliente 4 em 240 s, ao 3 em 300 s e volta em 480 s;
///   veículo 2 sem rota, e o veículo 3 nem aparece no plano.
/// </summary>
public class ReoptimizationSnapshotTests
{
    private const int FleetSize = 4;
    private const float Capacity = 100f;
    private const double EdgeSeconds = 60.0;

    private static readonly DirectedGraph Graph = BuildLineGraph(nodeCount: 5);
    private static readonly CostMatrix Matrix = BuildMatrix(demands: [0, 10, 20, 30, 15]);

    [Fact]
    public void Initial_PoeAFrotaInteiraNoDepositoComTodosOsClientesPendentes()
    {
        var snapshot = ReoptimizationSnapshot.Initial(Matrix, FleetSize, Capacity);

        Assert.True(snapshot.IsInitial);
        Assert.Equal(0.0, snapshot.EventTimeSeconds);
        Assert.Equal([0, 1, 2, 3], snapshot.ActiveVehicles.Select(v => v.VehicleId));
        Assert.All(snapshot.ActiveVehicles, vehicle =>
        {
            Assert.Equal(0, vehicle.OriginNode);
            Assert.Equal(0.0, vehicle.AvailableAtSeconds);
            Assert.Equal(Capacity, vehicle.ResidualCapacity);
            Assert.Empty(vehicle.RemainingPlan);
        });
        Assert.Equal([1, 2, 3, 4], snapshot.PendingClients);
        Assert.Empty(snapshot.ServedClients);
        Assert.Empty(snapshot.FinishedVehicleIds);
    }

    [Fact]
    public void EventoAposOFimDaOperacao_TodosConcluidosENenhumPendente()
    {
        var snapshot = SnapshotOfDefaultPlanAt(1000.0);

        Assert.Empty(snapshot.ActiveVehicles);
        Assert.Empty(snapshot.PendingClients);
        Assert.Equal([1, 2, 3, 4], snapshot.ServedClients);
        Assert.Equal([0, 1], snapshot.FinishedVehicleIds);
    }

    [Fact]
    public void EventoNoMeioDaOperacao_AncoraOsVeiculosEPreservaAOrdemDoPlano()
    {
        // t = 30: os dois veículos estão no meio da aresta 0 -> 1 e são ancorados no nó 1 (t = 60).
        var snapshot = SnapshotOfDefaultPlanAt(30.0);

        Assert.Equal([1], snapshot.ServedClients);
        Assert.Equal([2, 3, 4], snapshot.PendingClients);
        Assert.Empty(snapshot.FinishedVehicleIds);

        VehicleState first = Assert.Single(snapshot.ActiveVehicles, v => v.VehicleId == 0);
        Assert.Equal(1, first.OriginNode);
        Assert.Equal(EdgeSeconds, first.AvailableAtSeconds);
        Assert.Equal(90f, first.ResidualCapacity);
        Assert.Equal([2], first.RemainingPlan);

        VehicleState second = Assert.Single(snapshot.ActiveVehicles, v => v.VehicleId == 1);
        Assert.Equal(1, second.OriginNode);
        Assert.Equal(100f, second.ResidualCapacity);
        Assert.Equal([4, 3], second.RemainingPlan);
    }

    [Fact]
    public void VeiculosAtivos_SeguemAOrdemDasRotasDoPlano()
    {
        // O veículo 1 vem antes do 0 no plano; a ordem do plano vale mais que o id.
        RouteTimeline[] plan = [Timeline(vehicleIndex: 1, [4, 3]), Timeline(vehicleIndex: 0, [1, 2])];

        var snapshot = ReoptimizationSnapshot.FromExecutingPlan(Initial(), plan, eventTimeSeconds: 30.0);

        Assert.Equal([1, 0], snapshot.ActiveVehicles.Select(v => v.VehicleId));
    }

    [Fact]
    public void VeiculoComRotaVazia_NaoApareceNoSnapshot()
    {
        var snapshot = SnapshotOfDefaultPlanAt(30.0);

        Assert.DoesNotContain(snapshot.ActiveVehicles, v => v.VehicleId is 2 or 3);
        Assert.DoesNotContain(2, snapshot.FinishedVehicleIds);
        Assert.DoesNotContain(3, snapshot.FinishedVehicleIds);
    }

    [Fact]
    public void VeiculoQueAtendeuTodosMasAindaVoltaAoDeposito_EConcluido()
    {
        // t = 90: o veículo 0 conclui a aresta 1 -> 2 e chega ao cliente 2, o seu último.
        var snapshot = SnapshotOfDefaultPlanAt(90.0);

        Assert.Contains(0, snapshot.FinishedVehicleIds);
        Assert.DoesNotContain(snapshot.ActiveVehicles, v => v.VehicleId == 0);
        Assert.Equal([1, 2], snapshot.ServedClients);
    }

    [Fact]
    public void PlanoSobrecarregado_LancaExcecaoDoInvariante4()
    {
        var initial = ReoptimizationSnapshot.Initial(Matrix, fleetSize: 2, vehicleCapacity: 50f);
        RouteTimeline[] overloadedPlan =
        [
            Timeline(vehicleIndex: 0, [1, 2, 3]), // demanda 60 > Q = 50
            Timeline(vehicleIndex: 1, [4]),
        ];

        var error = Assert.Throws<InvalidOperationException>(
            () => ReoptimizationSnapshot.FromExecutingPlan(initial, overloadedPlan, eventTimeSeconds: 30.0));

        Assert.Contains("Invariante 4", error.Message);
        Assert.Contains("inviavel", error.Message);
    }

    [Fact]
    public void ClienteAlcancadoExatamenteNoInstanteDoEvento_SegueARegraDoEventAnchor()
    {
        // t = 60: o veículo 0 está exatamente sobre o nó do cliente 1.
        RouteTimeline[] plan = DefaultPlan();
        EventAnchor anchor = plan[0].AnchorAfterEvent(EdgeSeconds);

        var snapshot = ReoptimizationSnapshot.FromExecutingPlan(Initial(), plan, EdgeSeconds);

        Assert.Equal([1], anchor.ServedClientIndices);
        Assert.Equal(anchor.ServedClientIndices, snapshot.ServedClients);
        Assert.Contains(2, snapshot.PendingClients);
    }

    [Fact]
    public void PlanoJaReotimizado_AcumulaAtendidosECapacidadeDoSnapshotAnterior()
    {
        // Primeiro evento em t = 30, quando o veículo 0 já atendeu o cliente 1. Na reotimização o
        // veículo 0 ficou com [3, 2], o 1 com [4] e o 2 continuou sem rota.
        var afterFirstEvent = SnapshotOfDefaultPlanAt(30.0);

        // Segundo evento em t = 400, sobre o plano reotimizado.
        EventAnchor[] reoptimizedPlanAnchors =
        [
            Anchor(vehicleIndex: 0, node: 3, served: [3], remaining: [2]),
            Anchor(vehicleIndex: 1, node: 4, served: [4], remaining: []),
            Anchor(vehicleIndex: 2, node: 0, served: [], remaining: []),
        ];

        var snapshot = ReoptimizationSnapshot.FromAnchors(afterFirstEvent, reoptimizedPlanAnchors, 400.0);

        Assert.Equal([1, 3, 4], snapshot.ServedClients);
        Assert.Equal([2], snapshot.PendingClients);
        Assert.Equal([1], snapshot.FinishedVehicleIds);

        VehicleState active = Assert.Single(snapshot.ActiveVehicles);
        Assert.Equal(0, active.VehicleId);
        Assert.Equal(3, active.OriginNode);
        Assert.Equal(60f, active.ResidualCapacity); // 100 - 10 (evento 1) - 30 (evento 2)
        Assert.Equal([2], active.RemainingPlan);
    }

    [Fact]
    public void PlanoJaReotimizado_NaoAcionaVeiculoQueNaoFoiDespachado()
    {
        var afterFirstEvent = SnapshotOfDefaultPlanAt(30.0);

        EventAnchor[] anchors =
        [
            Anchor(vehicleIndex: 0, node: 1, served: [], remaining: [2]),
            Anchor(vehicleIndex: 1, node: 1, served: [], remaining: [4]),
            Anchor(vehicleIndex: 2, node: 0, served: [], remaining: [3]),
        ];

        Assert.Throws<InvalidOperationException>(
            () => ReoptimizationSnapshot.FromAnchors(afterFirstEvent, anchors, 100.0));
    }

    [Fact]
    public void PlanoJaReotimizado_VeiculoAtivoQueFicouSemRota_EConcluido()
    {
        // Depois do primeiro evento, todos os pendentes foram para o veículo 1.
        var afterFirstEvent = SnapshotOfDefaultPlanAt(30.0);

        EventAnchor[] anchors =
        [
            Anchor(vehicleIndex: 0, node: 1, served: [], remaining: []),
            Anchor(vehicleIndex: 1, node: 2, served: [], remaining: [2, 4, 3]),
        ];

        var snapshot = ReoptimizationSnapshot.FromAnchors(afterFirstEvent, anchors, 100.0);

        Assert.Equal([0], snapshot.FinishedVehicleIds);
        Assert.Equal([1], snapshot.ActiveVehicles.Select(v => v.VehicleId));
    }

    [Fact]
    public void VeiculoAtivoDisponivelAntesDoEvento_LancaExcecaoDoInvariante6()
    {
        var afterFirstEvent = SnapshotOfDefaultPlanAt(30.0);

        EventAnchor[] anchors =
        [
            Anchor(vehicleIndex: 0, node: 1, served: [], remaining: [2], anchorTime: 90.0),
            Anchor(vehicleIndex: 1, node: 1, served: [], remaining: [4, 3]),
        ];

        var error = Assert.Throws<InvalidOperationException>(
            () => ReoptimizationSnapshot.FromAnchors(afterFirstEvent, anchors, 100.0));

        Assert.Contains("Invariante 6", error.Message);
    }

    [Fact]
    public void Initial_RejeitaFrotaOuCapacidadeNaoPositiva()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReoptimizationSnapshot.Initial(Matrix, 0, Capacity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReoptimizationSnapshot.Initial(Matrix, FleetSize, 0f));
    }


    private static ReoptimizationSnapshot Initial() =>
        ReoptimizationSnapshot.Initial(Matrix, FleetSize, Capacity);

    private static ReoptimizationSnapshot SnapshotOfDefaultPlanAt(double t) =>
        ReoptimizationSnapshot.FromExecutingPlan(Initial(), DefaultPlan(), t);

    private static RouteTimeline[] DefaultPlan() =>
    [
        Timeline(vehicleIndex: 0, [1, 2]),
        Timeline(vehicleIndex: 1, [4, 3]),
        Timeline(vehicleIndex: 2, []),
    ];

    private static RouteTimeline Timeline(int vehicleIndex, int[] clients)
    {
        float demand = clients.Sum(client => Matrix.Points[client].Demand);
        var route = new VehicleRoute(vehicleIndex, clients, travelTimeSeconds: 0f, distanceMeters: 0f, demand, Capacity);
        return RouteTimeline.Build(route, Graph, Matrix);
    }

    private static EventAnchor Anchor(
        int vehicleIndex, int node, int[] served, int[] remaining, double anchorTime = 500.0) =>
        new(vehicleIndex, node, anchorTime, WaitSeconds: 0.0, served, remaining, IsFinished: false);

    private static DirectedGraph BuildLineGraph(int nodeCount)
    {
        Node[] nodes = Enumerable.Range(0, nodeCount)
            .Select(i => new Node(-24.72 + (i * 0.001), -53.74))
            .ToArray();

        Edge[][] adjacency = Enumerable.Range(0, nodeCount)
            .Select(i => new[] { i - 1, i + 1 }
                .Where(neighbor => neighbor >= 0 && neighbor < nodeCount)
                .Select(neighbor => new Edge(neighbor, (float)EdgeSeconds, DistanceMeters: 500f, SpeedLimitKmh: 30f))
                .ToArray())
            .ToArray();

        return new DirectedGraph(nodes, adjacency, maxSpeedLimitKmh: 30f);
    }

    // Ponto i fica no nó viário i; os custos da matriz não são usados pela RouteTimeline.
    private static CostMatrix BuildMatrix(int[] demands)
    {
        LogisticPoint[] points = demands
            .Select((demand, i) => new LogisticPoint(i, -24.72 + (i * 0.001), -53.74, demand, RoadNodeIndex: i))
            .ToArray();

        int entries = points.Length * points.Length;
        return new CostMatrix(points, new float[entries], new float[entries]);
    }
}
