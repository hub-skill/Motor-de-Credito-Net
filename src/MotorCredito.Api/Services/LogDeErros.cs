using System.Threading.Channels;
using MotorCredito.Core.Listas;

namespace MotorCredito.Api.Services;

/// <summary>
/// Fila em memória do log de erros. A resposta ao cliente nunca espera o banco: o erro entra aqui e o
/// <see cref="GravadorDeErros"/> grava em lotes. Com a fila cheia (banco lento ou fora), o registro é descartado
/// e contado. Mesmo comportamento do Motor Python (app/services/log_erros.py).
/// </summary>
public sealed partial class FilaDeErros
{
    public const int Capacidade = 1000;

    private readonly ILogger<FilaDeErros> _logger;
    private long _descartados;

    public FilaDeErros(ILogger<FilaDeErros> logger, int capacidade = Capacidade)
    {
        _logger = logger;
        Canal = Channel.CreateBounded<RegistroDeErro>(
            new BoundedChannelOptions(capacidade) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ =>
            {
                var total = Interlocked.Increment(ref _descartados);
                if (total == 1 || total % 100 == 0)
                {
                    LogFilaCheia(_logger, total);
                }
            });
    }

    internal Channel<RegistroDeErro> Canal { get; }

    public long Descartados => Interlocked.Read(ref _descartados);

    /// <summary>Não bloqueia: com a fila cheia, descarta (o cliente já recebeu a resposta de erro).</summary>
    public void Registrar(RegistroDeErro registro) => Canal.Writer.TryWrite(registro);

    [LoggerMessage(Level = LogLevel.Warning, Message = "fila do log de erros cheia: {Total} registros descartados")]
    private static partial void LogFilaCheia(ILogger logger, long total);
}

/// <summary>Tarefa de fundo que grava a fila na tabela log_erros. Uma falha do banco nunca derruba a API.</summary>
public sealed partial class GravadorDeErros(
    FilaDeErros fila, IRepositorioDeErros destino, ILogger<GravadorDeErros> logger) : BackgroundService
{
    public const int TamanhoDoLote = 100;
    private static readonly TimeSpan PrazoAoParar = TimeSpan.FromSeconds(5);
    private long _perdidos;

    /// <summary>Registros de lotes que o banco recusou.</summary>
    public long Perdidos => Interlocked.Read(ref _perdidos);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Grava o que ainda está na fila antes de a aplicação fechar.
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(PrazoAoParar);
        while (!prazo.IsCancellationRequested && fila.Canal.Reader.TryPeek(out _))
        {
            await GravarAsync(RetirarLote(), prazo.Token).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await fila.Canal.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await GravarAsync(RetirarLote(), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal: o StopAsync grava o que sobrou.
        }
    }

    private List<RegistroDeErro> RetirarLote()
    {
        var lote = new List<RegistroDeErro>(TamanhoDoLote);
        while (lote.Count < TamanhoDoLote && fila.Canal.Reader.TryRead(out var registro))
        {
            lote.Add(registro);
        }

        return lote;
    }

    private async Task GravarAsync(List<RegistroDeErro> lote, CancellationToken cancellationToken)
    {
        if (lote.Count == 0)
        {
            return;
        }

        try
        {
            await destino.GravarAsync(lote, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // o log de erros nunca pode derrubar a API: qualquer falha do banco só é registrada
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            Interlocked.Add(ref _perdidos, lote.Count);
            // Só o tipo: a mensagem do driver pode trazer detalhes da conexão.
            LogFalha(logger, e.GetType().Name, lote.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "falha ao gravar o log de erros no banco: {Tipo} ({Registros} registros)")]
    private static partial void LogFalha(ILogger logger, string tipo, int registros);
}
