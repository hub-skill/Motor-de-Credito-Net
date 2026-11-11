using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MotorCredito.Api.Configuration;
using MotorCredito.Api.Endpoints.V1;
using MotorCredito.Api.Errors;
using MotorCredito.Api.Health;
using MotorCredito.Api.Middleware;
using MotorCredito.Api.Security;
using MotorCredito.Api.Services;
using MotorCredito.Api.Validation;
using MotorCredito.Core.Configuracao;
using MotorCredito.Core.Listas;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;

// .env -> variáveis de ambiente (não sobrescreve as já definidas). Precisa vir antes do builder.
ArquivoEnv.CarregarSeExistir();

var builder = WebApplication.CreateBuilder(args);

// Configuração tipada e validada na inicialização.
builder.Services
    .AddOptions<ApiSettings>()
    .Bind(builder.Configuration.GetSection(ApiSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ApiSettings>>().Value);

var settings = builder.Configuration.GetSection(ApiSettings.SectionName).Get<ApiSettings>()!;
var bancos = builder.Configuration.GetSection(BancosSettings.SectionName).Get<BancosSettings>() ?? new BancosSettings();

const long TamanhoMaximoDoCorpo = 4 * 1024; // a entrada é um documento e um nome; o proxy deve limitar também

builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = TamanhoMaximoDoCorpo);

// Saída em snake_case, igual à da API Python (orgao_sancionador, consultado_em...).
builder.Services.ConfigureHttpJsonOptions(opcoes =>
    opcoes.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// Banco escolhido na configuração (Bancos:Provedor). As conexões só abrem no primeiro uso.
builder.Services.AddSingleton(bancos);
if (bancos.UsaOracle)
{
    builder.Services.AddSingleton<IRepositorioListas>(_ => new RepositorioOracle(bancos.Oracle));
}
else
{
    builder.Services.AddSingleton(_ => ConexoesPostgres.CriarFonteDeLeitura(bancos.Postgres));
    builder.Services.AddSingleton<IRepositorioListas, RepositorioPostgres>();
}

// Serviços e validação
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CacheDeOrigem>();
builder.Services.AddValidatorsFromAssemblyContaining<EntradaTriagemValidator>();

// Autenticação por API key. Por padrão, todas as rotas exigem autenticação.
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(opcoes =>
    opcoes.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Rate limit por IP.
builder.Services.AddRateLimiter(opcoes =>
{
    opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opcoes.OnRejected = (contexto, _) =>
    {
        contexto.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    opcoes.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
        RateLimitPartition.GetFixedWindowLimiter(
            contexto.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.RateLimitPorMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// Erros padronizados (RFC 7807), sempre com "code" e "request_id", inclusive 401, 404 e 429.
builder.Services.AddProblemDetails(opcoes => opcoes.CustomizeProblemDetails = contexto =>
{
    var status = contexto.ProblemDetails.Status ?? contexto.HttpContext.Response.StatusCode;
    contexto.ProblemDetails.Extensions.TryAdd("code", string.Create(CultureInfo.InvariantCulture, $"http_{status}"));
    contexto.ProblemDetails.Extensions.TryAdd("request_id", contexto.HttpContext.TraceIdentifier);
});
builder.Services.AddExceptionHandler<ErroDeDominioHandler>();

// Proxies: adicione os IPs confiáveis em KnownProxies. Sem isso, X-Forwarded-* é ignorado.
builder.Services.Configure<ForwardedHeadersOptions>(opcoes =>
    opcoes.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// CORS e filtro de hosts
builder.Services.AddCors(opcoes => opcoes.AddDefaultPolicy(politica =>
    politica.WithOrigins(settings.AllowedOrigins)
            .WithMethods("POST")
            .WithHeaders("Content-Type", ApiKeyAuthenticationHandler.Header)));
builder.Services.AddHostFiltering(opcoes => opcoes.AllowedHosts = settings.AllowedHosts);

// Observabilidade e documentação
builder.Services.AddHealthChecks().AddCheck<BancoHealthCheck>("banco");
builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metricas => metricas
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHostFiltering();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.Use(async (contexto, proximo) =>
{
    // O Kestrel também limita; esta checagem responde 413 no formato padrão de erro.
    if (contexto.Request.ContentLength > TamanhoMaximoDoCorpo)
    {
        throw new CorpoMuitoGrandeException();
    }

    await proximo(contexto);
});
app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Documentação apenas quando habilitada (desligada em produção por padrão).
if (settings.EnableDocs)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

// Health checks anônimos; métricas devem ficar na rede interna.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();

app.MapTriagemV1();

app.Run();

// Torna a classe Program acessível aos testes de integração.
public partial class Program { }
