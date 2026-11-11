using System.Text;
using MotorCredito.Core.Documentos;
using MotorCredito.Core.Listas;

namespace MotorCredito.Core.Regras;

/// <summary>A ordem dos valores é a gravidade: a decisão mais grave prevalece.</summary>
public enum Decisao
{
    Liberar = 0,
    Revisar = 1,
    Bloquear = 2,
}

public sealed record Motivo(string Codigo, string Texto);

public sealed record ResultadoDaTriagem(
    Decisao Decisao,
    IReadOnlyList<Motivo> Motivos,
    IReadOnlyList<Sancao> SancoesVigentes,
    IReadOnlyList<Sancao> SancoesEncerradas,
    bool PepVerificado,
    bool PepAtivo,
    IReadOnlyList<RegistroPep> Peps);

/// <summary>
/// Triagem de compliance: sanções (CEIS/CNEP) e PEP. Funções puras, sem banco e sem I/O.
/// Política PROVISÓRIA – validar com a área de risco/compliance:
///   S01 sanção vigente -> bloquear; S02 só sanções encerradas -> informativo;
///   P01 PEP em exercício ou na carência -> revisar; P02 PEP com carência encerrada -> informativo;
///   P03 CPF sem nome -> PEP não verificado.
/// </summary>
public static class MotorDeTriagem
{
    public static ResultadoDaTriagem Triar(
        TipoDocumento tipo,
        string? nome,
        IReadOnlyList<Sancao> sancoes,
        IReadOnlyList<RegistroPep> candidatosPep,
        DateOnly hoje)
    {
        ArgumentNullException.ThrowIfNull(sancoes);
        ArgumentNullException.ThrowIfNull(candidatosPep);

        var decisao = Decisao.Liberar;
        var motivos = new List<Motivo>();

        var vigentes = sancoes.Where(s => SancaoVigente(s, hoje)).ToList();
        var encerradas = sancoes.Where(s => !SancaoVigente(s, hoje)).ToList();
        if (vigentes.Count > 0)
        {
            var cadastros = vigentes.Select(s => s.Cadastro).Distinct().Order(StringComparer.Ordinal);
            decisao = Decisao.Bloquear;
            motivos.Add(new Motivo(
                "S01", $"Sanção vigente em {string.Join(", ", cadastros)} ({vigentes.Count} registro(s))."));
        }
        else if (encerradas.Count > 0)
        {
            motivos.Add(new Motivo("S02", "Possui apenas sanções já encerradas (informativo)."));
        }

        var pepVerificado = false;
        var pepAtivo = false;
        List<RegistroPep> peps = [];
        if (tipo == TipoDocumento.Cpf)
        {
            if (string.IsNullOrEmpty(nome))
            {
                motivos.Add(new Motivo(
                    "P03", "PEP não verificado: informe o nome (a CGU publica o CPF mascarado)."));
            }
            else
            {
                pepVerificado = true;
                var alvo = NormalizarNome(nome);
                peps = candidatosPep.Where(p => NormalizarNome(p.Nome) == alvo).ToList();
                var ativos = peps.Where(p => PepAtivo(p, hoje)).ToList();
                if (ativos.Count > 0)
                {
                    pepAtivo = true;
                    decisao = decisao > Decisao.Revisar ? decisao : Decisao.Revisar;
                    var funcao = string.IsNullOrEmpty(ativos[0].DescricaoFuncao)
                        ? "função não informada"
                        : ativos[0].DescricaoFuncao;
                    motivos.Add(new Motivo("P01", $"Pessoa exposta politicamente ({funcao})."));
                }
                else if (peps.Count > 0)
                {
                    motivos.Add(new Motivo("P02", "Foi PEP; carência encerrada (informativo)."));
                }
            }
        }

        if (motivos.Count == 0)
        {
            motivos.Add(new Motivo("OK", "Nenhuma restrição nas listas consultadas."));
        }

        return new ResultadoDaTriagem(decisao, motivos, vigentes, encerradas, pepVerificado, pepAtivo, peps);
    }

    /// <summary>Vigente: já começou (ou sem data de início) e não terminou (sem data final = sem prazo).</summary>
    public static bool SancaoVigente(Sancao sancao, DateOnly hoje)
    {
        ArgumentNullException.ThrowIfNull(sancao);
        var comecou = sancao.DataInicio is null || sancao.DataInicio <= hoje;
        var naoTerminou = sancao.DataFim is null || sancao.DataFim >= hoje;
        return comecou && naoTerminou;
    }

    /// <summary>PEP enquanto exerce a função e até o fim da carência. Datas não informadas contam como ativas.</summary>
    public static bool PepAtivo(RegistroPep pep, DateOnly hoje)
    {
        ArgumentNullException.ThrowIfNull(pep);
        if (pep.DataFimCarencia is not null)
        {
            return pep.DataFimCarencia >= hoje;
        }

        return pep.DataFimExercicio is null || pep.DataFimExercicio >= hoje;
    }

    /// <summary>Maiúsculas, sem acentos e com espaços simples: "José  da Silva" vira "JOSE DA SILVA".</summary>
    public static string NormalizarNome(string nome)
    {
        ArgumentNullException.ThrowIfNull(nome);
        var semAcento = new StringBuilder(nome.Length);
        foreach (var c in nome.Normalize(NormalizationForm.FormKD))
        {
            if (c <= 127)
            {
                semAcento.Append(char.ToUpperInvariant(c));
            }
        }

        var partes = semAcento.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', partes);
    }
}
