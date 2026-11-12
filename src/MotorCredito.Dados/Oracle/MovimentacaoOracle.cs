using System.Data;
using System.Globalization;
using MotorCredito.Core.Listas;
using Oracle.ManagedDataAccess.Client;

namespace MotorCredito.Dados.BancoOracle;

/// <summary>
/// Movimentação em MOTOR_CREDITO_NET: o MOTOR_APP_NET não tem acesso às tabelas, só executa as duas procedures
/// (pelos sinônimos), que numeram, gravam e completam a linha.
/// </summary>
public sealed class MovimentacaoOracle(OracleSettings settings) : IRepositorioDeMovimentacao
{
    private readonly string _conexao = ConexoesOracle.Montar(settings, settings.Usuario, settings.Senha);

    public async Task<(int Ano, long Requisicao)> RegistrarRequisicaoAsync(
        string metodo, string rota, string? conteudo, CancellationToken cancellationToken)
    {
        using var conexao = new OracleConnection(_conexao);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transacao = conexao.BeginTransaction();
        using var comando = Procedure(conexao, transacao, "registrar_requisicao");
        comando.Parameters.Add("p_metodo", OracleDbType.Varchar2).Value = metodo;
        comando.Parameters.Add("p_rota", OracleDbType.Varchar2).Value = rota;
        comando.Parameters.Add("p_conteudo", OracleDbType.Clob).Value = (object?)conteudo ?? DBNull.Value;
        var ano = comando.Parameters.Add("p_ano", OracleDbType.Int32, ParameterDirection.Output);
        var requisicao = comando.Parameters.Add("p_requisicao", OracleDbType.Int64, ParameterDirection.Output);

        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (
            Convert.ToInt32(ano.Value.ToString(), CultureInfo.InvariantCulture),
            Convert.ToInt64(requisicao.Value.ToString(), CultureInfo.InvariantCulture));
    }

    public async Task RegistrarRespostasAsync(
        IReadOnlyList<RegistroDeResposta> respostas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(respostas);
        if (respostas.Count == 0)
        {
            return;
        }

        using var conexao = new OracleConnection(_conexao);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transacao = conexao.BeginTransaction();
        using var comando = Procedure(conexao, transacao, "registrar_resposta");
        var ano = comando.Parameters.Add("p_ano", OracleDbType.Int32);
        var requisicao = comando.Parameters.Add("p_requisicao", OracleDbType.Int64);
        var status = comando.Parameters.Add("p_status_http", OracleDbType.Int32);
        var conteudo = comando.Parameters.Add("p_conteudo", OracleDbType.Clob);
        var situacaoRequisicao = comando.Parameters.Add("p_situacao_requisicao", OracleDbType.Varchar2);
        var situacaoResposta = comando.Parameters.Add("p_situacao_resposta", OracleDbType.Varchar2);

        foreach (var r in respostas)
        {
            ano.Value = r.Ano;
            requisicao.Value = r.Requisicao;
            status.Value = r.StatusHttp;
            conteudo.Value = (object?)r.Conteudo ?? DBNull.Value;
            situacaoRequisicao.Value = r.SituacaoRequisicao;
            situacaoResposta.Value = r.SituacaoResposta;
            await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private OracleCommand Procedure(OracleConnection conexao, OracleTransaction transacao, string nome)
    {
        var comando = conexao.CreateCommand();
        comando.Transaction = transacao;
        comando.BindByName = true;
        comando.CommandType = CommandType.StoredProcedure;
        comando.CommandText = nome;
        comando.CommandTimeout = settings.TimeoutSegundos;
        return comando;
    }
}
