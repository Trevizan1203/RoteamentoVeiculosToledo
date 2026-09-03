namespace Dvrp.Domain.Optimization;

public enum GaStopReason
{
    MaxGenerationsReached,
    Stagnation,
    Cancelled
}

public readonly record struct GaRunResult(
    Chromosome Best,
    IReadOnlyList<Chromosome> FinalPopulation,
    int GenerationsRun,
    GaStopReason StopReason
);
