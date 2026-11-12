using System.Data.Common;
using System.Diagnostics;
using MotorCredito.Core.Cgu;
using MotorCredito.Core.Configuracao;
using MotorCredito.Dados;
using MotorCredito.Dados.BancoOracle;
using MotorCredito.Dados.BancoPostgres;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace MotorCredito.Carga;

/// <summary>Comandos de banco: migrar (criar schema), carregar (listas da CGU) e comparar (Python x .NET).</summary>
internal static partial class Comandos
{
    private static readonly string[] Tipos = ["ceis", "cnep", "pep"];
    private static readonly LayoutTabela[] Tabelas = [LayoutsCgu.Sancoes, LayoutsCgu.Peps];

    public static int Ajuda()
    {
        Console.WriteLine("""
            Motor de Crédito .NET - ferramenta
              migrar      cria usuários, schema e tabelas (.NET) nos bancos
              carregar    baixa CEIS, CNEP e PEP da CGU e grava nos bancos (marca DOTNET)
                          --pasta <dir>   usa CSVs já baixados em vez de baixar
              comparar    compara, campo a campo, as tabelas do Python com as do .NET
              testar-api  chama a API em execução e confere as respostas
                          --url <endereço> (padrão http://127.0.0.1:5080)
            Opção comum: --banco postgres|oracle|todos (padrão: todos)
            """);
        return 0;
    }

    /// <summary>Scripts do schema, em ordem: tabelas das listas, movimentação e log de erros (depende da movimentação).</summary>
    public static string[] ScriptsDeMigracao(string pastaDosScripts, string banco) =>
        [.. NomesDosScripts.Select(arquivo => Path.Combine(pastaDosScripts, banco, arquivo))];

    private static readonly string[] NomesDosScripts = ["01_schema_net.sql", "02_movimentacao.sql", "03_log_erros.sql"];

    public static async Task<int> MigrarAsync(Opcoes opcoes, CancellationToken cancellationToken)
    {
        var bancos = BancosSettings.DoAmbiente();
        var admin = AdminSettings.DoAmbiente();
        var scripts = Path.Combine(ArquivoEnv.RaizDoRepositorio(), "banco");

        if (opcoes.Inclui("postgres"))
        {
            Console.WriteLine("=== PostgreSQL");
            var conexaoAdmin = ConexoesPostgres.Montar(
                bancos.Postgres, admin.PostgresUsuario, admin.PostgresSenha, somenteLeitura: false, "motor-credito-net-migracao");
            var passos = await MigradorPostgres.MigrarAsync(
                conexaoAdmin, bancos.Postgres.Senha, ScriptsDeMigracao(scripts, "postgres"), cancellationToken);
            foreach (var passo in passos)
            {
                Console.WriteLine($"[ok] {passo}");
            }

            await using var app = new NpgsqlConnection(ConexaoDoAppPostgres(bancos, "motor-credito-net-migracao"));
            await app.OpenAsync(cancellationToken);
            foreach (var tabela in Tabelas)
            {
                var linhas = await ContarAsync(app, $"select count(*) from {Esquemas.PostgresNet}.{tabela.Tabela}", cancellationToken);
                Console.WriteLine($"[ok] {bancos.Postgres.Usuario} acessa {Esquemas.PostgresNet}.{tabela.Tabela}: {linhas} linhas");
            }
        }

        if (opcoes.Inclui("oracle"))
        {
            Console.WriteLine("=== Oracle");
            var conexaoAdmin = ConexoesOracle.Montar(bancos.Oracle, admin.OracleUsuario, admin.OracleSenha);
            var passos = MigradorOracle.Migrar(
                conexaoAdmin, bancos.Oracle.Senha, ScriptsDeMigracao(scripts, "oracle"));
            foreach (var passo in passos)
            {
                Console.WriteLine($"[ok] {passo}");
            }

            using var app = new OracleConnection(ConexoesOracle.Montar(bancos.Oracle, bancos.Oracle.Usuario, bancos.Oracle.Senha));
            await app.OpenAsync(cancellationToken);
            foreach (var tabela in Tabelas)
            {
                // Sem o nome do schema: prova que os sinônimos funcionam.
                var linhas = await ContarAsync(app, $"select count(*) from {tabela.Tabela}", cancellationToken);
                Console.WriteLine($"[ok] {bancos.Oracle.Usuario} acessa {tabela.Tabela} pelo sinônimo: {linhas} linhas");
            }
        }

        return 0;
    }

    public static async Task<int> CarregarAsync(Opcoes opcoes, CancellationToken cancellationToken)
    {
        var bancos = BancosSettings.DoAmbiente();
        var arquivos = new Dictionary<string, string>(StringComparer.Ordinal);
        var pasta = opcoes.Valor("pasta");
        if (pasta is null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("motor-credito-net-carga/1.0");
            var baixador = new BaixadorCgu(http);
            var destino = Path.Combine(ArquivoEnv.RaizDoRepositorio(), "dados_cgu");
            var hoje = DateOnly.FromDateTime(DateTime.Now);
            foreach (var tipo in Tipos)
            {
                arquivos[tipo] = await baixador.BaixarAsync(tipo, destino, hoje, cancellationToken);
                Console.WriteLine($"[download] {tipo.ToUpperInvariant(),-4} -> {Path.GetFileName(arquivos[tipo])}");
            }
        }
        else
        {
            foreach (var tipo in Tipos)
            {
                var padrao = $"*_{tipo.ToUpperInvariant()}.csv";
                arquivos[tipo] = Directory.EnumerateFiles(pasta, padrao, SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal).LastOrDefault()
                    ?? throw new FileNotFoundException($"Nenhum {padrao} em {pasta}");
            }
        }

        var cargas = new List<CargaTabela>();
        foreach (var tipo in Tipos)
        {
            var layout = tipo == "pep" ? LayoutsCgu.Peps : LayoutsCgu.Sancoes;
            var linhas = LeitorDeArquivoCgu.Ler(arquivos[tipo], layout);
            cargas.Add(new CargaTabela(layout, tipo == "pep" ? null : tipo.ToUpperInvariant(), linhas));
            Console.WriteLine($"[leitura]  {Path.GetFileName(arquivos[tipo]),-22} {linhas.Count,7} linhas válidas");
        }

        if (opcoes.Inclui("postgres"))
        {
            var resultados = await CarregadorPostgres.CarregarAsync(
                ConexaoDoAppPostgres(bancos, "motor-credito-net-carga"), cargas, cancellationToken);
            Imprimir("postgres", resultados);
        }

        if (opcoes.Inclui("oracle"))
        {
            var resultados = CarregadorOracle.Carregar(
                ConexoesOracle.Montar(bancos.Oracle, bancos.Oracle.Usuario, bancos.Oracle.Senha), cargas);
            Imprimir("oracle", resultados);
        }

        Console.WriteLine($"[ok] carga concluída; registros marcados com origem_carga = {LayoutsCgu.MarcaDeOrigem}");
        return 0;
    }

    public static async Task<int> CompararAsync(Opcoes opcoes, CancellationToken cancellationToken)
    {
        var bancos = BancosSettings.DoAmbiente();
        var admin = AdminSettings.DoAmbiente();
        var todosIguais = true;

        if (opcoes.Inclui("postgres"))
        {
            await using var con = new NpgsqlConnection(ConexoesPostgres.Montar(
                bancos.Postgres, admin.PostgresUsuario, admin.PostgresSenha, somenteLeitura: false, "motor-credito-net-comparacao"));
            await con.OpenAsync(cancellationToken);
            foreach (var tabela in Tabelas)
            {
                var relogio = Stopwatch.StartNew();
                var resultado = await ComparadorDeCargas.CompararAsync(
                    con,
                    "postgres",
                    $"{Esquemas.PostgresPython}.{tabela.Tabela}",
                    $"{Esquemas.PostgresNet}.{tabela.Tabela}",
                    tabela,
                    preparar: null,
                    cancellationToken);
                todosIguais &= Imprimir(resultado, relogio.Elapsed);
            }
        }

        if (opcoes.Inclui("oracle"))
        {
            using var con = new OracleConnection(ConexoesOracle.Montar(bancos.Oracle, admin.OracleUsuario, admin.OracleSenha));
            await con.OpenAsync(cancellationToken);
            foreach (var tabela in Tabelas)
            {
                var relogio = Stopwatch.StartNew();
                var resultado = await ComparadorDeCargas.CompararAsync(
                    con,
                    "oracle",
                    $"{Esquemas.OraclePython}.{tabela.Tabela}",
                    $"{Esquemas.OracleNet}.{tabela.Tabela}",
                    tabela,
                    TrazerClobInteiro,
                    cancellationToken);
                todosIguais &= Imprimir(resultado, relogio.Elapsed);
            }
        }

        Console.WriteLine(todosIguais
            ? "\n[ok] as gravações do .NET são idênticas às do Python em todas as tabelas comparadas"
            : "\n[ATENÇÃO] há diferenças entre as gravações do Python e do .NET");
        return todosIguais ? 0 : 1;
    }

    private static string ConexaoDoAppPostgres(BancosSettings bancos, string aplicacao) => ConexoesPostgres.Montar(
        bancos.Postgres, bancos.Postgres.Usuario, bancos.Postgres.Senha, somenteLeitura: false, aplicacao);

    // Traz o CLOB inteiro junto com a linha, em vez de uma ida ao banco por campo.
    private static void TrazerClobInteiro(DbCommand comando)
    {
        if (comando is OracleCommand oracle)
        {
            oracle.InitialLOBFetchSize = -1;
        }
    }

    private static async Task<long> ContarAsync(DbConnection conexao, string sql, CancellationToken cancellationToken)
    {
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // nomes de tabela vêm de constantes internas
        comando.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToInt64(await comando.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Imprimir(string banco, IReadOnlyList<ResultadoCarga> resultados)
    {
        foreach (var resultado in resultados)
        {
            Console.WriteLine(
                $"[{banco,-8}] {resultado.Rotulo,-16} {resultado.Linhas,7} linhas em {resultado.Duracao.TotalSeconds,5:F1}s");
        }
    }

    private static bool Imprimir(ResultadoComparacao r, TimeSpan duracao)
    {
        Console.WriteLine($"\n=== {r.Banco} / {r.Tabela}  ({duracao.TotalSeconds:F1}s)");
        Console.WriteLine($"  antigos (Python): {r.Antigos} | novos (.NET): {r.Novos} | idênticos campo a campo: {r.Identicos}");
        Console.WriteLine($"  só no antigo: {r.SoNoAntigo} | só no novo: {r.SoNoNovo} | novos com a marca DOTNET: {r.NovosMarcados} de {r.Novos}");
        Console.WriteLine($"  arquivos antigos: {string.Join(", ", r.ArquivosAntigos)}");
        Console.WriteLine($"  arquivos novos:   {string.Join(", ", r.ArquivosNovos)}");
        if (!r.MesmosArquivos)
        {
            Console.WriteLine("  AVISO: as cargas vieram de arquivos diferentes; diferenças podem ser do próprio arquivo da CGU.");
        }

        foreach (var exemplo in r.Exemplos)
        {
            Console.WriteLine($"    {exemplo}");
        }

        Console.WriteLine(r.Igual ? "  RESULTADO: IGUAL" : "  RESULTADO: DIFERENTE");
        return r.Igual;
    }
}
