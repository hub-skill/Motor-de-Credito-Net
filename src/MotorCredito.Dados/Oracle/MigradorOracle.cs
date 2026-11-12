using Oracle.ManagedDataAccess.Client;

namespace MotorCredito.Dados.BancoOracle;

/// <summary>
/// Cria MOTOR_CREDITO_NET (dono das tabelas, sem login), MOTOR_APP_NET (aplicação) e executa os scripts, em ordem.
/// Roda como administrador no banco plugável. Pode ser executado de novo: "já existe" é ignorado.
/// </summary>
public static class MigradorOracle
{
    // ORA-01920 usuário, ORA-00955 objeto, ORA-01408 índice, ORA-02260/02261 chaves: já existem.
    private static readonly HashSet<int> JaExiste = [1920, 955, 1408, 2260, 2261];

    public static IReadOnlyList<string> Migrar(
        string conexaoAdmin, string senhaDoApp, IReadOnlyList<string> caminhosDosScripts)
    {
        if (string.IsNullOrEmpty(senhaDoApp) || senhaDoApp.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Bancos__Oracle__Senha vazia ou com aspas duplas.");
        }

        var passos = new List<string>();
        using var con = new OracleConnection(conexaoAdmin);
        con.Open();

        var criouDono = Executar(
            con, $"CREATE USER {Esquemas.OracleNet} NO AUTHENTICATION DEFAULT TABLESPACE users QUOTA 2G ON users");
        passos.Add(criouDono ? $"schema {Esquemas.OracleNet} criado (sem login)" : $"schema {Esquemas.OracleNet} já existia");

        var criouApp = Executar(
            con,
            $"CREATE USER {Esquemas.UsuarioAppOracle} IDENTIFIED BY \"{senhaDoApp}\" DEFAULT TABLESPACE users QUOTA 0 ON users");
        if (!criouApp)
        {
            Executar(con, $"ALTER USER {Esquemas.UsuarioAppOracle} IDENTIFIED BY \"{senhaDoApp}\"");
        }

        Executar(con, $"GRANT CREATE SESSION TO {Esquemas.UsuarioAppOracle}");
        passos.Add(criouApp
            ? $"usuário {Esquemas.UsuarioAppOracle} criado"
            : $"usuário {Esquemas.UsuarioAppOracle} já existia: senha sincronizada com o .env");

        foreach (var caminho in caminhosDosScripts)
        {
            var executados = 0;
            var existentes = 0;
            foreach (var comando in Separar(File.ReadAllLines(caminho)))
            {
                if (Executar(con, comando))
                {
                    executados++;
                }
                else
                {
                    existentes++;
                }
            }

            passos.Add($"{Path.GetFileName(caminho)}: {executados} comandos executados, {existentes} já existiam");
        }

        ConferirProcedures(con, passos);
        return passos;
    }

    /// <summary>
    /// CREATE PROCEDURE com erro de compilação não lança exceção (o objeto fica INVALID): confere o status e, se
    /// houver erro, interrompe a migração mostrando o que o Oracle reclamou.
    /// </summary>
    private static void ConferirProcedures(OracleConnection con, List<string> passos)
    {
        using var consulta = con.CreateCommand();
        consulta.BindByName = true;
        consulta.CommandText =
            "select object_name, status from all_objects where owner = :dono and object_type = 'PROCEDURE' order by 1";
        consulta.Parameters.Add("dono", Esquemas.OracleNet);
        var invalidas = new List<string>();
        using (var leitor = consulta.ExecuteReader())
        {
            while (leitor.Read())
            {
                var nome = leitor.GetString(0);
                var status = leitor.GetString(1);
                passos.Add($"procedure {nome}: {status}");
                if (status != "VALID")
                {
                    invalidas.Add(nome);
                }
            }
        }

        if (invalidas.Count == 0)
        {
            return;
        }

        using var erros = con.CreateCommand();
        erros.BindByName = true;
        erros.CommandText = "select name, line, text from all_errors where owner = :dono order by name, sequence";
        erros.Parameters.Add("dono", Esquemas.OracleNet);
        var detalhes = new List<string>();
        using (var leitor = erros.ExecuteReader())
        {
            while (leitor.Read())
            {
                detalhes.Add($"{leitor.GetString(0)} linha {leitor.GetInt32(1)}: {leitor.GetString(2).Trim()}");
            }
        }

        throw new InvalidOperationException(
            $"Procedure(s) inválida(s) no Oracle: {string.Join(", ", invalidas)}. {string.Join(" | ", detalhes)}");
    }

    /// <summary>Separa o script nas linhas que contêm só "/" (formato SQL*Plus) e descarta comentários.</summary>
    public static IEnumerable<string> Separar(IEnumerable<string> linhas)
    {
        ArgumentNullException.ThrowIfNull(linhas);
        var atual = new List<string>();
        foreach (var linha in linhas)
        {
            if (linha.Trim() == "/")
            {
                var sql = string.Join('\n', atual).Trim();
                atual.Clear();
                if (sql.Length > 0)
                {
                    yield return sql;
                }
            }
            else if (!linha.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                atual.Add(linha);
            }
        }
    }

    private static bool Executar(OracleConnection con, string sql)
    {
        using var comando = con.CreateCommand();
#pragma warning disable CA2100 // script versionado e nomes constantes; a senha vem do .env e não aceita aspas
        comando.CommandText = sql;
#pragma warning restore CA2100
        try
        {
            comando.ExecuteNonQuery();
            return true;
        }
        catch (OracleException e) when (JaExiste.Contains(e.Number))
        {
            return false;
        }
    }
}
