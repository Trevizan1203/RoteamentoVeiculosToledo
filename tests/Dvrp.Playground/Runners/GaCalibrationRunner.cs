using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Infrastructure.Osm;
using E = Dvrp.Playground.Runners.DynamicReoptimizationExperiment;

namespace Dvrp.Playground.Runners;

/// <summary>
/// Calibração do AG inicial, sem evento, na mesma instância do lote. Testa combinações de
/// população, mutação e cruzamento com seeds que não são usadas no lote.
/// </summary>
public static class GaCalibrationRunner
{
    // Diferentes das seeds do lote.
    internal static readonly int[] Seeds = [13001, 24007, 35023];

    private static readonly int[] PopulationSizes = [50, 100, 200];
    private static readonly float[] MutationRates = [0.05f, 0.15f, 0.30f];
    private static readonly float[] CrossoverRates = [0.70f, 0.85f, 0.95f];

    internal const string OutputFileName = "dvrp-calibracao.csv";

    // A calibração foi feita com limite de 1000 gerações; a configuração anterior aparece no
    // ranking como referência.
    private const int MaxGenerations = 1000;
    private static readonly Config PreviousConfig = new(PopulationSize: 100, MutationRate: 0.15f, CrossoverRate: 0.85f);

    private readonly record struct Config(int PopulationSize, float MutationRate, float CrossoverRate)
    {
        public bool IsPrevious => this == PreviousConfig;

        public override string ToString() =>
            $"pop {PopulationSize,3} mut {MutationRate.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"cx {CrossoverRate.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    private sealed record Run(int Seed, Config Config, GaRunResult Ga, double ElapsedMs);

    public static void RunAll(string osmFilePath)
    {
        DirectedGraph graph = new OsmRoadNetworkParser().ParseFromFile(osmFilePath).Graph;
        Config[] grid =
        [
            .. from population in PopulationSizes
               from mutation in MutationRates
               from crossover in CrossoverRates
               select new Config(population, mutation, crossover),
        ];

        Console.WriteLine($"Calibracao do AG inicial: {grid.Length} configuracoes x {Seeds.Length} seeds ({string.Join(", ", Seeds)}).");

        var runs = new List<Run>();
        Stopwatch total = Stopwatch.StartNew();

        foreach (int seed in Seeds)
        {
            IReadOnlyList<DeliveryPoint> points = E.BuildRandomDeliveryPoints(graph, E.DeliveryPointCount, seed, E.MinDemand, E.MaxDemand);
            CostMatrix matrix = new CostMatrixService().BuildMatrix(graph, points);
            int fleetSize = (int)Math.Ceiling(points.Skip(1).Sum(p => p.Demand) / (double)E.VehicleCapacity) + 20;

            foreach (Config config in grid)
            {
                var engine = new GeneticAlgorithmEngine(new SplitService(), new GeneticOperators(), new Random(seed));
                Stopwatch clock = Stopwatch.StartNew();
                GaRunResult ga = engine.Run(matrix.Size - 1, matrix, fleetSize, E.VehicleCapacity,
                    config.PopulationSize, MaxGenerations, config.CrossoverRate, config.MutationRate);
                clock.Stop();

                runs.Add(new Run(seed, config, ga, clock.Elapsed.TotalMilliseconds));
                Console.WriteLine($"  seed {seed} | {config} | fitness {ga.Best.Fitness:F1} | {ga.GenerationsRun} geracoes | {clock.Elapsed.TotalSeconds:F1} s");
            }
        }

        total.Stop();
        string path = Path.Combine(DynamicReoptimizationBatchRunner.ResolveOutputDirectory(), OutputFileName);
        WriteCsv(path, runs);
        PrintSummary(runs, total.Elapsed);
        Console.WriteLine($"CSV: {path}");
    }

    private static void WriteCsv(string path, IReadOnlyList<Run> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Calibracao do AG inicial (sem evento, grafo base). Seeds: {string.Join(" ", Seeds)}. " +
            $"Fixos: elite 1, estagnacao 100, max {MaxGenerations} geracoes, penalidade 5000, operadores atuais. " +
            $"Instancia: {E.DeliveryPointCount} pontos, demanda {E.MinDemand}..{E.MaxDemand}, Q = {E.VehicleCapacity}, m = ceil(demanda/Q) + 20.");
        sb.AppendLine("seed,populacao,mutacao,cruzamento,fitness,ttv,dtp,tempo_ms,geracoes,motivo_parada");

        foreach (Run run in runs)
        {
            sb.AppendLine(string.Join(',',
                run.Seed.ToString(CultureInfo.InvariantCulture),
                run.Config.PopulationSize.ToString(CultureInfo.InvariantCulture),
                run.Config.MutationRate.ToString(CultureInfo.InvariantCulture),
                run.Config.CrossoverRate.ToString(CultureInfo.InvariantCulture),
                run.Ga.Best.Fitness.ToString("F4", CultureInfo.InvariantCulture),
                run.Ga.Best.TotalTravelTimeSeconds.ToString("F4", CultureInfo.InvariantCulture),
                run.Ga.Best.TotalDistanceMeters.ToString("F4", CultureInfo.InvariantCulture),
                run.ElapsedMs.ToString("F1", CultureInfo.InvariantCulture),
                run.Ga.GenerationsRun.ToString(CultureInfo.InvariantCulture),
                run.Ga.StopReason));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void PrintSummary(IReadOnlyList<Run> runs, TimeSpan total)
    {
        var summary = runs
            .GroupBy(run => run.Config)
            .Select(group =>
            {
                double[] fitness = group.Select(run => (double)run.Ga.Best.Fitness).ToArray();
                double mean = fitness.Average();
                double std = Math.Sqrt(fitness.Sum(f => (f - mean) * (f - mean)) / (fitness.Length - 1)); // desvio amostral
                return (Config: group.Key, Mean: mean, Std: std,
                    TimeMs: group.Average(run => run.ElapsedMs),
                    AtMax: group.Count(run => run.Ga.GenerationsRun == MaxGenerations));
            })
            .OrderBy(s => s.Mean)
            .ToList();

        // Não dominada: nenhuma outra configuração é melhor ou igual em fitness e em tempo ao mesmo
        // tempo, sendo melhor em pelo menos um deles.
        bool IsDominated((Config Config, double Mean, double Std, double TimeMs, int AtMax) s) =>
            summary.Any(o => o.Mean <= s.Mean && o.TimeMs <= s.TimeMs && (o.Mean < s.Mean || o.TimeMs < s.TimeMs));

        Console.WriteLine();
        Console.WriteLine("Rank | configuracao              | fitness medio |  desvio | tempo medio | MaxIter | ");
        for (int i = 0; i < summary.Count; i++)
        {
            var s = summary[i];
            string marks = (IsDominated(s) ? "" : "nao dominada ") + (s.Config.IsPrevious ? "<- ANTERIOR" : "");
            Console.WriteLine($"{i + 1,4} | {s.Config} | {s.Mean,13:F1} | {s.Std,7:F1} | {s.TimeMs / 1000,9:F2} s | {s.AtMax,3}/{Seeds.Length}   | {marks}");
        }

        int currentRank = summary.FindIndex(s => s.Config.IsPrevious) + 1;
        Console.WriteLine();
        Console.WriteLine($"Configuracao anterior ({PreviousConfig}): posicao {currentRank}/{summary.Count} no ranking de fitness medio.");
        Console.WriteLine($"Tempo total da calibracao: {total:hh\\:mm\\:ss} ({runs.Count} execucoes, soma dos AGs {runs.Sum(r => r.ElapsedMs) / 1000:F0} s).");
    }
}
