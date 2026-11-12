namespace MotorCredito.Core.Listas;

/// <summary>
/// Uma resposta de erro (4xx ou 5xx) da API, para a tabela log_erros. Só metadados: nunca documento, nome ou
/// corpo da requisição. Mesmos campos do Motor Python.
/// </summary>
public sealed record RegistroDeErro(
    string RequestId,
    string Metodo,
    string Rota,
    int Status,
    string Codigo,
    string Titulo,
    string? TipoExcecao,
    int? DuracaoMs,
    string Ambiente);

/// <summary>Grava lotes na tabela log_erros. A aplicação só tem INSERT nela.</summary>
public interface IRepositorioDeErros
{
    Task GravarAsync(IReadOnlyList<RegistroDeErro> registros, CancellationToken cancellationToken);
}
