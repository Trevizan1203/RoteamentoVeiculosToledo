using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;

namespace Dvrp.Domain.Reoptimization;

/// <summary>
/// O PROBLEMA RESTANTE no instante de um evento dinâmico: veículos ainda em operação (com origem e
/// capacidade residual), veículos que já concluíram e a partição dos clientes em atendidos e
/// pendentes. Os clientes são sempre identificados pelo índice na <see cref="CostMatrix"/> original.
///
/// Cada snapshot nasce do anterior, e o primeiro é sempre <see cref="Initial"/>. Assim, atendidos
/// e capacidade residual se acumulam ao longo de eventos em sequência, mesmo quando o plano em
/// execução já é resultado de uma reotimização anterior.
/// </summary>
public sealed class ReoptimizationSnapshot
{
    // Demanda por índice da CostMatrix (0 = depósito). Herdada snapshot a snapshot.
    private readonly IReadOnlyList<int> _demands;

    public double EventTimeSeconds { get; }
    public float VehicleCapacity { get; }

    /// <summary>true no snapshot pré-partida: frota toda no depósito, sem plano a seguir.</summary>
    public bool IsInitial { get; }

    public IReadOnlyList<VehicleState> ActiveVehicles { get; }
    public IReadOnlyList<int> FinishedVehicleIds { get; }
    public IReadOnlyList<int> PendingClients { get; }
    public IReadOnlyList<int> ServedClients { get; }

    private ReoptimizationSnapshot(
        IReadOnlyList<int> demands,
        float vehicleCapacity,
        double eventTimeSeconds,
        bool isInitial,
        IReadOnlyList<VehicleState> activeVehicles,
        IReadOnlyList<int> finishedVehicleIds,
        IReadOnlyList<int> servedClients)
    {
        _demands = demands;
        VehicleCapacity = vehicleCapacity;
        EventTimeSeconds = eventTimeSeconds;
        IsInitial = isInitial;
        ActiveVehicles = activeVehicles.ToArray();
        FinishedVehicleIds = finishedVehicleIds.Order().ToArray();
        ServedClients = servedClients.Order().ToArray();
        PendingClients = AllClients().Except(servedClients).ToArray();

        ValidatePartition();

        if (!IsInitial)
        {
            ValidatePlanCoverage();
        }

        ValidateVehicles();
    }

    /// <summary>
    /// Problema original: <paramref name="fleetSize"/> veículos no depósito com capacidade cheia e
    /// todos os clientes pendentes. É o estado dos experimentos atuais (evento antes da partida).
    /// </summary>
    public static ReoptimizationSnapshot Initial(CostMatrix matrix, int fleetSize, float vehicleCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fleetSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vehicleCapacity);

        int depotNode = matrix.Points[0].RoadNodeIndex;

        VehicleState[] fleet = Enumerable.Range(0, fleetSize)
            .Select(id => new VehicleState(id, depotNode, AvailableAtSeconds: 0.0, vehicleCapacity, Array.Empty<int>()))
            .ToArray();

        return new ReoptimizationSnapshot(
            demands: matrix.Points.Select(point => point.Demand).ToArray(),
            vehicleCapacity,
            eventTimeSeconds: 0.0,
            isInitial: true,
            activeVehicles: fleet,
            finishedVehicleIds: Array.Empty<int>(),
            servedClients: Array.Empty<int>());
    }

    /// <summary>
    /// Snapshot no instante <paramref name="eventTimeSeconds"/> a partir do plano em execução
    /// (uma <see cref="RouteTimeline"/> por rota, incluindo as vazias). A classificação de cada
    /// cliente em atendido ou pendente vem de <see cref="RouteTimeline.AnchorAfterEvent"/>.
    /// </summary>
    public static ReoptimizationSnapshot FromExecutingPlan(
        ReoptimizationSnapshot previous,
        IReadOnlyList<RouteTimeline> executingPlan,
        double eventTimeSeconds)
    {
        EventAnchor[] anchors = executingPlan
            .Select(timeline => timeline.AnchorAfterEvent(eventTimeSeconds))
            .ToArray();

        return FromAnchors(previous, anchors, eventTimeSeconds);
    }

    /// <summary>
    /// Mesmo que <see cref="FromExecutingPlan"/>, recebendo as âncoras já calculadas (uma por
    /// veículo do plano em execução).
    /// </summary>
    public static ReoptimizationSnapshot FromAnchors(
        ReoptimizationSnapshot previous,
        IReadOnlyList<EventAnchor> anchors,
        double eventTimeSeconds)
    {
        if (eventTimeSeconds < previous.EventTimeSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(eventTimeSeconds), eventTimeSeconds,
                $"O evento nao pode ocorrer antes do snapshot anterior (t = {previous.EventTimeSeconds}).");
        }

        var activeVehicles = new List<VehicleState>();
        var finishedVehicleIds = new List<int>(previous.FinishedVehicleIds);
        var servedClients = new List<int>(previous.ServedClients);
        var seenVehicleIds = new HashSet<int>();

        foreach (EventAnchor anchor in anchors)
        {
            if (!seenVehicleIds.Add(anchor.VehicleIndex))
            {
                throw new ArgumentException(
                    $"O veiculo {anchor.VehicleIndex} aparece mais de uma vez no plano em execucao.", nameof(anchors));
            }

            VehicleState? before = previous.ActiveVehicles.FirstOrDefault(v => v.VehicleId == anchor.VehicleIndex);

            switch (Classify(anchor, before, previous.IsInitial))
            {
                case VehicleStatus.Active:
                    float residual = before!.ResidualCapacity - previous.DemandOf(anchor.ServedClientIndices);
                    activeVehicles.Add(new VehicleState(
                        anchor.VehicleIndex, anchor.AnchorNodeIndex, anchor.AnchorTimeSeconds, residual,
                        anchor.RemainingClientIndices));
                    break;

                case VehicleStatus.Finished:
                    finishedVehicleIds.Add(anchor.VehicleIndex);
                    break;

                case VehicleStatus.NotDispatched:
                    continue;
            }

            servedClients.AddRange(anchor.ServedClientIndices);
        }

        return new ReoptimizationSnapshot(
            previous._demands,
            previous.VehicleCapacity,
            eventTimeSeconds,
            isInitial: false,
            activeVehicles,
            finishedVehicleIds,
            servedClients);
    }

    private enum VehicleStatus
    {
        Active,
        Finished,
        NotDispatched
    }

    /// <summary>
    /// Papel do veículo no novo snapshot, a partir da sua âncora e do seu estado no snapshot
    /// anterior (<paramref name="before"/> é null se ele não estava ativo lá).
    /// </summary>
    private static VehicleStatus Classify(EventAnchor anchor, VehicleState? before, bool previousIsInitial)
    {
        bool hasRemaining = anchor.RemainingClientIndices.Count > 0;
        bool hasServed = anchor.ServedClientIndices.Count > 0;

        if (before is null)
        {
            return hasRemaining || hasServed
                ? throw new InvalidOperationException(
                    $"O veiculo {anchor.VehicleIndex} nao estava ativo no snapshot anterior (nao despachado ou ja " +
                    "concluido) e nao pode receber clientes apos o evento.")
                : VehicleStatus.NotDispatched;
        }

        if (hasRemaining)
        {
            return VehicleStatus.Active;
        }

        // Rota vazia vinda do Initial: o veículo nunca saiu do depósito. Vinda de um plano
        // reotimizado, ele já estava em operação e esvaziou a rota: conta como concluído.
        return hasServed || !previousIsInitial ? VehicleStatus.Finished : VehicleStatus.NotDispatched;
    }

    private float DemandOf(IEnumerable<int> clients) => clients.Sum(client => _demands[client]);

    private IEnumerable<int> AllClients() => Enumerable.Range(1, _demands.Count - 1);

    // Invariante 1: atendidos e pendentes particionam o conjunto de clientes.
    private void ValidatePartition()
    {
        int duplicated = ServedClients.CountBy(client => client)
            .Where(entry => entry.Value > 1)
            .Select(entry => entry.Key)
            .FirstOrDefault(-1);
        if (duplicated >= 0)
        {
            throw new InvalidOperationException($"Invariante 1 violado: o cliente {duplicated} foi atendido mais de uma vez.");
        }

        int unknown = ServedClients.Except(AllClients()).FirstOrDefault(-1);
        if (unknown >= 0)
        {
            throw new InvalidOperationException($"Invariante 1 violado: o indice {unknown} nao e um cliente do problema original.");
        }
    }

    // Invariantes 2 e 3: os planos restantes cobrem exatamente os pendentes, e todo ativo tem plano.
    private void ValidatePlanCoverage()
    {
        VehicleState? idle = ActiveVehicles.FirstOrDefault(vehicle => vehicle.RemainingPlan.Count == 0);
        if (idle is not null)
        {
            throw new InvalidOperationException($"Invariante 3 violado: o veiculo ativo {idle.VehicleId} nao tem clientes pendentes.");
        }

        Dictionary<int, int> occurrences = ActiveVehicles
            .SelectMany(vehicle => vehicle.RemainingPlan)
            .CountBy(client => client)
            .ToDictionary();

        foreach (int client in PendingClients)
        {
            int count = occurrences.GetValueOrDefault(client);
            if (count != 1)
            {
                throw new InvalidOperationException(
                    $"Invariante 2 violado: o cliente pendente {client} aparece em {count} planos restantes (esperado: 1).");
            }
        }

        int stray = occurrences.Keys.Except(PendingClients).FirstOrDefault(-1);
        if (stray >= 0)
        {
            throw new InvalidOperationException(
                $"Invariante 2 violado: o cliente {stray} esta num plano restante mas nao esta pendente.");
        }
    }

    // Invariantes 4 e 5 (a carga restante cabe no veículo e a capacidade residual está em [0, Q])
    // e 6 (o veículo só fica disponível depois do evento).
    private void ValidateVehicles()
    {
        foreach (VehicleState vehicle in ActiveVehicles)
        {
            if (vehicle.AvailableAtSeconds < EventTimeSeconds)
            {
                throw new InvalidOperationException(
                    $"Invariante 6 violado: o veiculo {vehicle.VehicleId} fica disponivel em {vehicle.AvailableAtSeconds} s, " +
                    $"antes do evento em {EventTimeSeconds} s.");
            }

            if (vehicle.ResidualCapacity < 0 || vehicle.ResidualCapacity > VehicleCapacity)
            {
                throw new InvalidOperationException(
                    $"Invariante 5 violado: o veiculo {vehicle.VehicleId} tem capacidade residual {vehicle.ResidualCapacity} " +
                    $"fora de [0, {VehicleCapacity}]. O plano despachado era inviavel (rota acima da capacidade Q).");
            }

            float remainingDemand = DemandOf(vehicle.RemainingPlan);
            if (remainingDemand > vehicle.ResidualCapacity)
            {
                throw new InvalidOperationException(
                    $"Invariante 4 violado: o veiculo {vehicle.VehicleId} ainda deve entregar {remainingDemand}, mas so tem " +
                    $"{vehicle.ResidualCapacity} de capacidade residual. O plano despachado era inviavel (rota acima da capacidade Q).");
            }
        }
    }
}
