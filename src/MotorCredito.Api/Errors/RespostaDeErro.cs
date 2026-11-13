using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using MotorCredito.Api.Services;
using MotorCredito.Core.Listas;

namespace MotorCredito.Api.Errors;

/// <summary>
/// Formato único de erro das APIs do Motor de Crédito: RFC 9457 (Problem Details), o mesmo do Motor Python.
/// O corpo tem sempre exatamente <c>title</c>, <c>status</c>, <c>code</c> e <c>request_id</c> (sem <c>type</c>:
/// ausente = about:blank) e o tipo de conteúdo é <c>application/problem+json</c>.
/// </summary>
public static class RespostaDeErro
{
    public const string TipoDeConteudo = "application/problem+json";

    /// <summary>Chave em HttpContext.Items com o instante de início da requisição (Stopwatch), para a duração no log.</summary>
    public const string ChaveDoInicio = "motor.inicio";

    /// <summary>Chave em HttpContext.Items com o ULID desta requisição, se ela entra na movimentação.</summary>
    public const string ChaveDaMovimentacao = "motor.movimentacao";

    /// <summary>Chave em HttpContext.Items com o <see cref="RegistroDeErro"/> da resposta de erro (gravado com a movimentação).</summary>
    public const string ChaveDoErro = "motor.erro";

    /// <summary>Título em português dos erros genéricos do framework (401, 404, 405). Outros: mantém o do framework.</summary>
    public static string? TituloGenerico(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Chave de API ausente ou inválida.",
        StatusCodes.Status404NotFound => "Recurso não encontrado.",
        StatusCodes.Status405MethodNotAllowed => "Método não permitido.",
        _ => null
    };

    /// <summary>
    /// Erros que o framework produz sem corpo (401, 404, 405...): código <c>http_&lt;status&gt;</c> e título em
    /// português quando conhecido. Usado pelo <c>UseStatusCodePages</c>.
    /// </summary>
    public static Task EscreverGenericoAsync(HttpContext contexto)
    {
        var status = contexto.Response.StatusCode;
        var frase = ReasonPhrases.GetReasonPhrase(status);
        return EscreverAsync(
            contexto,
            status,
            string.Create(CultureInfo.InvariantCulture, $"http_{status}"),
            TituloGenerico(status) ?? (string.IsNullOrEmpty(frase) ? "Erro" : frase),
            contexto.RequestAborted);
    }

    public static Task EscreverAsync(
        HttpContext contexto,
        int status,
        string codigo,
        string titulo,
        CancellationToken cancellationToken,
        string? tipoExcecao = null)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        Registrar(contexto, status, codigo, titulo, tipoExcecao);

        // Com a resposta guardada em memória (MovimentacaoMiddleware), descarta o que um erro tenha deixado escrito.
        if (!contexto.Response.HasStarted && contexto.Response.Body is MemoryStream { CanSeek: true } guardada)
        {
            guardada.SetLength(0);
        }

        var problema = new ProblemDetails { Status = status, Title = titulo };
        problema.Extensions["code"] = codigo;
        problema.Extensions["request_id"] = contexto.TraceIdentifier;

        contexto.Response.StatusCode = status;
        return contexto.Response.WriteAsJsonAsync(
            problema, options: null, contentType: TipoDeConteudo, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Deixa os metadados do erro no <c>HttpContext.Items</c>: o <c>MovimentacaoMiddleware</c> os grava junto com a movimentação
    /// da requisição. Só metadados: nunca o corpo. Requisição sem movimentação (rota excluída ou sem chave de API válida)
    /// não tem erro gravado: fica só no log da aplicação.
    /// </summary>
    private static void Registrar(HttpContext contexto, int status, string codigo, string titulo, string? tipoExcecao)
    {
        if (!contexto.Items.ContainsKey(ChaveDaMovimentacao))
        {
            return;
        }

        int? duracao = contexto.Items.TryGetValue(ChaveDoInicio, out var inicio) && inicio is long marca
            ? (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(marca).TotalMilliseconds)
            : null;
        var ambiente = contexto.RequestServices?.GetService<IHostEnvironment>()?.EnvironmentName ?? "-";
        contexto.Items[ChaveDoErro] = new RegistroDeErro(
            Cortar(contexto.TraceIdentifier, 64),
            Cortar(contexto.Request.Method, 10),
            Cortar(contexto.Request.Path.Value is { Length: > 0 } caminho ? caminho : "/", 200),
            status,
            Cortar(codigo, 40),
            Cortar(titulo, 200),
            tipoExcecao is null ? null : Cortar(tipoExcecao, 200),
            duracao,
            Cortar(ambiente, 20));
    }

    private static string Cortar(string texto, int maximo) => texto.Length <= maximo ? texto : texto[..maximo];
}
