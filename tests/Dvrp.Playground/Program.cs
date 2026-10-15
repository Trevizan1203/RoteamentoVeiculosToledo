using System.Globalization;
using Dvrp.Playground.Runners;

// Experimentos do trabalho, sobre a malha viária real de Toledo-PR (Data/toledo.osm):
//
//   dotnet run -c Release -- calibracao
//       Calibração dos parâmetros do AG inicial (./output/dvrp-calibracao.csv).
//
//   dotnet run -c Release -- lote [f ...] [--geracoes N]
//       Lote com o evento durante a operação: f é a fração de clientes já atendidos no instante do
//       evento (padrão: 0 0.25 0.5 0.75). Grava ./output/dvrp-lote-evento-em-operacao.csv (+ .meta.txt).
//       Com --geracoes N, troca o limite de gerações da reotimização (padrão 1000) e grava em
//       ./output/dvrp-lote-orcamento-N.csv, sem sobrescrever o lote principal.
//
// A agregação (tabelas e figuras) é feita depois por scripts/agregar_lote.py, a partir dos CSVs.

string osmFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "toledo.osm");

if (args.Length > 0 && args[0] == "calibracao")
{
    GaCalibrationRunner.RunAll(osmFilePath);
    return;
}

if (args.Length > 0 && args[0] == "lote")
{
    int budgetIndex = Array.IndexOf(args, "--geracoes");
    int reoptimizationMaxGenerations = budgetIndex > 0
        ? int.Parse(args[budgetIndex + 1], CultureInfo.InvariantCulture)
        : DynamicReoptimizationExperiment.GaReoptimizationMaxGenerations;

    double[] servedFractions = args
        .Skip(1)
        .Where((_, i) => budgetIndex < 0 || (i + 1 != budgetIndex && i + 1 != budgetIndex + 1))
        .Select(arg => double.Parse(arg, CultureInfo.InvariantCulture))
        .ToArray();

    bool ok = DynamicReoptimizationBatchRunner.RunAll(
        osmFilePath,
        servedFractions.Length > 0 ? servedFractions : DynamicReoptimizationBatchRunner.ServedFractions,
        reoptimizationMaxGenerations);

    Environment.ExitCode = ok ? 0 : 1;
    return;
}

Console.WriteLine("Uso: dotnet run -c Release -- calibracao | lote [f ...] [--geracoes N]");
Environment.ExitCode = 1;
