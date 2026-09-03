namespace Dvrp.Domain.Optimization;

public class GeneticOperators
{
    public IReadOnlyList<Chromosome> BinaryTournament(
        Random random,
        IReadOnlyList<Chromosome> population)
    {
        List<Chromosome> winners = new (population.Count / 2);
        
        if (population.Count < 2)
        {
            throw new ArgumentException(
                "Para fazer o torneio binario eh necessario ter ao menos 2 individuos na populacao.",
                nameof(population));
        }

        for (int i = 0; i < population.Count / 2; i++)
        {
            int index_a = random.Next(population.Count);
            int index_b = random.Next(population.Count);

            while (index_b == index_a)
            { 
                index_b = random.Next(population.Count);
            }
            
            Chromosome chromosome_a = population[index_a];
            Chromosome chromosome_b = population[index_b];

            if (chromosome_a.Fitness < chromosome_b.Fitness)
            {
                winners.Add(chromosome_a);
                continue;
            }

            winners.Add(chromosome_b);
        }
        
        return winners;
    }

    public Chromosome Crossover(
        Chromosome parentA,
        Chromosome parentB,
        Random random)
    {
        int length = parentA.Genes.Length;
        int[] childGenes = new int[length];
        
        int cut1 = random.Next(length);
        int cut2 = random.Next(length);

        int start = Math.Min(cut1, cut2);
        int end = Math.Max(cut1, cut2);
        int sliceLength = (end - start) + 1; // Garante que a fatia tenha ao menos 1 elemento
        
        HashSet<int> copiedGenes = new(length);

        for (int i = start; i <= end; i++)
        {
            int gene = parentA.Genes[i];
            childGenes[i] = gene;
            copiedGenes.Add(gene);
        }

        int childIndex = (end + 1) % length;
        int parentBIndex = (end + 1) % length;
        
        while (copiedGenes.Count < length)
        {
            int candidateGene = parentB.Genes[parentBIndex];

            if (!copiedGenes.Contains(candidateGene))
            {
                childGenes[childIndex] = candidateGene;
                copiedGenes.Add(candidateGene);
                
                childIndex = (childIndex + 1) % length;
            }
            
            parentBIndex = (parentBIndex + 1) % length;
        }
        
        return new Chromosome(childGenes);
    }

    public void MutationSwap(Chromosome chromosome, Random random)
    {
        int genesLength = chromosome.Genes.Length;

        if (genesLength < 2)
        {
            throw new ArgumentException("Eh necessario ao menos 2 genes para fazer a mutacao de swap.", nameof(chromosome));
        }

        int indexA = random.Next(genesLength);
        int indexB = random.Next(genesLength);

        while (indexA == indexB)
        {
            indexB = random.Next(genesLength);
        }
        
        (chromosome.Genes[indexA], chromosome.Genes[indexB]) = (chromosome.Genes[indexB], chromosome.Genes[indexA]);
    }
    
    public void MutationReverse(Chromosome chromosome, Random random)
    {
        int genesLength = chromosome.Genes.Length;
        
        if (genesLength < 2)
        {
            throw new ArgumentException("Eh necessario ao menos 2 genes para fazer a mutacao de reverse.", nameof(chromosome));
        }
        
        int cut1 = random.Next(genesLength); 
        int cut2 = random.Next(genesLength);
        
        int start = Math.Min(cut1, cut2); 
        int end = Math.Max(cut1, cut2);
        
        int count = (end - start) + 1;

        Array.Reverse(chromosome.Genes, start, count);
    }
}