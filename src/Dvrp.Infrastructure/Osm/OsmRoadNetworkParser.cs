using Dvrp.Domain.Common;
using Dvrp.Domain.Graph;
using OsmSharp.Streams;
using OsmSharp.Tags;
using OsmGeo = OsmSharp.OsmGeo;
using OsmGeoType = OsmSharp.OsmGeoType;
using OsmNode = OsmSharp.Node;
using OsmWay = OsmSharp.Way;

namespace Dvrp.Infrastructure.Osm;

/// <summary>
/// Constrói um <see cref="DirectedGraph"/> (malha viária, formato CSR) a partir de um extrato
/// real do OpenStreetMap: filtra só vias motorizadas, respeita sentido único/rotatórias, aplica
/// velocidade real ou um padrão por categoria, e por fim poda para o Maior Componente
/// Fortemente Conexo antes de gerar o grafo final.
/// </summary>
public sealed class OsmRoadNetworkParser
{
    /// <summary>
    /// Resultado da leitura: o <see cref="DirectedGraph"/> pronto + as contagens de nós/arestas
    /// antes e depois da poda (para relatar quanto da malha bruta era "ilha"/beco sem saída).
    /// </summary>
    public readonly record struct ParseResult(
        DirectedGraph Graph,
        int RawNodeCount,
        int RawEdgeCount,
        int FilteredNodeCount,
        int FilteredEdgeCount);

    // Tags "highway" aceitas como via motorizada
    private static readonly HashSet<string> MotorizedHighwayTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "motorway", "trunk", "primary", "secondary", "tertiary",
        "unclassified", "residential", "living_street", "service",
    };

    public ParseResult ParseFromFile(string osmFilePath)
    {
        // 1ª passada pelo arquivo: só coleta "id do nó OSM -> coordenada". Feita à parte (não
        // assume que <node> vem antes de <way> no XML, mesmo sendo a convenção mais comum).
        Dictionary<long, Node> nodeCoordinatesById = ReadNodeCoordinates(osmFilePath);

        // Nem todo nó do arquivo entra no grafo — só quem é ponta de algum trecho de via
        // motorizada mantida. Por isso os índices do grafo são atribuídos sob demanda aqui,
        // na 2ª passada, em vez de reaproveitar os ids do OSM diretamente.
        var nodeIdToIndex = new Dictionary<long, int>();
        var nodes = new List<Node>();
        var edgesByNode = new List<List<Edge>>();

        int GetOrCreateNodeIndex(long osmNodeId)
        {
            if (nodeIdToIndex.TryGetValue(osmNodeId, out int existingIndex))
            {
                return existingIndex;
            }

            int newIndex = nodes.Count;
            nodes.Add(nodeCoordinatesById[osmNodeId]);
            edgesByNode.Add(new List<Edge>());
            nodeIdToIndex[osmNodeId] = newIndex;
            return newIndex;
        }

        // 2ª passada: agora processa as <way> (vias), uma de cada vez.
        using (FileStream stream = File.OpenRead(osmFilePath))
        {
            var source = new XmlOsmStreamSource(stream);

            foreach (OsmGeo element in source)
            {
                if (element.Type != OsmGeoType.Way || element is not OsmWay way || way.Nodes is null || way.Tags is null)
                {
                    continue;
                }

                // Filtro de vias: só motorizadas seguem adiante.
                if (!TryGetHighwayType(way.Tags, out string highwayType))
                {
                    continue;
                }

                // Direção (mão única/dupla/rotatória) e velocidade valem para a via inteira,
                // então são resolvidas uma vez só, antes de percorrer os pares de nós.
                RoadDirection direction = ResolveDirection(way.Tags);
                float speedKmh = ResolveSpeedKmh(way.Tags, highwayType);

                // Uma <way> é uma sequência de nós (ex.: [A, B, C, D]) que representa a rua
                // inteira; cada PAR CONSECUTIVO (A-B, B-C, C-D) vira um segmento/aresta do grafo.
                long[] wayNodeIds = way.Nodes;
                for (int i = 0; i < wayNodeIds.Length - 1; i++)
                {
                    long fromOsmId = wayNodeIds[i];
                    long toOsmId = wayNodeIds[i + 1];

                    if (!nodeCoordinatesById.ContainsKey(fromOsmId) || !nodeCoordinatesById.ContainsKey(toOsmId))
                    {
                        // Nó referenciado pela via mas ausente do extrato
                        continue;
                    }

                    int fromIndex = GetOrCreateNodeIndex(fromOsmId);
                    int toIndex = GetOrCreateNodeIndex(toOsmId);

                    if (fromIndex == toIndex)
                    {
                        continue; // node duplicado
                    }

                    float distanceMeters = GeoMath.HaversineDistanceMeters(nodes[fromIndex], nodes[toIndex]);
                    float travelTimeSeconds = distanceMeters / (speedKmh / 3.6f); // km/h -> m/s

                    // Aresta "de ida" (from -> to): existe sempre, exceto quando a via é mão
                    // única no sentido CONTRÁRIO ao da lista de nós (oneway=-1).
                    if (direction != RoadDirection.ReverseOnly)
                    {
                        edgesByNode[fromIndex].Add(new Edge(toIndex, travelTimeSeconds, distanceMeters, speedKmh));
                    }

                    // Aresta "de volta" (to -> from): só existe se a via for mão dupla.
                    if (direction != RoadDirection.ForwardOnly)
                    {
                        edgesByNode[toIndex].Add(new Edge(fromIndex, travelTimeSeconds, distanceMeters, speedKmh));
                    }
                }
            }
        }

        Node[] nodesArray = nodes.ToArray();
        List<Edge>[] edgesByNodeArray = edgesByNode.ToArray();

        int rawEdgeCount = 0;
        foreach (List<Edge> edges in edgesByNodeArray)
        {
            rawEdgeCount += edges.Count;
        }

        // Só agora, com o grafo preliminar inteiro montado, poda para o Maior SCC.
        var filter = new StronglyConnectedComponentsFilter();
        StronglyConnectedComponentsFilter.FilterResult filtered = filter.FilterToLargestComponent(nodesArray, edgesByNodeArray);

        float maxSpeedLimitKmh = filtered.AdjacencyList
            .SelectMany(edges => edges)
            .Select(edge => edge.SpeedLimitKmh)
            .DefaultIfEmpty(1f)
            .Max();

        var graph = new DirectedGraph(filtered.Nodes, filtered.AdjacencyList, maxSpeedLimitKmh);

        return new ParseResult(graph, nodesArray.Length, rawEdgeCount, filtered.FilteredNodeCount, filtered.FilteredEdgeCount);
    }

    private static Dictionary<long, Node> ReadNodeCoordinates(string osmFilePath)
    {
        var coordinates = new Dictionary<long, Node>();

        using FileStream stream = File.OpenRead(osmFilePath);
        var source = new XmlOsmStreamSource(stream);

        foreach (OsmGeo element in source)
        {
            if (element.Type != OsmGeoType.Node || element is not OsmNode node)
            {
                continue;
            }

            if (node.Id is null || node.Latitude is null || node.Longitude is null)
            {
                continue;
            }

            coordinates[node.Id.Value] = new Node(node.Latitude.Value, node.Longitude.Value);
        }

        return coordinates;
    }

    private static bool TryGetHighwayType(TagsCollectionBase tags, out string highwayType)
    {
        if (tags.TryGetValue("highway", out string? value) && value is not null && MotorizedHighwayTags.Contains(value))
        {
            highwayType = value;
            return true;
        }

        highwayType = string.Empty;
        return false;
    }

    private enum RoadDirection
    {
        Bidirectional,
        ForwardOnly, // só from -> to (sentido da lista de nós da via)
        ReverseOnly, // só to -> from (oneway=-1: mão única no sentido inverso)
    }

    /// <summary>
    /// "oneway" manda quando presente. Sem ela, só rotatórias (junction=roundabout/circular)
    /// são mão única por padrão — o resto da malha é mão dupla.
    /// </summary>
    private static RoadDirection ResolveDirection(TagsCollectionBase tags)
    {
        if (tags.TryGetValue("oneway", out string? onewayValue) && onewayValue is not null)
        {
            return onewayValue.Trim().ToLowerInvariant() switch
            {
                "-1" => RoadDirection.ReverseOnly,
                "yes" or "1" or "true" => RoadDirection.ForwardOnly,
                _ => RoadDirection.Bidirectional, // "no" / "0" / "false" / valor não reconhecido
            };
        }

        bool isRoundabout = tags.TryGetValue("junction", out string? junctionValue)
            && junctionValue is not null
            && junctionValue.Trim().ToLowerInvariant() is "roundabout" or "circular";

        return isRoundabout ? RoadDirection.ForwardOnly : RoadDirection.Bidirectional;
    }

    /// <summary>
    /// "maxspeed" da tag manda quando presente e parseável; senão cai no padrão por categoria.
    /// </summary>
    private static float ResolveSpeedKmh(TagsCollectionBase tags, string highwayType)
    {
        if (tags.TryGetValue("maxspeed", out string? maxSpeedValue) && maxSpeedValue is not null)
        {
            float? parsed = TryParseMaxSpeedKmh(maxSpeedValue);
            if (parsed is > 0f)
            {
                return parsed.Value;
            }
        }

        return DefaultSpeedKmh(highwayType);
    }

    private static float? TryParseMaxSpeedKmh(string rawValue)
    {
        // Alguns extratos trazem listas (ex.: "110;90", limites diferentes por sentido/faixa)
        // ou sufixos de unidade (ex.: "50 km/h") — usa só o primeiro valor numérico da string.
        string firstToken = rawValue.Split(';', ' ')[0];
        string digits = new(firstToken.TakeWhile(char.IsDigit).ToArray());

        return digits.Length > 0 && int.TryParse(digits, out int kmh) ? kmh : null;
    }

    /// <summary>
    /// Velocidades padrão urbanas brasileiras quando a via não tem "maxspeed". "living_street"
    /// não está na tabela pedida — fica abaixo de "residential" por definição (via de
    /// convivência/pedestres com prioridade), não como categoria à parte.
    /// </summary>
    private static float DefaultSpeedKmh(string highwayType) => highwayType switch
    {
        "motorway" or "trunk" => 80f,
        "primary" => 60f,
        "secondary" => 50f,
        "tertiary" => 40f,
        "living_street" => 20f,
        _ => 30f, //nao-classificadas
    };
}
