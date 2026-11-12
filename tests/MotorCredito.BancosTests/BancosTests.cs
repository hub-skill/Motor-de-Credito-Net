using MotorCredito.Core.Cgu;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace MotorCredito.BancosTests;

/// <summary>
/// Testes contra os bancos reais (schemas .NET já carregados). Só leitura, exceto tabelas temporárias e o log de erros (só insert).
/// Rode com: dotnet test --filter Categoria=Bancos
/// </summary>
[Trait("Categoria", "Bancos")]
public sealed class BancosTests
{
    private const string CnpjSancionado = "19182685000162"; // Declaração de Inidoneidade (CEIS), publicado pela CGU

    static BancosTests() => ArquivoEnv.CarregarSeExistir();

    public static TheoryData<string> Bancos => ["postgres", "oracle"];

    private static IRepositorioListas Repositorio(string banco)
    {
        var settings = BancosSettings.DoAmbiente();
        return banco == "oracle"
            ? new RepositorioOracle(settings.Oracle)
            : new RepositorioPostgres(ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres));
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Banco_responde(string banco) =>
        await Repositorio(banco).VerificarAsync(CancellationToken.None);

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Cnpj_sancionado(string banco)
    {
        var sancao = Assert.Single(await Repositorio(banco).BuscarSancoesAsync(CnpjSancionado, CancellationToken.None));
        Assert.Equal("CEIS", sancao.Cadastro);
        Assert.Equal("Declaração de Inidoneidade sem prazo determinado", sancao.Categoria);
        Assert.Equal(new DateOnly(2016, 6, 28), sancao.DataInicio);
        Assert.Null(sancao.DataFim);
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Documento_sem_sancao(string banco) =>
        Assert.Empty(await Repositorio(banco).BuscarSancoesAsync("00000000000191", CancellationToken.None));

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Origem_mostra_os_arquivos_carregados(string banco)
    {
        var origem = await Repositorio(banco).ObterOrigemAsync(CancellationToken.None);
        Assert.Equal(banco, origem.Banco);
        Assert.Contains(origem.Arquivos, a => a.EndsWith("_PEP.csv", StringComparison.Ordinal));
        Assert.Contains(origem.Arquivos, a => a.EndsWith("_CEIS.csv", StringComparison.Ordinal));
        Assert.NotNull(origem.CarregadoEm);
    }

    [Theory]
    [MemberData(nameof(Bancos))]
    public async Task Peps_por_miolo_devolvem_registros(string banco)
    {
        // Miolo de um registro real, obtido pela própria API de leitura: o primeiro PEP com sigla PREFEI.
        var repo = Repositorio(banco);
        var miolo = await MioloDeUmPrefeitoAsync();
        var peps = await repo.BuscarPepsPorMioloAsync(miolo, CancellationToken.None);
        Assert.NotEmpty(peps);
        Assert.All(peps, p => Assert.False(string.IsNullOrEmpty(p.Nome)));
    }

    [Fact]
    public async Task Usuario_da_api_nao_grava_pela_conexao_de_leitura()
    {
        var settings = BancosSettings.DoAmbiente();
        await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres);
        await using var comando = fonte.CreateCommand("delete from credito_net.peps where 1 = 0");
        var erro = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("25006", erro.SqlState); // read_only_sql_transaction
    }

    [Fact]
    public async Task Comparador_detecta_diferencas_e_marca_ausente()
    {
        // Tabelas temporárias com o mesmo layout: prova que o comparador não responde "igual" sempre.
        // Usa o administrador, como o comando "comparar": o usuário da aplicação não pode criar tabelas temporárias.
        var settings = BancosSettings.DoAmbiente();
        var admin = AdminSettings.DoAmbiente();
        var conexao = ConexoesPostgres.Montar(settings.Postgres, admin.PostgresUsuario, admin.PostgresSenha, somenteLeitura: false);
        await using var con = new NpgsqlConnection(conexao);
        await con.OpenAsync();
        await Executar(con, """
            create temp table antiga as select * from credito_net.peps where false;
            create temp table nova as select * from credito_net.peps where false;
            insert into antiga (id, cpf_mascarado, cpf_miolo, nome, sigla_funcao, data_inicio_exercicio, arquivo_origem, carregado_em, origem_carga)
                values (1, '***.111.111-**', '111111', 'A', 'X', date '2021-01-01', 'f.csv', now(), 'DOTNET'),
                       (2, '***.222.222-**', '222222', 'B', 'X', date '2021-01-01', 'f.csv', now(), 'DOTNET'),
                       (3, '***.333.333-**', '333333', 'C', 'X', date '2021-01-01', 'f.csv', now(), 'DOTNET');
            insert into nova (id, cpf_mascarado, cpf_miolo, nome, sigla_funcao, data_inicio_exercicio, arquivo_origem, carregado_em, origem_carga)
                values (10, '***.111.111-**', '111111', 'A', 'X', date '2021-01-01', 'f.csv', now(), 'DOTNET'),
                       (20, '***.222.222-**', '222222', 'B-ALTERADO', 'X', date '2021-01-01', 'f.csv', now(), 'DOTNET'),
                       (40, '***.444.444-**', '444444', 'D', 'X', date '2021-01-01', 'f.csv', now(), 'DOTNET');
            """);

        var r = await ComparadorDeCargas.CompararAsync(con, "postgres", "antiga", "nova", LayoutsCgu.Peps, null, CancellationToken.None);

        Assert.False(r.Igual);
        Assert.Equal(("postgres", "peps", 3, 3), (r.Banco, r.Tabela, r.Antigos, r.Novos));
        Assert.Equal(["f.csv"], r.ArquivosAntigos);
        Assert.True(r.MesmosArquivos);
        Assert.Equal(1, r.Identicos);
        Assert.Equal(2, r.SoNoAntigo);
        Assert.Equal(2, r.SoNoNovo);
        Assert.Contains(r.Exemplos, e => e.Contains("colunas diferentes: nome", StringComparison.Ordinal));
        Assert.Contains(r.Exemplos, e => e.Contains("existe só na tabela antiga", StringComparison.Ordinal));
        Assert.Contains(r.Exemplos, e => e.Contains("existe só na tabela nova", StringComparison.Ordinal));
    }

    private static async Task<string> MioloDeUmPrefeitoAsync()
    {
        var settings = BancosSettings.DoAmbiente();
        await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres);
        await using var comando = fonte.CreateCommand(
            "select cpf_miolo from credito_net.peps where sigla_funcao = 'PREFEI' order by cpf_miolo limit 1");
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task Executar(NpgsqlConnection con, string sql)
    {
#pragma warning disable CA2100 // SQL fixo do teste
        await using var comando = new NpgsqlCommand(sql, con);
#pragma warning restore CA2100
        await comando.ExecuteNonQueryAsync();
    }
}
