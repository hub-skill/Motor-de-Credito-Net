using System.Data.Common;
using System.Globalization;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Xunit;

namespace MotorCredito.BancosTests;

/// <summary>
/// Movimentação e log de erros contra os bancos reais (PostgreSQL e Oracle). A aplicação só executa as
/// funções/procedures da movimentação e só insere na log_erros; as conferências e a limpeza usam o administrador.
/// Rode com: dotnet test --filter Categoria=Bancos
/// </summary>
[Trait("Categoria", "Bancos")]
[Collection(ColecaoDoBanco.Nome)]
public sealed class MovimentacaoBancosTests
{
    private static readonly int AnoAtual = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).Year; // horário de Brasília

    static MovimentacaoBancosTests() => ArquivoEnv.CarregarSeExistir();

    public static TheoryData<string> Bancos => ["postgres", "oracle"];

    private static IRepositorioDeMovimentacao Movimentacao(string banco, out IAsyncDisposable? descarte)
    {
        var settings = BancosSettings.DoAmbiente();
        if (banco == "oracle")
        {
            descarte = null;
            return new MovimentacaoOracle(settings.Oracle);
        }

        var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres);
        descarte = fonte;
        return new MovimentacaoPostgres(fonte);
    }

    private static string Tabela(string banco, string nome) =>
        banco == "oracle" ? $"{Esquemas.OracleNet}.{nome}" : $"{Esquemas.PostgresNet}.{nome}";

    /// <summary>Executa como administrador (só para conferir e limpar). Parâmetros: ? no texto, em ordem.</summary>
    private static async Task<List<object?[]>> AdminAsync(string banco, string sql, params object[] parametros)
    {
        var bancos = BancosSettings.DoAmbiente();
        var admin = AdminSettings.DoAmbiente();
        await using DbConnection conexao = banco == "oracle"
            ? new OracleConnection(ConexoesOracle.Montar(bancos.Oracle, admin.OracleUsuario, admin.OracleSenha))
            : new NpgsqlConnection(ConexoesPostgres.Montar(bancos.Postgres, admin.PostgresUsuario, admin.PostgresSenha, somenteLeitura: false));
        await conexao.OpenAsync();
        await using var transacao = await conexao.BeginTransactionAsync();
        await using var comando = conexao.CreateCommand();
        comando.Transaction = transacao;
        var texto = sql;
        for (var n = 1; n <= parametros.Length; n++)
        {
            var indice = texto.IndexOf('?', StringComparison.Ordinal);
            texto = string.Concat(texto.AsSpan(0, indice), banco == "oracle" ? $":{n}" : $"${n}", texto.AsSpan(indice + 1));
            var parametro = comando.CreateParameter();
            parametro.Value = parametros[n - 1];
            comando.Parameters.Add(parametro);
        }

#pragma warning disable CA2100 // SQL fixo do próprio teste
        comando.CommandText = texto;
#pragma warning restore CA2100

        var linhas = new List<object?[]>();
        await using (var leitor = await comando.ExecuteReaderAsync())
        {
            while (await leitor.ReadAsync())
            {
                var linha = new object?[leitor.FieldCount];
                for (var i = 0; i < linha.Length; i++)
                {
                    var valor = leitor.IsDBNull(i) ? null : leitor.GetValue(i);
                    linha[i] = valor is OracleClob clob ? clob.Value : valor;
                }

                linhas.Add(linha);
            }
        }

        await transacao.CommitAsync();
        return linhas;
    }

    private static async Task<Dictionary<string, object?>> LinhaAsync(string banco, int ano, long requisicao)
    {
        string[] nomes =
        [
            "metodo", "rota", "requisicao_conteudo", "situacao_requisicao", "status_http", "resposta_conteudo",
            "resposta_em", "situacao_resposta", "requisicao_em",
        ];
        var linhas = await AdminAsync(
            banco,
            $"select {string.Join(", ", nomes)} from {Tabela(banco, "movimentacao")} where ano = ? and requisicao = ?",
            ano,
            requisicao);
        var linha = Assert.Single(linhas);
        return nomes.Zip(linha).ToDictionary(par => par.First, par => par.Second);
    }

    private static async Task LimparAsync(string banco, IEnumerable<(int Ano, long Requisicao)> chaves)
    {
        foreach (var (ano, requisicao) in chaves)
        {
            await AdminAsync(banco, $"delete from {Tabela(banco, "log_erros")} where ano = ? and requisicao = ?", ano, requisicao);
            await AdminAsync(banco, $"delete from {Tabela(banco, "movimentacao")} where ano = ? and requisicao = ?", ano, requisicao);
        }
    }

    private static string? Texto(object? valor) => valor is null ? null : Convert.ToString(valor, CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Requisicao_e_numerada_e_a_chegada_e_gravada(string banco)
    {
        var movimentacao = Movimentacao(banco, out var descarte);
        var chaves = new List<(int, long)>();
        try
        {
            var (ano, requisicao) = await movimentacao.RegistrarRequisicaoAsync("POST", "/api/v1/triagem", "{\"documento\": \"***.982.247-**\"}", CancellationToken.None);
            chaves.Add((ano, requisicao));

            Assert.Equal(AnoAtual, ano);
            Assert.True(requisicao >= 1);
            var linha = await LinhaAsync(banco, ano, requisicao);
            Assert.Equal(("POST", "/api/v1/triagem"), (Texto(linha["metodo"]), Texto(linha["rota"])));
            Assert.Equal("{\"documento\": \"***.982.247-**\"}", Texto(linha["requisicao_conteudo"]));
            Assert.NotNull(linha["requisicao_em"]);
            // Na chegada a resposta e as duas situações ainda não existem.
            Assert.Null(linha["resposta_em"]);
            Assert.Null(linha["status_http"]);
            Assert.Null(linha["situacao_requisicao"]);
            Assert.Null(linha["situacao_resposta"]);
        }
        finally
        {
            await LimparAsync(banco, chaves);
            if (descarte is not null)
            {
                await descarte.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Resposta_completa_a_mesma_linha_uma_vez_so(string banco)
    {
        var movimentacao = Movimentacao(banco, out var descarte);
        var chaves = new List<(int, long)>();
        try
        {
            var (ano, requisicao) = await movimentacao.RegistrarRequisicaoAsync("POST", "/api/v1/triagem", "{}", CancellationToken.None);
            chaves.Add((ano, requisicao));

            await movimentacao.RegistrarRespostasAsync(
                [new RegistroDeResposta(ano, requisicao, 422, "{\"code\": \"entrada_invalida\"}", "ER", "ER")], CancellationToken.None);
            var linha = await LinhaAsync(banco, ano, requisicao);
            Assert.Equal(("422", "ER", "ER"), (Texto(linha["status_http"]), Texto(linha["situacao_requisicao"]), Texto(linha["situacao_resposta"])));
            Assert.Equal("{\"code\": \"entrada_invalida\"}", Texto(linha["resposta_conteudo"]));
            Assert.NotNull(linha["resposta_em"]);

            // Uma segunda resposta para a mesma requisição não sobrescreve a primeira.
            await movimentacao.RegistrarRespostasAsync([new RegistroDeResposta(ano, requisicao, 200, "outra", "OK", "OK")], CancellationToken.None);
            Assert.Equal("{\"code\": \"entrada_invalida\"}", Texto((await LinhaAsync(banco, ano, requisicao))["resposta_conteudo"]));
        }
        finally
        {
            await LimparAsync(banco, chaves);
            if (descarte is not null)
            {
                await descarte.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Requisicoes_ao_mesmo_tempo_recebem_numeros_diferentes(string banco)
    {
        var movimentacao = Movimentacao(banco, out var descarte);
        IReadOnlyList<(int Ano, long Requisicao)> chaves = [];
        try
        {
            chaves = await Task.WhenAll(Enumerable.Range(0, 20).Select(n =>
                movimentacao.RegistrarRequisicaoAsync("GET", $"/x/{n}", null, CancellationToken.None)));

            Assert.Equal(20, chaves.Distinct().Count());
            Assert.All(chaves, chave => Assert.Equal(AnoAtual, chave.Ano));
        }
        finally
        {
            await LimparAsync(banco, chaves);
            if (descarte is not null)
            {
                await descarte.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Conteudo_aceita_qualquer_formato_e_tamanho_e_pode_ser_nulo(string banco)
    {
        var movimentacao = Movimentacao(banco, out var descarte);
        var chaves = new List<(int, long)>();
        try
        {
            // ~32 mil caracteres: passa de 32767 bytes, o limite de texto comum do Oracle.
            string?[] conteudos = [string.Concat(Enumerable.Repeat("ação é ç ", 3500)), "<?xml version=\"1.0\"?><a b=\"1\">ç</a>", null];
            foreach (var conteudo in conteudos)
            {
                var (ano, requisicao) = await movimentacao.RegistrarRequisicaoAsync("POST", "/x", conteudo, CancellationToken.None);
                chaves.Add((ano, requisicao));
                await movimentacao.RegistrarRespostasAsync([new RegistroDeResposta(ano, requisicao, 200, conteudo, "OK", "OK")], CancellationToken.None);

                var linha = await LinhaAsync(banco, ano, requisicao);
                Assert.Equal(conteudo, Texto(linha["requisicao_conteudo"]));
                Assert.Equal(conteudo, Texto(linha["resposta_conteudo"]));
            }
        }
        finally
        {
            await LimparAsync(banco, chaves);
            if (descarte is not null)
            {
                await descarte.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Aplicacao_nao_le_nem_altera_a_movimentacao(string banco)
    {
        // O usuário da aplicação só executa as funções/procedures: select, update e delete nas tabelas são negados.
        var settings = BancosSettings.DoAmbiente();
        string[] negados =
        [
            "select count(*) from {m}",
            "select count(*) from {c}",
            "update {c} set ultimo = 1",
            "delete from {m}",
        ];
        foreach (var modelo in negados)
        {
            var prefixo = banco == "oracle" ? string.Empty : $"{Esquemas.PostgresNet}.";
            var sql = modelo.Replace("{m}", $"{prefixo}movimentacao", StringComparison.Ordinal)
                .Replace("{c}", $"{prefixo}contador_requisicao", StringComparison.Ordinal);
            if (banco == "oracle")
            {
                using var conexao = new OracleConnection(ConexoesOracle.Montar(settings.Oracle, settings.Oracle.Usuario, settings.Oracle.Senha));
                await conexao.OpenAsync();
                using var comando = new OracleCommand(sql, conexao);
                await Assert.ThrowsAsync<OracleException>(() => comando.ExecuteNonQueryAsync());
            }
            else
            {
                // Sessão de escrita com o usuário da aplicação: a negação vem da permissão (42501), não do modo
                // somente leitura que a API usa (25006), que barraria o update e o delete antes.
                await using var conexao = new NpgsqlConnection(ConexoesPostgres.Montar(
                    settings.Postgres, settings.Postgres.Usuario, settings.Postgres.Senha, somenteLeitura: false));
                await conexao.OpenAsync();
                await using var comando = new NpgsqlCommand(sql, conexao);
                var erro = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Grava_log_de_erros_com_a_chave_da_movimentacao_mas_nao_le(string banco)
    {
        // A aplicação só tem INSERT em log_erros: gravar funciona, ler é negado.
        var settings = BancosSettings.DoAmbiente();
        var movimentacao = Movimentacao(banco, out var descarte);
        var chaves = new List<(int, long)>();
        try
        {
            var (ano, requisicao) = await movimentacao.RegistrarRequisicaoAsync("POST", "/api/v1/triagem", "{}", CancellationToken.None);
            chaves.Add((ano, requisicao));
            RegistroDeErro[] erros =
            [
                new(ano, requisicao, "teste-bancos-net-1", "POST", "/api/v1/triagem", 422, "entrada_invalida", "Dados inválidos.", null, 5, "Teste"),
                new(ano, requisicao, "teste-bancos-net-2", "GET", "/x", 500, "erro_interno", "Erro interno.", "System.InvalidOperationException", null, "Teste"),
            ];

            if (banco == "oracle")
            {
                await new LogDeErrosOracle(settings.Oracle).GravarAsync(erros, CancellationToken.None);
                using var conexao = new OracleConnection(ConexoesOracle.Montar(settings.Oracle, settings.Oracle.Usuario, settings.Oracle.Senha));
                await conexao.OpenAsync();
                using var leitura = new OracleCommand("select count(*) from log_erros", conexao);
                await Assert.ThrowsAsync<OracleException>(() => leitura.ExecuteScalarAsync());
            }
            else
            {
                await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres);
                await new LogDeErrosPostgres(fonte).GravarAsync(erros, CancellationToken.None);
                await using var leitura = fonte.CreateCommand("select count(*) from credito_net.log_erros");
                var erro = await Assert.ThrowsAsync<PostgresException>(() => leitura.ExecuteScalarAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
            }

            var gravadas = await AdminAsync(
                banco,
                $"select ano, requisicao, codigo from {Tabela(banco, "log_erros")} where ano = ? and requisicao = ? order by id",
                ano,
                requisicao);
            Assert.Equal(
                [(ano.ToString(CultureInfo.InvariantCulture), requisicao.ToString(CultureInfo.InvariantCulture), "entrada_invalida"),
                 (ano.ToString(CultureInfo.InvariantCulture), requisicao.ToString(CultureInfo.InvariantCulture), "erro_interno")],
                gravadas.Select(l => (Texto(l[0])!, Texto(l[1])!, Texto(l[2])!)));
        }
        finally
        {
            await LimparAsync(banco, chaves);
            if (descarte is not null)
            {
                await descarte.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Log_de_erros_recusa_erro_sem_movimentacao(string banco)
    {
        // Chave que não existe na movimentação: o banco recusa (chave estrangeira); nunca grava linha solta.
        var settings = BancosSettings.DoAmbiente();
        RegistroDeErro[] inexistente =
        [
            new(AnoAtual, 999_999_999, "teste-bancos-net-x", "GET", "/x", 500, "erro_interno", "Erro interno.", null, null, "Teste"),
        ];

        if (banco == "oracle")
        {
            await Assert.ThrowsAsync<OracleException>(() => new LogDeErrosOracle(settings.Oracle).GravarAsync(inexistente, CancellationToken.None));
        }
        else
        {
            await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres);
            var erro = await Assert.ThrowsAsync<PostgresException>(() => new LogDeErrosPostgres(fonte).GravarAsync(inexistente, CancellationToken.None));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, erro.SqlState);
        }

        var total = await AdminAsync(banco, $"select count(*) from {Tabela(banco, "log_erros")} where request_id = ?", "teste-bancos-net-x");
        Assert.Equal("0", Texto(Assert.Single(total)[0]));
    }
}
