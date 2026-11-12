using MotorCredito.Core.Listas;
using Npgsql;
using NpgsqlTypes;

namespace MotorCredito.Dados.BancoPostgres;

/// <summary>
/// Movimentação em credito_net: o motor_app_net não tem acesso às tabelas, só executa as duas funções
/// (SECURITY DEFINER), que numeram, gravam e completam a linha.
/// </summary>
public sealed class MovimentacaoPostgres(NpgsqlDataSource fonte) : IRepositorioDeMovimentacao
{
    private const string SqlRequisicao =
        "select ano, requisicao from credito_net.registrar_requisicao($1::varchar, $2::varchar, $3::text)";

    private const string SqlResposta =
        "select credito_net.registrar_resposta($1::smallint, $2::bigint, $3::smallint, $4::text, $5::char(2), $6::char(2))";

    public async Task<(int Ano, long Requisicao)> RegistrarRequisicaoAsync(
        string metodo, string rota, string? conteudo, CancellationToken cancellationToken)
    {
        await using var conexao = await fonte.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SomenteEstaTransacaoEscreveAsync(conexao, transacao, cancellationToken).ConfigureAwait(false);

        await using var comando = new NpgsqlCommand(SqlRequisicao, conexao, transacao);
        comando.Parameters.Add(new NpgsqlParameter { Value = metodo });
        comando.Parameters.Add(new NpgsqlParameter { Value = rota });
        comando.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = conteudo, NpgsqlDbType = NpgsqlDbType.Text });

        (int Ano, long Requisicao) numero;
        await using (var leitor = await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("registrar_requisicao não devolveu o número.");
            }

            numero = (leitor.GetInt16(0), leitor.GetInt64(1));
        }

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return numero;
    }

    public async Task RegistrarRespostasAsync(
        IReadOnlyList<RegistroDeResposta> respostas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(respostas);
        await using var conexao = await fonte.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SomenteEstaTransacaoEscreveAsync(conexao, transacao, cancellationToken).ConfigureAwait(false);

        await using var lote = new NpgsqlBatch(conexao, transacao);
        foreach (var r in respostas)
        {
            var comando = new NpgsqlBatchCommand(SqlResposta);
            comando.Parameters.Add(new NpgsqlParameter { Value = (short)r.Ano });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Requisicao });
            comando.Parameters.Add(new NpgsqlParameter { Value = (short)r.StatusHttp });
            comando.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.Conteudo, NpgsqlDbType = NpgsqlDbType.Text });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.SituacaoRequisicao });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.SituacaoResposta });
            lote.BatchCommands.Add(comando);
        }

        await lote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // A sessão é somente leitura (default_transaction_read_only); só esta transação, com SQL fixo, escreve.
    private static async Task SomenteEstaTransacaoEscreveAsync(
        NpgsqlConnection conexao, NpgsqlTransaction transacao, CancellationToken cancellationToken)
    {
        await using var leituraEscrita = new NpgsqlCommand("set transaction read write", conexao, transacao);
        await leituraEscrita.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
