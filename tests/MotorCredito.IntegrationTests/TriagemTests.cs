using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MotorCredito.Core.Listas;
using Xunit;

namespace MotorCredito.IntegrationTests;

public sealed class TriagemTests : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/triagem";
    private const string CpfValido = "52998224725";
    private const string CnpjValido = "00000000000191";
    private readonly ApiFactory _fabrica;

    public TriagemTests(ApiFactory fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Repositorio.Limpar();
    }

    private HttpClient ClienteAutenticado()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ChaveDeTeste);
        return cliente;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resposta)
    {
        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return documento.RootElement.Clone();
    }

    [Fact]
    public async Task Cpf_sem_restricao_libera_e_mascara_o_documento()
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-25", nome = "Fulano" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await Json(resposta);
        Assert.Equal("liberar", corpo.GetProperty("resultado").GetString());
        Assert.Equal("***.982.247-**", corpo.GetProperty("documento").GetString()); // CPF nunca volta completo
        Assert.True(corpo.GetProperty("pep").GetProperty("verificado").GetBoolean());
        Assert.Equal("falso", corpo.GetProperty("fonte").GetProperty("banco").GetString());
        Assert.Equal("2026-11-10", corpo.GetProperty("consultado_em").GetString()); // snake_case, igual ao Python
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Cnpj_com_sancao_vigente_bloqueia_e_nao_consulta_pep()
    {
        _fabrica.Repositorio.Sancoes[CnpjValido] =
            [new Sancao("CEIS", 79854, "Inidoneidade", "Prefeitura X", "SC", new DateOnly(2016, 6, 28), null, "104/2014")];

        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "00.000.000/0001-91" });
        var corpo = await Json(resposta);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("bloquear", corpo.GetProperty("resultado").GetString());
        Assert.True(corpo.GetProperty("sancoes")[0].GetProperty("vigente").GetBoolean());
        Assert.Equal("orgao_sancionador", corpo.GetProperty("sancoes")[0].EnumerateObject().ElementAt(2).Name);
        Assert.Equal("S01", corpo.GetProperty("motivos")[0].GetProperty("codigo").GetString());
        Assert.Empty(_fabrica.Repositorio.ConsultasPep);
    }

    [Fact]
    public async Task Pep_ativo_vai_para_revisao()
    {
        _fabrica.Repositorio.Peps["982247"] =
            [new RegistroPep("MARIA DA SILVA", "PREFEITO", "PREFEITURA", new DateOnly(2025, 1, 1), null, null)];

        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido, nome = "maria da silva" });
        var corpo = await Json(resposta);

        Assert.Equal("revisar", corpo.GetProperty("resultado").GetString());
        Assert.True(corpo.GetProperty("pep").GetProperty("ativo").GetBoolean());
        Assert.Equal("PREFEITO", corpo.GetProperty("pep").GetProperty("funcoes")[0].GetString());
        Assert.Equal(["982247"], _fabrica.Repositorio.ConsultasPep);
    }

    [Fact]
    public async Task Cpf_sem_nome_nao_consulta_pep()
    {
        var corpo = await Json(await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido }));
        Assert.False(corpo.GetProperty("pep").GetProperty("verificado").GetBoolean());
        Assert.Empty(_fabrica.Repositorio.ConsultasPep);
    }

    [Fact]
    public async Task Sem_chave_ou_chave_errada_responde_401()
    {
        var semChave = await _fabrica.CreateClient().PostAsJsonAsync(Url, new { documento = CpfValido });
        await ContratoDeErro.AfirmarProblema(semChave, HttpStatusCode.Unauthorized, "http_401");
        Assert.Equal("APIKey", semChave.Headers.WwwAuthenticate.ToString()); // igual ao Motor Python

        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", "errada");
        var errada = await cliente.PostAsJsonAsync(Url, new { documento = CpfValido });
        await ContratoDeErro.AfirmarProblema(errada, HttpStatusCode.Unauthorized, "http_401");
    }

    [Theory]
    [InlineData("52998224724", null)]                    // dígito errado
    [InlineData("", null)]
    [InlineData("' OR 1=1 --", null)]                    // injeção
    [InlineData(CpfValido, "Maria\u0000Silva")]          // byte nulo no nome
    [InlineData("111111111111111111111", null)]          // documento grande demais
    public async Task Entradas_invalidas_respondem_422_sem_ecoar_a_entrada(string documento, string? nome)
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento, nome });
        var texto = await resposta.Content.ReadAsStringAsync();

        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.UnprocessableEntity, "entrada_invalida");
        Assert.DoesNotContain("52998224724", texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nome_grande_demais_responde_422()
    {
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido, nome = new string('x', 201) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
    }

    [Fact]
    public async Task Json_invalido_nao_responde_500()
    {
        using var conteudo = new StringContent("{nao e json", Encoding.UTF8, "application/json");
        var resposta = await ClienteAutenticado().PostAsync(Url, conteudo);
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.BadRequest, "requisicao_invalida");
    }

    [Fact]
    public async Task Corpo_grande_demais_responde_413()
    {
        using var conteudo = new StringContent($"{{\"documento\": \"{new string('1', 5000)}\"}}", Encoding.UTF8, "application/json");
        var resposta = await ClienteAutenticado().PostAsync(Url, conteudo);
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.RequestEntityTooLarge, "corpo_muito_grande");
    }

    [Fact]
    public async Task Banco_fora_do_ar_responde_503_e_ready_fica_indisponivel()
    {
        _fabrica.Repositorio.Falhar = true;
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.ServiceUnavailable, "banco_indisponivel");

        var pronto = await _fabrica.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pronto.StatusCode);
        Assert.Equal("banco_indisponivel", (await Json(pronto)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Banco_lento_responde_504()
    {
        _fabrica.Repositorio.Atraso = TimeSpan.FromSeconds(3); // RequestTimeoutSeconds = 1 nos testes
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.GatewayTimeout, "timeout");
    }

    [Fact]
    public async Task Banco_expirou_responde_504_banco_timeout()
    {
        _fabrica.Repositorio.Lancar = new BancoExpirouException();
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.GatewayTimeout, "banco_timeout");
    }

    [Fact]
    public async Task Erro_inesperado_responde_500_generico_sem_vazar_detalhes()
    {
        _fabrica.Repositorio.Lancar = new InvalidOperationException("detalhe interno: senha=abc tabela=credito_net.sancoes");
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        var texto = await resposta.Content.ReadAsStringAsync();

        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.InternalServerError, "erro_interno");
        Assert.DoesNotContain("detalhe interno", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_e_headers_de_seguranca()
    {
        var cliente = _fabrica.CreateClient();
        var vivo = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));
        var pronto = await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, vivo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pronto.StatusCode);
        // Mesmo corpo JSON do Motor Python.
        Assert.Equal("ok", (await Json(vivo)).GetProperty("status").GetString());
        var corpoPronto = await Json(pronto);
        Assert.Equal("ok", corpoPronto.GetProperty("status").GetString());
        Assert.Equal("postgres", corpoPronto.GetProperty("banco").GetString());
        Assert.Equal("nosniff", vivo.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", vivo.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Rota_inexistente_responde_404_no_formato_padrao()
    {
        var resposta = await ClienteAutenticado().GetAsync(new Uri("/nao-existe", UriKind.Relative));
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.NotFound, "http_404");
    }

    [Theory]
    [InlineData("/nao-existe")]
    [InlineData(Url)]
    public async Task Sem_chave_rota_inexistente_ou_metodo_errado_responde_401(string rota)
    {
        // Igual ao Motor Python: sem chave, a API não revela quais rotas existem.
        var resposta = await _fabrica.CreateClient().GetAsync(new Uri(rota, UriKind.Relative));
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.Unauthorized, "http_401");
        Assert.Equal("APIKey", resposta.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Metodo_errado_responde_405_no_formato_padrao()
    {
        var resposta = await ClienteAutenticado().GetAsync(new Uri(Url, UriKind.Relative));
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.MethodNotAllowed, "http_405");
        Assert.Contains("POST", resposta.Content.Headers.Allow);
    }

    private async Task<HttpResponseMessage> EnviarJsonAsync(string corpo)
    {
        using var conteudo = new StringContent(corpo, Encoding.UTF8, "application/json");
        return await ClienteAutenticado().PostAsync(new Uri(Url, UriKind.Relative), conteudo);
    }

    /// <summary>Corpo válido com um campo extra aninhado até <paramref name="niveis"/> no total (a raiz conta como 1).</summary>
    private static string Aninhado(int niveis) =>
        $"{{\"documento\": \"{CpfValido}\", \"extra\": " + new string('[', niveis - 1) + new string(']', niveis - 1) + "}";

    [Theory]
    [InlineData("sem_chave", 401)]
    [InlineData("entrada_invalida", 422)]
    [InlineData("rota_inexistente", 404)]
    [InlineData("erro_inesperado", 500)]
    public async Task Respostas_de_erro_levam_os_cabecalhos_de_seguranca_e_o_request_id(string chamada, int status)
    {
        // O tratador de exceções limpa os headers ao montar o erro: eles precisam ser definidos na hora de enviar.
        HttpResponseMessage resposta;
        switch (chamada)
        {
            case "sem_chave":
                resposta = await _fabrica.CreateClient().PostAsJsonAsync(Url, new { documento = CpfValido });
                break;
            case "entrada_invalida":
                resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = "529.982.247-24" });
                break;
            case "rota_inexistente":
                resposta = await ClienteAutenticado().GetAsync(new Uri("/nao-existe", UriKind.Relative));
                break;
            default:
                _fabrica.Repositorio.Lancar = new InvalidOperationException("falha");
                resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
                break;
        }

        Assert.Equal(status, (int)resposta.StatusCode);
        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resposta.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var corpo = await Json(resposta);
        Assert.Equal(corpo.GetProperty("request_id").GetString(), resposta.Headers.GetValues("X-Request-ID").Single());
    }

    [Fact]
    public async Task Json_ate_64_niveis_passa_e_acima_responde_400()
    {
        // Mesmo limite do Motor Python. Chaves dentro de textos não contam.
        Assert.Equal(HttpStatusCode.OK, (await EnviarJsonAsync(Aninhado(64))).StatusCode);
        var textoComChaves = $"{{\"documento\": \"{CpfValido}\", \"nome\": \"{new string('[', 100)}\"}}";
        Assert.Equal(HttpStatusCode.OK, (await EnviarJsonAsync(textoComChaves)).StatusCode);
        await ContratoDeErro.AfirmarProblema(await EnviarJsonAsync(Aninhado(65)), HttpStatusCode.BadRequest, "requisicao_invalida");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Nan_e_infinity_respondem_400_como_json_malformado(string valor)
    {
        // Não existem em JSON (RFC 8259): erro de leitura, como no Motor Python.
        var corpo = $"{{\"documento\": \"{CpfValido}\", \"nome\": {valor}}}";
        await ContratoDeErro.AfirmarProblema(await EnviarJsonAsync(corpo), HttpStatusCode.BadRequest, "requisicao_invalida");
    }

    [Fact]
    public async Task Utf8_invalido_responde_400()
    {
        // 0xFF nunca aparece em UTF-8 válido: corpo ilegível, como no Motor Python.
        using var conteudo = new ByteArrayContent([.. "{\"documento\": \"52998224725\", \"nome\": \"Jos"u8, 0xFF, .. "\"}"u8]);
        conteudo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var resposta = await ClienteAutenticado().PostAsync(new Uri(Url, UriKind.Relative), conteudo);

        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.BadRequest, "requisicao_invalida");
    }

    [Fact]
    public async Task Json_fundo_sem_chave_responde_401()
    {
        using var conteudo = new StringContent(Aninhado(65), Encoding.UTF8, "application/json");
        var resposta = await _fabrica.CreateClient().PostAsync(new Uri(Url, UriKind.Relative), conteudo);
        await ContratoDeErro.AfirmarProblema(resposta, HttpStatusCode.Unauthorized, "http_401");
    }

    [Theory]
    [InlineData("{\"documento\": 52998224725}")]
    [InlineData("{\"documento\": [\"529.982.247-25\"]}")]
    [InlineData("{\"documento\": \"529.982.247-25\", \"nome\": {\"a\": 1}}")]
    [InlineData("[{\"documento\": \"529.982.247-25\"}]")]
    [InlineData("\"529.982.247-25\"")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Json_valido_com_forma_errada_responde_422(string corpo)
    {
        // Igual ao Motor Python: só o corpo ilegível é 400; JSON válido com forma errada é 422.
        await ContratoDeErro.AfirmarProblema(await EnviarJsonAsync(corpo), HttpStatusCode.UnprocessableEntity, "entrada_invalida");
    }
}
