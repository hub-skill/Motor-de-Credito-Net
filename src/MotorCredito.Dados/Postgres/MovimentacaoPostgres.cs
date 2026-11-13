using MotorCredito.Core.Listas;
using Npgsql;
using NpgsqlTypes;

namespace MotorCredito.Dados.BancoPostgres;

/// <summary>
/// Movimentação em credito_net: o motor_app_net só tem INSERT em movimentacao e log_erros (nada de leitura, alteração
/// ou exclusão). Cada lote é uma transação: a movimentação e, quando houver, o seu erro. A sessão da API é somente
/// leitura (default_transaction_read_only): só as transações daqui pedem escrita.
/// </summary>
public sealed class MovimentacaoPostgres(NpgsqlDataSource fonte) : IRepositorioDeMovimentacao
{
    private const string SqlMovimentacao =
        "insert into credito_net.movimentacao (requisicao_id, metodo, rota, requisicao_conteudo, requisicao_em,"
        + " situacao_requisicao, status_http, resposta_conteudo, resposta_em, situacao_resposta)"
        + " values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)";

    private const string SqlErro =
        "insert into credito_net.log_erros (requisicao_id, request_id, metodo, rota, status, codigo, titulo,"
        + " tipo_excecao, duracao_ms, ambiente) values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)";

    public async Task GravarAsync(IReadOnlyList<RegistroDeMovimentacao> registros, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registros);
        if (registros.Count == 0)
        {
            return;
        }

        await using var conexao = await fonte.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var leituraEscrita = new NpgsqlCommand("set transaction read write", conexao, transacao))
        {
            await leituraEscrita.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var lote = new NpgsqlBatch(conexao, transacao);
        foreach (var r in registros)
        {
            var comando = new NpgsqlBatchCommand(SqlMovimentacao);
            comando.Parameters.Add(new NpgsqlParameter { Value = r.RequisicaoId });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Metodo });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Rota });
            comando.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.RequisicaoConteudo, NpgsqlDbType = NpgsqlDbType.Text });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.RequisicaoEm.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.SituacaoRequisicao });
            comando.Parameters.Add(new NpgsqlParameter { Value = (short)r.StatusHttp });
            comando.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.RespostaConteudo, NpgsqlDbType = NpgsqlDbType.Text });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.RespostaEm.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.SituacaoResposta });
            lote.BatchCommands.Add(comando);

            if (r.Erro is { } e)
            {
                var erro = new NpgsqlBatchCommand(SqlErro);
                erro.Parameters.Add(new NpgsqlParameter { Value = r.RequisicaoId });
                erro.Parameters.Add(new NpgsqlParameter { Value = e.RequestId });
                erro.Parameters.Add(new NpgsqlParameter { Value = e.Metodo });
                erro.Parameters.Add(new NpgsqlParameter { Value = e.Rota });
                erro.Parameters.Add(new NpgsqlParameter { Value = (short)e.Status });
                erro.Parameters.Add(new NpgsqlParameter { Value = e.Codigo });
                erro.Parameters.Add(new NpgsqlParameter { Value = e.Titulo });
                erro.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = e.TipoExcecao, NpgsqlDbType = NpgsqlDbType.Varchar });
                erro.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = e.DuracaoMs, NpgsqlDbType = NpgsqlDbType.Integer });
                erro.Parameters.Add(new NpgsqlParameter { Value = e.Ambiente });
                lote.BatchCommands.Add(erro);
            }
        }

        await lote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
