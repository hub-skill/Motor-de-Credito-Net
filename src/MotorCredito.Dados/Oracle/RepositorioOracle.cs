using MotorCredito.Core.Listas;
using Oracle.ManagedDataAccess.Client;

namespace MotorCredito.Dados.BancoOracle;

public static class ConexoesOracle
{
    public static string Montar(OracleSettings settings, string usuario, string senha)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new OracleConnectionStringBuilder
        {
            DataSource = settings.DataSource,
            UserID = usuario,
            Password = senha,
            Pooling = true,
            MaxPoolSize = 5,
            ConnectionTimeout = 5,
        }.ConnectionString;
    }
}

/// <summary>Leitura das listas no Oracle. O MOTOR_APP_NET acessa MOTOR_CREDITO_NET pelos sinônimos.</summary>
public sealed class RepositorioOracle(OracleSettings settings) : IRepositorioListas
{
    private const string SqlSancoes =
        "select cadastro, codigo_sancao, categoria, orgao_sancionador, uf_orgao, data_inicio, data_fim, numero_processo"
        + " from sancoes where documento = :parametro order by data_inicio desc";

    private const string SqlPeps =
        "select nome, descricao_funcao, nome_orgao, data_inicio_exercicio, data_fim_exercicio, data_fim_carencia"
        + " from peps where cpf_miolo = :parametro";

    private const string SqlOrigem =
        "select arquivo_origem, max(carregado_em) from sancoes group by arquivo_origem"
        + " union all select arquivo_origem, max(carregado_em) from peps group by arquivo_origem";

    // ORA-01013: comando cancelado pelo tempo limite. Os demais: listener, serviço ou conexão perdida.
    private const int OraCancelado = 1013;
    private static readonly HashSet<int> OraIndisponivel = [1017, 1033, 1034, 1089, 3113, 3114, 3135, 12154, 12170, 12514, 12537, 12541, 12545, 28000];

    private readonly string _conexao = ConexoesOracle.Montar(settings, settings.Usuario, settings.Senha);

    public Task<IReadOnlyList<Sancao>> BuscarSancoesAsync(string documento, CancellationToken cancellationToken) =>
        ConsultarAsync(
            SqlSancoes,
            documento,
            static r => new Sancao(
                r.GetString(0), r.GetInt64(1), Texto(r, 2), Texto(r, 3), Texto(r, 4), Data(r, 5), Data(r, 6), Texto(r, 7)),
            cancellationToken);

    public Task<IReadOnlyList<RegistroPep>> BuscarPepsPorMioloAsync(string miolo, CancellationToken cancellationToken) =>
        ConsultarAsync(
            SqlPeps,
            miolo,
            static r => new RegistroPep(r.GetString(0), Texto(r, 1), Texto(r, 2), Data(r, 3), Data(r, 4), Data(r, 5)),
            cancellationToken);

    public async Task<OrigemDados> ObterOrigemAsync(CancellationToken cancellationToken)
    {
        // carregado_em é TIMESTAMP sem fuso, gravado na hora local do servidor.
        var linhas = await ConsultarAsync(
            SqlOrigem,
            parametro: null,
            static r => (Arquivo: r.GetString(0), Carregado: DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Local)),
            cancellationToken).ConfigureAwait(false);
        return new OrigemDados(
            "oracle",
            linhas.Select(l => l.Arquivo).Order(StringComparer.Ordinal).ToList(),
            linhas.Count == 0 ? null : new DateTimeOffset(linhas.Max(l => l.Carregado)));
    }

    public async Task VerificarAsync(CancellationToken cancellationToken) =>
        await ConsultarAsync("select 1 from dual", parametro: null, static r => r.GetInt32(0), cancellationToken)
            .ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> ConsultarAsync<T>(
        string sql, string? parametro, Func<OracleDataReader, T> mapear, CancellationToken cancellationToken)
    {
        try
        {
            using var conexao = new OracleConnection(_conexao);
            await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var comando = conexao.CreateCommand();
            comando.BindByName = true;
            comando.CommandTimeout = settings.TimeoutSegundos;
            comando.CommandText = sql;
            if (parametro is not null)
            {
                comando.Parameters.Add("parametro", OracleDbType.Varchar2).Value = parametro;
            }

            using var leitor = (OracleDataReader)await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var lista = new List<T>();
            while (await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lista.Add(mapear(leitor));
            }

            return lista;
        }
        catch (OracleException e) when (e.Number == OraCancelado)
        {
            throw new BancoExpirouException(e);
        }
        catch (OracleException e) when (OraIndisponivel.Contains(e.Number) || e.Number is >= 12500 and <= 12699)
        {
            throw new BancoIndisponivelException(e);
        }
    }

    private static string? Texto(OracleDataReader leitor, int indice) =>
        leitor.IsDBNull(indice) ? null : leitor.GetString(indice);

    // O Oracle devolve DATE como DateTime; o contrato usa DateOnly.
    private static DateOnly? Data(OracleDataReader leitor, int indice) =>
        leitor.IsDBNull(indice) ? null : DateOnly.FromDateTime(leitor.GetDateTime(indice));
}
