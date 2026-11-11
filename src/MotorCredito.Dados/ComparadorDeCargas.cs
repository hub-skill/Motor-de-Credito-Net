using System.Data.Common;
using System.Globalization;
using MotorCredito.Core.Cgu;

namespace MotorCredito.Dados;

public sealed record ResultadoComparacao(
    string Banco,
    string Tabela,
    int Antigos,
    int Novos,
    int Identicos,
    int SoNoAntigo,
    int SoNoNovo,
    int NovosMarcados,
    IReadOnlyList<string> ArquivosAntigos,
    IReadOnlyList<string> ArquivosNovos,
    IReadOnlyList<string> Exemplos)
{
    public bool MesmosArquivos => ArquivosAntigos.SequenceEqual(ArquivosNovos);

    /// <summary>Correto: mesmas linhas, campo a campo, e todo registro novo com a marca DOTNET.</summary>
    public bool Igual => SoNoAntigo == 0 && SoNoNovo == 0 && Antigos == Novos && NovosMarcados == Novos;
}

/// <summary>
/// Compara, campo a campo, a tabela antiga (gravada pelo Python) com a nova (gravada pelo .NET).
/// Ignora id, carregado_em e origem_carga, que são próprios de cada carga.
/// </summary>
public static class ComparadorDeCargas
{
    private const char Separador = '\u001f';
    private const string Nulo = "\u0000";

    public static async Task<ResultadoComparacao> CompararAsync(
        DbConnection conexao,
        string banco,
        string tabelaAntiga,
        string tabelaNova,
        LayoutTabela layout,
        Action<DbCommand>? preparar,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conexao);
        ArgumentNullException.ThrowIfNull(layout);
        var colunas = layout.Colunas.Where(c => c.Fonte != FonteDoValor.MarcaDeOrigem).Select(c => c.Nome).ToList();
        var lista = string.Join(", ", colunas);
        var indiceArquivo = colunas.IndexOf("arquivo_origem");

        // 1) Linhas antigas viram um multiconjunto: texto canônico da linha -> quantas vezes aparece.
        var antigas = new Dictionary<string, int>(StringComparer.Ordinal);
        var arquivosAntigos = new SortedSet<string>(StringComparer.Ordinal);
        var totalAntigos = 0;
        await foreach (var campos in LerAsync(conexao, $"select {lista} from {tabelaAntiga}", preparar, cancellationToken)
            .ConfigureAwait(false))
        {
            totalAntigos++;
            arquivosAntigos.Add(campos[indiceArquivo]);
            var chave = string.Join(Separador, campos);
            antigas[chave] = antigas.GetValueOrDefault(chave) + 1;
        }

        // 2) Cada linha nova "consome" uma antiga idêntica; o que sobra é diferença.
        var soNoNovo = new List<string[]>();
        var arquivosNovos = new SortedSet<string>(StringComparer.Ordinal);
        var totalNovos = 0;
        var identicos = 0;
        await foreach (var campos in LerAsync(conexao, $"select {lista} from {tabelaNova}", preparar, cancellationToken)
            .ConfigureAwait(false))
        {
            totalNovos++;
            arquivosNovos.Add(campos[indiceArquivo]);
            var chave = string.Join(Separador, campos);
            if (antigas.TryGetValue(chave, out var quantidade) && quantidade > 0)
            {
                antigas[chave] = quantidade - 1;
                identicos++;
            }
            else
            {
                soNoNovo.Add(campos);
            }
        }

        var soNoAntigo = antigas
            .Where(par => par.Value > 0)
            .SelectMany(par => Enumerable.Repeat(par.Key.Split(Separador), par.Value))
            .ToList();

        var marcados = await ContarAsync(
            conexao, $"select count(*) from {tabelaNova} where origem_carga = '{LayoutsCgu.MarcaDeOrigem}'", cancellationToken)
            .ConfigureAwait(false);

        return new ResultadoComparacao(
            banco,
            layout.Tabela,
            totalAntigos,
            totalNovos,
            identicos,
            soNoAntigo.Count,
            soNoNovo.Count,
            marcados,
            [.. arquivosAntigos],
            [.. arquivosNovos],
            Exemplos(layout, colunas, soNoAntigo, soNoNovo));
    }

    /// <summary>Até 5 diferenças, só com a chave do registro e o nome das colunas que divergem (sem os valores).</summary>
    private static List<string> Exemplos(
        LayoutTabela layout, List<string> colunas, List<string[]> soNoAntigo, List<string[]> soNoNovo)
    {
        var indicesDaChave = layout.Chave.Select(nome => colunas.IndexOf(nome)).ToArray();
        string Chave(string[] campos) => string.Join(" | ", indicesDaChave.Select(i => campos[i]));

        var novosPorChave = soNoNovo.GroupBy(Chave, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var exemplos = new List<string>();
        foreach (var antigo in soNoAntigo.Take(5))
        {
            var chave = Chave(antigo);
            if (novosPorChave.TryGetValue(chave, out var novo))
            {
                var divergentes = colunas.Where((_, i) => antigo[i] != novo[i]);
                exemplos.Add($"[{chave}] colunas diferentes: {string.Join(", ", divergentes)}");
            }
            else
            {
                exemplos.Add($"[{chave}] existe só na tabela antiga");
            }
        }

        var chavesAntigas = soNoAntigo.Select(Chave).ToHashSet(StringComparer.Ordinal);
        exemplos.AddRange(soNoNovo.Where(n => !chavesAntigas.Contains(Chave(n))).Take(5)
            .Select(n => $"[{Chave(n)}] existe só na tabela nova"));
        return exemplos;
    }

    private static async IAsyncEnumerable<string[]> LerAsync(
        DbConnection conexao,
        string sql,
        Action<DbCommand>? preparar,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comando = conexao.CreateCommand();
        await using (comando.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // nomes de tabela e coluna vêm de constantes internas
            comando.CommandText = sql;
#pragma warning restore CA2100
            comando.CommandTimeout = 600;
            preparar?.Invoke(comando);
            var leitor = await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (leitor.ConfigureAwait(false))
            {
                var valores = new object[leitor.FieldCount];
                while (await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    leitor.GetValues(valores);
                    yield return Array.ConvertAll(valores, Canonico);
                }
            }
        }
    }

    private static async Task<int> ContarAsync(DbConnection conexao, string sql, CancellationToken cancellationToken)
    {
        var comando = conexao.CreateCommand();
        await using (comando.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // nomes de tabela vêm de constantes internas
            comando.CommandText = sql;
#pragma warning restore CA2100
            var valor = await comando.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(valor, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Texto único por valor, igual para tipos equivalentes (data sem hora, número sem zeros à direita).</summary>
    private static string Canonico(object valor) => valor switch
    {
        DBNull => Nulo,
        string texto => texto,
        DateTime data => data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly data => data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal numero => numero.ToString("0.############", CultureInfo.InvariantCulture),
        IFormattable formatavel => formatavel.ToString(null, CultureInfo.InvariantCulture),
        _ => valor.ToString() ?? string.Empty,
    };
}
