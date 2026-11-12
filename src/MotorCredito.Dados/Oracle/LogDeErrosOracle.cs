using MotorCredito.Core.Listas;
using Oracle.ManagedDataAccess.Client;

namespace MotorCredito.Dados.BancoOracle;

/// <summary>Grava o log de erros pelo sinônimo log_erros (MOTOR_APP_NET só tem INSERT em MOTOR_CREDITO_NET.LOG_ERROS).</summary>
public sealed class LogDeErrosOracle(OracleSettings settings) : IRepositorioDeErros
{
    private const string SqlInsert =
        "insert into log_erros (request_id, metodo, rota, status, codigo, titulo, tipo_excecao, duracao_ms, ambiente)"
        + " values (:request_id, :metodo, :rota, :status, :codigo, :titulo, :tipo_excecao, :duracao_ms, :ambiente)";

    private readonly string _conexao = ConexoesOracle.Montar(settings, settings.Usuario, settings.Senha);

    public async Task GravarAsync(IReadOnlyList<RegistroDeErro> registros, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registros);
        if (registros.Count == 0)
        {
            return;
        }

        using var conexao = new OracleConnection(_conexao);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transacao = conexao.BeginTransaction();
        using var comando = conexao.CreateCommand();
        comando.Transaction = transacao;
        comando.BindByName = true;
        comando.CommandTimeout = settings.TimeoutSegundos;
        comando.CommandText = SqlInsert;
        comando.ArrayBindCount = registros.Count; // um único envio para o lote inteiro

        Adicionar(comando, "request_id", OracleDbType.Varchar2, registros.Select(r => (object)r.RequestId));
        Adicionar(comando, "metodo", OracleDbType.Varchar2, registros.Select(r => (object)r.Metodo));
        Adicionar(comando, "rota", OracleDbType.Varchar2, registros.Select(r => (object)r.Rota));
        Adicionar(comando, "status", OracleDbType.Int32, registros.Select(r => (object)r.Status));
        Adicionar(comando, "codigo", OracleDbType.Varchar2, registros.Select(r => (object)r.Codigo));
        Adicionar(comando, "titulo", OracleDbType.Varchar2, registros.Select(r => (object)r.Titulo));
        Adicionar(comando, "tipo_excecao", OracleDbType.Varchar2, registros.Select(r => (object?)r.TipoExcecao ?? DBNull.Value));
        Adicionar(comando, "duracao_ms", OracleDbType.Int32, registros.Select(r => r.DuracaoMs is int d ? (object)d : DBNull.Value));
        Adicionar(comando, "ambiente", OracleDbType.Varchar2, registros.Select(r => (object)r.Ambiente));

        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Adicionar(OracleCommand comando, string nome, OracleDbType tipo, IEnumerable<object> valores) =>
        comando.Parameters.Add(nome, tipo).Value = valores.ToArray();
}
