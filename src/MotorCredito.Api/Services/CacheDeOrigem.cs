using MotorCredito.Core.Listas;

namespace MotorCredito.Api.Services;

/// <summary>Guarda por alguns minutos de qual arquivo e carga vêm os dados (evita uma consulta por requisição).</summary>
public sealed class CacheDeOrigem(TimeProvider relogio)
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(5);
    private OrigemDados? _valor;
    private DateTimeOffset _expira;

    public async Task<OrigemDados> ObterAsync(IRepositorioListas repositorio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        var agora = relogio.GetUtcNow();
        var atual = _valor;
        if (atual is not null && agora < _expira)
        {
            return atual;
        }

        var novo = await repositorio.ObterOrigemAsync(cancellationToken);
        _valor = novo;
        _expira = agora + Validade;
        return novo;
    }
}
