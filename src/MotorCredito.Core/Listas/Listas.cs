namespace MotorCredito.Core.Listas;

public sealed record Sancao(
    string Cadastro,
    long CodigoSancao,
    string? Categoria,
    string? OrgaoSancionador,
    string? UfOrgao,
    DateOnly? DataInicio,
    DateOnly? DataFim,
    string? NumeroProcesso);

public sealed record RegistroPep(
    string Nome,
    string? DescricaoFuncao,
    string? NomeOrgao,
    DateOnly? DataInicioExercicio,
    DateOnly? DataFimExercicio,
    DateOnly? DataFimCarencia);

public sealed record OrigemDados(string Banco, IReadOnlyList<string> Arquivos, DateTimeOffset? CarregadoEm);

/// <summary>Contrato dos repositórios: a regra de negócio não sabe qual banco está por trás.</summary>
public interface IRepositorioListas
{
    Task<IReadOnlyList<Sancao>> BuscarSancoesAsync(string documento, CancellationToken cancellationToken);

    Task<IReadOnlyList<RegistroPep>> BuscarPepsPorMioloAsync(string miolo, CancellationToken cancellationToken);

    Task<OrigemDados> ObterOrigemAsync(CancellationToken cancellationToken);

    /// <summary>Lança <see cref="ErroDeBancoException"/> se o banco não responder (usado pelo /health/ready).</summary>
    Task VerificarAsync(CancellationToken cancellationToken);
}

public class ErroDeBancoException(string mensagem, Exception? causa) : Exception(mensagem, causa);

public sealed class BancoIndisponivelException(Exception? causa = null)
    : ErroDeBancoException("Banco de dados indisponível.", causa);

public sealed class BancoExpirouException(Exception? causa = null)
    : ErroDeBancoException("O banco de dados não respondeu dentro do tempo limite.", causa);
