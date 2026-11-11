using System.Diagnostics;
using MotorCredito.Core.Cgu;
using Npgsql;
using NpgsqlTypes;

namespace MotorCredito.Dados.BancoPostgres;

/// <summary>
/// Grava as listas no PostgreSQL (schema credito_net) com COPY binário.
/// Tudo numa transação: se algo falhar, nada é gravado e os dados anteriores continuam lá.
/// </summary>
public static class CarregadorPostgres
{
    public static async Task<IReadOnlyList<ResultadoCarga>> CarregarAsync(
        string conexao, IReadOnlyList<CargaTabela> cargas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cargas);
        var resultados = new List<ResultadoCarga>();
        await using var con = new NpgsqlConnection(conexao);
        await con.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = await con.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var carga in cargas)
        {
            var relogio = Stopwatch.StartNew();
            // Nomes de schema e tabela vêm de constantes internas, nunca de entrada externa.
            var tabela = $"{Esquemas.PostgresNet}.{carga.Layout.Tabela}";
            var apagar = carga.FiltroCadastro is null
                ? $"delete from {tabela}"
                : $"delete from {tabela} where cadastro = $1";
#pragma warning disable CA2100 // SQL montado só com constantes internas; o valor vai por parâmetro
            await using (var comando = new NpgsqlCommand(apagar, con, transacao))
#pragma warning restore CA2100
            {
                if (carga.FiltroCadastro is not null)
                {
                    comando.Parameters.Add(new NpgsqlParameter { Value = carga.FiltroCadastro });
                }

                await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var colunas = carga.Layout.Colunas;
            var nomes = string.Join(", ", colunas.Select(c => c.Nome));
            var importador = await con.BeginBinaryImportAsync(
                $"copy {tabela} ({nomes}) from stdin (format binary)", cancellationToken).ConfigureAwait(false);
            await using (importador.ConfigureAwait(false))
            {
                foreach (var linha in carga.Linhas)
                {
                    await importador.StartRowAsync(cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < colunas.Count; i++)
                    {
                        await EscreverAsync(importador, linha[i], colunas[i].Tipo, cancellationToken).ConfigureAwait(false);
                    }
                }

                await importador.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            resultados.Add(new ResultadoCarga(carga.Rotulo, carga.Linhas.Count, relogio.Elapsed));
        }

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resultados;
    }

    private static Task EscreverAsync(
        NpgsqlBinaryImporter importador, object? valor, TipoColuna tipo, CancellationToken cancellationToken) =>
        valor switch
        {
            null => importador.WriteNullAsync(cancellationToken),
            string texto => importador.WriteAsync(texto, TipoDeTexto(tipo), cancellationToken),
            long inteiro => importador.WriteAsync(inteiro, NpgsqlDbType.Bigint, cancellationToken),
            decimal numero => importador.WriteAsync(numero, NpgsqlDbType.Numeric, cancellationToken),
            DateOnly data => importador.WriteAsync(data, NpgsqlDbType.Date, cancellationToken),
            _ => throw new InvalidOperationException($"Tipo não suportado na carga: {valor.GetType().Name}"),
        };

    private static NpgsqlDbType TipoDeTexto(TipoColuna tipo) => tipo switch
    {
        TipoColuna.TextoFixo => NpgsqlDbType.Char,
        TipoColuna.TextoLongo => NpgsqlDbType.Text,
        _ => NpgsqlDbType.Varchar,
    };
}
