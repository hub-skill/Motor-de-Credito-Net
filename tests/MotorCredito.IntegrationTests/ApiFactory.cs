using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;

namespace MotorCredito.IntegrationTests;

/// <summary>Sobe a API em memória com um repositório falso: os testes nunca tocam os bancos reais.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ChaveDeTeste = "chave-de-teste-com-pelo-menos-32-caracteres";

    public RepositorioFalso Repositorio { get; } = new();

    /// <summary>Recebe o que a API grava no log de erros (no lugar da tabela log_erros).</summary>
    public DestinoDeErrosFalso Erros { get; } = new();

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
            servicos.RemoveAll<IRepositorioDeErros>();
            servicos.AddSingleton<IRepositorioDeErros>(Erros);
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

/// <summary>Destino falso do log de erros: guarda os registros e, se pedido, falha.</summary>
public sealed class DestinoDeErrosFalso : IRepositorioDeErros
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<RegistroDeErro> _registros = new();

    public int FalharVezes { get; set; }

    public List<int> TamanhosDosLotes { get; } = [];

    public IReadOnlyList<RegistroDeErro> Registros => [.. _registros];

    public void Limpar()
    {
        _registros.Clear();
        TamanhosDosLotes.Clear();
        FalharVezes = 0;
    }

    public Task GravarAsync(IReadOnlyList<RegistroDeErro> registros, CancellationToken cancellationToken)
    {
        if (FalharVezes > 0)
        {
            FalharVezes--;
            throw new InvalidOperationException("banco fora: senha=abc");
        }

        lock (TamanhosDosLotes)
        {
            TamanhosDosLotes.Add(registros.Count);
        }

        foreach (var r in registros)
        {
            _registros.Enqueue(r);
        }

        return Task.CompletedTask;
    }

    /// <summary>Espera (até 5 s) chegar o registro com este request_id: a gravação é assíncrona.</summary>
    public async Task<RegistroDeErro> EsperarAsync(string requestId)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var achado = _registros.FirstOrDefault(r => r.RequestId == requestId);
            if (achado is not null)
            {
                return achado;
            }

            await Task.Delay(10, prazo.Token);
        }
    }
}
