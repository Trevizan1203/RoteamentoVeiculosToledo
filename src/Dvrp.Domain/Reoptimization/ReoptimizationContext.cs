using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;

namespace Dvrp.Domain.Reoptimization;

/// <summary>
/// Tudo o que o Split multi-origem precisa para reotimizar o problema restante: o snapshot e a
/// matriz de custos calculada no grafo penalizado a partir da posição atual de cada veículo.
///
/// Convenção de índices da <see cref="Matrix"/>:
///   0 = depósito; 1..n = clientes, com os MESMOS índices do problema original (nunca remapeados);
///   n+1, n+2, ... = origens anexadas, uma por nó viário distinto.
/// Resolução da origem de um veículo pelo seu nó: nó do depósito → índice do depósito; nó de um
/// cliente PENDENTE → índice desse cliente; senão → índice anexado. Atendidos não têm linha
/// calculada e por isso nunca são reaproveitados como origem.
/// Só são calculadas as linhas do depósito, dos clientes pendentes e das origens, e só as colunas
/// do depósito e dos pendentes; o resto vale <see cref="CostMatrix.NotComputed"/>.
/// </summary>
public sealed class ReoptimizationContext
{
    public const int DepotIndex = SplitService.DepotIndex;

    public ReoptimizationSnapshot Snapshot { get; }
    public CostMatrix Matrix { get; }

    /// <summary>Índice na <see cref="Matrix"/> da origem de cada veículo, alinhado com <c>Snapshot.ActiveVehicles</c>.</summary>
    public IReadOnlyList<int> VehicleOriginIndices { get; }

    /// <summary>
    /// Entrada do Split multi-origem: um veículo por ativo, na ordem de <c>Snapshot.ActiveVehicles</c>
    /// (a ordem das rotas no plano em execução), com sua origem e capacidade residual.
    /// </summary>
    public IReadOnlyList<SplitVehicle> SplitVehicles { get; }

    private ReoptimizationContext(ReoptimizationSnapshot snapshot, CostMatrix matrix, IReadOnlyList<int> vehicleOriginIndices)
    {
        Snapshot = snapshot;
        Matrix = matrix;
        VehicleOriginIndices = vehicleOriginIndices;
        SplitVehicles = snapshot.ActiveVehicles
            .Select((vehicle, v) => new SplitVehicle(vehicle.VehicleId, vehicleOriginIndices[v], vehicle.ResidualCapacity))
            .ToArray();
    }

    /// <summary>
    /// Problema original (antes da partida): snapshot <see cref="ReoptimizationSnapshot.Initial"/>
    /// sobre a própria matriz original, que já é a matriz desse contexto — toda a frota parte do
    /// depósito, então não há origens anexadas nem nada a recalcular.
    /// </summary>
    public static ReoptimizationContext Initial(CostMatrix originalMatrix, int fleetSize, float vehicleCapacity)
    {
        var snapshot = ReoptimizationSnapshot.Initial(originalMatrix, fleetSize, vehicleCapacity);
        return new ReoptimizationContext(snapshot, originalMatrix, new int[fleetSize]); // todos no índice 0 (depósito)
    }

    /// <param name="penalizedGraph">Grafo com o evento já aplicado (saída do <see cref="RoadEventApplier"/>).</param>
    /// <param name="points">Depósito e clientes já associados aos nós viários (φ), ex.: <c>CostMatrix.Points</c> original.</param>
    public static ReoptimizationContext Build(
        ReoptimizationSnapshot snapshot,
        DirectedGraph penalizedGraph,
        IReadOnlyList<LogisticPoint> points,
        CostMatrixService costMatrixService)
    {
        // Nós que já têm linha calculada: depósito e pendentes. Depósito primeiro, e entre
        // pendentes no mesmo nó vale o de menor índice, então a resolução é determinística.
        var indexByNode = new Dictionary<int, int> { [points[DepotIndex].RoadNodeIndex] = DepotIndex };
        foreach (int client in snapshot.PendingClients)
        {
            indexByNode.TryAdd(points[client].RoadNodeIndex, client);
        }

        var extraOriginNodes = new List<int>();
        var vehicleOriginIndices = new int[snapshot.ActiveVehicles.Count];

        for (int v = 0; v < vehicleOriginIndices.Length; v++)
        {
            int originNode = snapshot.ActiveVehicles[v].OriginNode;

            if (!indexByNode.TryGetValue(originNode, out int index))
            {
                index = points.Count + extraOriginNodes.Count;
                extraOriginNodes.Add(originNode);
                indexByNode.Add(originNode, index);
            }

            vehicleOriginIndices[v] = index;
        }

        int[] destinations = [DepotIndex, .. snapshot.PendingClients];
        int[] origins = [.. destinations, .. Enumerable.Range(points.Count, extraOriginNodes.Count)];

        CostMatrix matrix = costMatrixService.BuildMatrix(penalizedGraph, points, extraOriginNodes, origins, destinations);

        return new ReoptimizationContext(snapshot, matrix, vehicleOriginIndices);
    }
}
