using MotorCredito.Core.Listas;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Xunit;

namespace MotorCredito.UnitTests;

/// <summary>
/// Drivers reais (Npgsql e ODP.NET) apontando para uma porta sem servidor: o repositório deve traduzir a falha
/// em BancoIndisponivelException (vira 503 na API), e não deixar escapar a exceção do driver (que viraria 500).
/// Não precisa de banco no ar: roda também no CI.
/// </summary>
public sealed class FalhasDeBancoTests
{
    private const int PortaSemServidor = 1;

    [Fact]
    public async Task Postgres_fora_do_ar_vira_banco_indisponivel()
    {
        var settings = new PostgresSettings { Host = "127.0.0.1", Porta = PortaSemServidor, Senha = "x" };
        await using var fonte = ConexoesPostgres.CriarFonteDeLeitura(settings);
        var repositorio = new RepositorioPostgres(fonte);

        await Assert.ThrowsAsync<BancoIndisponivelException>(() => repositorio.BuscarSancoesAsync("00000000000191", CancellationToken.None));
        await Assert.ThrowsAsync<BancoIndisponivelException>(() => repositorio.VerificarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Oracle_fora_do_ar_vira_banco_indisponivel()
    {
        var settings = new OracleSettings { DataSource = $"127.0.0.1:{PortaSemServidor}/XEPDB1", Senha = "x" };
        var repositorio = new RepositorioOracle(settings);

        await Assert.ThrowsAsync<BancoIndisponivelException>(() => repositorio.BuscarPepsPorMioloAsync("123456", CancellationToken.None));
        await Assert.ThrowsAsync<BancoIndisponivelException>(() => repositorio.VerificarAsync(CancellationToken.None));
    }
}
