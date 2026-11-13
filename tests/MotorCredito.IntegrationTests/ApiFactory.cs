using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MotorCredito.Api.Middleware;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;
using Xunit;

namespace MotorCredito.IntegrationTests;

/// <summary>Sobe a API em memória com um repositório falso: os testes nunca tocam os bancos reais.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ChaveDeTeste = "chave-de-teste-com-pelo-menos-32-caracteres";

    public RepositorioFalso Repositorio { get; } = new();

    /// <summary>Recebe o que a API grava (no lugar das tabelas movimentacao e log_erros).</summary>
    public MovimentacaoFalsa Movimentacao { get; } = new();

    /// <summary>Visão dos erros gravados junto com as movimentações (log_erros).</summary>
    public ErrosGravados Erros => new(Movimentacao);

    public RelogioFixo Relogio { get; } = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    static ApiFactory()
    {
        // Não carrega o .env local: a configuração dos testes não depende da máquina.
        Environment.SetEnvironmentVariable(ArquivoEnv.VariavelDeControle, "off");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Como na produção. O padrão do WebApplicationFactory é Development, que muda o tratamento de erros do
        // ASP.NET (ex.: JSON inválido) e escondeu um erro real: a API respondia http_400 em vez de requisicao_invalida.
        builder.UseEnvironment("Production");
        builder.UseSetting("Api:ApiKey", ChaveDeTeste);
        builder.UseSetting("Api:RequestTimeoutSeconds", "1");
        builder.UseSetting("Api:RateLimitPorMinuto", "10000");
        builder.ConfigureTestServices(servicos =>
        {
            servicos.RemoveAll<IRepositorioListas>();
            servicos.AddSingleton<IRepositorioListas>(Repositorio);
            servicos.RemoveAll<IRepositorioDeMovimentacao>();
            servicos.AddSingleton<IRepositorioDeMovimentacao>(Movimentacao);
            servicos.RemoveAll<MovimentacaoOpcoes>();
            servicos.AddSingleton(new MovimentacaoOpcoes(4 * 1024));
            servicos.RemoveAll<TimeProvider>();
            servicos.AddSingleton<TimeProvider>(Relogio);
        });
    }
}

public sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => agora;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public sealed class RepositorioFalso : IRepositorioListas
{
    public Dictionary<string, List<Sancao>> Sancoes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<RegistroPep>> Peps { get; } = new(StringComparer.Ordinal);

    public List<string> ConsultasPep { get; } = [];

    public bool Falhar { get; set; }

    public TimeSpan Atraso { get; set; }

    /// <summary>Exceção lançada na busca de sanções (para provocar 504 do banco ou erro 500 inesperado).</summary>
    public Exception? Lancar { get; set; }

    public void Limpar()
    {
        Sancoes.Clear();
        Peps.Clear();
        ConsultasPep.Clear();
        Falhar = false;
        Atraso = TimeSpan.Zero;
        Lancar = null;
    }

    public async Task<IReadOnlyList<Sancao>> BuscarSancoesAsync(string documento, CancellationToken cancellationToken)
    {
        if (Falhar)
        {
            throw new BancoIndisponivelException();
        }

        if (Lancar is not null)
        {
            throw Lancar;
        }

        await Task.Delay(Atraso, cancellationToken);
        return Sancoes.GetValueOrDefault(documento) ?? [];
    }

    public Task<IReadOnlyList<RegistroPep>> BuscarPepsPorMioloAsync(string miolo, CancellationToken cancellationToken)
    {
        ConsultasPep.Add(miolo);
        return Task.FromResult<IReadOnlyList<RegistroPep>>(Peps.GetValueOrDefault(miolo) ?? []);
    }

    public Task<OrigemDados> ObterOrigemAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new OrigemDados("falso", ["20261110_CEIS.csv"], new DateTimeOffset(2026, 11, 10, 7, 0, 0, TimeSpan.Zero)));

    public Task VerificarAsync(CancellationToken cancellationToken) =>
        Falhar ? throw new BancoIndisponivelException() : Task.CompletedTask;
}

/// <summary>Visão dos erros (log_erros) que a API gravou junto com as movimentações.</summary>
public sealed class ErrosGravados(MovimentacaoFalsa movimentacao)
{
    public IReadOnlyList<RegistroDeErro> Registros => [.. movimentacao.Registros.Where(r => r.Erro is not null).Select(r => r.Erro!)];

    public void Limpar() => movimentacao.Limpar();

    /// <summary>Espera (até 5 s) chegar o erro com este request_id: a gravação é em segundo plano.</summary>
    public async Task<RegistroDeErro> EsperarAsync(string requestId)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var achado = Registros.FirstOrDefault(r => r.RequestId == requestId);
            if (achado is not null)
            {
                return achado;
            }

            await Task.Delay(10, prazo.Token);
        }
    }
}

/// <summary>Repositório falso: guarda as movimentações na memória; pode falhar ou demorar, como um banco com problema.</summary>
public sealed class MovimentacaoFalsa : IRepositorioDeMovimentacao
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RegistroDeMovimentacao> _registros = new();

    /// <summary>Simula o banco fora: a gravação falha.</summary>
    public bool Falha { get; set; }

    /// <summary>Simula o banco lento: enquanto o portão não for liberado, a gravação espera.</summary>
    public TaskCompletionSource? Portao { get; set; }

    public IReadOnlyList<RegistroDeMovimentacao> Registros => [.. _registros.Values];

    public void Limpar()
    {
        Falha = false;
        Portao = null;
        _registros.Clear();
    }

    public async Task GravarAsync(IReadOnlyList<RegistroDeMovimentacao> registros, CancellationToken cancellationToken)
    {
        if (Falha)
        {
            throw new InvalidOperationException("banco fora");
        }

        if (Portao is { } portao)
        {
            await portao.Task.WaitAsync(cancellationToken);
        }

        foreach (var r in registros)
        {
            _registros[r.RequisicaoId] = r;
        }
    }

    /// <summary>Espera (até 5 s) a movimentação gravada desta requisição: a gravação é em segundo plano.</summary>
    public async Task<RegistroDeMovimentacao> EsperarAsync(string requisicaoId)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (_registros.TryGetValue(requisicaoId, out var registro))
            {
                return registro;
            }

            await Task.Delay(10, prazo.Token);
        }
    }

    /// <summary>ULID da requisição a partir do header X-Requisicao.</summary>
    public static string IdDoHeader(HttpResponseMessage resposta)
    {
        ArgumentNullException.ThrowIfNull(resposta);
        var id = resposta.Headers.GetValues(MovimentacaoMiddleware.Header).Single();
        Assert.True(Ulid.Valido(id), $"X-Requisicao não é um ULID: {id}");
        return id;
    }
}

/// <summary>Destino que só conta os lotes: para os testes da fila e do gravador em segundo plano.</summary>
public sealed class DestinoDeLotesFalso : IRepositorioDeMovimentacao
{
    private readonly object _trava = new();

    public int FalharVezes { get; set; }

    public List<int> TamanhosDosLotes { get; } = [];

    public List<RegistroDeMovimentacao> Registros { get; } = [];

    public Task GravarAsync(IReadOnlyList<RegistroDeMovimentacao> registros, CancellationToken cancellationToken)
    {
        lock (_trava)
        {
            if (FalharVezes > 0)
            {
                FalharVezes--;
                throw new InvalidOperationException("banco fora: senha=abc");
            }

            TamanhosDosLotes.Add(registros.Count);
            Registros.AddRange(registros);
        }

        return Task.CompletedTask;
    }
}
