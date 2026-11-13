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
/// Movimentação e log de erros contra os bancos reais (PostgreSQL e Oracle). A aplicação só insere em movimentacao e
/// log_erros; as conferências e a limpeza usam o administrador.
/// Rode com: dotnet test --filter Categoria=Bancos
/// </summary>
[Trait("Categoria", "Bancos")]
[Collection(ColecaoDoBanco.Nome)]
public sealed class MovimentacaoBancosTests
{
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

    private static async Task LimparAsync(string banco, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            await AdminAsync(banco, $"delete from {Tabela(banco, "log_erros")} where requisicao_id = ?", id);
            await AdminAsync(banco, $"delete from {Tabela(banco, "movimentacao")} where requisicao_id = ?", id);
        }
    }

    private static string? Texto(object? valor) => valor is null ? null : Convert.ToString(valor, CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Lote_grava_requisicao_resposta_e_erro_na_mesma_transacao(string banco)
    {
        var agora = DateTimeOffset.UtcNow;
        var ok = Ulid.Novo(agora);
        var comErro = Ulid.Novo(agora);
        var grande = Ulid.Novo(agora);
        var texto = new string('x', RegrasDeMovimentacao.LimiteDoConteudo);
        var erro = new RegistroDeErro("rid", "POST", "/teste", 422, "entrada_invalida", "Dados de entrada inválidos.", null, 5, "Teste");
        var repositorio = Movimentacao(banco, out var descarte);
        try
        {
            await repositorio.GravarAsync(
            [
                new RegistroDeMovimentacao(ok, "POST", "/teste", "{\"x\":1}", agora, 200, "{\"y\":2}", agora.AddMilliseconds(3), "OK", "OK", null),
                new RegistroDeMovimentacao(comErro, "POST", "/teste", null, agora, 422, "{}", agora.AddMilliseconds(2), "ER", "ER", erro),
                new RegistroDeMovimentacao(grande, "POST", "/teste", texto, agora, 200, texto, agora, "OK", "OK", null),
            ], CancellationToken.None);

            var linha = Assert.Single(await AdminAsync(
                banco,
                $"select metodo, rota, requisicao_conteudo, situacao_requisicao, status_http, resposta_conteudo, situacao_resposta from {Tabela(banco, "movimentacao")} where requisicao_id = ?",
                ok));
            Assert.Equal(["POST", "/teste", "{\"x\":1}", "OK", "200", "{\"y\":2}", "OK"], linha.Select(v => Texto(v) ?? string.Empty).ToArray());
            var longa = Assert.Single(await AdminAsync(banco, $"select requisicao_conteudo, resposta_conteudo from {Tabela(banco, "movimentacao")} where requisicao_id = ?", grande));
            Assert.Equal((texto, texto), (Texto(longa[0]), Texto(longa[1]))); // 32 KB ida e volta (CLOB no Oracle)
            var logado = Assert.Single(await AdminAsync(banco, $"select status, codigo, ambiente from {Tabela(banco, "log_erros")} where requisicao_id = ?", comErro));
            Assert.Equal(["422", "entrada_invalida", "Teste"], logado.Select(v => Texto(v) ?? string.Empty).ToArray());
            Assert.Empty(await AdminAsync(banco, $"select 1 from {Tabela(banco, "log_erros")} where requisicao_id = ?", ok));

            // O instante é gravado com fuso: o que volta é o mesmo instante, em qualquer fuso do servidor.
            var instante = banco == "oracle"
                ? $"select round((cast(requisicao_em at time zone 'UTC' as date) - date '1970-01-01') * 86400) from {Tabela(banco, "movimentacao")} where requisicao_id = ?"
                : $"select extract(epoch from requisicao_em)::bigint from {Tabela(banco, "movimentacao")} where requisicao_id = ?";
            var segundos = Convert.ToInt64(Assert.Single(await AdminAsync(banco, instante, ok))[0], CultureInfo.InvariantCulture);
            Assert.InRange(segundos - agora.ToUnixTimeSeconds(), -1, 1);

            // O mesmo ULID não entra duas vezes (a chave primária recusa) e o lote inteiro é desfeito.
            var repetido = new RegistroDeMovimentacao(ok, "GET", "/dup", null, agora, 200, null, agora, "OK", "OK", null);
            var novo = new RegistroDeMovimentacao(Ulid.Novo(agora), "GET", "/novo", null, agora, 200, null, agora, "OK", "OK", null);
            await Assert.ThrowsAnyAsync<Exception>(() => repositorio.GravarAsync([novo, repetido], CancellationToken.None));
            Assert.Empty(await AdminAsync(banco, $"select 1 from {Tabela(banco, "movimentacao")} where requisicao_id = ?", novo.RequisicaoId));
        }
        finally
        {
            await LimparAsync(banco, [ok, comErro, grande]);
            if (descarte is not null)
            {
                await descarte.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Aplicacao_so_insere_na_movimentacao_e_no_log_de_erros(string banco)
    {
        // O usuário da aplicação só insere: select, update e delete nas duas tabelas são negados.
        var settings = BancosSettings.DoAmbiente();
        string[] negados =
        [
            "select count(*) from {m}",
            "select count(*) from {l}",
            "update {m} set status_http = 200",
            "delete from {m}",
            "update {l} set codigo = 'x'",
        ];
        var prefixo = banco == "oracle" ? string.Empty : $"{Esquemas.PostgresNet}.";
        foreach (var modelo in negados)
        {
            var sql = modelo.Replace("{m}", $"{prefixo}movimentacao", StringComparison.Ordinal)
                .Replace("{l}", $"{prefixo}log_erros", StringComparison.Ordinal);
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
    public async Task Log_de_erros_recusa_linha_sem_movimentacao(string banco)
    {
        // INSERT na log_erros é permitido, mas a chave estrangeira recusa um erro sem a sua movimentação.
        var settings = BancosSettings.DoAmbiente();
        const string Colunas = "(requisicao_id, request_id, metodo, rota, status, codigo, titulo, ambiente)";
        if (banco == "oracle")
        {
            using var conexao = new OracleConnection(ConexoesOracle.Montar(settings.Oracle, settings.Oracle.Usuario, settings.Oracle.Senha));
            await conexao.OpenAsync();
            using var comando = new OracleCommand($"insert into log_erros {Colunas} values ('SEM-PAI', 'x', 'GET', '/', 400, 'x', 'x', 'x')", conexao);
            var erro = await Assert.ThrowsAsync<OracleException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal(2291, erro.Number); // ORA-02291: chave pai não encontrada
        }
        else
        {
            await using var conexao = new NpgsqlConnection(ConexoesPostgres.Montar(
                settings.Postgres, settings.Postgres.Usuario, settings.Postgres.Senha, somenteLeitura: false));
            await conexao.OpenAsync();
            await using var comando = new NpgsqlCommand($"insert into {Esquemas.PostgresNet}.log_erros {Colunas} values ('SEM-PAI', 'x', 'GET', '/', 400, 'x', 'x', 'x')", conexao);
            var erro = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, erro.SqlState);
        }
    }
}
