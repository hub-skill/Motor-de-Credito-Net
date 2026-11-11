using MotorCredito.Core.Cgu;
using MotorCredito.Dados.BancoOracle;
using Xunit;

namespace MotorCredito.UnitTests;

public sealed class CguTests
{
    [Fact]
    public void Csv_com_aspas_quebra_de_linha_e_crlf()
    {
        const string texto = "A;B;C\r\n1;\"x;y\";\"linha1\nlinha2\"\r\n\r\n2;\"diz \"\"oi\"\"\";\n3;;fim";
        var registros = LeitorCsv.Ler(new StringReader(texto)).ToList();

        Assert.Equal(4, registros.Count); // a linha vazia é ignorada
        Assert.Equal(["1", "x;y", "linha1\nlinha2"], registros[1]);
        Assert.Equal(["2", "diz \"oi\"", ""], registros[2]);
        Assert.Equal(["3", "", "fim"], registros[3]); // último registro sem quebra de linha
    }

    [Fact]
    public void Conversores()
    {
        Assert.Null(Core.Cgu.Conversores.Texto("   "));
        Assert.Equal("abc", Core.Cgu.Conversores.Texto("  abc "));
        Assert.Equal(new DateOnly(2016, 6, 28), Core.Cgu.Conversores.DataBr("28/06/2016"));
        Assert.Null(Core.Cgu.Conversores.DataBr("Não informada"));
        Assert.Null(Core.Cgu.Conversores.DataBr(""));
        Assert.Equal(813870.96m, Core.Cgu.Conversores.DecimalBr("813870,96"));
        Assert.Equal(1234567.5m, Core.Cgu.Conversores.DecimalBr("1.234.567,50"));
        Assert.Equal(79854L, Core.Cgu.Conversores.Inteiro(" 79854 "));
        Assert.Equal("123456", Core.Cgu.Conversores.MioloCpf("***.123.456-**"));
        Assert.Throws<FormatException>(() => Core.Cgu.Conversores.DataBr("31/02/2026"));
    }

    [Fact]
    public void Le_arquivo_ceis_sem_coluna_de_multa_e_marca_dotnet()
    {
        var csv = Cabecalho() + "\n"
            + "CEIS;79854;J;19182685000162;EMPRESA;EMPRESA;;;104/2014;Inidoneidade;28/06/2016;;28/06/2016;DOU;;;Sem Informação;Prefeitura;SC;MUNICIPAL;LEI 8666;29/06/2016;Prefeitura;\n";
        var linhas = LeitorDeArquivoCgu.Ler(new StringReader(csv), LayoutsCgu.Sancoes, "20261110_CEIS.csv");

        var linha = Assert.Single(linhas);
        var valores = LayoutsCgu.Sancoes.Colunas.Select(c => c.Nome).Zip(linha).ToDictionary(p => p.First, p => p.Second);
        Assert.Equal(79854L, valores["codigo_sancao"]);
        Assert.Equal("19182685000162", valores["documento"]);
        Assert.Null(valores["valor_multa"]);      // coluna opcional ausente no CEIS
        Assert.Null(valores["data_fim"]);         // vazia = sem prazo
        Assert.Equal(new DateOnly(2016, 6, 28), valores["data_inicio"]);
        Assert.Equal("20261110_CEIS.csv", valores["arquivo_origem"]);
        Assert.Equal("DOTNET", valores["origem_carga"]);
    }

    [Fact]
    public void Rejeita_layout_sem_coluna_obrigatoria()
    {
        var erro = Assert.Throws<ArquivoCguInvalidoException>(() =>
            LeitorDeArquivoCgu.Ler(new StringReader("CADASTRO;OUTRA\nCEIS;x\n"), LayoutsCgu.Sancoes, "x.csv"));
        Assert.Contains("colunas ausentes", erro.Problemas[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Rejeita_valor_maior_que_a_coluna_e_data_invalida()
    {
        var csv = "CPF;Nome_PEP;Sigla_Função;Descrição_Função;Nível_Função;Nome_Órgão;Data_Início_Exercício;Data_Fim_Exercício;Data_Fim_Carência\n"
            + $"***.123.456-**;{new string('X', 101)};VEREAD;VEREADOR;;CAMARA;99/99/2021;Não informada;Não informada\n";
        var erro = Assert.Throws<ArquivoCguInvalidoException>(() => LeitorDeArquivoCgu.Ler(new StringReader(csv), LayoutsCgu.Peps, "p.csv"));
        Assert.Contains(erro.Problemas, p => p.Contains("nome: 101 caracteres", StringComparison.Ordinal));
        Assert.Contains(erro.Problemas, p => p.Contains("data_inicio_exercicio: valor inválido", StringComparison.Ordinal));
    }

    [Fact]
    public void Separa_script_oracle_por_barra_e_ignora_comentarios()
    {
        string[] linhas = ["-- comentário", "CREATE TABLE t (x NUMBER)", "/", "", "/", "GRANT SELECT ON t TO u", "/"];
        Assert.Equal(["CREATE TABLE t (x NUMBER)", "GRANT SELECT ON t TO u"], MigradorOracle.Separar(linhas));
    }

    private static string Cabecalho() => string.Join(';',
        "CADASTRO", "CÓDIGO DA SANÇÃO", "TIPO DE PESSOA", "CPF OU CNPJ DO SANCIONADO", "NOME DO SANCIONADO",
        "NOME INFORMADO PELO ÓRGÃO SANCIONADOR", "RAZÃO SOCIAL - CADASTRO RECEITA", "NOME FANTASIA - CADASTRO RECEITA",
        "NÚMERO DO PROCESSO", "CATEGORIA DA SANÇÃO", "DATA INÍCIO SANÇÃO", "DATA FINAL SANÇÃO", "DATA PUBLICAÇÃO",
        "PUBLICAÇÃO", "DETALHAMENTO DO MEIO DE PUBLICAÇÃO", "DATA DO TRÂNSITO EM JULGADO", "ABRAGÊNCIA DA SANÇÃO",
        "ÓRGÃO SANCIONADOR", "UF ÓRGÃO SANCIONADOR", "ESFERA ÓRGÃO SANCIONADOR", "FUNDAMENTAÇÃO LEGAL",
        "DATA ORIGEM INFORMAÇÃO", "ORIGEM INFORMAÇÕES", "OBSERVAÇÕES");
}
