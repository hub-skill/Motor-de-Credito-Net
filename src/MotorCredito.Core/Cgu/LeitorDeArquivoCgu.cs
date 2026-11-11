using System.Text;

namespace MotorCredito.Core.Cgu;

public sealed class ArquivoCguInvalidoException(string arquivo, IReadOnlyList<string> problemas)
    : Exception($"{arquivo}: {problemas.Count} problema(s); nada será gravado.")
{
    public string Arquivo { get; } = arquivo;

    public IReadOnlyList<string> Problemas { get; } = problemas;
}

/// <summary>Lê um CSV da CGU (latin-1, ';'), converte os valores e confere os tamanhos.</summary>
public static class LeitorDeArquivoCgu
{
    public static IReadOnlyList<object?[]> Ler(string caminho, LayoutTabela layout)
    {
        // Os arquivos da CGU vêm em ISO-8859-1 (latin-1), não em UTF-8.
        using var leitor = new StreamReader(caminho, Encoding.Latin1);
        return Ler(leitor, layout, Path.GetFileName(caminho));
    }

    public static IReadOnlyList<object?[]> Ler(TextReader leitor, LayoutTabela layout, string nomeDoArquivo)
    {
        ArgumentNullException.ThrowIfNull(layout);
        using var registros = LeitorCsv.Ler(leitor).GetEnumerator();
        if (!registros.MoveNext())
        {
            throw new ArquivoCguInvalidoException(nomeDoArquivo, ["arquivo vazio"]);
        }

        var cabecalho = registros.Current.ToArray();
        var colunas = layout.Colunas;
        var indices = new int[colunas.Count];
        var ausentes = new List<string>();
        for (var i = 0; i < colunas.Count; i++)
        {
            indices[i] = -1;
            if (colunas[i].Fonte != FonteDoValor.Csv)
            {
                continue;
            }

            indices[i] = Array.LastIndexOf(cabecalho, colunas[i].ColunaCsv);
            if (indices[i] < 0 && !colunas[i].OpcionalNoCsv)
            {
                ausentes.Add(colunas[i].ColunaCsv!);
            }
        }

        if (ausentes.Count > 0)
        {
            throw new ArquivoCguInvalidoException(
                nomeDoArquivo, [$"colunas ausentes no CSV (o layout mudou?): {string.Join(", ", ausentes)}"]);
        }

        var linhas = new List<object?[]>();
        var problemas = new List<string>();
        var numero = 1;
        while (registros.MoveNext())
        {
            numero++;
            var registro = registros.Current;
            var valores = new object?[colunas.Count];
            for (var i = 0; i < colunas.Count; i++)
            {
                var coluna = colunas[i];
                if (coluna.Fonte == FonteDoValor.NomeDoArquivo)
                {
                    valores[i] = nomeDoArquivo;
                }
                else if (coluna.Fonte == FonteDoValor.MarcaDeOrigem)
                {
                    valores[i] = LayoutsCgu.MarcaDeOrigem;
                }
                else
                {
                    var bruto = indices[i] >= 0 && indices[i] < registro.Count ? registro[indices[i]] : string.Empty;
                    try
                    {
                        valores[i] = coluna.Converter(bruto);
                    }
                    catch (Exception e) when (e is FormatException or OverflowException)
                    {
                        problemas.Add($"linha {numero}, {coluna.Nome}: valor inválido");
                    }
                }

                if (coluna.TamanhoMaximo is int maximo && valores[i] is string texto && texto.Length > maximo)
                {
                    problemas.Add($"linha {numero}, {coluna.Nome}: {texto.Length} caracteres (máximo {maximo})");
                }
            }

            linhas.Add(valores);
        }

        if (problemas.Count > 0)
        {
            throw new ArquivoCguInvalidoException(nomeDoArquivo, problemas);
        }

        return linhas;
    }
}
