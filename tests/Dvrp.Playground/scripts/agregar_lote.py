"""
Agregação dos resultados do lote.

Lê o CSV gerado pelo DynamicReoptimizationBatchRunner e grava na mesma pasta o dvrp-agregado.csv
(tabelas em formato longo), o dvrp-resumo.md e as figuras em figuras/ (PDF e PNG).

Uso: python3 agregar_lote.py <dvrp-lote-evento-em-operacao.csv>

O ganho de A sobre B é 100 * (B - A) / B, comparando a mesma seed, área, penalidade e f; valor
positivo significa que A teve custo menor. As tabelas de ganho não incluem execuções triviais, as
de controle (afeta_matriz = 0, que têm tabela própria) nem os pares em que alguma estratégia usou
rotas extras.
"""

import csv
import statistics
import sys
from collections import defaultdict
from pathlib import Path

# Diferenças menores que isso (relativas) são tratadas como empate, por causa do arredondamento do CSV.
TOLERANCIA_RELATIVA = 1e-6

ESTATICA, REINICIO, REAPROVEITAMENTO = "estatica", "reinicio", "reaproveitamento"
ESTRATEGIAS = [ESTATICA, REINICIO, REAPROVEITAMENTO]

METRICAS_GRUPO = ["ttv_pos", "ttv_total", "dtp_pos", "dtp_total", "tr_ms", "geracoes"]

# (A, B, métricas): ganho de A sobre B.
COMPARACOES = [
    (REAPROVEITAMENTO, ESTATICA, ["ttv_pos", "ttv_total", "dtp_pos"]),
    (REINICIO, ESTATICA, ["ttv_pos", "ttv_total", "dtp_pos"]),
    (REAPROVEITAMENTO, REINICIO, ["ttv_pos", "tr_ms"]),
]


def numero(texto):
    return float(texto) if texto != "" else None


def ler_execucoes(caminho):
    """Agrupa as linhas do CSV por execução: (seed, área, penalidade, f) -> {estratégia: linha}."""
    execucoes = defaultdict(dict)
    with open(caminho, encoding="utf-8-sig") as arquivo:
        for linha in csv.DictReader(l for l in arquivo if not l.startswith("#")):
            chave = (linha["seed"], float(linha["event_area_fraction"]), float(linha["penalty_factor"]), float(linha["f"]))
            execucoes[chave][linha["estrategia"]] = {
                **{k: numero(linha[k]) for k in ["ttv_pos", "ttv_total", "dtp_pos", "dtp_total", "tr_ms", "geracoes",
                                                 "fitness", "fitness_inicial", "trp"]},
                "rotas_extras": int(linha["rotas_extras"]),
                "trivial": linha["trivial"] == "1",
                "afeta_matriz": linha["afeta_matriz"] == "1",
            }
    return execucoes


def descrever(valores):
    if not valores:
        return {"n": 0, "media": None, "mediana": None, "min": None, "max": None}
    return {"n": len(valores), "media": statistics.mean(valores), "mediana": statistics.median(valores),
            "min": min(valores), "max": max(valores)}


def ganho(a, b):
    return 100.0 * (b - a) / b


def empate(a, b):
    return abs(b - a) <= TOLERANCIA_RELATIVA * abs(b)


def resumo_ganhos(pares):
    """pares: lista de (valor_A, valor_B)."""
    ganhos = [ganho(a, b) for a, b in pares]
    vitorias = sum(1 for a, b in pares if a < b and not empate(a, b))
    empates = sum(1 for a, b in pares if empate(a, b))
    d = descrever(ganhos)
    d["vitoria_pct"] = 100.0 * vitorias / len(pares) if pares else None
    d["empates"] = empates
    return d


def area_txt(area):
    return "1/3" if abs(area - 1 / 3) < 1e-9 else "2/3" if abs(area - 2 / 3) < 1e-9 else f"{area:g}"


def fmt(valor, casas=2):
    return "—" if valor is None else f"{valor:,.{casas}f}".replace(",", "X").replace(".", ",").replace("X", ".")

# ----------------------------------------------------------------------------------------------
# Figuras. Usam os mesmos filtros das tabelas de ganho e não têm título (a legenda fica no LaTeX).
# ----------------------------------------------------------------------------------------------

# Cada estratégia tem cor, tipo de linha e marcador próprios, para continuar legível em preto e branco.
COR = {REAPROVEITAMENTO: "#2a78d6", REINICIO: "#eb6834", ESTATICA: "#52514e"}
LINHA = {REAPROVEITAMENTO: "-", REINICIO: "--"}
MARCADOR = {REAPROVEITAMENTO: "o", REINICIO: "s"}
NOME = {REAPROVEITAMENTO: "Reaproveitamento", REINICIO: "Reinício completo", ESTATICA: "Estática"}
AZUL_CLARO = "#9ec5f4"

TINTA, TINTA_SECUNDARIA, GRADE = "#0b0b0b", "#52514e", "#e4e3df"
LARGURA_POL = 16 / 2.54  # largura do texto em A4


def _decimal(valor, casas=None):
    """Número com vírgula decimal (padrão ABNT) e sinal de menos tipográfico."""
    texto = f"{valor:.{casas}f}" if casas is not None else f"{valor:g}"
    return texto.replace(".", ",").replace("-", "−")


def _configurar_matplotlib():
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    plt.rcParams.update({
        "font.family": "DejaVu Sans",
        "font.size": 9,
        "axes.labelsize": 9,
        "xtick.labelsize": 8,
        "ytick.labelsize": 8,
        "legend.fontsize": 8,
        "axes.edgecolor": TINTA_SECUNDARIA,
        "axes.labelcolor": TINTA,
        "xtick.color": TINTA_SECUNDARIA,
        "ytick.color": TINTA_SECUNDARIA,
        "axes.spines.top": False,
        "axes.spines.right": False,
        "axes.grid": True,
        "axes.grid.axis": "y",
        "grid.color": GRADE,
        "grid.linewidth": 0.6,
        "axes.axisbelow": True,
        "savefig.dpi": 300,
        "pdf.fonttype": 42,  # embute a fonte no PDF
        "svg.hashsalt": "dvrp",
    })
    return plt


def _formatar_eixo_decimal(eixo, casas=None):
    from matplotlib.ticker import FuncFormatter

    eixo.set_major_formatter(FuncFormatter(lambda v, _: _decimal(v, casas)))


def _salvar(plt, figura, pasta, nome):
    """Salva em PDF e PNG. Sem data nos metadados, para que rodar de novo gere o mesmo arquivo."""
    pasta.mkdir(parents=True, exist_ok=True)
    figura.savefig(pasta / f"{nome}.pdf", bbox_inches="tight", metadata={"CreationDate": None, "ModDate": None})
    figura.savefig(pasta / f"{nome}.png", bbox_inches="tight", metadata={"Software": None})
    plt.close(figura)


def _quartis(valores):
    """(1º quartil, mediana, 3º quartil)."""
    q1, mediana, q3 = statistics.quantiles(valores, n=4, method="inclusive") if len(valores) > 1 else (valores[0],) * 3
    return q1, mediana, q3


def _mediana_com_quartis(eixo, xs, grupos, estrategia, rotulo=True):
    """Mediana ligada por linha, com uma barra do 1º ao 3º quartil em cada ponto."""
    pontos = [(x, _quartis(v)) for x, v in zip(xs, grupos) if v]
    x = [p[0] for p in pontos]
    q1 = [p[1][0] for p in pontos]
    mediana = [p[1][1] for p in pontos]
    q3 = [p[1][2] for p in pontos]
    eixo.errorbar(x, mediana, yerr=[[m - a for m, a in zip(mediana, q1)], [b - m for m, b in zip(mediana, q3)]],
                  color=COR[estrategia], linestyle=LINHA[estrategia], marker=MARCADOR[estrategia], markersize=5,
                  linewidth=1.4, elinewidth=1.4, capsize=0, label=NOME[estrategia] if rotulo else None)


def _pares(com_efeito, a, b, metrica, filtro=lambda k: True):
    return [(k, e[a][metrica], e[b][metrica]) for k, e in sorted(com_efeito.items())
            if filtro(k) and e[a]["rotas_extras"] == 0 and e[b]["rotas_extras"] == 0]


def figura_ganho_vs_estatica(plt, com_efeito, pasta):
    fracoes = [0.25, 0.5, 0.75]
    penalidades = sorted({k[2] for k in com_efeito})
    figura, eixos = plt.subplots(1, len(penalidades), figsize=(LARGURA_POL, 2.6), sharey=True)
    for eixo, penalidade in zip(eixos, penalidades):
        eixo.axhline(0, color=TINTA_SECUNDARIA, linewidth=0.8)
        for deslocamento, estrategia in [(-0.04, REAPROVEITAMENTO), (0.04, REINICIO)]:
            grupos = [[ganho(a, b) for _, a, b in _pares(com_efeito, estrategia, ESTATICA, "ttv_pos",
                                                         lambda k: k[2] == penalidade and k[3] == f)] for f in fracoes]
            _mediana_com_quartis(eixo, [f + deslocamento for f in fracoes], grupos, estrategia, rotulo=eixo is eixos[0])
        eixo.set_xticks(fracoes, [_decimal(f) for f in fracoes])
        eixo.set_xlim(0.15, 0.85)
        eixo.set_title(f"Penalidade {_decimal(penalidade)}×", fontsize=8, color=TINTA_SECUNDARIA)
        eixo.set_xlabel("Fração atendida (f)")
        _formatar_eixo_decimal(eixo.yaxis)
    eixos[0].set_ylabel("Ganho no TTV pós-evento\nsobre a estática (%)")
    figura.legend(loc="upper center", ncol=2, frameon=False, bbox_to_anchor=(0.5, 1.08))
    figura.tight_layout()
    _salvar(plt, figura, pasta, "ganho_vs_estatica")
    return ("ganho_vs_estatica", "Ganho percentual pareado no TTV pós-evento do reaproveitamento e do reinício completo "
            "sobre a estática, por fração atendida (f > 0) e penalidade: mediana (ponto) e 1º a 3º quartil (barra); "
            "valores acima de zero indicam menor tempo que a estática.")


def figura_tr_por_estrategia(plt, com_efeito, pasta):
    from matplotlib.ticker import FuncFormatter, LogLocator

    fracoes = sorted({k[3] for k in com_efeito})
    figura, eixo = plt.subplots(figsize=(LARGURA_POL * 0.7, 2.6))
    for estrategia in [REAPROVEITAMENTO, REINICIO]:
        quartis = [_quartis([e[estrategia]["tr_ms"] / 1000 for k, e in com_efeito.items()
                             if k[3] == f and e[estrategia]["rotas_extras"] == 0]) for f in fracoes]
        eixo.fill_between(fracoes, [q[0] for q in quartis], [q[2] for q in quartis], color=COR[estrategia], alpha=0.15,
                          linewidth=0)
        eixo.plot(fracoes, [q[1] for q in quartis], color=COR[estrategia], linestyle=LINHA[estrategia],
                  marker=MARCADOR[estrategia], markersize=5, linewidth=1.4, label=NOME[estrategia])
    eixo.set_yscale("log")
    eixo.yaxis.set_major_locator(LogLocator(base=10, subs=(1.0, 3.0)))
    eixo.yaxis.set_major_formatter(FuncFormatter(lambda v, _: _decimal(v)))
    eixo.yaxis.set_minor_formatter(FuncFormatter(lambda v, _: ""))
    eixo.set_xticks(fracoes, [_decimal(f) for f in fracoes])
    eixo.set_xlabel("Fração atendida (f)")
    eixo.set_ylabel("Tempo de reotimização (s)")
    eixo.legend(frameon=False, loc="upper right")
    figura.tight_layout()
    _salvar(plt, figura, pasta, "tr_por_estrategia")
    return ("tr_por_estrategia", "Tempo de reotimização (TR, escala logarítmica) do reinício completo e do reaproveitamento, "
            "por fração atendida: mediana (linha) e 1º a 3º quartil (faixa).")


def decomposicao(e):
    """(reavaliação, evolução) do ganho do reaproveitamento sobre a estática, em % do ttv_pos da estática."""
    estatica = e[ESTATICA]["ttv_pos"]
    return (ganho(e[REAPROVEITAMENTO]["fitness_inicial"], estatica),
            100.0 * (e[REAPROVEITAMENTO]["fitness_inicial"] - e[REAPROVEITAMENTO]["fitness"]) / estatica)


def figura_decomposicao_ganho(plt, com_efeito, pasta):
    fracoes = sorted({k[3] for k in com_efeito})
    medias = []
    for f in fracoes:
        partes = [decomposicao(e) for k, e in com_efeito.items() if k[3] == f and e[REAPROVEITAMENTO]["rotas_extras"] == 0]
        medias.append((statistics.mean(p[0] for p in partes), statistics.mean(p[1] for p in partes)))
    x = range(len(fracoes))
    figura, eixo = plt.subplots(figsize=(LARGURA_POL * 0.7, 2.6))
    eixo.bar(x, [m[0] for m in medias], 0.55, color=AZUL_CLARO, edgecolor="white", linewidth=1,
             label="Reavaliação da população adaptada")
    eixo.bar(x, [m[1] for m in medias], 0.55, bottom=[m[0] for m in medias], color=COR[REAPROVEITAMENTO],
             edgecolor="white", linewidth=1, label="Evolução do AG")
    for i, (reavaliacao, evolucao) in enumerate(medias):
        eixo.text(i, reavaliacao + evolucao + 0.08, _decimal(reavaliacao + evolucao, 1), ha="center", va="bottom", fontsize=8)
    eixo.set_xticks(x, [_decimal(f) for f in fracoes])
    eixo.set_ylim(0, max(sum(m) for m in medias) * 1.25)
    eixo.set_xlabel("Fração atendida (f)")
    eixo.set_ylabel("Ganho médio no TTV pós-evento\nsobre a estática (%)")
    _formatar_eixo_decimal(eixo.yaxis)
    eixo.legend(frameon=False, loc="upper center", ncol=2, bbox_to_anchor=(0.5, 1.15))
    figura.tight_layout()
    _salvar(plt, figura, pasta, "decomposicao_ganho")
    return ("decomposicao_ganho", "Ganho médio do reaproveitamento sobre a estática no TTV pós-evento, por fração atendida "
            "(penalidades agregadas), decomposto na parcela obtida só pela reavaliação da população adaptada e na parcela "
            "obtida pela evolução do AG; o total aparece sobre cada barra.")


def figura_trp_vs_f(plt, com_efeito, pasta):
    fracoes = sorted({k[3] for k in com_efeito})
    medias, desvios = [], []
    for f in fracoes:
        valores = [e[REAPROVEITAMENTO]["trp"] for k, e in com_efeito.items() if k[3] == f and e[REAPROVEITAMENTO]["trp"] is not None]
        medias.append(statistics.mean(valores))
        desvios.append(statistics.stdev(valores))
    figura, eixo = plt.subplots(figsize=(LARGURA_POL * 0.55, 2.4))
    eixo.errorbar(fracoes, medias, yerr=desvios, color=COR[REAPROVEITAMENTO], marker=MARCADOR[REAPROVEITAMENTO],
                  markersize=5, linewidth=1.4, elinewidth=1.4, capsize=0)
    eixo.set_xticks(fracoes, [_decimal(f) for f in fracoes])
    eixo.set_ylim(0, 100)
    eixo.set_yticks([0, 50, 100])
    eixo.set_xlabel("Fração atendida (f)")
    eixo.set_ylabel("TRP (%)")
    figura.tight_layout()
    _salvar(plt, figura, pasta, "trp_vs_f")
    return ("trp_vs_f", "TRP médio do reaproveitamento por fração atendida (inclui f = 0), com barras de ± 1 desvio-padrão "
            "amostral entre execuções.")


def figura_tradeoff_dtp_ttv(plt, com_efeito, pasta):
    variacao = lambda a, b: 100.0 * (a - b) / b
    pontos = [(variacao(e[REAPROVEITAMENTO]["dtp_pos"], e[ESTATICA]["dtp_pos"]),
               variacao(e[REAPROVEITAMENTO]["ttv_pos"], e[ESTATICA]["ttv_pos"]))
              for k, e in sorted(com_efeito.items()) if e[REAPROVEITAMENTO]["rotas_extras"] == 0]
    figura, eixo = plt.subplots(figsize=(LARGURA_POL * 0.6, 2.8))
    eixo.axhline(0, color=TINTA_SECUNDARIA, linewidth=0.8, zorder=1)
    eixo.axvline(0, color=TINTA_SECUNDARIA, linewidth=0.8, zorder=1)
    eixo.scatter([p[0] for p in pontos], [p[1] for p in pontos], s=12, color=COR[REAPROVEITAMENTO], alpha=0.55,
                 linewidths=0, zorder=2)
    eixo.grid(False)
    eixo.set_xlabel("Variação do DTP pós-evento (%)")
    eixo.set_ylabel("Variação do TTV pós-evento (%)")
    _formatar_eixo_decimal(eixo.xaxis)
    _formatar_eixo_decimal(eixo.yaxis)
    figura.tight_layout()
    _salvar(plt, figura, pasta, "tradeoff_dtp_ttv")
    return ("tradeoff_dtp_ttv", "Variação do DTP (x) e do TTV (y) pós-evento do reaproveitamento em relação à estática, um ponto "
            "por par; valores negativos indicam menos distância ou menos tempo que a estática.")


def figura_calibracao(plt, caminho_calibracao, pasta):
    with open(caminho_calibracao, encoding="utf-8-sig") as arquivo:
        linhas = list(csv.DictReader(l for l in arquivo if not l.startswith("#")))
    melhor = {}
    for l in linhas:
        melhor[l["seed"]] = min(melhor.get(l["seed"], float("inf")), float(l["fitness"]))
    grupos = defaultdict(list)
    for l in linhas:
        chave = (int(l["populacao"]), float(l["mutacao"]), float(l["cruzamento"]))
        grupos[chave].append((100.0 * (float(l["fitness"]) / melhor[l["seed"]] - 1), float(l["tempo_ms"]) / 1000))
    pontos = {k: (statistics.mean(t for _, t in v), statistics.mean(g for g, _ in v)) for k, v in sorted(grupos.items())}
    fronteira = sorted(p for p in pontos.values()
                       if not any(t2 <= p[0] and g2 <= p[1] and (t2 < p[0] or g2 < p[1]) for t2, g2 in pontos.values()))
    anterior, escolhida = pontos[(100, 0.15, 0.85)], pontos[(100, 0.05, 0.70)]

    figura, eixo = plt.subplots(figsize=(LARGURA_POL * 0.6, 2.8))
    eixo.scatter([t for t, _ in pontos.values()], [g for _, g in pontos.values()], s=12, color="#bdbcb7", linewidths=0, zorder=2)
    eixo.plot([p[0] for p in fronteira], [p[1] for p in fronteira], color=TINTA_SECUNDARIA, linewidth=1, zorder=3)
    for (t, g), rotulo, marcador in [(anterior, "anterior", "s"), (escolhida, "escolhida", "o")]:
        eixo.scatter(t, g, s=28, marker=marcador, color=TINTA, zorder=4)
        eixo.annotate(rotulo, (t, g), xytext=(6, 0), textcoords="offset points", va="center", fontsize=8)
    eixo.grid(False)
    eixo.set_xlabel("Tempo médio por execução (s)")
    eixo.set_ylabel("Gap médio de fitness (%)")
    _formatar_eixo_decimal(eixo.xaxis)
    _formatar_eixo_decimal(eixo.yaxis)
    figura.tight_layout()
    _salvar(plt, figura, pasta, "calibracao")
    return ("calibracao", "Calibração do AG inicial: gap médio de fitness em relação à melhor configuração de cada seed "
            "por tempo médio de execução, uma marca por configuração; a linha liga a fronteira não dominada e estão "
            "indicadas a configuração anterior (100 / 0,15 / 0,85) e a escolhida (100 / 0,05 / 0,70), em "
            "população / mutação / cruzamento.")


# ----------------------------------------------------------------------------------------------
# Comparação entre limites de gerações (1000 no lote principal, 5000 na rodada complementar), f = 0.
# ----------------------------------------------------------------------------------------------

ORCAMENTO_COMPLEMENTAR = 5000
ORCAMENTO_PRINCIPAL = 1000
SERIES_ORCAMENTO = [(REAPROVEITAMENTO, ORCAMENTO_PRINCIPAL), (REAPROVEITAMENTO, ORCAMENTO_COMPLEMENTAR),
                    (REINICIO, ORCAMENTO_PRINCIPAL), (REINICIO, ORCAMENTO_COMPLEMENTAR)]


def series_orcamento(principal, complementar):
    """Ganhos sobre a estática para cada estratégia e limite de gerações. Usa só f = 0 com
    afeta_matriz = 1; quando o evento não muda a matriz, o reinício só repete o AG inicial."""
    fontes = {ORCAMENTO_PRINCIPAL: principal, ORCAMENTO_COMPLEMENTAR: complementar}
    series = {}
    for estrategia, orcamento in SERIES_ORCAMENTO:
        execucoes = {k: e for k, e in fontes[orcamento].items()
                     if k[3] == 0 and not e[ESTATICA]["trivial"] and e[ESTATICA]["afeta_matriz"]}
        linhas = [(e[estrategia], e[ESTATICA]) for k, e in sorted(execucoes.items()) if e[estrategia]["rotas_extras"] == 0]
        series[(estrategia, orcamento)] = {
            "ganhos": [ganho(l["ttv_pos"], est["ttv_pos"]) for l, est in linhas],
            "linhas": [l for l, _ in linhas],
            "excluidas": sum(1 for e in execucoes.values() if e[estrategia]["rotas_extras"] > 0),
        }
    return series


def figura_orcamento_reinicio_f0(plt, series, pasta):
    from matplotlib.ticker import FuncFormatter, LogLocator

    figura, (eixo_a, eixo_b) = plt.subplots(1, 2, figsize=(LARGURA_POL, 2.6))
    posicao = {(REAPROVEITAMENTO, ORCAMENTO_PRINCIPAL): 0, (REAPROVEITAMENTO, ORCAMENTO_COMPLEMENTAR): 1,
               (REINICIO, ORCAMENTO_PRINCIPAL): 2.4, (REINICIO, ORCAMENTO_COMPLEMENTAR): 3.4}

    def pontos(eixo, valores_por_serie):
        """Mediana com barra do 1º ao 3º quartil; a linha liga 1.000 -> 5.000 da mesma estratégia.
        Marcador vazado = 1.000 gerações, cheio = 5.000."""
        for estrategia in [REAPROVEITAMENTO, REINICIO]:
            chaves = [(estrategia, ORCAMENTO_PRINCIPAL), (estrategia, ORCAMENTO_COMPLEMENTAR)]
            quartis = [_quartis(valores_por_serie[c]) for c in chaves]
            xs = [posicao[c] for c in chaves]
            eixo.plot(xs, [q[1] for q in quartis], color=COR[estrategia], linestyle=LINHA[estrategia], linewidth=1.2, zorder=2)
            for x, q, cheio in zip(xs, quartis, [False, True]):
                eixo.errorbar(x, q[1], yerr=[[q[1] - q[0]], [q[2] - q[1]]], color=COR[estrategia], elinewidth=1.4,
                              capsize=0, marker=MARCADOR[estrategia], markersize=6, markeredgewidth=1.4,
                              markerfacecolor=COR[estrategia] if cheio else "white", zorder=3)
        eixo.set_xticks(list(posicao.values()), ["1.000", "5.000", "1.000", "5.000"])
        eixo.set_xlim(-0.6, 4.0)
        for x, estrategia in [(0.5, REAPROVEITAMENTO), (2.9, REINICIO)]:
            eixo.text(x, -0.2, NOME[estrategia], transform=eixo.get_xaxis_transform(), ha="center", va="top", fontsize=8)

    pontos(eixo_a, {c: series[c]["ganhos"] for c in SERIES_ORCAMENTO})
    eixo_a.axhline(0, color=TINTA_SECUNDARIA, linewidth=0.8, zorder=1)
    eixo_a.set_ylabel("Ganho no TTV pós-evento\nsobre a estática (%)")
    _formatar_eixo_decimal(eixo_a.yaxis)
    eixo_a.text(-0.02, 1.04, "A", transform=eixo_a.transAxes, fontsize=10, fontweight="bold")

    pontos(eixo_b, {c: [l["tr_ms"] / 1000 for l in series[c]["linhas"]] for c in SERIES_ORCAMENTO})
    eixo_b.set_yscale("log")
    eixo_b.yaxis.set_major_locator(LogLocator(base=10, subs=(1.0, 2.0, 5.0)))
    eixo_b.yaxis.set_major_formatter(FuncFormatter(lambda v, _: _decimal(v)))
    eixo_b.yaxis.set_minor_formatter(FuncFormatter(lambda v, _: ""))
    eixo_b.set_ylabel("Tempo de reotimização (s)")
    eixo_b.text(-0.02, 1.04, "B", transform=eixo_b.transAxes, fontsize=10, fontweight="bold")

    figura.tight_layout()
    _salvar(plt, figura, pasta, "orcamento_reinicio_f0")
    return ("orcamento_reinicio_f0", "Sensibilidade ao limite de gerações com o evento antes da partida (f = 0, só execuções "
            "em que o evento altera a matriz): (A) ganho pareado no TTV pós-evento sobre a estática e (B) tempo de "
            "reotimização, para reaproveitamento e reinício completo com limite de 1.000 (marcador vazado, lote principal) "
            "e 5.000 gerações (marcador cheio); ponto na mediana e barra do 1º ao 3º quartil.")


def secao_orcamento(series):
    md = ["\n## 8. Sensibilidade ao orçamento (f = 0)\n",
          "Evento antes da partida, só execuções com afeta_matriz = 1 e sem rotas extras. Orçamento 1.000: lote principal; "
          f"{ORCAMENTO_COMPLEMENTAR:,}: rodada complementar (dvrp-lote-orcamento-{ORCAMENTO_COMPLEMENTAR}.csv). "
          "Ganho pareado no ttv_pos sobre a estática da mesma execução.\n".replace("5,000", "5.000"),
          "| estratégia | orçamento | n | ganho médio | ganho mediano | vitória | TR médio | gerações médias | pararam por estagnação antes do limite | excluídas (rotas extras) |",
          "|---|---|---|---|---|---|---|---|---|---|"]
    for estrategia, orcamento in SERIES_ORCAMENTO:
        serie = series[(estrategia, orcamento)]
        ganhos, linhas = serie["ganhos"], serie["linhas"]
        vitorias = sum(1 for g in ganhos if g > 100.0 * TOLERANCIA_RELATIVA)
        geracoes = [l["geracoes"] for l in linhas]
        md.append(f"| {NOME[estrategia]} | {orcamento:,} | {len(ganhos)} | {fmt(statistics.mean(ganhos))}% | "
                  f"{fmt(statistics.median(ganhos))}% | {fmt(100.0 * vitorias / len(ganhos), 1)}% | "
                  f"{fmt(statistics.mean(l['tr_ms'] for l in linhas) / 1000)} s | {fmt(statistics.mean(geracoes), 0)} | "
                  f"{sum(1 for g in geracoes if g < orcamento)}/{len(geracoes)} | {serie['excluidas']} |".replace("1,000", "1.000").replace("5,000", "5.000"))
    return md


def gerar_figuras(com_efeito, caminho_csv):
    plt = _configurar_matplotlib()
    pasta = caminho_csv.parent / "figuras"
    figuras = [
        figura_ganho_vs_estatica(plt, com_efeito, pasta),
        figura_tr_por_estrategia(plt, com_efeito, pasta),
        figura_decomposicao_ganho(plt, com_efeito, pasta),
        figura_trp_vs_f(plt, com_efeito, pasta),
        figura_tradeoff_dtp_ttv(plt, com_efeito, pasta),
    ]
    calibracao = caminho_csv.parent / "dvrp-calibracao.csv"
    if calibracao.exists():
        figuras.append(figura_calibracao(plt, calibracao, pasta))
    return pasta, figuras, plt


def main(caminho_csv):
    caminho_csv = Path(caminho_csv)
    execucoes = ler_execucoes(caminho_csv)
    validas = {k: e for k, e in execucoes.items() if not e[ESTATICA]["trivial"]}
    com_efeito = {k: e for k, e in validas.items() if e[ESTATICA]["afeta_matriz"]}
    controle = {k: e for k, e in validas.items() if not e[ESTATICA]["afeta_matriz"]}
    fracoes = sorted({k[3] for k in execucoes})

    linhas_csv = []  # formato longo
    md = []

    def registrar(secao, area, penalidade, f, grupo, metrica, d):
        linhas_csv.append({"secao": secao, "area": area, "penalidade": penalidade, "f": f, "grupo": grupo,
                           "metrica": metrica, **{k: d.get(k) for k in ["n", "media", "mediana", "min", "max",
                                                                        "vitoria_pct", "empates"]}})

    md.append(f"# Resumo do lote — {caminho_csv.name}\n")
    md.append(f"- Execuções: {len(execucoes)} (triviais: {len(execucoes) - len(validas)}; "
              f"com efeito na matriz: {len(com_efeito)}; controle, afeta_matriz = 0: {len(controle)}).")
    md.append(f"- Ganho de A sobre B = 100·(B − A)/B, pareado por (seed, área, penalidade, f); positivo = A melhor.")
    md.append(f"- Tolerância de empate: {TOLERANCIA_RELATIVA:g} relativo. Taxa de vitória = % de pares com ganho acima dela.")
    md.append("- Tabelas de ganho: só execuções com afeta_matriz = 1, sem os pares em que alguma estratégia usou rotas extras.\n")

    # 1. Médias e medianas por (área, penalidade, f, estratégia).
    md.append("## 1. Métricas por área, penalidade, f e estratégia (média / mediana)\n")
    md.append("Todas as execuções não triviais (inclui controle e rotas extras; ver seções 5 e 6).\n")
    md.append("| área | pen. | f | estratégia | n | ttv_pos | ttv_total | dtp_pos | dtp_total | tr_ms | gerações |")
    md.append("|---|---|---|---|---|---|---|---|---|---|---|")
    grupos = defaultdict(list)
    for (seed, area, penalidade, f), e in validas.items():
        for estrategia in ESTRATEGIAS:
            grupos[(area, penalidade, f, estrategia)].append(e[estrategia])
    for (area, penalidade, f, estrategia) in sorted(grupos, key=lambda k: (k[0], k[1], k[2], ESTRATEGIAS.index(k[3]))):
        linhas = grupos[(area, penalidade, f, estrategia)]
        celulas = []
        for metrica in METRICAS_GRUPO:
            d = descrever([l[metrica] for l in linhas if l[metrica] is not None])
            registrar("1_metricas", area_txt(area), penalidade, f, estrategia, metrica, d)
            celulas.append("—" if d["n"] == 0 else f"{fmt(d['media'], 0)} / {fmt(d['mediana'], 0)}")
        md.append(f"| {area_txt(area)} | {penalidade:g} | {f:g} | {estrategia} | {len(linhas)} | " + " | ".join(celulas) + " |")

    # 2. Ganhos pareados.
    md.append("\n## 2. Ganho pareado (%)\n")
    md.append("| comparação | métrica | f | n | média | mediana | mín | máx | vitória | empates |")
    md.append("|---|---|---|---|---|---|---|---|---|---|")
    for a, b, metricas in COMPARACOES:
        for metrica in metricas:
            for f in fracoes + [None]:
                pares = [(e[a][metrica], e[b][metrica]) for k, e in com_efeito.items()
                         if (f is None or k[3] == f) and e[a]["rotas_extras"] == 0 and e[b]["rotas_extras"] == 0]
                d = resumo_ganhos(pares)
                registrar("2_ganho", "todas", "todas", "todas" if f is None else f, f"{a} vs {b}", metrica, d)
                md.append(f"| {a} vs {b} | {metrica} | {'todas' if f is None else f'{f:g}'} | {d['n']} | {fmt(d['media'])} | "
                          f"{fmt(d['mediana'])} | {fmt(d['min'])} | {fmt(d['max'])} | {fmt(d['vitoria_pct'], 1)}% | {d['empates']} |")
            # Detalhe por (área, penalidade, f) só no CSV.
            for (area, penalidade, f) in sorted({(k[1], k[2], k[3]) for k in com_efeito}):
                pares = [(e[a][metrica], e[b][metrica]) for k, e in com_efeito.items()
                         if (k[1], k[2], k[3]) == (area, penalidade, f) and e[a]["rotas_extras"] == 0 and e[b]["rotas_extras"] == 0]
                registrar("2_ganho_detalhe", area_txt(area), penalidade, f, f"{a} vs {b}", metrica, resumo_ganhos(pares))

    # 3. Decomposição do ganho do reaproveitamento sobre a estática (ttv_pos).
    md.append("\n## 3. Decomposição do ganho do reaproveitamento sobre a estática (ttv_pos, % do custo da estática)\n")
    md.append("Reavaliação = estática → melhor da população adaptada (fitness_inicial); "
              "evolução = fitness_inicial → fitness final. Total = soma das duas.\n")
    md.append("| f | n | reavaliação (média) | evolução (média) | total (média) | parcela evolutiva |")
    md.append("|---|---|---|---|---|---|")
    for f in fracoes + [None]:
        partes = [(ganho(e[REAPROVEITAMENTO]["fitness_inicial"], e[ESTATICA]["ttv_pos"]),
                   100.0 * (e[REAPROVEITAMENTO]["fitness_inicial"] - e[REAPROVEITAMENTO]["fitness"]) / e[ESTATICA]["ttv_pos"])
                  for k, e in com_efeito.items()
                  if (f is None or k[3] == f) and e[REAPROVEITAMENTO]["rotas_extras"] == 0]
        reavaliacao = descrever([p[0] for p in partes])
        evolucao = descrever([p[1] for p in partes])
        total = descrever([p[0] + p[1] for p in partes])
        rotulo = "todas" if f is None else f
        registrar("3_decomposicao", "todas", "todas", rotulo, REAPROVEITAMENTO, "reavaliacao", reavaliacao)
        registrar("3_decomposicao", "todas", "todas", rotulo, REAPROVEITAMENTO, "evolucao", evolucao)
        registrar("3_decomposicao", "todas", "todas", rotulo, REAPROVEITAMENTO, "total", total)
        parcela = None if not partes or total["media"] == 0 else 100.0 * evolucao["media"] / total["media"]
        md.append(f"| {'todas' if f is None else f'{f:g}'} | {len(partes)} | {fmt(reavaliacao['media'])}% | "
                  f"{fmt(evolucao['media'])}% | {fmt(total['media'])}% | {fmt(parcela, 1)}% |")

    # 4. TRP por f.
    md.append("\n## 4. TRP médio por f (reaproveitamento)\n")
    md.append("| f | n (com efeito) | TRP médio | n (controle) | TRP médio controle |")
    md.append("|---|---|---|---|---|")
    for f in fracoes:
        efeito = descrever([e[REAPROVEITAMENTO]["trp"] for k, e in com_efeito.items() if k[3] == f])
        ctrl = descrever([e[REAPROVEITAMENTO]["trp"] for k, e in controle.items() if k[3] == f])
        registrar("4_trp", "todas", "todas", f, REAPROVEITAMENTO, "trp", efeito)
        registrar("4_trp_controle", "todas", "todas", f, REAPROVEITAMENTO, "trp", ctrl)
        md.append(f"| {f:g} | {efeito['n']} | {fmt(efeito['media'])}% | {ctrl['n']} | {fmt(ctrl['media'])}% |")

    # 5. Rotas extras.
    md.append("\n## 5. Linhas com rotas extras > 0 (fora das médias de ganho)\n")
    contagem = defaultdict(lambda: [0, 0])
    for (seed, area, penalidade, f), e in validas.items():
        for estrategia in ESTRATEGIAS:
            if e[estrategia]["rotas_extras"] > 0:
                c = contagem[(estrategia, area, penalidade, f)]
                c[0] += 1
                c[1] += e[estrategia]["rotas_extras"]
    total_por_estrategia = {s: sum(c[0] for k, c in contagem.items() if k[0] == s) for s in ESTRATEGIAS}
    md.append("Total por estratégia: " + ", ".join(f"{s} {n}" for s, n in total_por_estrategia.items()) + ".\n")
    if contagem:
        md.append("| estratégia | área | pen. | f | linhas | rotas extras (soma) |")
        md.append("|---|---|---|---|---|---|")
        for (estrategia, area, penalidade, f), (linhas, soma) in sorted(contagem.items(), key=lambda kv: (ESTRATEGIAS.index(kv[0][0]), kv[0][1:])):
            registrar("5_rotas_extras", area_txt(area), penalidade, f, estrategia, "linhas", {"n": linhas, "media": soma / linhas})
            md.append(f"| {estrategia} | {area_txt(area)} | {penalidade:g} | {f:g} | {linhas} | {soma} |")

    # 6. Controle.
    md.append("\n## 6. Controle: execuções em que o evento não altera a matriz (afeta_matriz = 0)\n")
    md.append("Ganho médio pareado (%), sem os pares com rotas extras.\n")
    md.append("| área | pen. | f | execuções | reaprov. vs estática (ttv_pos) | reinício vs estática (ttv_pos) | reaprov. vs reinício (ttv_pos) | reaprov. vs reinício (tr_ms) |")
    md.append("|---|---|---|---|---|---|---|---|")
    for (area, penalidade, f) in sorted({(k[1], k[2], k[3]) for k in controle}):
        grupo = [e for k, e in controle.items() if (k[1], k[2], k[3]) == (area, penalidade, f)]
        celulas = []
        for a, b, metrica in [(REAPROVEITAMENTO, ESTATICA, "ttv_pos"), (REINICIO, ESTATICA, "ttv_pos"),
                              (REAPROVEITAMENTO, REINICIO, "ttv_pos"), (REAPROVEITAMENTO, REINICIO, "tr_ms")]:
            d = resumo_ganhos([(e[a][metrica], e[b][metrica]) for e in grupo
                               if e[a]["rotas_extras"] == 0 and e[b]["rotas_extras"] == 0])
            registrar("6_controle", area_txt(area), penalidade, f, f"{a} vs {b}", metrica, d)
            celulas.append(f"{fmt(d['media'])}%")
        md.append(f"| {area_txt(area)} | {penalidade:g} | {f:g} | {len(grupo)} | " + " | ".join(celulas) + " |")

    # 7. Figuras.
    pasta_figuras, figuras, plt = gerar_figuras(com_efeito, caminho_csv)

    # 8. Sensibilidade ao orçamento (só se a rodada complementar existir; não entra no CSV agregado).
    complementar = caminho_csv.parent / f"dvrp-lote-orcamento-{ORCAMENTO_COMPLEMENTAR}.csv"
    secao_8 = []
    if complementar.exists():
        series = series_orcamento(execucoes, ler_execucoes(complementar))
        figuras.append(figura_orcamento_reinicio_f0(plt, series, pasta_figuras))
        secao_8 = secao_orcamento(series)

    md.append(f"\n## 7. Figuras ({pasta_figuras.name}/, PDF e PNG 300 dpi)\n")
    for nome, descricao in figuras:
        md.append(f"- `{nome}`: {descricao}")
    md.extend(secao_8)

    saida_csv = caminho_csv.parent / "dvrp-agregado.csv"
    with open(saida_csv, "w", encoding="utf-8", newline="") as arquivo:
        escritor = csv.DictWriter(arquivo, fieldnames=list(linhas_csv[0].keys()))
        escritor.writeheader()
        escritor.writerows(linhas_csv)

    saida_md = caminho_csv.parent / "dvrp-resumo.md"
    saida_md.write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"Gerados: {saida_csv} ({len(linhas_csv)} linhas), {saida_md} e {len(figuras)} figuras em {pasta_figuras}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("Uso: python3 agregar_lote.py <dvrp-lote-evento-em-operacao.csv>")
    main(sys.argv[1])
