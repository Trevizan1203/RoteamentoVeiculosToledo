namespace Dvrp.Domain.Optimization;

using Dvrp.Domain.Logistics;

/// <summary>
/// Veículo disponível para o Split. A ordem da lista é a ordem em que os veículos recebem os
/// segmentos do giant tour: o 1º veículo atende o 1º segmento, o 2º o seguinte, e assim por diante.
/// </summary>
/// <param name="OriginIndex">Índice na CostMatrix de onde o veículo parte (depósito ou posição atual).</param>
/// <param name="Capacity">Capacidade disponível (Q, ou a residual em reotimização).</param>
public readonly record struct SplitVehicle(int VehicleId, int OriginIndex, float Capacity);

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
            fleet[v] = new SplitVehicle(VehicleId: v, DepotIndex, vehicleCapacity);
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
    /// Frota só de veículos idênticos no depósito (capacidade Q): a dimensão de veículo é
    /// redundante, e vale o Split clássico de Prins, O(n·L), com a penalidade por rota excedente
    /// aplicada DEPOIS da partição — exatamente o comportamento original. Com origens ou
    /// capacidades diferentes, vale a DP por veículo, com a penalidade DENTRO da DP. As duas
    /// formas só divergem quando a partição passa de m rotas, o que fica registrado em
    /// <see cref="SplitResult.ExtraRouteCount"/>.
    ///
    /// DP por veículo: cost[k][i] = menor custo para atender os 'i' primeiros clientes do tour
    /// usando os 'k' primeiros veículos; o veículo k sai da SUA origem, atende um segmento
    /// contíguo (possivelmente vazio) e volta ao depósito. A última camada recebe as rotas extras:
    /// clientes que sobram depois do último veículo seguem em rotas virtuais a partir do
    /// depósito, cada uma pagando a penalidade.
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
        List<Segment> segments = IsUniformDepotFleet(vehicles, extraRouteCapacity)
            ? SplitWithoutFleetLimit(giantTour, matrix, vehicles.Count, extraRouteCapacity)
            : SplitByVehicle(giantTour, matrix, vehicles, extraRouteCapacity);

        return BuildResult(giantTour, matrix, vehicles, segments, extraRouteCapacity);
    }

    /// <summary>Segmento [Start .. End-1] do tour; VehicleSlot é a posição na lista, ou -1 para rota extra.</summary>
    private readonly record struct Segment(int VehicleSlot, int Start, int End);

    private static bool IsUniformDepotFleet(IReadOnlyList<SplitVehicle> vehicles, float capacity) =>
        vehicles.All(vehicle => vehicle.OriginIndex == DepotIndex && vehicle.Capacity == capacity);

    /// <summary>
    /// Split clássico: melhor partição sem limite de frota. As primeiras rotas do tour ficam com
    /// os veículos, na ordem da lista; as que passam do tamanho da frota viram rotas extras.
    /// </summary>
    private static List<Segment> SplitWithoutFleetLimit(
        IReadOnlyList<int> giantTour, CostMatrix matrix, int vehicleCount, float capacity)
    {
        int n = giantTour.Count;
        float[] best = new float[n + 1];
        int[] start = new int[n + 1];
        Array.Fill(best, float.PositiveInfinity);
        best[0] = 0f;

        RelaxSegments(giantTour, matrix, best, best, start, DepotIndex, capacity, fixedCost: 0f, allowEmpty: false);

        int routeCount = 0;
        for (int end = n; end > 0; end = start[end])
        {
            routeCount++;
        }

        // Reconstrução de trás para frente: começa pela última rota do tour.
        var segments = new List<Segment>(routeCount);
        int position = routeCount - 1;
        for (int end = n; end > 0; end = start[end], position--)
        {
            segments.Add(new Segment(position < vehicleCount ? position : -1, start[end], end));
        }

        return segments;
    }

    private static List<Segment> SplitByVehicle(
        IReadOnlyList<int> giantTour, CostMatrix matrix, IReadOnlyList<SplitVehicle> vehicles, float extraRouteCapacity)
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
            SplitVehicle vehicle = vehicles[k - 1];
            RelaxSegments(giantTour, matrix, cost[k - 1], cost[k], start[k],
                vehicle.OriginIndex, vehicle.Capacity, fixedCost: 0f, allowEmpty: true);
        }

        // 2. Rotas extras: partem do custo final da frota e se estendem dentro da mesma camada.
        //    start == i marca "nenhuma rota extra termina aqui".
        Array.Copy(cost[vehicleCount], cost[extraLayer], n + 1);
        for (int i = 0; i <= n; i++)
        {
            start[extraLayer][i] = i;
        }

        RelaxSegments(giantTour, matrix, cost[extraLayer], cost[extraLayer], start[extraLayer],
            DepotIndex, extraRouteCapacity, fixedCost: ExtraVehiclePenalty, allowEmpty: false);

        // 3. Reconstrução de trás para frente: rotas extras, depois veículo K, K-1, ..., 1.
        var segments = new List<Segment>();
        int end = n;
        int[] extraStart = start[extraLayer];

        while (extraStart[end] != end)
        {
            segments.Add(new Segment(VehicleSlot: -1, extraStart[end], end));
            end = extraStart[end];
        }

        for (int k = vehicleCount; k >= 1; k--)
        {
            int segmentStart = start[k][end];

            // Segmento vazio fora do depósito ainda é uma rota: a volta da origem ao depósito.
            if (segmentStart < end || vehicles[k - 1].OriginIndex != DepotIndex)
            {
                segments.Add(new Segment(k - 1, segmentStart, end));
            }

            end = segmentStart;
        }

        return segments;
    }

    /// <summary>
    /// Relaxa uma camada da DP: para cada início 'j' alcançável em <paramref name="from"/>, tenta
    /// fechar um segmento [j .. i-1] com um veículo que parte de <paramref name="origin"/>.
    /// </summary>
    private static void RelaxSegments(
        IReadOnlyList<int> tour,
        CostMatrix matrix,
        float[] from,
        float[] to,
        int[] start,
        int origin,
        float capacity,
        float fixedCost,
        bool allowEmpty)
    {
        int n = tour.Count;

        // Segmento vazio: o veículo não atende ninguém, mas está na rua e precisa voltar.
        // Saindo do depósito, custa 0.
        float emptyCost = matrix.GetTravelTime(origin, DepotIndex);

        for (int j = 0; j <= n; j++)
        {
            if (float.IsPositiveInfinity(from[j]))
                continue;

            if (allowEmpty && from[j] + emptyCost < to[j])
            {
                to[j] = from[j] + emptyCost;
                start[j] = j;
            }

            float load = 0f;
            float pathTime = 0f; // tempo da origem até o cliente atual, passando pelo segmento

            for (int i = j + 1; i <= n; i++)
            {
                int client = tour[i - 1];
                load += matrix.Points[client].Demand;

                // Poda por capacidade: o segmento só cresce, então nenhum fim maior cabe.
                if (load > capacity)
                    break;

                pathTime += i == j + 1
                    ? matrix.GetTravelTime(origin, client)
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
    /// Monta as rotas a partir dos segmentos (na ordem de trás para frente da reconstrução) e
    /// soma os totais nessa mesma ordem.
    /// </summary>
    private static SplitResult BuildResult(
        IReadOnlyList<int> tour,
        CostMatrix matrix,
        IReadOnlyList<SplitVehicle> vehicles,
        List<Segment> segments,
        float extraRouteCapacity)
    {
        var routes = new List<VehicleRoute>(segments.Count);
        float totalTtv = 0f;
        float totalDtp = 0f;
        int extraRouteCount = 0;

        foreach (Segment segment in segments)
        {
            VehicleRoute route;
            if (segment.VehicleSlot < 0)
            {
                extraRouteCount++;
                route = BuildRoute(tour, segment.Start, segment.End, matrix,
                    vehicleId: -extraRouteCount, DepotIndex, extraRouteCapacity, isVirtual: true);
            }
            else
            {
                SplitVehicle vehicle = vehicles[segment.VehicleSlot];
                route = BuildRoute(tour, segment.Start, segment.End, matrix,
                    vehicle.VehicleId, vehicle.OriginIndex, vehicle.Capacity, isVirtual: false);
            }

            routes.Add(route);
            totalTtv += route.TravelTimeSeconds;
            totalDtp += route.DistanceMeters;
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
    /// Rota Origem -> [tour[start..end-1]] -> Depósito com demanda, tempo e distância completos.
    /// </summary>
    private static VehicleRoute BuildRoute(
        IReadOnlyList<int> tour, int start, int end, CostMatrix matrix,
        int vehicleId, int origin, float capacity, bool isVirtual)
    {
        int[] clients = new int[end - start];
        float demand = 0f;

        for (int i = start; i < end; i++)
        {
            clients[i - start] = tour[i];
            demand += matrix.Points[tour[i]].Demand;
        }

        CalculateRouteMetrics(origin, clients, matrix, out float time, out float distance);

        return new VehicleRoute(vehicleId, clients, time, distance, demand, capacity, isVirtual, origin);
    }

    /// <summary>
    /// Calcula tempo e distância de uma rota já definida, somando o percurso
    /// Origem -> clientes -> Depósito(0). Sem clientes, é só a volta da origem ao depósito.
    /// </summary>
    private static void CalculateRouteMetrics(
        int origin, IReadOnlyList<int> clientIndices, CostMatrix matrix,
        out float time, out float distance)
    {
        time = 0f;
        distance = 0f;
        int previous = origin;

        foreach (int client in clientIndices)
        {
            time += matrix.GetTravelTime(previous, client);
            distance += matrix.GetDistance(previous, client);
            previous = client;
        }

        time += matrix.GetTravelTime(previous, DepotIndex);
        distance += matrix.GetDistance(previous, DepotIndex);
    }

    /// <summary>
    /// Recalcula tempo/distância de uma rota já particionada com uma CostMatrix nova, sem
    /// alterar VehicleIndex/ClientIndices/TotalDemand. Usada pela estratégia estática de
    /// resposta a eventos dinâmicos (mantém o planejamento, só atualiza custos de arestas).
    /// </summary>
    public VehicleRoute RecalculateRoute(VehicleRoute route, CostMatrix matrix, float vehicleCapacity)
    {
        CalculateRouteMetrics(route.OriginIndex, route.ClientIndices, matrix, out float time, out float distance);

        return new VehicleRoute(
            vehicleIndex: route.VehicleIndex,
            clientIndices: route.ClientIndices,
            travelTimeSeconds: time,
            distanceMeters: distance,
            totalDemand: route.TotalDemand,
            maxCapacity: vehicleCapacity,
            isVirtual: route.IsVirtual,
            originIndex: route.OriginIndex
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