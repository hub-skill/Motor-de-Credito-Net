using MotorCredito.Core.Cgu;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Npgsql;
using Xunit;

namespace MotorCredito.BancosTests;

/// <summary>Falhas contra os servidores reais e campos que a API não expõe, conferidos com valores reais.</summary>
[Trait("Categoria", "Bancos")]
public sealed class FalhasERegistrosTests
{
    static FalhasERegistrosTests() => ArquivoEnv.CarregarSeExistir();

    [Fact]
    public async Task Postgres_com_senha_errada_vira_banco_indisponivel()
    {
        var settings = BancosSettings.DoAmbiente().Postgres;
        var errada = new PostgresSettings { Host = settings.Host, Porta = settings.Porta, Banco = settings.Banco, Usuario = settings.Usuario, Senha = "senha-errada-de-teste" };
        await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(errada);

        // Servidor responde 28P01 (autenticação): deve virar 503, não 500.
        await Assert.ThrowsAsync<BancoIndisponivelException>(() =>
            new RepositorioPostgres(fonte).BuscarSancoesAsync("00000000000191", CancellationToken.None));
    }

    [Fact]
    public async Task Oracle_com_senha_errada_vira_banco_indisponivel()
    {
        var settings = BancosSettings.DoAmbiente().Oracle;
        var errada = new OracleSettings { DataSource = settings.DataSource, Usuario = settings.Usuario, Senha = "senha-errada-de-teste" };

        // ORA-01017 (usuário/senha inválidos): deve virar 503, não 500.
        await Assert.ThrowsAsync<BancoIndisponivelException>(() =>
            new RepositorioOracle(errada).VerificarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Carga_com_valor_invalido_e_desfeita_e_mantem_os_dados()
    {
        var bancos = BancosSettings.DoAmbiente();
        var conexao = ConexoesPostgres.Montar(bancos.Postgres, bancos.Postgres.Usuario, bancos.Postgres.Senha, somenteLeitura: false);
        var antes = await ContarPepsAsync(conexao);
        Assert.True(antes > 0, "a tabela credito_net.peps precisa estar carregada");

        // Um tipo que a carga não sabe gravar: a transação inteira (inclusive o delete) deve ser desfeita.
        var linhaInvalida = new object?[LayoutsCgu.Peps.Colunas.Count];
        linhaInvalida[0] = Guid.NewGuid();
        var carga = new CargaTabela(LayoutsCgu.Peps, null, [linhaInvalida]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CarregadorPostgres.CarregarAsync(conexao, [carga], CancellationToken.None));
        Assert.Equal(antes, await ContarPepsAsync(conexao));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("oracle")]
    public async Task Campos_da_sancao_e_do_pep_vem_completos(string banco)
    {
        var settings = BancosSettings.DoAmbiente();
        IRepositorioListas repositorio = banco == "oracle"
            ? new RepositorioOracle(settings.Oracle)
            : new RepositorioPostgres(ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres));

        var sancao = Assert.Single(await repositorio.BuscarSancoesAsync("19182685000162", CancellationToken.None));
        Assert.Equal(79854L, sancao.CodigoSancao);
        Assert.Equal("104/2014", sancao.NumeroProcesso);
        Assert.Equal("SC", sancao.UfOrgao);

        var miolo = await MioloDeUmPrefeitoEmExercicioAsync(settings);
        var pep = (await repositorio.BuscarPepsPorMioloAsync(miolo, CancellationToken.None)).First(p => p.DescricaoFuncao == "PREFEITO");
        Assert.False(string.IsNullOrEmpty(pep.NomeOrgao));
        Assert.NotNull(pep.DataInicioExercicio);
        Assert.True(pep.DataInicioExercicio <= pep.DataFimExercicio);
    }

    private static async Task<long> ContarPepsAsync(string conexao)
    {
        await using var con = new NpgsqlConnection(conexao);
        await con.OpenAsync();
        await using var comando = new NpgsqlCommand("select count(*) from credito_net.peps", con);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<string> MioloDeUmPrefeitoEmExercicioAsync(BancosSettings settings)
    {
        await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings.Postgres);
        await using var comando = fonte.CreateCommand(
            "select cpf_miolo from credito_net.peps where descricao_funcao = 'PREFEITO'"
            + " and data_fim_exercicio >= current_date order by cpf_miolo limit 1");
        return (string)(await comando.ExecuteScalarAsync())!;
    }
}
