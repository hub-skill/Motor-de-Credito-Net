using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using MotorCredito.Api.Configuration;
using MotorCredito.Api.Contracts;
using MotorCredito.Api.Errors;
using MotorCredito.Api.Services;
using MotorCredito.Core.Documentos;
using MotorCredito.Core.Listas;
using MotorCredito.Core.Regras;

namespace MotorCredito.Api.Endpoints.V1;

public static class TriagemEndpoints
{
    public static IEndpointRouteBuilder MapTriagemV1(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/v1")
            .RequireAuthorization()
            .WithTags("triagem");

        // POST (e não GET) para o documento não aparecer em URLs, logs de proxy e histórico.
        grupo.MapPost("/triagem", TriarAsync)
            .WithName("TriarDocumento")
            .WithSummary("Verifica um CPF/CNPJ nas listas de sanções (CEIS/CNEP) e de PEP da CGU")
            .Produces<SaidaTriagem>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    private static async Task<Ok<SaidaTriagem>> TriarAsync(
        EntradaTriagem? entrada,
        IValidator<EntradaTriagem> validador,
        IRepositorioListas repositorio,
        CacheDeOrigem cache,
        ApiSettings settings,
        TimeProvider relogio,
        CancellationToken cancellationToken)
    {
        // Corpo vazio ou "null": 422, como no Motor Python (sem o "?" o ASP.NET respondia 400 antes de chegar aqui).
        if (entrada is null)
        {
            throw new EntradaInvalidaException("Dados de entrada inválidos.");
        }

        // Remove espaços nas extremidades antes de validar; nome vazio conta como não informado.
        var normalizada = new EntradaTriagem(
            entrada.Documento?.Trim(),
            string.IsNullOrWhiteSpace(entrada.Nome) ? null : entrada.Nome.Trim());

        var validacao = await validador.ValidateAsync(normalizada, cancellationToken);
        if (!validacao.IsValid || !ValidadorDeDocumento.TentarNormalizar(normalizada.Documento, out var documento))
        {
            // Mensagem genérica: não ecoa a entrada (CPF, nome) na resposta.
            throw new EntradaInvalidaException("Dados de entrada inválidos.");
        }

        var hoje = DateOnly.FromDateTime(relogio.GetLocalNow().DateTime);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        IReadOnlyList<Sancao> sancoes;
        IReadOnlyList<RegistroPep> candidatos = [];
        OrigemDados origem;
        try
        {
            sancoes = await repositorio.BuscarSancoesAsync(documento.Valor, cts.Token);
            if (documento.Tipo == TipoDocumento.Cpf && normalizada.Nome is not null)
            {
                candidatos = await repositorio.BuscarPepsPorMioloAsync(
                    ValidadorDeDocumento.MioloCpf(documento.Valor), cts.Token);
            }

            origem = await cache.ObterAsync(repositorio, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Expirou o nosso timeout (não foi o cliente que cancelou).
            throw new ProcessamentoExpirouException();
        }

        var triagem = MotorDeTriagem.Triar(documento.Tipo, normalizada.Nome, sancoes, candidatos, hoje);
        return TypedResults.Ok(new SaidaTriagem(
            Documento: ValidadorDeDocumento.Mascarar(documento),
            Tipo: documento.Tipo == TipoDocumento.Cpf ? "CPF" : "CNPJ",
            Resultado: triagem.Decisao switch
            {
                Decisao.Bloquear => "bloquear",
                Decisao.Revisar => "revisar",
                _ => "liberar",
            },
            Motivos: [.. triagem.Motivos.Select(m => new MotivoSaida(m.Codigo, m.Texto))],
            Sancoes: [.. sancoes.Select(s => new SancaoSaida(
                s.Cadastro,
                s.Categoria,
                s.OrgaoSancionador,
                s.UfOrgao,
                s.DataInicio,
                s.DataFim,
                s.NumeroProcesso,
                MotorDeTriagem.SancaoVigente(s, hoje)))],
            Pep: new PepSaida(
                triagem.PepVerificado,
                triagem.PepAtivo,
                [.. triagem.Peps
                    .Select(p => p.DescricaoFuncao)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Select(f => f!)
                    .Distinct()
                    .Order(StringComparer.Ordinal)]),
            ConsultadoEm: hoje,
            Fonte: new FonteSaida(origem.Banco, origem.Arquivos, origem.CarregadoEm)));
    }
}
