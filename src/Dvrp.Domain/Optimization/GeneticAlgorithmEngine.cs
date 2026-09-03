using Dvrp.Domain.Logistics;

namespace Dvrp.Domain.Optimization;

public sealed class GeneticAlgorithmEngine(
    SplitService splitService,
    GeneticOperators geneticOperators,
    Random random)
{
    public List<Chromosome> InitializePopulation(
        int populationSize,
        int clientCount,
        CostMatrix costMatrix,
        int maxVehicles,
        int vehicleCapacity)
    {
        int[] baseGenes = new int[clientCount];
        for (int i = 0; i < clientCount; i++)
        {
            baseGenes[i] = i + 1;
        }
        
        List<Chromosome> population = new(populationSize);

        for (int i = 0; i < populationSize; i++)
        {
            //clona e embaralha genes
            int[] shuffledGenes = (int[])baseGenes.Clone();
            Shuffle(shuffledGenes);

            Chromosome individual = new Chromosome(shuffledGenes);
            
            // chama splitService, calcula o fitnesse desse individuo
            splitService.Evaluate(individual, costMatrix, maxVehicles, vehicleCapacity);
            
            population.Add(individual);
        }
        
        return population;
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
        CancellationToken cancellationToken = default)
    {
        List<Chromosome> population;
        if (initialPopulation is null)
        {
            population = InitializePopulation(
                populationSize,
                clientCount,
                costMatrix,
                maxVehicles,
                vehicleCapacity);
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
                
                splitService.Evaluate(child, costMatrix, maxVehicles, vehicleCapacity);
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
}