namespace Dvrp.Domain.Optimization;

public sealed class Chromosome
{
    public int[] Genes { get; }
    public float Fitness { get; set; } = float.PositiveInfinity; // J(S)
    public float TotalTravelTimeSeconds { get; set; }           // TTV
    public float TotalDistanceMeters { get; set; }              // DTP
    public bool IsFeasible { get; set; }
    public IReadOnlyList<VehicleRoute> Routes { get; set; } = Array.Empty<VehicleRoute>();

    public Chromosome(int[] genes)
    {
        Genes = genes ?? throw new ArgumentNullException(nameof(genes));
    }

    /// <summary>
    /// Clona o indivíduo para operações de seleção e cruzamento sem efeitos colaterais.
    /// </summary>
    public Chromosome Clone()
    {
        int[] clonedGenes = new int[Genes.Length];
        Array.Copy(Genes, clonedGenes, Genes.Length);

        return new Chromosome(clonedGenes)
        {
            Fitness = Fitness,
            TotalTravelTimeSeconds = TotalTravelTimeSeconds,
            TotalDistanceMeters = TotalDistanceMeters,
            IsFeasible = IsFeasible,
            Routes = Routes
        };
    }
}