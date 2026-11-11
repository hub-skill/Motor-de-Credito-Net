using System.IO.Compression;
using System.Net;
using MotorCredito.Core.Cgu;
using MotorCredito.Core.Configuracao;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Xunit;

namespace MotorCredito.UnitTests;

/// <summary>Proteções para situações anormais que os dados reais não produzem.</summary>
public sealed class ProtecoesTests : IDisposable
{
    private readonly string _pasta = Directory.CreateTempSubdirectory("motor-credito-protecoes-").FullName;

    public void Dispose() => Directory.Delete(_pasta, recursive: true);

    [Fact]
    public void Csv_vazio_e_rejeitado()
    {
        var erro = Assert.Throws<ArquivoCguInvalidoException>(() =>
            LeitorDeArquivoCgu.Ler(new StringReader(string.Empty), LayoutsCgu.Peps, "vazio.csv"));
        Assert.Equal(["arquivo vazio"], erro.Problemas);
    }

    [Fact]
    public void Csv_com_quebra_de_linha_antiga_do_mac_e_texto_depois_da_aspa()
    {
        // \r sozinho separa registros; texto depois da aspa de fechamento continua o campo.
        var registros = LeitorCsv.Ler(new StringReader("A;B\r1;\"ab\"c\r2;x")).ToList();
        Assert.Equal(3, registros.Count);
        Assert.Equal(["1", "abc"], registros[1]);
        Assert.Equal(["2", "x"], registros[2]);
    }

    [Fact]
    public async Task Zip_sem_csv_e_ignorado_e_sem_arquivo_lanca_erro()
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var escritor = new StreamWriter(await zip.CreateEntry("leia-me.txt").OpenAsync());
            await escritor.WriteAsync("sem csv");
        }

        var zipSemCsv = memoria.ToArray();
        using var http = new HttpClient(new RespostaFixa(zipSemCsv));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BaixadorCgu(http).BaixarAsync("ceis", _pasta, new DateOnly(2026, 11, 10), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_pasta));
    }

    [Theory]
    [InlineData("")]
    [InlineData("com\\barra")]
    public async Task Migrador_postgres_recusa_senha_invalida_antes_de_conectar(string senha)
    {
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigradorPostgres.MigrarAsync("Host=127.0.0.1;Port=1", senha, "nao-usado.sql", CancellationToken.None));
        Assert.Contains("Bancos__Postgres__Senha", erro.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("com\"aspas")]
    public void Migrador_oracle_recusa_senha_invalida_antes_de_conectar(string senha)
    {
        var erro = Assert.Throws<InvalidOperationException>(() =>
            MigradorOracle.Migrar("Data Source=127.0.0.1:1/XEPDB1", senha, "nao-usado.sql"));
        Assert.Contains("Bancos__Oracle__Senha", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arquivo_env_ignora_comentarios_e_linhas_invalidas_e_nao_sobrescreve()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var nova = $"TESTE_ENV_NOVA_{sufixo}";
        var existente = $"TESTE_ENV_EXISTENTE_{sufixo}";
        File.WriteAllLines(Path.Combine(_pasta, ".env"), ["# comentário", "", "LINHA_SEM_IGUAL", "=sem_chave", $"{nova} = valor ", $"{existente}=do_arquivo"]);
        Environment.SetEnvironmentVariable(existente, "do_ambiente");
        try
        {
            var caminho = ArquivoEnv.CarregarSeExistir(_pasta);

            Assert.Equal(Path.Combine(_pasta, ".env"), caminho);
            Assert.Equal("valor", Environment.GetEnvironmentVariable(nova));
            Assert.Equal("do_ambiente", Environment.GetEnvironmentVariable(existente)); // ambiente tem prioridade
            Assert.Null(Environment.GetEnvironmentVariable("LINHA_SEM_IGUAL"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(nova, null);
            Environment.SetEnvironmentVariable(existente, null);
        }
    }

    [Fact]
    public void Arquivo_env_desligado_e_arquivo_inexistente()
    {
        Environment.SetEnvironmentVariable(ArquivoEnv.VariavelDeControle, "off");
        try
        {
            Assert.Null(ArquivoEnv.CarregarSeExistir(_pasta));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArquivoEnv.VariavelDeControle, null);
        }

        Assert.Null(ArquivoEnv.Localizar(_pasta, $"nao-existe-{Guid.NewGuid():N}.env"));
    }

    [Fact]
    public void Comparacao_igual_exige_mesmas_linhas_e_todas_marcadas()
    {
        static ResultadoComparacao Resultado(int marcados, string arquivoNovo) =>
            new("postgres", "peps", 10, 10, 10, 0, 0, marcados, ["a.csv"], [arquivoNovo], []);

        Assert.True(Resultado(10, "a.csv").Igual);
        Assert.True(Resultado(10, "a.csv").MesmosArquivos);
        Assert.False(Resultado(9, "a.csv").Igual);            // um registro novo sem a marca DOTNET
        Assert.False(Resultado(10, "b.csv").MesmosArquivos);  // cargas de arquivos diferentes
    }

    private sealed class RespostaFixa(byte[] corpo) : HttpMessageHandler
    {
        private bool _respondeu;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Só o primeiro período tem arquivo (um zip sem CSV); os demais respondem 403, como no Portal.
            var resposta = _respondeu
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corpo) };
            _respondeu = true;
            return Task.FromResult(resposta);
        }
    }
}
