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
        Assert.Equal(HttpStatusCode.Unauthorized, semChave.StatusCode);

        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-API-Key", "errada");
        var errada = await cliente.PostAsJsonAsync(Url, new { documento = CpfValido });
        Assert.Equal(HttpStatusCode.Unauthorized, errada.StatusCode);
        Assert.Equal("http_401", (await Json(errada)).GetProperty("code").GetString());
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

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        using var json = JsonDocument.Parse(texto);
        Assert.Equal("entrada_invalida", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("request_id").GetString()));
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
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Corpo_grande_demais_responde_413()
    {
        using var conteudo = new StringContent($"{{\"documento\": \"{new string('1', 5000)}\"}}", Encoding.UTF8, "application/json");
        var resposta = await ClienteAutenticado().PostAsync(Url, conteudo);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resposta.StatusCode);
    }

    [Fact]
    public async Task Banco_fora_do_ar_responde_503_e_ready_fica_indisponivel()
    {
        _fabrica.Repositorio.Falhar = true;
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal("banco_indisponivel", (await Json(resposta)).GetProperty("code").GetString());

        var pronto = await _fabrica.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pronto.StatusCode);
    }

    [Fact]
    public async Task Banco_lento_responde_504()
    {
        _fabrica.Repositorio.Atraso = TimeSpan.FromSeconds(3); // RequestTimeoutSeconds = 1 nos testes
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        Assert.Equal(HttpStatusCode.GatewayTimeout, resposta.StatusCode);
        Assert.Equal("timeout", (await Json(resposta)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Banco_expirou_responde_504_banco_timeout()
    {
        _fabrica.Repositorio.Lancar = new BancoExpirouException();
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });

        Assert.Equal(HttpStatusCode.GatewayTimeout, resposta.StatusCode);
        Assert.Equal("banco_timeout", (await Json(resposta)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Erro_inesperado_responde_500_generico_sem_vazar_detalhes()
    {
        _fabrica.Repositorio.Lancar = new InvalidOperationException("detalhe interno: senha=abc tabela=credito_net.sancoes");
        var resposta = await ClienteAutenticado().PostAsJsonAsync(Url, new { documento = CpfValido });
        var texto = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        using var json = JsonDocument.Parse(texto);
        Assert.Equal("erro_interno", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("request_id").GetString()));
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
        Assert.Equal("nosniff", vivo.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", vivo.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Rota_inexistente_responde_404_no_formato_padrao()
    {
        var resposta = await ClienteAutenticado().GetAsync(new Uri("/nao-existe", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("http_404", (await Json(resposta)).GetProperty("code").GetString());
    }
}
