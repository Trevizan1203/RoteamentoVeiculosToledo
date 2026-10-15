using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dvrp.Domain.Graph;
using Dvrp.Infrastructure.Osm;
using E = Dvrp.Playground.Runners.DynamicReoptimizationExperiment;

namespace Dvrp.Playground.Runners;

/// <summary>
/// Roda o lote de experimentos: para cada seed gera o plano inicial uma vez e, para cada
/// combinação de penalidade, área e fração atendida, executa as três estratégias. O resultado vai
/// para um CSV (uma linha por execução e estratégia) com um arquivo .meta.txt ao lado.
/// </summary>
public static class DynamicReoptimizationBatchRunner
{
    internal static readonly int[] Seeds =
    [
        11007, 22013, 33019, 44029, 55043, 66053, 77061, 88073, 99089, 111097,
    ];

    // Penalidade leve, média e forte.
    private static readonly float[] PenaltyFactors = [1.5f, 3.0f, 5.0f];

    private static readonly double[] EventAreaFractions = [2.0 / 3.0, 1.0 / 3.0];

    public static readonly double[] ServedFractions = [0.0, 0.25, 0.50, 0.75];

    internal const string OutputFileName = "dvrp-lote-evento-em-operacao.csv";

    // Usado quando o lote roda com outro limite de gerações na reotimização, para não sobrescrever
    // o lote principal.
    internal static string BudgetOutputFileName(int reoptimizationMaxGenerations) =>
        $"dvrp-lote-orcamento-{reoptimizationMaxGenerations}.csv";

    private sealed record Row(float PenaltyFactor, double EventAreaFraction, E.EventRunResult Run);

    public static bool RunAll(
        string osmFilePath, IReadOnlyList<double>? servedFractions = null, int reoptimizationMaxGenerations = E.GaReoptimizationMaxGenerations)
    {
        servedFractions ??= ServedFractions;
        bool isDefaultBudget = reoptimizationMaxGenerations == E.GaReoptimizationMaxGenerations;

        Console.WriteLine("======================================================");
        Console.WriteLine(" Lote - reotimizacao com evento durante a operacao");
        Console.WriteLine("======================================================");

        DirectedGraph graph = new OsmRoadNetworkParser().ParseFromFile(osmFilePath).Graph;
        int total = Seeds.Length * PenaltyFactors.Length * EventAreaFractions.Length * servedFractions.Count;
        Console.WriteLine($"Malha: {graph.NodeCount} nos, {graph.EdgeCount} arestas. Execucoes: {total} " +
            $"({Seeds.Length} seeds x {PenaltyFactors.Length} penalidades x {EventAreaFractions.Length} areas x {servedFractions.Count} fracoes).");

        var rows = new List<Row>();
        var initialPlans = new List<E.InitialPlan>();
        var failures = new List<string>();
        Stopwatch batchClock = Stopwatch.StartNew();
        int index = 0;

        foreach (int seed in Seeds)
        {
            E.InitialPlan plan = E.BuildInitialPlan(graph, seed);
            initialPlans.Add(plan);
            Console.WriteLine($"AG inicial seed {seed}: fitness {plan.Ga.Best.Fitness:F1}, {plan.Ga.GenerationsRun} geracoes ({plan.Ga.StopReason}).");

            foreach (float penaltyFactor in PenaltyFactors)
            {
                foreach (double areaFraction in EventAreaFractions)
                {
                    foreach (double servedFraction in servedFractions)
                    {
                        index++;
                        string label = $"seed={seed}, penalidade={penaltyFactor}, area={areaFraction:P0}, f={servedFraction}";
                        Console.Write($"[{index}/{total}] {label} ... ");

                        try
                        {
                            E.EventRunResult run = E.RunEventDuringOperation(graph, plan, penaltyFactor, areaFraction, servedFraction, reoptimizationMaxGenerations);
                            rows.Add(new Row(penaltyFactor, areaFraction, run));
                            Console.WriteLine($"{run.PendingCount} pendentes, {run.ActiveCount} ativos{(run.IsTrivial ? ", TRIVIAL" : "")}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"FALHOU: {ex.Message}");
                            failures.Add($"{label} -> {ex.Message}");
                        }
                    }
                }
            }
        }

        batchClock.Stop();

        string outputDirectory = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        string csvPath = Path.Combine(outputDirectory, isDefaultBudget ? OutputFileName : BudgetOutputFileName(reoptimizationMaxGenerations));
        WriteCsv(csvPath, rows, failures);
        WriteMetadata(csvPath + ".meta.txt", servedFractions, batchClock.Elapsed, rows.Count, failures.Count, initialPlans,
            reoptimizationMaxGenerations);

        Console.WriteLine();
        Console.WriteLine($"Lote concluido em {batchClock.Elapsed:hh\\:mm\\:ss}: {rows.Count}/{total} execucoes, {failures.Count} falha(s).");
        Console.WriteLine($"Casos triviais: {rows.Count(r => r.Run.IsTrivial)}. " +
            $"Execucoes com rotas extras: {rows.Count(r => r.Run.Strategies.Any(s => s.ExtraRouteCount > 0))}.");
        Console.WriteLine($"AG inicial no limite de {E.GaInitialMaxGenerations} geracoes: " +
            $"{initialPlans.Count(p => p.Ga.GenerationsRun == E.GaInitialMaxGenerations)}/{initialPlans.Count} seeds.");
        foreach (string strategy in new[] { E.StaticStrategy, E.RestartStrategy, E.ReuseStrategy })
        {
            Console.WriteLine($"Linhas com rotas extras ({strategy}): " +
                $"{rows.Count(r => r.Run.Strategies.Any(s => s.Strategy == strategy && s.ExtraRouteCount > 0))}.");
        }

        Console.WriteLine($"CSV: {csvPath}");

        return failures.Count == 0;
    }

    private static void WriteCsv(string path, IReadOnlyList<Row> rows, IReadOnlyList<string> failures)
    {
        var sb = new StringBuilder();
        foreach (string failure in failures)
        {
            sb.AppendLine($"# FALHA: {failure}");
        }

        sb.AppendLine("seed,penalty_factor,event_area_fraction,f,t,pendentes,ativos,estrategia,ttv_total,ttv_pos," +
            "dtp_total,dtp_pos,tr_ms,trp,geracoes,fitness,rotas_extras,trivial,fitness_inicial,afeta_matriz");

        foreach (Row row in rows)
        {
            E.EventRunResult run = row.Run;
            foreach (E.StrategyOutcome s in run.Strategies)
            {
                sb.AppendLine(string.Join(',',
                    run.Seed.ToString(CultureInfo.InvariantCulture),
                    Invariant(row.PenaltyFactor),
                    Invariant(row.EventAreaFraction),
                    Invariant(run.ServedFraction),
                    run.EventTimeSeconds.ToString("F4", CultureInfo.InvariantCulture),
                    run.PendingCount.ToString(CultureInfo.InvariantCulture),
                    run.ActiveCount.ToString(CultureInfo.InvariantCulture),
                    s.Strategy,
                    s.TtvTotal.ToString("F4", CultureInfo.InvariantCulture),
                    s.TtvPos.ToString("F4", CultureInfo.InvariantCulture),
                    s.DtpTotal.ToString("F4", CultureInfo.InvariantCulture),
                    s.DtpPos.ToString("F4", CultureInfo.InvariantCulture),
                    s.TrMs.ToString("F4", CultureInfo.InvariantCulture),
                    s.TrpPercent?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                    s.GenerationsRun?.ToString(CultureInfo.InvariantCulture) ?? "",
                    s.Fitness.ToString("F4", CultureInfo.InvariantCulture),
                    s.ExtraRouteCount.ToString(CultureInfo.InvariantCulture),
                    run.IsTrivial ? "1" : "0",
                    s.InitialFitness?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                    run.AffectsMatrix ? "1" : "0"));
            }
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteMetadata(
        string path, IReadOnlyList<double> servedFractions, TimeSpan elapsed, int runs, int failures, IReadOnlyList<E.InitialPlan> initialPlans,
        int reoptimizationMaxGenerations)
    {
        string initialRuns = string.Join(Environment.NewLine, initialPlans.Select(p =>
            $"  seed {p.Seed}: fitness {p.Ga.Best.Fitness.ToString("F4", CultureInfo.InvariantCulture)}, " +
            $"{p.Ga.GenerationsRun} geracoes ({p.Ga.StopReason}), {p.Ga.Best.Routes.Count} rotas, frota m = {p.FleetSize}"));

        string purpose = reoptimizationMaxGenerations == E.GaReoptimizationMaxGenerations
            ? "Lote principal."
            : $"RODADA DE SENSIBILIDADE DE ORCAMENTO: reinicio e reaproveitamento com limite de {reoptimizationMaxGenerations} " +
              $"geracoes (lote principal: {E.GaReoptimizationMaxGenerations}); estagnacao 100 mantida. Nao substitui o lote principal.";
        string metadata = $"""
            {purpose}
            Gerado por DynamicReoptimizationBatchRunner em {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            Commit: {GitCommitHash()}
            Duracao do lote: {elapsed:hh\:mm\:ss} ({runs} execucoes, {failures} falhas)

            Instancia: {E.DeliveryPointCount} pontos (1 deposito), demanda {E.MinDemand}..{E.MaxDemand}, Q = {E.VehicleCapacity}, frota m = ceil(demanda/Q) + 20
            Seeds: {string.Join(", ", Seeds)}
            AG (escolhido na calibracao, dvrp-calibracao.csv): populacao {E.GaPopulationSize}, cruzamento {Invariant(E.GaCrossoverRate)}, mutacao {Invariant(E.GaMutationRate)} (swap/reverse 50%), elite 1, estagnacao 100 geracoes, penalidade 5000 por rota extra
              Limite de geracoes: AG inicial (planejamento, sem restricao de tempo) {E.GaInitialMaxGenerations}; reotimizacao (reinicio e reaproveitamento) {reoptimizationMaxGenerations}
            Estrategias dinamicas: engine novo com new Random(seed); reinicio = CreateRandomPopulation, reaproveitamento = AdaptPopulation(populacao final do AG inicial)
            Evento: canto nordeste do bounding box, fracoes de area {string.Join(", ", EventAreaFractions.Select(Invariant))}, penalidades {string.Join(", ", PenaltyFactors.Select(Invariant))}
            Instante do evento: fracoes de clientes atendidos {string.Join(", ", servedFractions.Select(Invariant))} do plano inicial;
              t no meio do intervalo com exatamente floor(f*n) atendidos (regra da ancora); se um empate impedir, o primeiro intervalo seguinte. f = 0: snapshot Initial (antes da partida).
            Metricas: *_pos = so o restante dos veiculos ativos (ancora -> deposito); *_total = operacao inteira, todos os veiculos
              (executado ate a ancora + restante da estrategia; concluidos seguem a geometria original no grafo penalizado, iguais nas 3 estrategias).
            TR: aplicar evento + snapshot + contexto + populacao inicial + AG (estatica: so o recalculo de custo, nao comparavel).
            fitness_inicial: melhor fitness da populacao inicial (reinicio: aleatoria; reaproveitamento: adaptada), antes da 1a geracao.
            afeta_matriz: 1 se alguma entrada calculada da matriz do contexto difere entre o grafo base e o penalizado (fora do TR).
            TRP (so reaproveitamento): pares de clientes pendentes na mesma rota no top {Invariant(E.DefaultTrpTopFraction)} da populacao antes do evento
              que continuam juntos no top da populacao final.

            AG inicial por seed:
            {initialRuns}
            """;

        File.WriteAllText(path, metadata, Encoding.UTF8);
    }

    private static string Invariant(float value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string GitCommitHash()
    {
        try
        {
            using Process git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                WorkingDirectory = ResolveOutputDirectory(),
            })!;
            string hash = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 ? hash : "desconhecido";
        }
        catch (Exception)
        {
            return "desconhecido";
        }
    }

    /// <summary>Pasta output/ na raiz do projeto (procura o .csproj a partir do diretório do executável).</summary>
    internal static string ResolveOutputDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && directory.GetFiles("*.csproj").Length == 0)
        {
            directory = directory.Parent;
        }

        string projectRoot = directory?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(projectRoot, "output");
    }
}
