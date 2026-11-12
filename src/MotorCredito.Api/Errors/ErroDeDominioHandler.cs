using Microsoft.AspNetCore.Diagnostics;
using MotorCredito.Core.Listas;

namespace MotorCredito.Api.Errors;

public sealed partial class ErroDeDominioHandler(ILogger<ErroDeDominioHandler> logger) : IExceptionHandler
{
    [LoggerMessage(Level = LogLevel.Error, Message = "erro não tratado")]
    private static partial void LogErroNaoTratado(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "banco de dados com problema: {Tipo}")]
    private static partial void LogErroDeBanco(ILogger logger, string tipo);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, codigo, mensagem) = exception switch
        {
            ErroDeDominioException erro => (erro.StatusCode, erro.Codigo, erro.Mensagem),
            BancoExpirouException erro => (StatusCodes.Status504GatewayTimeout, "banco_timeout", erro.Message),
            ErroDeBancoException => (StatusCodes.Status503ServiceUnavailable, "banco_indisponivel",
                "Banco de dados indisponível. Tente novamente."),
            BadHttpRequestException erro => (erro.StatusCode, "requisicao_invalida", "Requisição inválida."),
            _ => (StatusCodes.Status500InternalServerError, "erro_interno",
                  "Erro interno. Informe o request_id ao suporte.")
        };

        if (exception is ErroDeBancoException)
        {
            // Só o tipo: a mensagem do driver pode trazer detalhes da conexão.
            LogErroDeBanco(logger, exception.InnerException?.GetType().Name ?? exception.GetType().Name);
        }
        else if (status == StatusCodes.Status500InternalServerError)
        {
            // Stack trace apenas nos logs internos; o cliente recebe mensagem genérica.
            LogErroNaoTratado(logger, exception);
        }

        await RespostaDeErro.EscreverAsync(httpContext, status, codigo, mensagem, cancellationToken);
        return true;
    }
}
