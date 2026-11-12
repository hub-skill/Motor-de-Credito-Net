using MotorCredito.Core.Listas;
using Npgsql;

namespace MotorCredito.Dados.BancoPostgres;

/// <summary>Grava o log de erros em credito_net.log_erros (o motor_app_net só tem INSERT nela).</summary>
public sealed class LogDeErrosPostgres(NpgsqlDataSource fonte) : IRepositorioDeErros
{
    private const string SqlInsert =
        "insert into credito_net.log_erros"
        + " (request_id, metodo, rota, status, codigo, titulo, tipo_excecao, duracao_ms, ambiente)"
        + " values ($1, $2, $3, $4, $5, $6, $7, $8, $9)";

    public async Task GravarAsync(IReadOnlyList<RegistroDeErro> registros, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registros);
        await using var conexao = await fonte.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A sessão é somente leitura (default_transaction_read_only); só esta transação, com SQL fixo, escreve.
        await using (var leituraEscrita = new NpgsqlCommand("set transaction read write", conexao, transacao))
        {
            await leituraEscrita.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var lote = new NpgsqlBatch(conexao, transacao);
        foreach (var r in registros)
        {
            var comando = new NpgsqlBatchCommand(SqlInsert);
            comando.Parameters.Add(new NpgsqlParameter { Value = r.RequestId });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Metodo });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Rota });
            comando.Parameters.Add(new NpgsqlParameter { Value = (short)r.Status });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Codigo });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Titulo });
            comando.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = r.TipoExcecao, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
            comando.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = r.DuracaoMs, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
            comando.Parameters.Add(new NpgsqlParameter { Value = r.Ambiente });
            lote.BatchCommands.Add(comando);
        }

        await lote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
