namespace MotorCredito.Core.Configuracao;

/// <summary>
/// Carrega o arquivo .env para variáveis de ambiente, sem sobrescrever as que já existem.
/// O .NET lê "Secao__Chave" como "Secao:Chave" na configuração.
/// </summary>
public static class ArquivoEnv
{
    /// <summary>Defina MOTOR_DOTENV=off para não carregar o .env (usado nos testes de integração).</summary>
    public const string VariavelDeControle = "MOTOR_DOTENV";

    public static string? CarregarSeExistir(string? pastaInicial = null)
    {
        var controle = Environment.GetEnvironmentVariable(VariavelDeControle);
        if (string.Equals(controle, "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var caminho = Localizar(pastaInicial ?? Directory.GetCurrentDirectory()) ?? Localizar(AppContext.BaseDirectory);
        if (caminho is null)
        {
            return null;
        }

        foreach (var linha in File.ReadLines(caminho))
        {
            var texto = linha.Trim();
            if (texto.Length == 0 || texto[0] == '#')
            {
                continue;
            }

            var igual = texto.IndexOf('=', StringComparison.Ordinal);
            if (igual <= 0)
            {
                continue;
            }

            var chave = texto[..igual].Trim();
            var valor = texto[(igual + 1)..].Trim();
            if (Environment.GetEnvironmentVariable(chave) is null)
            {
                Environment.SetEnvironmentVariable(chave, valor);
            }
        }

        return caminho;
    }

    /// <summary>Procura o arquivo na pasta informada e nas pastas acima dela.</summary>
    public static string? Localizar(string pasta, string nome = ".env")
    {
        for (var atual = new DirectoryInfo(pasta); atual is not null; atual = atual.Parent)
        {
            var candidato = Path.Combine(atual.FullName, nome);
            if (File.Exists(candidato))
            {
                return candidato;
            }
        }

        return null;
    }

    /// <summary>Pasta raiz do repositório (onde está a solução), a partir da pasta atual ou do executável.</summary>
    public static string RaizDoRepositorio()
    {
        var solucao = Localizar(Directory.GetCurrentDirectory(), "MotorCredito.slnx")
            ?? Localizar(AppContext.BaseDirectory, "MotorCredito.slnx");
        return solucao is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(solucao)!;
    }
}
