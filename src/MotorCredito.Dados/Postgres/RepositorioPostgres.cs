using MotorCredito.Core.Listas;
using Npgsql;

namespace MotorCredito.Dados.BancoPostgres;

public static class ConexoesPostgres
{
    /// <summary>Fonte de conexões (com pool) da API: sessão somente leitura e com tempo limite por comando.</summary>
    public static NpgsqlDataSource CriarFonteDeLeitura(PostgresSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return NpgsqlDataSource.Create(Montar(settings, settings.Usuario, settings.Senha, somenteLeitura: true));
    }

    public static string Montar(
        PostgresSettings settings, string usuario, string senha, bool somenteLeitura, string aplicacao = "motor-credito-net")
    {
        ArgumentNullException.ThrowIfNull(settings);
        var construtor = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host,
            Port = settings.Porta,
            Database = settings.Banco,
            Username = usuario,
            Password = senha,
            SslMode = Enum.Parse<SslMode>(settings.SslMode, ignoreCase: true),
            Timeout = 5,
            ApplicationName = aplicacao,
            MaxPoolSize = 5,
        };
        if (somenteLeitura)
        {
            var limiteMs = settings.TimeoutSegundos * 1000;
            construtor.CommandTimeout = settings.TimeoutSegundos + 2;
            construtor.Options = $"-c default_transaction_read_only=on -c statement_timeout={limiteMs}";
        }
        else
        {
            construtor.CommandTimeout = 600; // cargas e migrações
        }

        return construtor.ConnectionString;
    }
}

/// <summary>Leitura das listas no PostgreSQL (schema credito_net). Só consultas parametrizadas.</summary>
public sealed class RepositorioPostgres(NpgsqlDataSource fonte) : IRepositorioListas
{
    private const string SqlSancoes =
        "select cadastro, codigo_sancao, categoria, orgao_sancionador, uf_orgao, data_inicio, data_fim, numero_processo"
        + " from credito_net.sancoes where documento = $1 order by data_inicio desc";

    private const string SqlPeps =
        "select nome, descricao_funcao, nome_orgao, data_inicio_exercicio, data_fim_exercicio, data_fim_carencia"
        + " from credito_net.peps where cpf_miolo = $1";

    private const string SqlOrigem =
        "select arquivo_origem, max(carregado_em) from credito_net.sancoes group by arquivo_origem"
        + " union all select arquivo_origem, max(carregado_em) from credito_net.peps group by arquivo_origem";

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
        var linhas = await ConsultarAsync(
            SqlOrigem,
            parametro: null,
            static r => (Arquivo: r.GetString(0), Carregado: r.GetFieldValue<DateTimeOffset>(1)),
            cancellationToken).ConfigureAwait(false);
        return new OrigemDados(
            "postgres",
            linhas.Select(l => l.Arquivo).Order(StringComparer.Ordinal).ToList(),
            linhas.Count == 0 ? null : linhas.Max(l => l.Carregado));
    }

    public async Task VerificarAsync(CancellationToken cancellationToken) =>
        await ConsultarAsync("select 1", parametro: null, static r => r.GetInt32(0), cancellationToken)
            .ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> ConsultarAsync<T>(
        string sql, string? parametro, Func<NpgsqlDataReader, T> mapear, CancellationToken cancellationToken)
    {
        try
        {
            await using var comando = fonte.CreateCommand(sql);
            if (parametro is not null)
            {
                comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
            }

            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var lista = new List<T>();
            while (await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lista.Add(mapear(leitor));
            }

            return lista;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new BancoExpirouException(e); // statement_timeout
        }
        catch (PostgresException e) when (ErroDeConexao(e.SqlState))
        {
            throw new BancoIndisponivelException(e);
        }
        catch (NpgsqlException e) when (e is not PostgresException)
        {
            // Rede, servidor fora do ar ou tempo limite do driver.
            if (e.InnerException is TimeoutException)
            {
                throw new BancoExpirouException(e);
            }

            throw new BancoIndisponivelException(e);
        }
    }

    // Classes 08 (conexão), 28 (autenticação), 53 (recursos) e 57P (servidor desligando).
    private static bool ErroDeConexao(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal)
        || sqlState.StartsWith("28", StringComparison.Ordinal)
        || sqlState.StartsWith("53", StringComparison.Ordinal)
        || sqlState.StartsWith("57P", StringComparison.Ordinal);

    private static string? Texto(NpgsqlDataReader leitor, int indice) =>
        leitor.IsDBNull(indice) ? null : leitor.GetString(indice);

    private static DateOnly? Data(NpgsqlDataReader leitor, int indice) =>
        leitor.IsDBNull(indice) ? null : leitor.GetFieldValue<DateOnly>(indice);
}
