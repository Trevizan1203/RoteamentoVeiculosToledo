namespace Dvrp.Domain.Graph;

/// <summary>
/// Ponto de partida da reotimização de UM veículo após um evento dinâmico, segundo a regra da
/// proposta: o veículo pego no meio de uma aresta conclui essa aresta antes de reotimizar.
/// Calculado por <see cref="RouteTimeline.AnchorAfterEvent"/>.
/// </summary>
/// <param name="AnchorNodeIndex">Nó viário de onde o veículo parte na reotimização (nova origem).</param>
/// <param name="AnchorTimeSeconds">Instante em que o veículo chega ao nó âncora.</param>
/// <param name="WaitSeconds">Âncora menos o instante do evento: tempo até o veículo ficar disponível.</param>
/// <param name="ServedClientIndices">Clientes já atendidos até a âncora (índices na CostMatrix).</param>
/// <param name="RemainingClientIndices">Clientes ainda pendentes, na ordem original da rota.</param>
/// <param name="IsFinished">true quando a rota já terminou no instante do evento.</param>
public readonly record struct EventAnchor(
    int AnchorNodeIndex,
    double AnchorTimeSeconds,
    double WaitSeconds,
    IReadOnlyList<int> ServedClientIndices,
    IReadOnlyList<int> RemainingClientIndices,
    bool IsFinished
);
