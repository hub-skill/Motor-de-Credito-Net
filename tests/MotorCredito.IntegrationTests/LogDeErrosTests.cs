using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MotorCredito.Core.Listas;
using Xunit;

namespace MotorCredito.IntegrationTests;

/// <summary>Toda resposta de erro (4xx e 5xx) vai para o log de erros, só com metadados (igual ao Motor Python).</summary>
public sealed class LogDeErrosTests : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/triagem";
    private const string CpfValido = "52998224725";
    private readonly ApiFactory _fabrica;

    public LogDeErrosTests(ApiFactory fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Repositorio.Limpar();
        _fabrica.Erros.Limpar();
        _fabrica.Movimentacao.Limpar();
    }

    private HttpClient ClienteAutenticado()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ChaveDeTeste);
        return cliente;
    }

    private static async Task<string> RequestId(HttpResponseMessage resposta)
    {
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return corpo.RootElement.GetProperty("request_id").GetString()!;
    }

    [Fact]
    public async Task Entrada_invalida_registra_metadados_sem_dados_pessoais()
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-24", nome = "Fulano de Tal" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);

        var registro = await _fabrica.Erros.EsperarAsync(await RequestId(resposta));

        Assert.Equal(("POST", Url, 422, "entrada_invalida"), (registro.Metodo, registro.Rota, registro.Status, registro.Codigo));
        Assert.False(string.IsNullOrEmpty(registro.Titulo));
        Assert.Null(registro.TipoExcecao);
        Assert.True(registro.DuracaoMs >= 0);
        Assert.Equal("Production", registro.Ambiente);
        Assert.DoesNotContain("982", registro.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Fulano", registro.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cada_erro_e_gravado_junto_com_a_sua_movimentacao()
    {
        var primeira = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-24" });
        var segunda = await ClienteAutenticado().GetAsync(new Uri("/nao-existe", UriKind.Relative));

        foreach (var resposta in new[] { primeira, segunda })
        {
            var movimentacao = await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(resposta));
            var erro = Assert.IsType<RegistroDeErro>(movimentacao.Erro);
            Assert.Equal(await RequestId(resposta), erro.RequestId);
            Assert.Equal(movimentacao.StatusHttp, erro.Status);
        }
    }

    [Fact]
    public async Task Sucesso_e_health_nao_registram()
    {
        var cliente = ClienteAutenticado();
        Assert.Equal(HttpStatusCode.OK, (await cliente.PostAsJsonAsync(Url, new { documento = CpfValido })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);

        // Um erro de marcação: quando ele chega, os pedidos anteriores já teriam chegado também.
        var marca = await cliente.GetAsync(new Uri("/nao-existe", UriKind.Relative));
        await _fabrica.Erros.EsperarAsync(await RequestId(marca));

        Assert.Equal([404], _fabrica.Erros.Registros.Select(r => r.Status));
    }

    [Fact]
    public async Task Erros_do_framework_e_do_middleware_registram()
    {
        var semChave = await _fabrica.CreateClient().PostAsJsonAsync(Url, new { documento = CpfValido });
        var cliente = ClienteAutenticado();
        var rotaInexistente = await cliente.GetAsync(new Uri("/nao-existe", UriKind.Relative));
        var metodoErrado = await cliente.GetAsync(new Uri(Url, UriKind.Relative));
        using var jsonRuim = new StringContent("{nao e json", Encoding.UTF8, "application/json");
        var jsonInvalido = await cliente.PostAsync(new Uri(Url, UriKind.Relative), jsonRuim);
        using var grande = new StringContent("{" + new string(' ', 5000) + "}", Encoding.UTF8, "application/json");
        var corpoGrande = await cliente.PostAsync(new Uri(Url, UriKind.Relative), grande);

        var esperados = new (HttpResponseMessage Resposta, int Status, string Codigo, string Rota)[]
        {
            (rotaInexistente, 404, "http_404", "/nao-existe"),
            (metodoErrado, 405, "http_405", Url),
            (jsonInvalido, 400, "requisicao_invalida", Url),
            (corpoGrande, 413, "corpo_muito_grande", Url),
        };
        foreach (var (resposta, status, codigo, rota) in esperados)
        {
            var registro = await _fabrica.Erros.EsperarAsync(await RequestId(resposta));
            Assert.Equal((status, codigo, rota), (registro.Status, registro.Codigo, registro.Rota));
        }

        // O 401 sem chave não entra (sem chave válida não há movimentação, e a log_erros exige a chave dela).
        Assert.DoesNotContain(_fabrica.Erros.Registros, r => r.Status == 401);
        Assert.False(semChave.Headers.Contains("X-Requisicao"));
    }

    [Fact]
    public async Task Erro_interno_registra_o_tipo_da_excecao()
    {
        _fabrica.Repositorio.Lancar = new InvalidOperationException("detalhe interno: senha=abc");

        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);

        var registro = await _fabrica.Erros.EsperarAsync(await RequestId(resposta));
        Assert.Equal(("erro_interno", "System.InvalidOperationException"), (registro.Codigo, registro.TipoExcecao));
        Assert.DoesNotContain("senha", registro.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Banco_fora_registra_503()
    {
        _fabrica.Repositorio.Falhar = true;

        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        var registro = await _fabrica.Erros.EsperarAsync(await RequestId(resposta));
        Assert.Equal((503, "banco_indisponivel"), (registro.Status, registro.Codigo));
    }

    [Fact]
    public async Task Rota_muito_longa_e_truncada()
    {
        var resposta = await ClienteAutenticado().GetAsync(new Uri("/" + new string('a', 500), UriKind.Relative));

        var registro = await _fabrica.Erros.EsperarAsync(await RequestId(resposta));
        Assert.Equal(404, registro.Status);
        Assert.Equal(200, registro.Rota.Length);
    }
}
