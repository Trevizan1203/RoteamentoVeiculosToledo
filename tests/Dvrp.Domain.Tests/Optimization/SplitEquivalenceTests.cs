using Dvrp.Domain.Graph;
using Dvrp.Domain.Logistics;
using Dvrp.Domain.Optimization;
using Dvrp.Domain.Tests.Oracles;
using Dvrp.Infrastructure.Osm;

namespace Dvrp.Domain.Tests.Optimization;

/// <summary>
/// Com m veículos no depósito, o Split atual deve dar exatamente o mesmo custo e a mesma partição
/// do Split antigo (<see cref="LegacySplitService"/>), mesmo quando passa de m rotas.
/// </summary>
public class SplitEquivalenceTests(ToledoFixture toledo) : IClassFixture<ToledoFixture>
{
    private const float Capacity = 50f;

    [Fact]
    public void InstanciaPequena_CentenasDeToursAleatorios()
    {
        var random = new Random(7);
        CostMatrix matrix = RandomAsymmetricMatrix(random, clientCount: 12);
        int withinFleet = 0;

        for (int t = 0; t < 500; t++)
        {
            int maxVehicles = random.Next(1, 9); // ora sobra frota, ora falta
            if (AssertEquivalent(matrix, RandomTour(random, 12), maxVehicles, Capacity))
            {
                withinFleet++;
            }
        }

        // Os dois regimes precisam ter sido exercitados.
        Assert.InRange(withinFleet, 50, 450);
    }

    [Fact]
    public void MatrizDeToledo_ToursAleatorios()
    {
        var random = new Random(11007);
        int clientCount = toledo.Matrix.Size - 1;

        for (int t = 0; t < 200; t++)
        {
            // Frota dos experimentos (limite inferior + 20) e frota apertada (só o limite inferior).
            int maxVehicles = t % 2 == 0 ? toledo.MinVehicles + 20 : toledo.MinVehicles;
            AssertEquivalent(toledo.Matrix, RandomTour(random, clientCount), maxVehicles, ToledoFixture.Capacity);
        }
    }

    [Fact]
    public void FrotaUniformeNoDeposito_UsaOCaminhoClassicoComPenalidadeDepois()
    {
        // m = 1. A e B ficam a 5 s do depósito, mas a 1000 s um do outro: o melhor sem limite de
        // frota é [A], [B] (20 s), uma rota acima de m.
        CostMatrix matrix = MatrixFrom(
            demands: [0, 10, 10],
            times: new float[,] { { 0, 5, 5 }, { 5, 0, 1000 }, { 5, 1000, 0 } });
        var split = new SplitService();

        // Frota uniforme: caminho clássico, com a penalidade aplicada depois da partição.
        SplitResult classic = split.Split([1, 2], matrix, [new SplitVehicle(0, SplitService.DepotIndex, 100f)], 100f);
        Assert.Equal(20f + 5000f, classic.Fitness);
        Assert.Equal(1, classic.ExtraRouteCount);

        // Com capacidade diferente de Q entra a DP por veículo, que já considera a penalidade e
        // prefere [A, B] num único veículo.
        SplitResult byVehicle = split.Split([1, 2], matrix, [new SplitVehicle(0, SplitService.DepotIndex, 90f)], 100f);
        Assert.Equal(1010f, byVehicle.Fitness);
        Assert.Equal(0, byVehicle.ExtraRouteCount);
    }

    [Fact]
    public void RotasExtras_SaoVirtuaisContadasEPenalizadas()
    {
        // m = 1 e cada cliente ocupa 30 de 50: o segundo não cabe no único veículo.
        CostMatrix matrix = MatrixFrom(
            demands: [0, 30, 30],
            times: new float[,] { { 0, 5, 5 }, { 5, 0, 1 }, { 5, 1, 0 } });

        var chromosome = new Chromosome([1, 2]);
        new SplitService().Evaluate(chromosome, matrix, maxVehicles: 1, vehicleCapacity: 50f);

        Assert.Equal(1, chromosome.ExtraRouteCount);
        Assert.False(chromosome.IsFeasible);
        Assert.Equal(20f + 5000f, chromosome.Fitness);
        Assert.Equal([false, true], chromosome.Routes.Select(route => route.IsVirtual));
        Assert.Equal(0, chromosome.Routes[0].VehicleIndex);
    }

    /// <summary>Compara os dois Splits num tour; devolve true se o antigo coube na frota.</summary>
    private static bool AssertEquivalent(CostMatrix matrix, int[] tour, int maxVehicles, float capacity)
    {
        var legacy = new Chromosome((int[])tour.Clone());
        new LegacySplitService().Evaluate(legacy, matrix, maxVehicles, capacity);
        var unified = new Chromosome((int[])tour.Clone());
        new SplitService().Evaluate(unified, matrix, maxVehicles, capacity);

        Assert.Equal(legacy.Fitness, unified.Fitness);
        Assert.Equal(legacy.TotalTravelTimeSeconds, unified.TotalTravelTimeSeconds);
        Assert.Equal(legacy.TotalDistanceMeters, unified.TotalDistanceMeters);
        Assert.Equal(legacy.IsFeasible, unified.IsFeasible);
        Assert.Equal(legacy.Routes.Count, unified.Routes.Count);

        for (int r = 0; r < legacy.Routes.Count; r++)
        {
            Assert.Equal(legacy.Routes[r].ClientIndices, unified.Routes[r].ClientIndices);
            Assert.Equal(legacy.Routes[r].TravelTimeSeconds, unified.Routes[r].TravelTimeSeconds);
            Assert.Equal(legacy.Routes[r].DistanceMeters, unified.Routes[r].DistanceMeters);
            Assert.Equal(legacy.Routes[r].TotalDemand, unified.Routes[r].TotalDemand);
        }

        Assert.Equal(Math.Max(0, legacy.Routes.Count - maxVehicles), unified.ExtraRouteCount);
        return legacy.Routes.Count <= maxVehicles;
    }

    private static int[] RandomTour(Random random, int clientCount) =>
        Enumerable.Range(1, clientCount).OrderBy(_ => random.Next()).ToArray();

    private static CostMatrix RandomAsymmetricMatrix(Random random, int clientCount)
    {
        int size = clientCount + 1;
        var times = new float[size, size];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                times[i, j] = i == j ? 0f : (float)(1 + (random.NextDouble() * 99));
            }
        }

        int[] demands = Enumerable.Range(0, size).Select(i => i == 0 ? 0 : random.Next(5, 26)).ToArray();
        return MatrixFrom(demands, times);
    }

    // A distância é 7 vezes o tempo, só para o DTP também entrar na comparação.
    private static CostMatrix MatrixFrom(int[] demands, float[,] times)
    {
        int size = demands.Length;
        LogisticPoint[] points = demands.Select((demand, i) => new LogisticPoint(i, 0, 0, demand, i)).ToArray();
        float[] flatTimes = times.Cast<float>().ToArray();
        return new CostMatrix(points, flatTimes, flatTimes.Select(time => time * 7f).ToArray());
    }
}

/// <summary>
/// Instância no formato dos experimentos sobre a malha real (300 pontos, demanda 5..25, Q = 100),
/// carregada uma única vez para a classe de testes.
/// </summary>
public sealed class ToledoFixture
{
    public const float Capacity = 100f;

    public DirectedGraph Graph { get; }
    public CostMatrix Matrix { get; }
    public int MinVehicles { get; }

    public ToledoFixture()
    {
        DirectedGraph graph = new OsmRoadNetworkParser().ParseFromFile(FindOsmFile()).Graph;
        Graph = graph;
        var random = new Random(42);

        DeliveryPoint[] points = Enumerable.Range(0, 300)
            .Select(i =>
            {
                Node node = graph.GetNode(random.Next(graph.NodeCount));
                return new DeliveryPoint(i, node.Latitude, node.Longitude, i == 0 ? 0 : random.Next(5, 26));
            })
            .ToArray();

        Matrix = new CostMatrixService().BuildMatrix(graph, points);
        MinVehicles = (int)Math.Ceiling(points.Sum(point => point.Demand) / Capacity);
    }

    private static string FindOsmFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "Dvrp.Playground", "Data", "toledo.osm");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("toledo.osm nao encontrado acima do diretorio de testes.");
    }
}
