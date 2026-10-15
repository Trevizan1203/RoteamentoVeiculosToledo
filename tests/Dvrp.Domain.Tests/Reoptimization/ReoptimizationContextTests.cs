using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Pathfinding;
using Dvrp.Domain.Reoptimization;

namespace Dvrp.Domain.Tests.Reoptimization;

/// <summary>
/// Malha de 5 nós: 0 é o depósito, 1 a 3 são clientes e o 4 é só um ponto intermediário.
///   0 -> 1 e 1 -> 0 (60 s), 1 -> 2 (30 s, mão única), 2 -> 3, 3 -> 2, 3 -> 0 e 0 -> 3 (60 s);
///   0 -> 4 -> 2 é um atalho rápido (20 s + 20 s), porém longo (2000 m + 2000 m).
/// No snapshot padrão o veículo 0 já atendeu o cliente 1 e está no nó 4, indo para o 2; o veículo 1
/// está no nó do depósito e ainda tem o cliente 3.
/// </summary>
public class ReoptimizationContextTests
{
    private const int ClientCount = 3;
    private const int Vehicle0Origin = ClientCount + 1; // primeiro índice depois dos clientes

    private static readonly DirectedGraph Graph = BuildGraph();
    private static readonly CostMatrix OriginalMatrix = new CostMatrixService().BuildMatrix(Graph, DeliveryPoints());

    [Fact]
    public void ContextoDoInitial_ReproduzAMatrizAtual()
    {
        var initial = ReoptimizationSnapshot.Initial(OriginalMatrix, fleetSize: 3, vehicleCapacity: 100f);

        ReoptimizationContext context = Build(initial, Graph);

        Assert.Equal(OriginalMatrix.Size, context.Matrix.Size);
        Assert.Equal(OriginalMatrix.Points, context.Matrix.Points);
        for (int i = 0; i < OriginalMatrix.Size; i++)
        {
            for (int j = 0; j < OriginalMatrix.Size; j++)
            {
                Assert.Equal(OriginalMatrix.GetTravelTime(i, j), context.Matrix.GetTravelTime(i, j));
                Assert.Equal(OriginalMatrix.GetDistance(i, j), context.Matrix.GetDistance(i, j));
            }
        }

        Assert.All(context.VehicleOriginIndices, index => Assert.Equal(ReoptimizationContext.DepotIndex, index));
    }

    [Fact]
    public void VeiculoForaDoDeposito_GanhaIndiceExtraComCustosDoCaminhoMinimo()
    {
        ReoptimizationContext context = Build(InOperationSnapshot(), Graph);
        var astar = new AStarPathfinder();

        Assert.Equal(Vehicle0Origin, context.VehicleOriginIndices[0]);

        foreach (int destination in new[] { 0, 2, 3 })
        {
            PathResult expected = astar.FindPath(Graph, 4, OriginalMatrix.Points[destination].RoadNodeIndex);

            Assert.Equal(expected.TotalTravelTimeSeconds, context.Matrix.GetTravelTime(Vehicle0Origin, destination), precision: 3);
            Assert.Equal(expected.TotalDistanceMeters, context.Matrix.GetDistance(Vehicle0Origin, destination), precision: 3);
        }
    }

    [Fact]
    public void OrigemNoNoDoDeposito_UsaOIndiceDoDepositoSemLinhaExtra()
    {
        ReoptimizationContext context = Build(InOperationSnapshot(), Graph);

        Assert.Equal(ReoptimizationContext.DepotIndex, context.VehicleOriginIndices[1]);
        Assert.Equal(ClientCount + 2, context.Matrix.Size); // só a origem do veículo 0 foi anexada
    }

    [Fact]
    public void OrigemNoNoDeUmClientePendente_UsaOIndiceDoCliente()
    {
        // Veículo 1 ancorado no nó do cliente 2 (pendente, a cargo do veículo 0), indo para o 3.
        ReoptimizationSnapshot snapshot = SnapshotWith(
            Anchor(vehicle: 0, node: 4, served: [1], remaining: [2]),
            Anchor(vehicle: 1, node: 2, served: [], remaining: [3]));

        ReoptimizationContext context = Build(snapshot, Graph);

        Assert.Equal(2, context.VehicleOriginIndices[1]);
        Assert.Equal(ClientCount + 2, context.Matrix.Size);
    }

    [Fact]
    public void DoisVeiculosNoMesmoNoNovo_CompartilhamUmUnicoIndiceAnexado()
    {
        ReoptimizationSnapshot snapshot = SnapshotWith(
            Anchor(vehicle: 0, node: 4, served: [1], remaining: [2]),
            Anchor(vehicle: 1, node: 4, served: [], remaining: [3]));

        ReoptimizationContext context = Build(snapshot, Graph);

        Assert.Equal([Vehicle0Origin, Vehicle0Origin], context.VehicleOriginIndices);
        Assert.Equal(ClientCount + 2, context.Matrix.Size); // uma só linha anexada (um só Dijkstra)
    }

    [Fact]
    public void OrigemNoNoDeUmClienteAtendido_GanhaIndiceNovo()
    {
        // Veículo 0 acabou de atender o cliente 1 e está parado no nó dele.
        ReoptimizationSnapshot snapshot = SnapshotWith(
            Anchor(vehicle: 0, node: 1, served: [1], remaining: [2]),
            Anchor(vehicle: 1, node: 0, served: [], remaining: [3]));

        ReoptimizationContext context = Build(snapshot, Graph);

        Assert.Equal(Vehicle0Origin, context.VehicleOriginIndices[0]);
        Assert.False(context.Matrix.IsComputed(1, 2));
        Assert.Equal(30f, context.Matrix.GetTravelTime(Vehicle0Origin, 2));
    }

    [Fact]
    public void LinhasDeAtendidosEColunasDeOrigens_NaoSaoCalculadas()
    {
        ReoptimizationContext context = Build(InOperationSnapshot(), Graph);
        CostMatrix matrix = context.Matrix;

        for (int j = 0; j < matrix.Size; j++)
        {
            Assert.False(matrix.IsComputed(1, j)); // cliente 1 já atendido
            Assert.Equal(CostMatrix.NotComputed, matrix.GetDistance(1, j));
        }

        for (int i = 0; i < matrix.Size; i++)
        {
            Assert.False(matrix.IsComputed(i, Vehicle0Origin));
            Assert.False(matrix.IsComputed(i, 1));
        }

        Assert.Equal([2, 3], context.Snapshot.PendingClients);
        Assert.Equal([0, 1, 2, 3], matrix.Points.Select(point => point.Id));
        Assert.Equal(OriginalMatrix.GetTravelTime(2, 3), matrix.GetTravelTime(2, 3));
    }

    [Fact]
    public void GrafoPenalizado_EncareceSoAsLinhasAfetadas()
    {
        // Congestionamento em volta do nó 4: as arestas 0 -> 4 e 4 -> 2 ficam 10x mais lentas.
        DirectedGraph penalized = new RoadEventApplier().Apply(Graph,
            new RoadEvent(-24.7196, -24.7194, -53.7401, -53.7399, PenaltyFactor: 10f));
        ReoptimizationSnapshot snapshot = InOperationSnapshot();

        CostMatrix baseMatrix = Build(snapshot, Graph).Matrix;
        CostMatrix penalizedMatrix = Build(snapshot, penalized).Matrix;

        int[] rows = [0, 2, 3, Vehicle0Origin];
        int[] columns = [0, 2, 3];
        foreach (int i in rows)
        {
            foreach (int j in columns)
            {
                Assert.True(penalizedMatrix.GetTravelTime(i, j) >= baseMatrix.GetTravelTime(i, j));
            }
        }

        Assert.True(penalizedMatrix.GetTravelTime(0, 2) > baseMatrix.GetTravelTime(0, 2));
        Assert.True(penalizedMatrix.GetTravelTime(Vehicle0Origin, 2) > baseMatrix.GetTravelTime(Vehicle0Origin, 2));
        foreach (int j in columns)
        {
            Assert.Equal(baseMatrix.GetTravelTime(2, j), penalizedMatrix.GetTravelTime(2, j));
            Assert.Equal(baseMatrix.GetTravelTime(3, j), penalizedMatrix.GetTravelTime(3, j));
        }
    }

    [Fact]
    public void ViaDeMaoUnica_PreservaAAssimetria()
    {
        var initial = ReoptimizationSnapshot.Initial(OriginalMatrix, fleetSize: 2, vehicleCapacity: 100f);
        CostMatrix matrix = Build(initial, Graph).Matrix;

        Assert.Equal(30f, matrix.GetTravelTime(1, 2));  // 1 -> 2 direto
        Assert.Equal(180f, matrix.GetTravelTime(2, 1)); // 2 -> 3 -> 0 -> 1 (não há 2 -> 1)
    }

    [Fact]
    public void Distancia_EADoCaminhoDeMenorTempo()
    {
        CostMatrix matrix = Build(InOperationSnapshot(), Graph).Matrix;

        // 0 -> 4 -> 2 leva 40 s e 4000 m; 0 -> 1 -> 2 leva 90 s mas só 800 m.
        Assert.Equal(40f, matrix.GetTravelTime(0, 2));
        Assert.Equal(4000f, matrix.GetDistance(0, 2));
    }


    private static ReoptimizationContext Build(ReoptimizationSnapshot snapshot, DirectedGraph graph) =>
        ReoptimizationContext.Build(snapshot, graph, OriginalMatrix.Points, new CostMatrixService());

    private static ReoptimizationSnapshot InOperationSnapshot() =>
        SnapshotWith(
            Anchor(vehicle: 0, node: 4, served: [1], remaining: [2]),
            Anchor(vehicle: 1, node: 0, served: [], remaining: [3]));

    private static ReoptimizationSnapshot SnapshotWith(params EventAnchor[] anchors)
    {
        var initial = ReoptimizationSnapshot.Initial(OriginalMatrix, fleetSize: anchors.Length, vehicleCapacity: 100f);
        return ReoptimizationSnapshot.FromAnchors(initial, anchors, eventTimeSeconds: 10.0);
    }

    // O instante da âncora não importa para a matriz; basta ser posterior ao evento.
    private static EventAnchor Anchor(int vehicle, int node, int[] served, int[] remaining) =>
        new(vehicle, node, AnchorTimeSeconds: 20.0, WaitSeconds: 10.0, served, remaining, IsFinished: false);

    private static Node NodeAt(int index) =>
        index == 4 ? new Node(-24.7195, -53.74) : new Node(-24.72, -53.74 + (index * 0.0001));

    private static DirectedGraph BuildGraph()
    {
        static Edge To(int target, float seconds, float meters) => new(target, seconds, meters, SpeedLimitKmh: 60f);

        Edge[][] adjacency =
        [
            [To(1, 60, 500), To(3, 60, 500), To(4, 20, 2000)],
            [To(0, 60, 500), To(2, 30, 300)],
            [To(3, 60, 500)],
            [To(0, 60, 500), To(2, 60, 500)],
            [To(2, 20, 2000)],
        ];

        // Velocidade máxima alta para a heurística do A* não superestimar o tempo.
        return new DirectedGraph(Enumerable.Range(0, 5).Select(NodeAt).ToArray(), adjacency, maxSpeedLimitKmh: 360f);
    }

    private static DeliveryPoint[] DeliveryPoints() =>
        Enumerable.Range(0, ClientCount + 1)
            .Select(i => new DeliveryPoint(i, NodeAt(i).Latitude, NodeAt(i).Longitude, Demand: i == 0 ? 0 : 10))
            .ToArray();
}
