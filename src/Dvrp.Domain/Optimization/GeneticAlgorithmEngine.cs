using Dvrp.Domain.Logistics;
using Dvrp.Domain.Reoptimization;

namespace Dvrp.Domain.Optimization;

/// <summary>
/// AG sobre o giant tour dos clientes PENDENTES de um <see cref="ReoptimizationContext"/>,
/// avaliado pelo <see cref="SplitService"/> com os veículos ativos do contexto. O problema
/// original é o caso do contexto Initial (toda a frota no depósito, todos os clientes pendentes).
/// </summary>
public sealed class GeneticAlgorithmEngine(
    SplitService splitService,
    GeneticOperators geneticOperators,
    Random random)
{
    /// <summary>População inicial aleatória do problema original (contexto Initial).</summary>
    public List<Chromosome> InitializePopulation(
        int populationSize,
        int clientCount,
        CostMatrix costMatrix,
        int maxVehicles,
        int vehicleCapacity) =>
        CreateRandomPopulation(InitialContext(clientCount, costMatrix, maxVehicles, vehicleCapacity), populationSize);

    /// <summary>AG sobre o problema original (contexto Initial).</summary>
    public GaRunResult Run(
        int clientCount,
        CostMatrix costMatrix,
        int maxVehicles,
        int vehicleCapacity,
        int populationSize,
        int maxGenerations,
        float crossoverRate = 0.85f,
        float mutationRate = 0.15f,
        int eliteCount = 1,
        IReadOnlyList<Chromosome>? initialPopulation = null,
        CancellationToken cancellationToken = default) =>
        Run(InitialContext(clientCount, costMatrix, maxVehicles, vehicleCapacity),
            populationSize, maxGenerations, crossoverRate, mutationRate, eliteCount, initialPopulation, cancellationToken);

    /// <summary>
    /// AG sobre o problema descrito por <paramref name="context"/>. Sem
    /// <paramref name="initialPopulation"/>, parte de <see cref="CreateRandomPopulation"/>
    /// (reinício completo); com ela, usa-a como está — já avaliada neste contexto, como a devolvida
    /// por <see cref="AdaptPopulation"/> (reaproveitamento).
    /// </summary>
    public GaRunResult Run(
        ReoptimizationContext context,
        int populationSize,
        int maxGenerations,
        float crossoverRate = 0.85f,
        float mutationRate = 0.15f,
        int eliteCount = 1,
        IReadOnlyList<Chromosome>? initialPopulation = null,
        CancellationToken cancellationToken = default)
    {
        if (context.Snapshot.PendingClients.Count < 2)
        {
            throw new ArgumentException(
                "O AG precisa de ao menos 2 clientes pendentes (as mutacoes trocam/invertem genes).", nameof(context));
        }

        List<Chromosome> population;
        if (initialPopulation is null)
        {
            population = CreateRandomPopulation(context, populationSize);
        }
        else
        {
            if (initialPopulation.Count != populationSize)
            {
                throw new ArgumentException(
                    $"O tamanho da população inicial ({initialPopulation.Count}) deve ser igual a populationSize ({populationSize}).",
                    nameof(initialPopulation));
            }

            population = new List<Chromosome>(initialPopulation);
        }

        population.Sort((a, b) => a.Fitness.CompareTo(b.Fitness));
        Chromosome best = population[0];

        int unproductiveGenerations = 0;
        int maxUnproductiveGenerations = 100;

        int generationsRun = 0;
        GaStopReason stopReason = GaStopReason.MaxGenerationsReached;

        for (int i = 0; i < maxGenerations; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopReason = GaStopReason.Cancelled;
                break;
            }

            List<Chromosome> nextGeneration = new(populationSize);
            
            nextGeneration.AddRange(population.Take(eliteCount));

            IReadOnlyList<Chromosome> binaryWinners = geneticOperators.BinaryTournament(random, population);

            while (nextGeneration.Count < populationSize)
            {
                int parentA = random.Next(binaryWinners.Count);
                int parentB = random.Next(binaryWinners.Count);

                while (parentB == parentA)
                {
                    parentB = random.Next(binaryWinners.Count);
                }

                Chromosome child = random.NextDouble() < crossoverRate
                    ? geneticOperators.Crossover(binaryWinners[parentA], binaryWinners[parentB], random)
                    : new Chromosome((int[])binaryWinners[parentA].Genes.Clone());

                if (random.NextDouble() < mutationRate)
                {
                    if (random.NextDouble() < 0.5)
                        geneticOperators.MutationSwap(child, random);
                    else
                        geneticOperators.MutationReverse(child, random);
                }
                
                Evaluate(child, context);
                nextGeneration.Add(child);
            }
            
            population = nextGeneration;
            population.Sort((a, b) => a.Fitness.CompareTo(b.Fitness));
            generationsRun++;

            if (population[0].Fitness < best.Fitness)
            {
                best = population[0];
                unproductiveGenerations = 0;
            }
            else
            {
                unproductiveGenerations++;
                if (unproductiveGenerations == maxUnproductiveGenerations)
                {
                    stopReason = GaStopReason.Stagnation;
                    break;
                }
            }
        }

        return new GaRunResult(best, population, generationsRun, stopReason);
    }

    /// <summary>
    /// Reinício completo: <paramref name="populationSize"/> permutações aleatórias dos clientes
    /// pendentes, avaliadas no contexto.
    /// </summary>
    public List<Chromosome> CreateRandomPopulation(ReoptimizationContext context, int populationSize)
    {
        int[] pendingClients = context.Snapshot.PendingClients.ToArray();
        List<Chromosome> population = new(populationSize);

        for (int i = 0; i < populationSize; i++)
        {
            int[] shuffledGenes = (int[])pendingClients.Clone();
            Shuffle(shuffledGenes);

            Chromosome individual = new Chromosome(shuffledGenes);
            Evaluate(individual, context);
            population.Add(individual);
        }

        return population;
    }

    /// <summary>
    /// Reaproveitamento: a população anterior, cada indivíduo sem os clientes que deixaram de
    /// estar pendentes, avaliada no contexto. Mesmo tamanho; indivíduos que ficarem iguais são
    /// mantidos.
    /// </summary>
    public List<Chromosome> AdaptPopulation(IReadOnlyList<Chromosome> previousPopulation, ReoptimizationContext context)
    {
        HashSet<int> pendingClients = context.Snapshot.PendingClients.ToHashSet();

        return previousPopulation
            .Select(individual =>
            {
                Chromosome adapted = KeepOnlyPending(individual, pendingClients);
                Evaluate(adapted, context);
                return adapted;
            })
            .ToList();
    }

    /// <summary>
    /// O mesmo giant tour sem os clientes que não estão em <paramref name="pendingClients"/>,
    /// preservando a ordem relativa dos demais. Novo cromossomo, ainda não avaliado.
    /// </summary>
    public static Chromosome KeepOnlyPending(Chromosome individual, IReadOnlySet<int> pendingClients) =>
        new(individual.Genes.Where(pendingClients.Contains).ToArray());

    private void Evaluate(Chromosome chromosome, ReoptimizationContext context) =>
        splitService.Evaluate(chromosome, context.Matrix, context.SplitVehicles, context.Snapshot.VehicleCapacity);

    private static ReoptimizationContext InitialContext(int clientCount, CostMatrix costMatrix, int maxVehicles, int vehicleCapacity)
    {
        if (clientCount != costMatrix.Size - 1)
        {
            throw new ArgumentException(
                $"clientCount ({clientCount}) deve ser o numero de clientes da matriz ({costMatrix.Size - 1}).", nameof(clientCount));
        }

        return ReoptimizationContext.Initial(costMatrix, maxVehicles, vehicleCapacity);
    }

    private void Shuffle(int[] array)
    {
        int n = array.Length;
        for (int i = n - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }
}
