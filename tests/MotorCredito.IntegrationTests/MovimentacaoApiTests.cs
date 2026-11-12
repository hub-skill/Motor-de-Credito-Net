using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MotorCredito.Api.Services;
using MotorCredito.Core.Listas;
using Xunit;

namespace MotorCredito.IntegrationTests;

/// <summary>
/// Movimentação: cada requisição (menos /health e /metrics) é numerada na chegada e completada com a resposta.
/// Sem banco: a movimentação falsa numera e guarda a chegada e a resposta (mesmos casos do Motor Python).
/// </summary>
public sealed class MovimentacaoApiTests : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/triagem";
    private const string CpfValido = "52998224725";
    private readonly ApiFactory _fabrica;

    public MovimentacaoApiTests(ApiFactory fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Repositorio.Limpar();
        _fabrica.Movimentacao.Limpar();
        _fabrica.Erros.Limpar();
        InicioDoTeste = _fabrica.Movimentacao.TotalDeChegadas;
    }

    private int InicioDoTeste { get; }

    private HttpClient ClienteAutenticado()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ChaveDeTeste);
        return cliente;
    }

    private async Task<HttpResponseMessage> EnviarAsync(string corpo, bool comChave = true)
    {
        var cliente = comChave ? ClienteAutenticado() : _fabrica.CreateClient();
        using var conteudo = new StringContent(corpo, Encoding.UTF8, "application/json");
        return await cliente.PostAsync(new Uri(Url, UriKind.Relative), conteudo);
    }

    [Fact]
    public async Task Requisicao_com_sucesso_e_numerada_e_a_resposta_e_gravada()
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-25", nome = "Fulano de Tal" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var numero = MovimentacaoFalsa.NumeroDoHeader(resposta);
        var chegada = _fabrica.Movimentacao.ChegadaDe(numero)!;
        Assert.Equal(("POST", Url), (chegada.Metodo, chegada.Rota));
        Assert.Contains("Fulano de Tal", chegada.Conteudo, StringComparison.Ordinal);

        var gravada = await _fabrica.Movimentacao.EsperarRespostaAsync(numero);
        Assert.Equal((MovimentacaoFalsa.Ano, numero, 200), (gravada.Ano, gravada.Requisicao, gravada.StatusHttp));
        Assert.Equal(("OK", "OK"), (gravada.SituacaoRequisicao, gravada.SituacaoResposta));
        Assert.Equal(await resposta.Content.ReadAsStringAsync(), gravada.Conteudo); // exatamente o que o cliente recebeu
    }

    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData(CpfValido)]
    public async Task Cpf_e_mascarado_no_conteudo_da_requisicao_e_da_resposta(string documento)
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento });
        var numero = MovimentacaoFalsa.NumeroDoHeader(resposta);

        var chegada = _fabrica.Movimentacao.ChegadaDe(numero)!;
        Assert.Contains("***.982.247-**", chegada.Conteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("529", chegada.Conteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("-25", chegada.Conteudo, StringComparison.Ordinal);
        var gravada = await _fabrica.Movimentacao.EsperarRespostaAsync(numero);
        Assert.DoesNotContain("529.982.247-25", gravada.Conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conteudo_da_requisicao_aceita_qualquer_formato()
    {
        var corpos = new (string Entrada, string? Esperado)[]
        {
            ("{nao e json", "{nao e json"),
            ("[\"lista\"]", "[\"lista\"]"),
            ("<xml><a>1</a></xml>", "<xml><a>1</a></xml>"),
        };
        foreach (var (entrada, esperado) in corpos)
        {
            var numero = MovimentacaoFalsa.NumeroDoHeader(await EnviarAsync(entrada));
            Assert.Equal(esperado, _fabrica.Movimentacao.ChegadaDe(numero)!.Conteudo);
        }

        // Byte inválido em UTF-8 não derruba a gravação: vira U+FFFD.
        using var invalido = new ByteArrayContent([.. "texto solto "u8, 0xFF, .. " fim"u8]);
        invalido.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var resposta = await ClienteAutenticado().PostAsync(new Uri(Url, UriKind.Relative), invalido);
        Assert.Equal("texto solto � fim", _fabrica.Movimentacao.ChegadaDe(MovimentacaoFalsa.NumeroDoHeader(resposta))!.Conteudo);
    }

    [Theory]
    [InlineData("entrada_invalida", 422, "ER", "ER")]
    [InlineData("rota_inexistente", 404, "ER", "ER")]
    [InlineData("json_malformado", 400, "ER", "ER")]
    [InlineData("banco_fora", 503, "OK", "ER")]
    [InlineData("erro_inesperado", 500, "OK", "ER")]
    [InlineData("sucesso", 200, "OK", "OK")]
    public async Task Situacoes_da_requisicao_e_da_resposta(string chamada, int status, string situacaoRequisicao, string situacaoResposta)
    {
        var bom = new { documento = CpfValido };
        HttpResponseMessage resposta;
        switch (chamada)
        {
            case "entrada_invalida":
                resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-24" });
                break;
            case "rota_inexistente":
                resposta = await ClienteAutenticado().GetAsync(new Uri("/nao-existe", UriKind.Relative));
                break;
            case "json_malformado":
                resposta = await EnviarAsync("{nao e json");
                break;
            case "banco_fora":
                _fabrica.Repositorio.Falhar = true;
                resposta = await ClienteAutenticado().PostAsJsonAsync(Url, bom);
                break;
            case "erro_inesperado":
                _fabrica.Repositorio.Lancar = new InvalidOperationException("falha");
                resposta = await ClienteAutenticado().PostAsJsonAsync(Url, bom);
                break;
            default:
                resposta = await ClienteAutenticado().PostAsJsonAsync(Url, bom);
                break;
        }

        Assert.Equal(status, (int)resposta.StatusCode);
        var gravada = await _fabrica.Movimentacao.EsperarRespostaAsync(MovimentacaoFalsa.NumeroDoHeader(resposta));
        Assert.Equal((status, situacaoRequisicao, situacaoResposta), (gravada.StatusHttp, gravada.SituacaoRequisicao, gravada.SituacaoResposta));
    }

    [Theory]
    [InlineData("POST", Url, null)] // sem chave
    [InlineData("POST", Url, "chave-errada")]
    [InlineData("GET", "/nao-existe", null)] // sem chave: 401, não revela se a rota existe
    [InlineData("GET", Url, null)]
    public async Task Sem_chave_valida_nao_entra_na_movimentacao(string metodo, string rota, string? chave)
    {
        // Varreduras e chamadas sem chave recebem 401 e não gravam nada: nem movimentação, nem log de erros.
        var cliente = _fabrica.CreateClient();
        if (chave is not null)
        {
            cliente.DefaultRequestHeaders.Add("X-API-Key", chave);
        }

        using var pedido = new HttpRequestMessage(new HttpMethod(metodo), new Uri(rota, UriKind.Relative))
        {
            Content = JsonContent.Create(new { documento = CpfValido }),
        };
        var resposta = await cliente.SendAsync(pedido);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("X-Requisicao"));
        Assert.True(resposta.Headers.Contains("X-Request-ID")); // o 401 continua com request_id e headers de segurança

        // Uma chamada de marcação, já com chave: quando ela é gravada, qualquer gravação anterior já teria chegado.
        var marca = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        await _fabrica.Movimentacao.EsperarRespostaAsync(MovimentacaoFalsa.NumeroDoHeader(marca));
        Assert.Equal(1, _fabrica.Movimentacao.TotalDeChegadas - InicioDoTeste);
        Assert.Empty(_fabrica.Erros.Registros);
    }

    [Fact]
    public async Task Erro_nao_tratado_tambem_entra_na_movimentacao_com_os_cabecalhos_da_api()
    {
        _fabrica.Repositorio.Lancar = new InvalidOperationException("detalhe interno: senha=abc");
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.True(resposta.Headers.Contains("X-Requisicao") && resposta.Headers.Contains("X-Request-ID"));
        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        var gravada = await _fabrica.Movimentacao.EsperarRespostaAsync(MovimentacaoFalsa.NumeroDoHeader(resposta));
        Assert.Equal(500, gravada.StatusHttp);
        Assert.DoesNotContain("senha", gravada.Conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Limite_de_requisicoes_estourado_tambem_entra_na_movimentacao()
    {
        using var limitada = new ApiFactoryConfiguravel(new Dictionary<string, string> { ["Api:RateLimitPorMinuto"] = "1" });
        var cliente = limitada.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ChaveDeTeste);
        Assert.Equal(HttpStatusCode.OK, (await cliente.PostAsJsonAsync(Url, new { documento = CpfValido })).StatusCode);

        var bloqueada = await cliente.PostAsJsonAsync(Url, new { documento = CpfValido });

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("X-Requisicao"));
    }

    [Fact]
    public async Task Health_e_metrics_nao_entram_na_movimentacao()
    {
        var antes = _fabrica.Movimentacao.TotalDeChegadas;
        var cliente = _fabrica.CreateClient();
        foreach (var rota in new[] { "/health/live", "/health/ready", "/metrics" })
        {
            var resposta = await cliente.GetAsync(new Uri(rota, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.False(resposta.Headers.Contains("X-Requisicao"));
        }

        Assert.Equal(antes, _fabrica.Movimentacao.TotalDeChegadas);
    }

    [Fact]
    public async Task Corpo_acima_do_limite_registra_so_o_tamanho()
    {
        var corpo = "{\"documento\": \"" + new string('1', 5000) + "\"}";
        var resposta = await EnviarAsync(corpo);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resposta.StatusCode);
        var conteudo = _fabrica.Movimentacao.ChegadaDe(MovimentacaoFalsa.NumeroDoHeader(resposta))!.Conteudo;
        Assert.Contains(corpo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), conteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("1111", conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_numero_a_requisicao_segue_normalmente_e_nada_e_gravado()
    {
        _fabrica.Movimentacao.NumeracaoFalha = true;
        var antes = _fabrica.Movimentacao.TotalDeChegadas;

        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("X-Requisicao"));
        Assert.Equal(antes, _fabrica.Movimentacao.TotalDeChegadas);
    }

    [Fact]
    public async Task Banco_lento_na_chegada_nao_segura_a_requisicao_alem_do_prazo()
    {
        _fabrica.Movimentacao.NumeracaoAtraso = TimeSpan.FromSeconds(10);
        var antes = _fabrica.Movimentacao.TotalDeChegadas;

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        cronometro.Stop();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("X-Requisicao"));
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(5), $"demorou {cronometro.Elapsed}");
        Assert.Equal(antes, _fabrica.Movimentacao.TotalDeChegadas); // a gravação lenta foi abandonada
    }
}

/// <summary>Fila de respostas: grava em lotes, em segundo plano, sem derrubar a API.</summary>
public sealed class GravadorDeRespostasTests
{
    private static RegistroDeResposta Resposta(int n) => new(2026, n, 200, "{}", "OK", "OK");

    [Fact]
    public async Task Respostas_sao_gravadas_em_lotes_de_no_maximo_cem()
    {
        var destino = new MovimentacaoFalsa();
        var fila = new FilaDeRespostas(NullLogger<FilaDeRespostas>.Instance);
        for (var n = 1; n <= 250; n++)
        {
            fila.Registrar(Resposta(n));
        }

        using var gravador = new GravadorDeRespostas(fila, destino, NullLogger<GravadorDeRespostas>.Instance);
        await gravador.StartAsync(CancellationToken.None);
        var ultima = await destino.EsperarRespostaAsync(250);
        await gravador.StopAsync(CancellationToken.None);

        Assert.Equal(250, ultima.Requisicao);
    }
}
