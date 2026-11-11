using Microsoft.Extensions.Diagnostics.HealthChecks;
using MotorCredito.Core.Listas;

namespace MotorCredito.Api.Health;

/// <summary>Usado pelo /health/ready: a API só está pronta se o banco responde.</summary>
public sealed class BancoHealthCheck(IRepositorioListas repositorio) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await repositorio.VerificarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (ErroDeBancoException)
        {
            return HealthCheckResult.Unhealthy("banco indisponível");
        }
    }
}
