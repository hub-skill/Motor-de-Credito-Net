using MotorCredito.Core.Cgu;
using MotorCredito.Core.Configuracao;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace MotorCredito.BancosTests;

/// <summary>
/// Carga de ponta a ponta nos schemas .NET: lê os CSVs, migra (idempotente), grava com a marca DOTNET
/// e compara campo a campo com as tabelas do Python. As tabelas do Python só são lidas.
/// Precisa dos CSVs da carga do Python (variável MOTOR_CSV_CGU ou a pasta padrão do projeto Python).
/// </summary>
[Trait("Categoria", "Bancos")]
public sealed class CargaDePontaAPontaTests
{
    private const string PastaPadrao = @"C:\Felipe\Programacao\Python\APIs\Motor de credito\dados_cgu";

    static CargaDePontaAPontaTests() => ArquivoEnv.CarregarSeExistir();

    private static string PastaDosCsv => Ambiente.Obter("MOTOR_CSV_CGU", PastaPadrao);

    private static List<CargaTabela> LerCargas()
    {
        var cargas = new List<CargaTabela>();
        foreach (var tipo in (string[])["ceis", "cnep", "pep"])
        {
            var arquivo = Directory.EnumerateFiles(PastaDosCsv, $"*_{tipo.ToUpperInvariant()}.csv").Order(StringComparer.Ordinal).Last();
            var layout = tipo == "pep" ? LayoutsCgu.Peps : LayoutsCgu.Sancoes;
            cargas.Add(new CargaTabela(layout, tipo == "pep" ? null : tipo.ToUpperInvariant(), LeitorDeArquivoCgu.Ler(arquivo, layout)));
        }

        return cargas;
    }

    private static string Scripts(string banco) =>
        Path.Combine(ArquivoEnv.RaizDoRepositorio(), "banco", banco, "01_schema_net.sql");

    [Fact]
    public async Task Postgres_migra_carrega_e_fica_identico_ao_python()
    {
        var bancos = BancosSettings.DoAmbiente();
        var admin = AdminSettings.DoAmbiente();
        var conexaoAdmin = ConexoesPostgres.Montar(bancos.Postgres, admin.PostgresUsuario, admin.PostgresSenha, somenteLeitura: false);

        var passos = await MigradorPostgres.MigrarAsync(conexaoAdmin, bancos.Postgres.Senha, Scripts("postgres"), CancellationToken.None);
        Assert.Contains(passos, p => p.Contains("já existia", StringComparison.Ordinal)); // segunda execução não recria

        var cargas = LerCargas();
        var conexaoApp = ConexoesPostgres.Montar(bancos.Postgres, bancos.Postgres.Usuario, bancos.Postgres.Senha, somenteLeitura: false);
        var resultados = await CarregadorPostgres.CarregarAsync(conexaoApp, cargas, CancellationToken.None);
        Assert.Equal(cargas.Select(c => c.Linhas.Count), resultados.Select(r => r.Linhas));

        await using var con = new NpgsqlConnection(conexaoAdmin);
        await con.OpenAsync();
        foreach (var layout in (LayoutTabela[])[LayoutsCgu.Sancoes, LayoutsCgu.Peps])
        {
            var r = await ComparadorDeCargas.CompararAsync(
                con, "postgres", $"credito.{layout.Tabela}", $"credito_net.{layout.Tabela}", layout, null, CancellationToken.None);
            Assert.True(r.Igual, $"{layout.Tabela}: só no antigo {r.SoNoAntigo}, só no novo {r.SoNoNovo}");
            Assert.Equal(r.Novos, r.NovosMarcados);
        }
    }

    [Fact]
    public async Task Oracle_migra_carrega_e_fica_identico_ao_python()
    {
        var bancos = BancosSettings.DoAmbiente();
        var admin = AdminSettings.DoAmbiente();
        var conexaoAdmin = ConexoesOracle.Montar(bancos.Oracle, admin.OracleUsuario, admin.OracleSenha);

        var passos = MigradorOracle.Migrar(conexaoAdmin, bancos.Oracle.Senha, Scripts("oracle"));
        Assert.Contains(passos, p => p.Contains("já existia", StringComparison.Ordinal));

        var cargas = LerCargas();
        var resultados = CarregadorOracle.Carregar(ConexoesOracle.Montar(bancos.Oracle, bancos.Oracle.Usuario, bancos.Oracle.Senha), cargas);
        Assert.Equal(cargas.Select(c => c.Linhas.Count), resultados.Select(r => r.Linhas));

        using var con = new OracleConnection(conexaoAdmin);
        await con.OpenAsync();
        foreach (var layout in (LayoutTabela[])[LayoutsCgu.Sancoes, LayoutsCgu.Peps])
        {
            var r = await ComparadorDeCargas.CompararAsync(
                con, "oracle", $"MOTOR_CREDITO.{layout.Tabela}", $"MOTOR_CREDITO_NET.{layout.Tabela}", layout,
                c => ((OracleCommand)c).InitialLOBFetchSize = -1, CancellationToken.None);
            Assert.True(r.Igual, $"{layout.Tabela}: só no antigo {r.SoNoAntigo}, só no novo {r.SoNoNovo}");
            Assert.Equal(r.Novos, r.NovosMarcados);
        }
    }
}
