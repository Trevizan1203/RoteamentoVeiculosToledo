using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;

namespace Dvrp.Domain.Reoptimization;

/// <summary>
/// Tudo o que o Split multi-origem precisa para reotimizar o problema restante: o snapshot e a
/// matriz de custos calculada no grafo penalizado a partir da posição atual de cada veículo.
///
/// Convenção de índices da <see cref="Matrix"/>:
///   0 = depósito; 1..n = clientes, com os MESMOS índices do problema original (nunca remapeados);
///   n+1, n+2, ... = um índice por nó de origem distinto de veículo ativo fora do depósito.
/// Veículo ancorado no nó do depósito usa o índice do depósito, sem linha extra.
/// Só são calculadas as linhas do depósito, dos clientes pendentes e das origens, e só as colunas
/// do depósito e dos pendentes; o resto vale <see cref="CostMatrix.NotComputed"/>.
/// </summary>
public sealed class ReoptimizationContext
{
    public const int DepotIndex = 0;

    public ReoptimizationSnapshot Snapshot { get; }
    public CostMatrix Matrix { get; }

    /// <summary>Índice na <see cref="Matrix"/> da origem de cada veículo, alinhado com <c>Snapshot.ActiveVehicles</c>.</summary>
    public IReadOnlyList<int> VehicleOriginIndices { get; }

    private ReoptimizationContext(ReoptimizationSnapshot snapshot, CostMatrix matrix, IReadOnlyList<int> vehicleOriginIndices)
    {
        Snapshot = snapshot;
        Matrix = matrix;
        VehicleOriginIndices = vehicleOriginIndices;
    }

    /// <param name="penalizedGraph">Grafo com o evento já aplicado (saída do <see cref="RoadEventApplier"/>).</param>
    /// <param name="points">Depósito e clientes já associados aos nós viários (φ), ex.: <c>CostMatrix.Points</c> original.</param>
    public static ReoptimizationContext Build(
        ReoptimizationSnapshot snapshot,
        DirectedGraph penalizedGraph,
        IReadOnlyList<LogisticPoint> points,
        CostMatrixService costMatrixService)
    {
        int depotNode = points[DepotIndex].RoadNodeIndex;
        var extraOriginNodes = new List<int>();
        var vehicleOriginIndices = new int[snapshot.ActiveVehicles.Count];

        for (int v = 0; v < vehicleOriginIndices.Length; v++)
        {
            int originNode = snapshot.ActiveVehicles[v].OriginNode;

            if (originNode == depotNode)
            {
                vehicleOriginIndices[v] = DepotIndex;
                continue;
            }

            int position = extraOriginNodes.IndexOf(originNode);
            if (position < 0)
            {
                position = extraOriginNodes.Count;
                extraOriginNodes.Add(originNode);
            }

            vehicleOriginIndices[v] = points.Count + position;
        }

        int[] destinations = [DepotIndex, .. snapshot.PendingClients];
        int[] origins = [.. destinations, .. Enumerable.Range(points.Count, extraOriginNodes.Count)];

        CostMatrix matrix = costMatrixService.BuildMatrix(penalizedGraph, points, extraOriginNodes, origins, destinations);

        return new ReoptimizationContext(snapshot, matrix, vehicleOriginIndices);
    }
}
