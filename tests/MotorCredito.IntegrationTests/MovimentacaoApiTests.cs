using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MotorCredito.Api.Services;
using MotorCredito.Core.Listas;
using Xunit;

namespace MotorCredito.IntegrationTests;

/// <summary>
/// Movimentação: cada requisição com chave de API válida (menos /health e /metrics) ganha um ULID na chegada e é gravada,
/// com a resposta e o erro (se houver), em segundo plano. Sem banco: a movimentação falsa guarda tudo (mesmos casos do
/// Motor Python).
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
    }

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
    public async Task Requisicao_com_sucesso_ganha_ulid_e_e_gravada_com_a_resposta()
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-25", nome = "Fulano de Tal" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var id = MovimentacaoFalsa.IdDoHeader(resposta);
        var gravada = await _fabrica.Movimentacao.EsperarAsync(id);
        Assert.Equal(("POST", Url, 200), (gravada.Metodo, gravada.Rota, gravada.StatusHttp));
        Assert.Contains("Fulano de Tal", gravada.RequisicaoConteudo, StringComparison.Ordinal);
        Assert.Equal(("OK", "OK"), (gravada.SituacaoRequisicao, gravada.SituacaoResposta));
        Assert.Equal(await resposta.Content.ReadAsStringAsync(), gravada.RespostaConteudo); // exatamente o que o cliente recebeu
        Assert.Null(gravada.Erro);
        Assert.True(gravada.RespostaEm >= gravada.RequisicaoEm);
        Assert.InRange((gravada.RequisicaoEm - Ulid.Momento(id)).TotalMilliseconds, -2, 2); // o ULID carrega a hora de chegada
    }

    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData(CpfValido)]
    public async Task Cpf_e_mascarado_no_conteudo_da_requisicao_e_da_resposta(string documento)
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento });

        var gravada = await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(resposta));
        Assert.Contains("***.982.247-**", gravada.RequisicaoConteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("529", gravada.RequisicaoConteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("-25", gravada.RequisicaoConteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("529.982.247-25", gravada.RespostaConteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conteudo_da_requisicao_aceita_qualquer_formato()
    {
        foreach (var corpo in new[] { "{nao e json", "[\"lista\"]", "<xml><a>1</a></xml>" })
        {
            var gravada = await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(await EnviarAsync(corpo)));
            Assert.Equal(corpo, gravada.RequisicaoConteudo);
        }

        // Byte inválido em UTF-8 não derruba a gravação: vira U+FFFD.
        using var invalido = new ByteArrayContent([.. "texto solto "u8, 0xFF, .. " fim"u8]);
        invalido.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var resposta = await ClienteAutenticado().PostAsync(new Uri(Url, UriKind.Relative), invalido);
        var ultima = await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(resposta));
        Assert.Equal("texto solto � fim", ultima.RequisicaoConteudo);
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
        var gravada = await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(resposta));
        Assert.Equal((status, situacaoRequisicao, situacaoResposta), (gravada.StatusHttp, gravada.SituacaoRequisicao, gravada.SituacaoResposta));
    }

    [Theory]
    [InlineData("POST", Url, null)] // sem chave
    [InlineData("POST", Url, "chave-errada")]
    [InlineData("GET", "/nao-existe", null)] // sem chave: 401, não revela se a rota existe
    [InlineData("GET", Url, null)]
    public async Task Sem_chave_valida_nao_entra_na_movimentacao(string metodo, string rota, string? chave)
    {
        // Varreduras e chamadas sem chave recebem 401 e não gravam nada.
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
        await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(marca));
        Assert.DoesNotContain(_fabrica.Movimentacao.Registros, r => r.StatusHttp == 401);
    }

    [Fact]
    public async Task Erro_nao_tratado_tambem_entra_na_movimentacao_com_os_cabecalhos_da_api_e_o_erro_junto()
    {
        _fabrica.Repositorio.Lancar = new InvalidOperationException("detalhe interno: senha=abc");
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.True(resposta.Headers.Contains("X-Requisicao") && resposta.Headers.Contains("X-Request-ID"));
        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        var gravada = await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(resposta));
        Assert.Equal(500, gravada.StatusHttp);
        Assert.DoesNotContain("senha", gravada.RespostaConteudo, StringComparison.Ordinal);
        Assert.Equal(("erro_interno", "System.InvalidOperationException"), (gravada.Erro?.Codigo, gravada.Erro?.TipoExcecao));
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
        var gravada = await limitada.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(bloqueada));
        Assert.Equal(("ER", "ER"), (gravada.SituacaoRequisicao, gravada.SituacaoResposta));
        Assert.Equal("limite_excedido", gravada.Erro?.Codigo);
    }

    [Fact]
    public async Task Health_e_metrics_nao_entram_na_movimentacao()
    {
        var antes = _fabrica.Movimentacao.Registros.Count;
        var cliente = _fabrica.CreateClient();
        foreach (var rota in new[] { "/health/live", "/health/ready", "/metrics" })
        {
            var resposta = await cliente.GetAsync(new Uri(rota, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.False(resposta.Headers.Contains("X-Requisicao"));
        }

        Assert.Equal(antes, _fabrica.Movimentacao.Registros.Count);
    }

    [Fact]
    public async Task Corpo_acima_do_limite_registra_so_o_tamanho()
    {
        var corpo = "{\"documento\": \"" + new string('1', 5000) + "\"}";
        var resposta = await EnviarAsync(corpo);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resposta.StatusCode);
        var conteudo = (await _fabrica.Movimentacao.EsperarAsync(MovimentacaoFalsa.IdDoHeader(resposta))).RequisicaoConteudo;
        Assert.Contains(corpo.Length.ToString(CultureInfo.InvariantCulture), conteudo, StringComparison.Ordinal);
        Assert.DoesNotContain("1111", conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Banco_fora_ou_lento_na_gravacao_nao_afeta_a_resposta()
    {
        // Banco lento: a gravação fica presa num portão (sem cronômetro: o teste não depende da velocidade da máquina).
        _fabrica.Movimentacao.Portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode); // a resposta saiu com a gravação ainda presa
        var id = MovimentacaoFalsa.IdDoHeader(resposta); // o número nasce na API: sempre existe
        Assert.DoesNotContain(_fabrica.Movimentacao.Registros, r => r.RequisicaoId == id);
        _fabrica.Movimentacao.Portao.SetResult();
        Assert.Equal(id, (await _fabrica.Movimentacao.EsperarAsync(id)).RequisicaoId); // solto o portão, a gravação acontece

        // Banco fora: a gravação falha, e a resposta continua saindo normalmente.
        _fabrica.Movimentacao.Falha = true;
        var semBanco = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        Assert.Equal(HttpStatusCode.OK, semBanco.StatusCode);
        Assert.True(Ulid.Valido(semBanco.Headers.GetValues("X-Requisicao").Single()));
    }

    [Fact]
    public async Task Cada_requisicao_tem_um_ulid_proprio()
    {
        var ids = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            ids.Add(MovimentacaoFalsa.IdDoHeader(await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido })));
        }

        Assert.Equal(30, ids.Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>Fila e gravação em segundo plano: lotes, descarte com a fila cheia e falha do banco sem derrubar nada.</summary>
public sealed class GravadorDeMovimentacaoTests
{
    private static RegistroDeMovimentacao Registro(int n) => new(
        Ulid.Novo(), "POST", $"/r{n}", null, DateTimeOffset.UtcNow, 200, "{}", DateTimeOffset.UtcNow, "OK", "OK", null);

    private static async Task EsperarAsync(Func<bool> condicao)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condicao())
        {
            await Task.Delay(10, prazo.Token);
        }
    }

    [Fact]
    public async Task Grava_em_lotes_de_no_maximo_cem()
    {
        var destino = new DestinoDeLotesFalso();
        var fila = new FilaDeMovimentacao(NullLogger<FilaDeMovimentacao>.Instance);
        for (var n = 0; n < 250; n++)
        {
            fila.Registrar(Registro(n));
        }

        using var gravador = new GravadorDeMovimentacao(fila, destino, NullLogger<GravadorDeMovimentacao>.Instance);
        await gravador.StartAsync(CancellationToken.None);
        await EsperarAsync(() => destino.Registros.Count == 250);
        await gravador.StopAsync(CancellationToken.None);

        Assert.Equal([100, 100, 50], destino.TamanhosDosLotes);
        Assert.Equal("/r0", destino.Registros[0].Rota);
    }

    [Fact]
    public void Fila_cheia_descarta_sem_bloquear()
    {
        var fila = new FilaDeMovimentacao(NullLogger<FilaDeMovimentacao>.Instance, capacidade: 2);

        for (var n = 0; n < 3; n++)
        {
            fila.Registrar(Registro(n));
        }

        Assert.Equal(1, fila.Descartados);
    }

    [Fact]
    public async Task Falha_do_banco_perde_o_lote_e_continua()
    {
        var destino = new DestinoDeLotesFalso { FalharVezes = 1 };
        var fila = new FilaDeMovimentacao(NullLogger<FilaDeMovimentacao>.Instance);
        using var gravador = new GravadorDeMovimentacao(fila, destino, NullLogger<GravadorDeMovimentacao>.Instance);
        await gravador.StartAsync(CancellationToken.None);

        fila.Registrar(Registro(1));
        await EsperarAsync(() => gravador.Perdidos == 1);
        fila.Registrar(Registro(2));
        await EsperarAsync(() => destino.Registros.Count == 1);
        await gravador.StopAsync(CancellationToken.None);

        Assert.Equal("/r2", Assert.Single(destino.Registros).Rota);
    }

    [Fact]
    public async Task Parar_grava_o_que_ficou_na_fila()
    {
        var destino = new DestinoDeLotesFalso();
        var fila = new FilaDeMovimentacao(NullLogger<FilaDeMovimentacao>.Instance);
        using var gravador = new GravadorDeMovimentacao(fila, destino, NullLogger<GravadorDeMovimentacao>.Instance);
        for (var n = 0; n < 3; n++)
        {
            fila.Registrar(Registro(n));
        }

        await gravador.StopAsync(CancellationToken.None); // sem iniciar: tudo ainda na fila

        Assert.Equal(3, destino.Registros.Count);
    }
}
