namespace Dvrp.Domain.Optimization;

using Dvrp.Domain.Logistics;

/// <summary>
/// Veículo disponível para o Split. A ordem da lista é a ordem em que os veículos recebem os
/// segmentos do giant tour: o 1º veículo atende o 1º segmento, o 2º o seguinte, e assim por diante.
/// </summary>
public readonly record struct SplitVehicle(int VehicleId, float Capacity);

/// <param name="Fitness">J(S): soma dos tempos das rotas + penalidade por rota extra.</param>
/// <param name="ExtraRouteCount">Rotas virtuais usadas além da frota (cada uma paga a penalidade).</param>
public readonly record struct SplitResult(
    IReadOnlyList<VehicleRoute> Routes,
    float Fitness,
    float TotalTravelTimeSeconds,
    float TotalDistanceMeters,
    int ExtraRouteCount);

public sealed class SplitService
{
    public const int DepotIndex = 0;
    private const float ExtraVehiclePenalty = 5000f; // Penalidade por rota além da frota m

    /// <summary>
    /// Split clássico: frota de <paramref name="maxVehicles"/> veículos idênticos no depósito,
    /// todos com capacidade <paramref name="vehicleCapacity"/>.
    /// </summary>
    public void Evaluate(
        Chromosome chromosome,
        CostMatrix matrix,
        int maxVehicles,
        float vehicleCapacity)
    {
        var fleet = new SplitVehicle[maxVehicles];
        for (int v = 0; v < maxVehicles; v++)
        {
            fleet[v] = new SplitVehicle(VehicleId: v, vehicleCapacity);
        }

        SplitResult result = Split(chromosome.Genes, matrix, fleet, extraRouteCapacity: vehicleCapacity);

        chromosome.Fitness = result.Fitness;
        chromosome.TotalTravelTimeSeconds = result.TotalTravelTimeSeconds;
        chromosome.TotalDistanceMeters = result.TotalDistanceMeters;
        chromosome.IsFeasible = result.ExtraRouteCount == 0;
        chromosome.Routes = result.Routes;
    }

    /// <summary>
    /// Particiona o giant tour entre os veículos, NA ORDEM da lista, minimizando J(S).
    ///
    /// cost[k][i] = menor custo para atender os 'i' primeiros clientes do tour usando os 'k'
    /// primeiros veículos; o veículo k atende um segmento contíguo (possivelmente vazio) e volta
    /// ao depósito. A última camada recebe as rotas extras: clientes que sobram depois do último
    /// veículo seguem em rotas virtuais a partir do depósito, cada uma pagando a penalidade.
    ///
    /// Desempate (o mesmo do Split clássico): os inícios de segmento são varridos em ordem
    /// crescente e só uma melhora ESTRITA substitui o candidato, então vence o menor início.
    /// O segmento vazio (início = fim) é o último candidato e só vence se for estritamente melhor.
    /// </summary>
    public SplitResult Split(
        IReadOnlyList<int> giantTour,
        CostMatrix matrix,
        IReadOnlyList<SplitVehicle> vehicles,
        float extraRouteCapacity)
    {
        int n = giantTour.Count;
        int vehicleCount = vehicles.Count;
        int extraLayer = vehicleCount + 1;

        // start[k][i]: onde começa o segmento do veículo k na melhor solução até 'i' (start == i: vazio).
        float[][] cost = new float[extraLayer + 1][];
        int[][] start = new int[extraLayer + 1][];
        for (int k = 0; k <= extraLayer; k++)
        {
            cost[k] = new float[n + 1];
            start[k] = new int[n + 1];
            Array.Fill(cost[k], float.PositiveInfinity);
        }

        cost[0][0] = 0f;

        // 1. Uma camada por veículo, na ordem da lista.
        for (int k = 1; k <= vehicleCount; k++)
        {
            RelaxSegments(giantTour, matrix, cost[k - 1], cost[k], start[k],
                vehicles[k - 1].Capacity, fixedCost: 0f, allowEmpty: true);
        }

        // 2. Rotas extras: partem do custo final da frota e se estendem dentro da mesma camada.
        //    start == i marca "nenhuma rota extra termina aqui".
        Array.Copy(cost[vehicleCount], cost[extraLayer], n + 1);
        for (int i = 0; i <= n; i++)
        {
            start[extraLayer][i] = i;
        }

        RelaxSegments(giantTour, matrix, cost[extraLayer], cost[extraLayer], start[extraLayer],
            extraRouteCapacity, fixedCost: ExtraVehiclePenalty, allowEmpty: false);

        return BuildResult(giantTour, matrix, vehicles, start, extraRouteCapacity);
    }

    /// <summary>
    /// Relaxa uma camada da DP: para cada início 'j' alcançável em <paramref name="from"/>, tenta
    /// fechar um segmento [j .. i-1] com um veículo de capacidade <paramref name="capacity"/>.
    /// </summary>
    private static void RelaxSegments(
        IReadOnlyList<int> tour,
        CostMatrix matrix,
        float[] from,
        float[] to,
        int[] start,
        float capacity,
        float fixedCost,
        bool allowEmpty)
    {
        int n = tour.Count;

        for (int j = 0; j <= n; j++)
        {
            if (float.IsPositiveInfinity(from[j]))
                continue;

            // Segmento vazio: o veículo não atende ninguém (no depósito, custa 0).
            if (allowEmpty && from[j] < to[j])
            {
                to[j] = from[j];
                start[j] = j;
            }

            float load = 0f;
            float pathTime = 0f; // tempo do depósito até o cliente atual, passando pelo segmento

            for (int i = j + 1; i <= n; i++)
            {
                int client = tour[i - 1];
                load += matrix.Points[client].Demand;

                // Poda por capacidade: o segmento só cresce, então nenhum fim maior cabe.
                if (load > capacity)
                    break;

                pathTime += i == j + 1
                    ? matrix.GetTravelTime(DepotIndex, client)
                    : matrix.GetTravelTime(tour[i - 2], client);

                float candidate = from[j] + (pathTime + matrix.GetTravelTime(client, DepotIndex)) + fixedCost;

                if (candidate < to[i])
                {
                    to[i] = candidate;
                    start[i] = j;
                }
            }
        }
    }

    /// <summary>
    /// Reconstrói as rotas de trás para frente (rotas extras, depois veículo k, k-1, ..., 1) e
    /// soma os totais na mesma ordem.
    /// </summary>
    private static SplitResult BuildResult(
        IReadOnlyList<int> tour,
        CostMatrix matrix,
        IReadOnlyList<SplitVehicle> vehicles,
        int[][] start,
        float extraRouteCapacity)
    {
        var routes = new List<VehicleRoute>();
        float totalTtv = 0f;
        float totalDtp = 0f;
        int end = tour.Count;

        void AddRoute(int vehicleId, int from, int to, float capacity, bool isVirtual)
        {
            VehicleRoute route = BuildRoute(tour, from, to, matrix, vehicleId, capacity, isVirtual);
            routes.Add(route);
            totalTtv += route.TravelTimeSeconds;
            totalDtp += route.DistanceMeters;
        }

        int[] extraStart = start[vehicles.Count + 1];
        int extraRouteCount = 0;
        while (extraStart[end] != end)
        {
            extraRouteCount++;
            AddRoute(vehicleId: -extraRouteCount, extraStart[end], end, extraRouteCapacity, isVirtual: true);
            end = extraStart[end];
        }

        for (int k = vehicles.Count; k >= 1; k--)
        {
            int segmentStart = start[k][end];
            if (segmentStart < end)
            {
                AddRoute(vehicles[k - 1].VehicleId, segmentStart, end, vehicles[k - 1].Capacity, isVirtual: false);
            }

            end = segmentStart;
        }

        routes.Reverse();

        return new SplitResult(
            routes,
            Fitness: totalTtv + (extraRouteCount * ExtraVehiclePenalty),
            totalTtv,
            totalDtp,
            extraRouteCount);
    }

    /// <summary>
    /// Rota Depósito -> [tour[start..end-1]] -> Depósito com demanda, tempo e distância completos.
    /// </summary>
    private static VehicleRoute BuildRoute(
        IReadOnlyList<int> tour, int start, int end, CostMatrix matrix,
        int vehicleId, float capacity, bool isVirtual)
    {
        int[] clients = new int[end - start];
        float demand = 0f;

        for (int i = start; i < end; i++)
        {
            clients[i - start] = tour[i];
            demand += matrix.Points[tour[i]].Demand;
        }

        CalculateRouteMetrics(clients, matrix, out float time, out float distance);

        return new VehicleRoute(vehicleId, clients, time, distance, demand, capacity, isVirtual);
    }

    /// <summary>
    /// Calcula tempo e distância de uma rota já definida (sem depósito nas pontas de
    /// <paramref name="clientIndices"/>), somando o ciclo Depósito(0) -> clientes -> Depósito(0).
    /// </summary>
    private static void CalculateRouteMetrics(
        IReadOnlyList<int> clientIndices, CostMatrix matrix,
        out float time, out float distance)
    {
        time = 0f;
        distance = 0f;

        int first = clientIndices[0];
        int last = clientIndices[^1];

        time += matrix.GetTravelTime(0, first);
        distance += matrix.GetDistance(0, first);

        for (int i = 0; i < clientIndices.Count - 1; i++)
        {
            int client = clientIndices[i];
            int next = clientIndices[i + 1];
            time += matrix.GetTravelTime(client, next);
            distance += matrix.GetDistance(client, next);
        }

        time += matrix.GetTravelTime(last, 0);
        distance += matrix.GetDistance(last, 0);
    }

    /// <summary>
    /// Recalcula tempo/distância de uma rota já particionada com uma CostMatrix nova, sem
    /// alterar VehicleIndex/ClientIndices/TotalDemand. Usada pela estratégia estática de
    /// resposta a eventos dinâmicos (mantém o planejamento, só atualiza custos de arestas).
    /// </summary>
    public VehicleRoute RecalculateRoute(VehicleRoute route, CostMatrix matrix, float vehicleCapacity)
    {
        CalculateRouteMetrics(route.ClientIndices, matrix, out float time, out float distance);

        return new VehicleRoute(
            vehicleIndex: route.VehicleIndex,
            clientIndices: route.ClientIndices,
            travelTimeSeconds: time,
            distanceMeters: distance,
            totalDemand: route.TotalDemand,
            maxCapacity: vehicleCapacity,
            isVirtual: route.IsVirtual
        );
    }

    /// <summary>
    /// Recalcula um Chromosome inteiro com uma CostMatrix nova, sem re-particionar (sem DP do
    /// Split): aplica <see cref="RecalculateRoute"/> a cada rota existente, soma os totais e
    /// deriva Fitness/IsFeasible com o mesmo critério de <see cref="Evaluate"/>. Não muta o
    /// Chromosome original.
    /// </summary>
    public Chromosome RecalculateChromosome(
        Chromosome chromosome,
        CostMatrix matrix,
        int maxVehicles,
        float vehicleCapacity)
    {
        List<VehicleRoute> routes = new(chromosome.Routes.Count);
        float totalTtv = 0f;
        float totalDtp = 0f;

        foreach (VehicleRoute route in chromosome.Routes)
        {
            VehicleRoute recalculated = RecalculateRoute(route, matrix, vehicleCapacity);
            routes.Add(recalculated);
            totalTtv += recalculated.TravelTimeSeconds;
            totalDtp += recalculated.DistanceMeters;
        }

        float penalty = 0f;
        
        return new Chromosome((int[])chromosome.Genes.Clone())
        {
            Fitness = totalTtv + penalty,
            TotalTravelTimeSeconds = totalTtv,
            TotalDistanceMeters = totalDtp,
            IsFeasible = chromosome.IsFeasible,
            Routes = routes
        };
    }
}