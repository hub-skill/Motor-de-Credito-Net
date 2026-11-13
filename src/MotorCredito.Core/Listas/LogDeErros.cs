namespace MotorCredito.Core.Listas;

/// <summary>
/// Uma resposta de erro (4xx ou 5xx) da API, para a tabela log_erros. Só metadados: nunca documento, nome ou corpo da
/// requisição (o corpo, mascarado, fica na movimentação). Mesmos campos do Motor Python. A ligação com a
/// movimentação é o <c>requisicao_id</c> (ULID): o erro é gravado junto com a sua movimentação.
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
