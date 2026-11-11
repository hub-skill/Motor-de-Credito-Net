using System.IO.Compression;
using System.Net;
using MotorCredito.Core.Cgu;
using Xunit;

namespace MotorCredito.UnitTests;

public sealed class BaixadorCguTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "motor-credito-testes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    [Fact]
    public async Task Recua_os_dias_ate_achar_o_arquivo_e_extrai_so_o_nome()
    {
        // O Portal responde 403 para arquivos antigos; o de ontem existe.
        var portal = new PortalFalso(url => url.EndsWith("/ceis/20261109", StringComparison.Ordinal)
            ? Zip("../../fora/20261109_CEIS.csv", "CADASTRO\nCEIS\n") // caminho malicioso dentro do zip
            : null);
        using var http = new HttpClient(portal);

        var arquivo = await new BaixadorCgu(http).BaixarAsync("ceis", _pasta, new DateOnly(2026, 11, 10), CancellationToken.None);

        Assert.Equal(Path.Combine(_pasta, "20261109_CEIS.csv"), arquivo); // nunca sai da pasta de destino
        Assert.Equal("CADASTRO\nCEIS\n", await File.ReadAllTextAsync(arquivo));
        Assert.Equal(
            ["/download-de-dados/ceis/20261110", "/download-de-dados/ceis/20261109"],
            portal.Pedidos.Select(u => new Uri(u).AbsolutePath));
    }

    [Fact]
    public async Task Pep_procura_por_mes()
    {
        var portal = new PortalFalso(url => url.EndsWith("/pep/202609", StringComparison.Ordinal) ? Zip("202609_PEP.csv", "CPF\n") : null);
        using var http = new HttpClient(portal);

        var arquivo = await new BaixadorCgu(http).BaixarAsync("pep", _pasta, new DateOnly(2026, 11, 10), CancellationToken.None);

        Assert.EndsWith("202609_PEP.csv", arquivo, StringComparison.Ordinal);
        Assert.Equal(["202611", "202610", "202609"], portal.Pedidos.Select(u => u[^6..]));
    }

    [Fact]
    public async Task Sem_arquivo_disponivel_lanca_erro()
    {
        using var http = new HttpClient(new PortalFalso(_ => null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BaixadorCgu(http).BaixarAsync("cnep", _pasta, new DateOnly(2026, 11, 10), CancellationToken.None));
    }

    private static byte[] Zip(string nome, string conteudo)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var escritor = new StreamWriter(zip.CreateEntry(nome).Open());
            escritor.Write(conteudo);
        }

        return memoria.ToArray();
    }

    private sealed class PortalFalso(Func<string, byte[]?> responder) : HttpMessageHandler
    {
        public List<string> Pedidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Pedidos.Add(url);
            var corpo = responder(url);
            var resposta = corpo is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corpo) };
            return Task.FromResult(resposta);
        }
    }
}
