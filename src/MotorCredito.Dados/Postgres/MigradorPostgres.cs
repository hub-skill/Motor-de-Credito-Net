using Npgsql;

namespace MotorCredito.Dados.BancoPostgres;

/// <summary>
/// Cria o usuário motor_app_net e executa os scripts do schema credito_net, em ordem. Roda como administrador.
/// Pode ser executado de novo: o que já existe é mantido e a senha é sincronizada com o .env.
/// </summary>
public static class MigradorPostgres
{
    public static async Task<IReadOnlyList<string>> MigrarAsync(
        string conexaoAdmin, string senhaDoApp, IReadOnlyList<string> caminhosDosScripts, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(senhaDoApp) || senhaDoApp.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Bancos__Postgres__Senha vazia ou com barra invertida.");
        }

        var passos = new List<string>();
        await using var con = new NpgsqlConnection(conexaoAdmin);
        await con.OpenAsync(cancellationToken).ConfigureAwait(false);

        bool existe;
        await using (var consulta = new NpgsqlCommand("select 1 from pg_roles where rolname = $1", con))
        {
            consulta.Parameters.Add(new NpgsqlParameter { Value = Esquemas.UsuarioAppPostgres });
            existe = await consulta.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        // CREATE/ALTER ROLE não aceita parâmetro: a senha vai como literal, com as aspas escapadas.
        var senha = senhaDoApp.Replace("'", "''", StringComparison.Ordinal);
        var papel = existe
            ? $"alter role {Esquemas.UsuarioAppPostgres} with login password '{senha}'"
            : $"create role {Esquemas.UsuarioAppPostgres} login nosuperuser nocreatedb nocreaterole password '{senha}'";
#pragma warning disable CA2100 // nome do papel é constante interna; a senha vem do .env e é escapada
        await using (var comando = new NpgsqlCommand(papel, con))
#pragma warning restore CA2100
        {
            await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        passos.Add(existe
            ? $"usuário {Esquemas.UsuarioAppPostgres} já existia: senha sincronizada com o .env"
            : $"usuário {Esquemas.UsuarioAppPostgres} criado");

        foreach (var caminho in caminhosDosScripts)
        {
            var script = await File.ReadAllTextAsync(caminho, cancellationToken).ConfigureAwait(false);
#pragma warning disable CA2100 // script versionado no repositório
            await using (var comando = new NpgsqlCommand(script, con))
#pragma warning restore CA2100
            {
                await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            passos.Add($"{Path.GetFileName(caminho)} executado");
        }

        return passos;
    }
}
