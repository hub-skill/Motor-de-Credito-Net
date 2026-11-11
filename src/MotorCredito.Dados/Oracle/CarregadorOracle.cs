using System.Diagnostics;
using MotorCredito.Core.Cgu;
using Oracle.ManagedDataAccess.Client;

namespace MotorCredito.Dados.BancoOracle;

/// <summary>
/// Grava as listas no Oracle (pelos sinônimos do MOTOR_APP_NET) com array binding, em lotes.
/// Um único commit no fim: se algo falhar, nada é gravado.
/// </summary>
public static class CarregadorOracle
{
    private const int TamanhoDoLote = 5000;

    public static IReadOnlyList<ResultadoCarga> Carregar(string conexao, IReadOnlyList<CargaTabela> cargas)
    {
        ArgumentNullException.ThrowIfNull(cargas);
        var resultados = new List<ResultadoCarga>();
        using var con = new OracleConnection(conexao);
        con.Open();
        using var transacao = con.BeginTransaction();

        foreach (var carga in cargas)
        {
            var relogio = Stopwatch.StartNew();
            // Nome da tabela vem de constante interna (layout), nunca de entrada externa.
            var tabela = carga.Layout.Tabela;
            using (var apagar = con.CreateCommand())
            {
                apagar.Transaction = transacao;
                apagar.BindByName = true;
#pragma warning disable CA2100 // SQL montado só com constantes internas; o valor vai por parâmetro
                apagar.CommandText = carga.FiltroCadastro is null
                    ? $"delete from {tabela}"
                    : $"delete from {tabela} where cadastro = :cadastro";
#pragma warning restore CA2100
                if (carga.FiltroCadastro is not null)
                {
                    apagar.Parameters.Add("cadastro", OracleDbType.Varchar2).Value = carga.FiltroCadastro;
                }

                apagar.ExecuteNonQuery();
            }

            var colunas = carga.Layout.Colunas;
            var nomes = string.Join(", ", colunas.Select(c => c.Nome));
            var binds = string.Join(", ", colunas.Select((_, i) => $":p{i}"));
            var inserir = $"insert into {tabela} ({nomes}) values ({binds})";

            for (var inicio = 0; inicio < carga.Linhas.Count; inicio += TamanhoDoLote)
            {
                var quantidade = Math.Min(TamanhoDoLote, carga.Linhas.Count - inicio);
                using var comando = con.CreateCommand();
                comando.Transaction = transacao;
                comando.BindByName = true;
#pragma warning disable CA2100 // SQL montado só com constantes internas; os valores vão por array binding
                comando.CommandText = inserir;
#pragma warning restore CA2100
                comando.ArrayBindCount = quantidade;
                for (var c = 0; c < colunas.Count; c++)
                {
                    var valores = new object[quantidade];
                    for (var l = 0; l < quantidade; l++)
                    {
                        valores[l] = ParaOracle(carga.Linhas[inicio + l][c]);
                    }

                    comando.Parameters.Add(new OracleParameter($"p{c}", TipoOracle(colunas[c].Tipo)) { Value = valores });
                }

                comando.ExecuteNonQuery();
            }

            resultados.Add(new ResultadoCarga(carga.Rotulo, carga.Linhas.Count, relogio.Elapsed));
        }

        transacao.Commit();
        return resultados;
    }

    private static object ParaOracle(object? valor) => valor switch
    {
        null => DBNull.Value,
        DateOnly data => data.ToDateTime(TimeOnly.MinValue),
        _ => valor,
    };

    private static OracleDbType TipoOracle(TipoColuna tipo) => tipo switch
    {
        TipoColuna.Inteiro => OracleDbType.Int64,
        TipoColuna.Numerico => OracleDbType.Decimal,
        TipoColuna.Data => OracleDbType.Date,
        // Textos longos passam de 4.000 bytes: precisam ir como CLOB.
        TipoColuna.TextoLongo => OracleDbType.Clob,
        _ => OracleDbType.Varchar2,
    };
}
