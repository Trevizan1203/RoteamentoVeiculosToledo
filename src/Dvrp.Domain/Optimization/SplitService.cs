namespace Dvrp.Domain.Optimization;

using Dvrp.Domain.Logistics;

public sealed class SplitService
{
    private const float ExtraVehiclePenalty = 5000f; // Penalidade se usar mais que m veículos

    public void Evaluate(
        Chromosome chromosome,
        CostMatrix matrix,
        int maxVehicles,
        float vehicleCapacity)
    {
        int n = chromosome.Genes.Length;
        int[] genes = chromosome.Genes;

        // minCost[j]: menor tempo acumulado para atender os primeiros 'j' clientes
        float[] minCost = new float[n + 1];
        int[] predecessor = new int[n + 1];

        Array.Fill(minCost, float.PositiveInfinity);
        minCost[0] = 0f;

        // 1. Testa todos os cortes possíveis para particionar o Giant Tour
        for (int i = 0; i < n; i++)
        {
            // Se o estado 'i' for inalcançável (nenhuma combinação anterior conseguiu chegar aqui), pula
            if (float.IsPositiveInfinity(minCost[i])) 
                continue;

            float load = 0f;

            // Tenta estender a rota do caminhão atual atendendo os clientes da fatia [i .. j-1]
            // Exemplo: i = 0 e j = 1 -> atende só o 1º cliente da fila saindo da base
            // Exemplo: i = 0 e j = 3 -> tenta colocar o 1º, 2º e 3º clientes no mesmo caminhão
            for (int j = i + 1; j <= n; j++)
            {
                // Pega o cliente correspondente no vetor genes (0-based) e acumula o peso no veículo
                int currentClient = genes[j - 1];
                load += matrix.Points[currentClient].Demand;

                // Poda por Capacidade (Q): se o peso passar do limite do caminhão, para de adicionar
                // clientes nesta viagem imediatamente e encerra o loop 'j'
                if (load > vehicleCapacity)
                    break;

                // Calcula o ciclo completo do caminhão: Depósito(0) -> [genes[i..j-1]] -> Depósito(0)
                // Exemplo: se i=0 e j=2, calcula o tempo de: 0 -> C1 -> C2 -> 0
                float subRouteTime = CalculateRouteTime(genes, i, j, matrix);

                // Se o custo acumulado até 'i' + o tempo dessa nova viagem for menor do que a melhor
                // opção já conhecida para atender até o cliente 'j', encontramos um caminho melhor
                if (minCost[i] + subRouteTime < minCost[j])
                {
                    minCost[j] = minCost[i] + subRouteTime;

                    // Grava a "migalha de pão" para o backtracking:
                    // "Para atender até o cliente 'j' do jeito mais rápido, o caminhão começou no cliente 'i'"
                    // Exemplo: se predecessor[3] = 1, a última rota fez os clientes do índice 1 até 3 (C2 e C3)
                    predecessor[j] = i; 
                }
            }
        }

        // 2. Reconstrói as rotas de trás para frente usando o vetor
        List<VehicleRoute> routes = new();
        int curr = n;
        float totalTtv = 0f;
        float totalDtp = 0f;

        while (curr > 0)
        {
            // pego a posicao de inicio da sub-rota
            // exemplo, curr sendo n, saindo iniciando de qual rota consegui atender isso? eh exatamente o que prev eh
            int prev = predecessor[curr];
            int count = curr - prev;
            int[] routeClients = new int[count];

            for (int idx = 0; idx < count; idx++)
            {
                routeClients[idx] = genes[prev + idx];
            }

            // Calcula métricas finais da sub-rota
            CalculateRouteMetrics(
                genes, prev, curr, matrix,
                out float routeDemand,
                out float routeTime,
                out float routeDist
            );

            totalTtv += routeTime;
            totalDtp += routeDist;

            routes.Add(new VehicleRoute(
                vehicleIndex: routes.Count,
                clientIndices: routeClients,
                travelTimeSeconds: routeTime,
                distanceMeters: routeDist,
                totalDemand: routeDemand,
                maxCapacity: vehicleCapacity
            ));

            curr = prev; // Recua para o corte anterior
        }

        routes.Reverse();

        // 3. Aplica penalidade se a quantidade de rotas geradas for maior que a frota m
        float penalty = 0f;
        bool isFeasible = true;

        if (routes.Count > maxVehicles)
        {
            penalty = (routes.Count - maxVehicles) * ExtraVehiclePenalty;
            isFeasible = false;
        }

        chromosome.Fitness = totalTtv + penalty;
        chromosome.TotalTravelTimeSeconds = totalTtv;
        chromosome.TotalDistanceMeters = totalDtp;
        chromosome.IsFeasible = isFeasible;
        chromosome.Routes = routes;
    }

    /// <summary>
    /// Calcula o tempo em segundos da rota: Depósito(0) -> [genes[start..end-1]] -> Depósito(0)
    /// </summary>
    private static float CalculateRouteTime(int[] genes, int start, int end, CostMatrix matrix)
    {
        int first = genes[start];
        int last = genes[end - 1];

        float time = matrix.GetTravelTime(0, first);

        for (int i = start; i < end - 1; i++)
        {
            time += matrix.GetTravelTime(genes[i], genes[i + 1]);
        }

        time += matrix.GetTravelTime(last, 0);
        return time;
    }

    /// <summary>
    /// Calcula demanda, tempo e distância completos para a rota reconstruída.
    /// </summary>
    private static void CalculateRouteMetrics(
        int[] genes, int start, int end, CostMatrix matrix,
        out float demand, out float time, out float distance)
    {
        demand = 0f;
        time = 0f;
        distance = 0f;

        int first = genes[start];
        int last = genes[end - 1];

        time += matrix.GetTravelTime(0, first);
        distance += matrix.GetDistance(0, first);

        for (int i = start; i < end; i++)
        {
            int client = genes[i];
            demand += matrix.Points[client].Demand;

            if (i < end - 1)
            {
                int next = genes[i + 1];
                time += matrix.GetTravelTime(client, next);
                distance += matrix.GetDistance(client, next);
            }
        }

        time += matrix.GetTravelTime(last, 0);
        distance += matrix.GetDistance(last, 0);
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
            maxCapacity: vehicleCapacity
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