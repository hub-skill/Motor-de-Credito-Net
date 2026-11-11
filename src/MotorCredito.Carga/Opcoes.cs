namespace MotorCredito.Carga;

/// <summary>Opções no formato "--nome valor".</summary>
internal sealed class Opcoes
{
    private readonly Dictionary<string, string> _valores = new(StringComparer.OrdinalIgnoreCase);

    public static Opcoes Ler(IEnumerable<string> argumentos)
    {
        var opcoes = new Opcoes();
        string? pendente = null;
        foreach (var argumento in argumentos)
        {
            if (argumento.StartsWith("--", StringComparison.Ordinal))
            {
                pendente = argumento[2..];
                opcoes._valores[pendente] = string.Empty;
            }
            else if (pendente is not null)
            {
                opcoes._valores[pendente] = argumento;
                pendente = null;
            }
        }

        return opcoes;
    }

    public string? Valor(string nome) => _valores.TryGetValue(nome, out var valor) && valor.Length > 0 ? valor : null;

    /// <summary>--banco postgres|oracle|todos (padrão: todos).</summary>
    public bool Inclui(string banco)
    {
        var escolhido = Valor("banco") ?? "todos";
        return escolhido.Equals("todos", StringComparison.OrdinalIgnoreCase)
            || escolhido.Equals(banco, StringComparison.OrdinalIgnoreCase);
    }
}
