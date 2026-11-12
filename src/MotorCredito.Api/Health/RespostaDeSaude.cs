using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MotorCredito.Api.Health;

/// <summary>
/// Corpo JSON dos health checks, o mesmo do Motor Python:
/// /health/live -> {"status":"ok"}; /health/ready -> {"status":"ok","banco":"postgres"} ou 503 {"status":"banco_indisponivel"}.
/// O status HTTP (200/503) continua vindo do middleware de health checks.
/// </summary>
public static class RespostaDeSaude
{
    public static Task Vivo(HttpContext contexto, HealthReport relatorio) =>
        contexto.Response.WriteAsJsonAsync(new { status = "ok" });

    public static Task Pronto(HttpContext contexto, HealthReport relatorio, string banco)
    {
        ArgumentNullException.ThrowIfNull(relatorio);
        return relatorio.Status == HealthStatus.Healthy
            ? contexto.Response.WriteAsJsonAsync(new { status = "ok", banco })
            : contexto.Response.WriteAsJsonAsync(new { status = "banco_indisponivel" });
    }
}
