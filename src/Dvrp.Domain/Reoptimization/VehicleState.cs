namespace Dvrp.Domain.Reoptimization;

/// <summary>
/// Estado de UM veículo ativo no instante de um evento: de onde ele parte na reotimização, quanta
/// carga ainda leva e quais clientes ainda deve atender segundo o plano em execução.
/// </summary>
/// <param name="VehicleId">Mesmo <c>VehicleIndex</c> da rota no plano em execução.</param>
/// <param name="OriginNode">Nó do grafo viário (índice CSR) onde o EventAnchor ancorou o veículo.</param>
/// <param name="AvailableAtSeconds">Instante (relógio global) em que o veículo chega à origem. Não entra
/// na função objetivo do Split — o tempo até a âncora é constante por veículo e não altera o ótimo —;
/// serve ao TTV total (tempo até a âncora + rota restante) e para posicionar o veículo num segundo evento.</param>
/// <param name="ResidualCapacity">Q menos a demanda já atendida por este veículo.</param>
/// <param name="RemainingPlan">Clientes pendentes do veículo (índices na CostMatrix), na ordem do plano em execução.</param>
public sealed record VehicleState(
    int VehicleId,
    int OriginNode,
    double AvailableAtSeconds,
    float ResidualCapacity,
    IReadOnlyList<int> RemainingPlan
);
