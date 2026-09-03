using Dvrp.Domain.Graph;

namespace Dvrp.Infrastructure.Osm;

/// <summary>
/// Reduz um grafo viário preliminar ao seu Maior Componente Fortemente Conexo (Algoritmo de
/// Tarjan) — necessário porque Dijkstra/CostMatrix exigem que qualquer par de nós seja
/// mutuamente alcançável, e uma malha real extraída do OSM quase sempre tem ilhas e becos sem
/// saída (ex.: ruas de mão única que não fecham um ciclo de volta).
/// </summary>
public sealed class StronglyConnectedComponentsFilter
{
    public readonly record struct FilterResult(
        Node[] Nodes,
        Edge[][] AdjacencyList,
        int OriginalNodeCount,
        int OriginalEdgeCount)
    {
        public int FilteredNodeCount => Nodes.Length;
        public int FilteredEdgeCount => AdjacencyList.Sum(edges => edges.Length);
    }

    /// <summary>
    /// Recebe o grafo preliminar como <paramref name="nodes"/> (coordenadas, indexadas por
    /// posição) + <paramref name="edgesByNode"/> (lista de arestas de saída de cada nó, mesmo
    /// índice) e devolve só o Maior SCC, com os nós sobreviventes remapeados para 0..N-1.
    /// </summary>
    public FilterResult FilterToLargestComponent(Node[] nodes, List<Edge>[] edgesByNode)
    {
        if (nodes.Length != edgesByNode.Length)
        {
            throw new ArgumentException("A quantidade de nós deve corresponder à quantidade de listas de arestas.", nameof(edgesByNode));
        }

        int nodeCount = nodes.Length;

        int originalEdgeCount = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            originalEdgeCount += edgesByNode[i].Count;
        }

        // Passo 1: rotula cada nó com o id do seu SCC (0, 1, 2, ...) via Tarjan.
        (int[] componentId, int componentCount) = ComputeStronglyConnectedComponents(nodeCount, edgesByNode);

        // Passo 2: descobre qual desses SCCs é o maior (mais nós).
        int largestComponentId = FindLargestComponentId(componentId, componentCount);

        // Passo 3: monta o mapa "índice antigo -> índice novo", pulando quem ficou de fora do
        // maior SCC. Só quem está no mapa (valor != -1) sobrevive no grafo final.
        int[] oldToNewIndex = new int[nodeCount];
        Array.Fill(oldToNewIndex, -1);

        var filteredNodes = new List<Node>();
        for (int oldIndex = 0; oldIndex < nodeCount; oldIndex++)
        {
            if (componentId[oldIndex] == largestComponentId)
            {
                oldToNewIndex[oldIndex] = filteredNodes.Count;
                filteredNodes.Add(nodes[oldIndex]);
            }
        }

        // Passo 4: monta a lista de adjacência final — uma entrada por nó sobrevivente, já com
        // os índices de destino remapeados. Sem array plano nem offsets: cada nó carrega
        // diretamente o próprio array de arestas de saída.
        var adjacencyList = new Edge[filteredNodes.Count][];

        for (int oldIndex = 0; oldIndex < nodeCount; oldIndex++)
        {
            if (componentId[oldIndex] != largestComponentId)
            {
                continue;
            }

            var survivingEdges = new List<Edge>();

            foreach (Edge edge in edgesByNode[oldIndex])
            {
                // Só mantém a aresta se o destino também sobreviveu no Maior SCC — uma aresta
                // para fora do componente levaria a um nó sem garantia de caminho de volta.
                if (componentId[edge.TargetNodeIndex] == largestComponentId)
                {
                    survivingEdges.Add(edge with { TargetNodeIndex = oldToNewIndex[edge.TargetNodeIndex] });
                }
            }

            adjacencyList[oldToNewIndex[oldIndex]] = survivingEdges.ToArray();
        }

        return new FilterResult(filteredNodes.ToArray(), adjacencyList, nodeCount, originalEdgeCount);
    }

    /// <summary>
    /// Conta quantos nós cada componente tem e devolve o id do maior. Como
    /// <see cref="ComputeStronglyConnectedComponents"/> numera os componentes em sequência
    /// (0, 1, 2, ...), basta um array de contagem — dispensa um Dictionary.
    /// </summary>
    private static int FindLargestComponentId(int[] componentId, int componentCount)
    {
        var sizePerComponent = new int[componentCount];
        foreach (int id in componentId)
        {
            sizePerComponent[id]++;
        }

        int largestComponentId = 0;
        for (int id = 1; id < componentCount; id++)
        {
            if (sizePerComponent[id] > sizePerComponent[largestComponentId])
            {
                largestComponentId = id;
            }
        }

        return largestComponentId;
    }

    /// <summary>
    /// Algoritmo de Tarjan para encontrar todos os Componentes Fortemente Conexos.
    ///
    /// Ideia central: faz uma DFS guardando, para cada nó, em que "ordem de descoberta" ele foi
    /// visitado (<c>discoveryIndex</c>) e o menor índice de descoberta alcançável a partir dele
    /// voltando por no máximo uma aresta "de retorno" (<c>lowLink</c>). Um nó fecha um SCC
    /// quando <c>lowLink[v] == discoveryIndex[v]</c> — ou seja, nada abaixo dele na árvore da
    /// DFS consegue "escapar" para mais longe do que ele mesmo, então ele é a raiz do seu SCC.
    ///
    /// Implementado de forma ITERATIVA (pilha própria <c>callStack</c> em vez de recursão),
    /// porque a versão recursiva estouraria a pilha de chamadas em malhas reais com dezenas de
    /// milhares de nós (uma rua muito longa vira uma cadeia de recursão do mesmo tamanho).
    /// </summary>
    private static (int[] ComponentId, int ComponentCount) ComputeStronglyConnectedComponents(int nodeCount, List<Edge>[] edgesByNode)
    {
        int[] discoveryIndex = new int[nodeCount];
        Array.Fill(discoveryIndex, -1); // -1 = nó ainda não visitado
        int[] lowLink = new int[nodeCount];
        bool[] onStack = new bool[nodeCount];
        int[] componentId = new int[nodeCount];
        Array.Fill(componentId, -1);

        var sccStack = new Stack<int>(); // nós do "ramo atual" da DFS, candidatos ao SCC sendo fechado
        var callStack = new Stack<(int Node, int NextEdgeIndex)>(); // simula a pilha de recursão

        int discoveryCounter = 0;
        int componentCount = 0;

        // Percorre todo nó como possível raiz de uma nova DFS — o grafo pode ter vários
        // "pedaços" desconexos entre si.
        for (int start = 0; start < nodeCount; start++)
        {
            if (discoveryIndex[start] != -1)
            {
                continue; // já visitado por uma DFS anterior
            }

            discoveryIndex[start] = lowLink[start] = discoveryCounter++;
            sccStack.Push(start);
            onStack[start] = true;
            callStack.Push((start, 0));

            while (callStack.Count > 0)
            {
                (int currentNode, int edgeIndex) = callStack.Pop();
                List<Edge> edges = edgesByNode[currentNode];

                if (edgeIndex < edges.Count)
                {
                    // Ainda há vizinho a processar: reempilha o MESMO nó apontando para o
                    // próximo índice — é o "ponto de retorno" que uma chamada recursiva teria.
                    callStack.Push((currentNode, edgeIndex + 1));

                    int neighbor = edges[edgeIndex].TargetNodeIndex;

                    if (discoveryIndex[neighbor] == -1)
                    {
                        // Vizinho novo: "desce" a DFS nele (equivalente a StrongConnect(neighbor)).
                        discoveryIndex[neighbor] = lowLink[neighbor] = discoveryCounter++;
                        sccStack.Push(neighbor);
                        onStack[neighbor] = true;
                        callStack.Push((neighbor, 0));
                    }
                    else if (onStack[neighbor])
                    {
                        // Vizinho já visitado e ainda no ramo atual: é uma aresta de retorno —
                        // currentNode consegue "alcançar" pelo menos até discoveryIndex[neighbor].
                        lowLink[currentNode] = Math.Min(lowLink[currentNode], discoveryIndex[neighbor]);
                    }
                    // Se o vizinho já foi visitado mas NÃO está mais em onStack, ele pertence a
                    // um SCC diferente já fechado — não influencia o lowLink deste ramo.
                }
                else
                {
                    // Todos os vizinhos de currentNode já foram processados: o "frame" fecha.
                    // Propaga o lowLink para quem chamou currentNode (o frame logo abaixo).
                    if (callStack.Count > 0)
                    {
                        int parentNode = callStack.Peek().Node;
                        lowLink[parentNode] = Math.Min(lowLink[parentNode], lowLink[currentNode]);
                    }

                    // currentNode é raiz de um SCC: desempilha sccStack até ele mesmo, e todo
                    // nó retirado pertence a este novo componente.
                    if (lowLink[currentNode] == discoveryIndex[currentNode])
                    {
                        int poppedNode;
                        do
                        {
                            poppedNode = sccStack.Pop();
                            onStack[poppedNode] = false;
                            componentId[poppedNode] = componentCount;
                        } while (poppedNode != currentNode);

                        componentCount++;
                    }
                }
            }
        }

        return (componentId, componentCount);
    }
}
