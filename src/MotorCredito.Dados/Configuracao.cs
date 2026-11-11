using System.Globalization;

namespace MotorCredito.Dados;

/// <summary>Onde ficam as tabelas. As do Python (credito / MOTOR_CREDITO) nunca são alteradas pelo .NET.</summary>
public static class Esquemas
{
    public const string PostgresNet = "credito_net";
    public const string PostgresPython = "credito";
    public const string OracleNet = "MOTOR_CREDITO_NET";
    public const string OraclePython = "MOTOR_CREDITO";
    public const string UsuarioAppPostgres = "motor_app_net";
    public const string UsuarioAppOracle = "MOTOR_APP_NET";
}

public sealed class PostgresSettings
{
    public string Host { get; init; } = "127.0.0.1";

    public int Porta { get; init; } = 5432;

    public string Banco { get; init; } = "motor_credito";

    public string Usuario { get; init; } = Esquemas.UsuarioAppPostgres;

    public string Senha { get; init; } = string.Empty;

    public string SslMode { get; init; } = "Prefer";

    public int TimeoutSegundos { get; init; } = 5;
}

public sealed class OracleSettings
{
    public string DataSource { get; init; } = "localhost:1521/XEPDB1";

    public string Usuario { get; init; } = Esquemas.UsuarioAppOracle;

    public string Senha { get; init; } = string.Empty;

    public int TimeoutSegundos { get; init; } = 5;
}

public sealed class BancosSettings
{
    public const string SectionName = "Bancos";

    /// <summary>postgres ou oracle: as tabelas existem nos dois.</summary>
    public string Provedor { get; init; } = "postgres";

    public PostgresSettings Postgres { get; init; } = new();

    public OracleSettings Oracle { get; init; } = new();

    public bool UsaOracle => string.Equals(Provedor, "oracle", StringComparison.OrdinalIgnoreCase);

    /// <summary>Monta a configuração a partir das variáveis de ambiente (ferramentas de console e testes).</summary>
    public static BancosSettings DoAmbiente() => new()
    {
        Provedor = Ambiente.Obter("Bancos__Provedor", "postgres"),
        Postgres = new PostgresSettings
        {
            Host = Ambiente.Obter("Bancos__Postgres__Host", "127.0.0.1"),
            Porta = int.Parse(Ambiente.Obter("Bancos__Postgres__Porta", "5432"), CultureInfo.InvariantCulture),
            Banco = Ambiente.Obter("Bancos__Postgres__Banco", "motor_credito"),
            Usuario = Ambiente.Obter("Bancos__Postgres__Usuario", Esquemas.UsuarioAppPostgres),
            Senha = Ambiente.Obter("Bancos__Postgres__Senha"),
            SslMode = Ambiente.Obter("Bancos__Postgres__SslMode", "Prefer"),
        },
        Oracle = new OracleSettings
        {
            DataSource = Ambiente.Obter("Bancos__Oracle__DataSource", "localhost:1521/XEPDB1"),
            Usuario = Ambiente.Obter("Bancos__Oracle__Usuario", Esquemas.UsuarioAppOracle),
            Senha = Ambiente.Obter("Bancos__Oracle__Senha"),
        },
    };
}

/// <summary>Administradores: usados só pelas ferramentas (migrar e comparar). A API não usa.</summary>
public sealed record AdminSettings(string PostgresUsuario, string PostgresSenha, string OracleUsuario, string OracleSenha)
{
    public static AdminSettings DoAmbiente() => new(
        Ambiente.Obter("Admin__Postgres__Usuario", "postgres"),
        Ambiente.Obter("Admin__Postgres__Senha"),
        Ambiente.Obter("Admin__Oracle__Usuario", "SYSTEM"),
        Ambiente.Obter("Admin__Oracle__Senha"));
}

public static class Ambiente
{
    public static string Obter(string chave, string padrao = "") =>
        Environment.GetEnvironmentVariable(chave) is { Length: > 0 } valor ? valor : padrao;
}
