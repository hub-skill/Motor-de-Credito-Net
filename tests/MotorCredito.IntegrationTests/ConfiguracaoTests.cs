using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Xunit;

namespace MotorCredito.IntegrationTests;

/// <summary>Sobe a API com configurações diferentes da padrão dos testes (Oracle, rate limit baixo, docs ligadas).</summary>
public sealed class ApiFactoryConfiguravel(IReadOnlyDictionary<string, string> configuracao, bool repositorioFalso = true)
    : WebApplicationFactory<Program>
{
    static ApiFactoryConfiguravel() => Environment.SetEnvironmentVariable(ArquivoEnv.VariavelDeControle, "off");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production"); // como na produção (ver ApiFactory)
        builder.UseSetting("Api:ApiKey", ApiFactory.ChaveDeTeste);
        foreach (var (chave, valor) in configuracao)
        {
            builder.UseSetting(chave, valor);
        }

        builder.ConfigureTestServices(servicos =>
        {
            // O log de erros nunca vai para um banco real nos testes.
            servicos.RemoveAll<IRepositorioDeErros>();
            servicos.AddSingleton<IRepositorioDeErros>(new DestinoDeErrosFalso());
            if (repositorioFalso)
            {
                servicos.RemoveAll<IRepositorioListas>();
                servicos.AddSingleton<IRepositorioListas>(new RepositorioFalso());
            }
        });
    }
}

public sealed class ConfiguracaoTests
{
    private const string Url = "/api/v1/triagem";

    [Fact]
    public void Provedor_oracle_usa_o_repositorio_oracle()
    {
        // Só monta o repositório (não conecta): prova que a configuração escolhe o banco certo.
        using var fabrica = new ApiFactoryConfiguravel(new Dictionary<string, string> { ["Bancos:Provedor"] = "oracle" }, repositorioFalso: false);
        Assert.IsType<RepositorioOracle>(fabrica.Services.GetRequiredService<IRepositorioListas>());
    }

    [Fact]
    public void Provedor_padrao_usa_o_repositorio_postgres()
    {
        using var fabrica = new ApiFactoryConfiguravel(new Dictionary<string, string>(), repositorioFalso: false);
        Assert.IsType<RepositorioPostgres>(fabrica.Services.GetRequiredService<IRepositorioListas>());
    }

    [Fact]
    public async Task Rate_limit_responde_429_com_retry_after()
    {
        using var fabrica = new ApiFactoryConfiguravel(new Dictionary<string, string> { ["Api:RateLimitPorMinuto"] = "2" });
        var cliente = fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ChaveDeTeste);

        for (var i = 0; i < 2; i++)
        {
            var permitida = await cliente.PostAsJsonAsync(Url, new { documento = "52998224725" });
            Assert.Equal(HttpStatusCode.OK, permitida.StatusCode);
        }

        var bloqueada = await cliente.PostAsJsonAsync(Url, new { documento = "52998224725" });
        await ContratoDeErro.AfirmarProblema(bloqueada, HttpStatusCode.TooManyRequests, "limite_excedido");
        Assert.Equal("60", bloqueada.Headers.RetryAfter?.ToString());

        // Com o limite estourado, health e métricas continuam respondendo (probes do Kubernetes).
        foreach (var rota in new[] { "/health/live", "/health/ready", "/metrics" })
        {
            var resposta = await cliente.GetAsync(new Uri(rota, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        }
    }

    [Fact]
    public async Task Documentacao_so_existe_quando_ligada()
    {
        using var ligada = new ApiFactoryConfiguravel(new Dictionary<string, string> { ["Api:EnableDocs"] = "true" });
        var openApi = await ligada.CreateClient().GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var scalar = await ligada.CreateClient().GetAsync(new Uri("/scalar", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        Assert.Contains("/api/v1/triagem", await openApi.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, scalar.StatusCode);

        // Desligada: a rota não existe. Sem chave, a API responde 401 até para rotas inexistentes
        // (não revela quais rotas existem); com chave, responde 404.
        using var desligada = new ApiFactoryConfiguravel(new Dictionary<string, string>());
        var semChave = await desligada.CreateClient().GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, semChave.StatusCode);

        var autenticado = desligada.CreateClient();
        autenticado.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ChaveDeTeste);
        var comChave = await autenticado.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, comChave.StatusCode);
    }
}
