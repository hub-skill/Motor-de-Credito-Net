namespace MotorCredito.Core.Cgu;

public enum TipoColuna
{
    Texto,
    TextoFixo,
    TextoLongo,
    Inteiro,
    Numerico,
    Data,
}

public enum FonteDoValor
{
    Csv,
    NomeDoArquivo,
    MarcaDeOrigem,
}

/// <summary>Uma coluna da tabela: de qual coluna do CSV vem, o tipo e o tamanho máximo.</summary>
public sealed record ColunaLayout(
    string Nome,
    TipoColuna Tipo,
    string? ColunaCsv = null,
    int? TamanhoMaximo = null,
    Func<string, object?>? Conversor = null,
    bool OpcionalNoCsv = false,
    FonteDoValor Fonte = FonteDoValor.Csv)
{
    public object? Converter(string bruto) => Conversor is not null
        ? Conversor(bruto)
        : Tipo switch
        {
            TipoColuna.Inteiro => Conversores.Inteiro(bruto),
            TipoColuna.Numerico => Conversores.DecimalBr(bruto),
            TipoColuna.Data => Conversores.DataBr(bruto),
            _ => Conversores.Texto(bruto),
        };
}

public sealed record LayoutTabela(string Tabela, IReadOnlyList<ColunaLayout> Colunas, IReadOnlyList<string> Chave);

/// <summary>Layout das tabelas sancoes (CEIS + CNEP) e peps, na ordem das colunas gravadas.</summary>
public static class LayoutsCgu
{
    /// <summary>Marca gravada em origem_carga em todo registro carregado por esta aplicação .NET.</summary>
    public const string MarcaDeOrigem = "DOTNET";

    private static readonly ColunaLayout ArquivoOrigem =
        new("arquivo_origem", TipoColuna.Texto, TamanhoMaximo: 60, Fonte: FonteDoValor.NomeDoArquivo);

    private static readonly ColunaLayout OrigemCarga =
        new("origem_carga", TipoColuna.Texto, TamanhoMaximo: 10, Fonte: FonteDoValor.MarcaDeOrigem);

    public static LayoutTabela Sancoes { get; } = new(
        "sancoes",
        [
            new("cadastro", TipoColuna.Texto, "CADASTRO", 10),
            new("codigo_sancao", TipoColuna.Inteiro, "CÓDIGO DA SANÇÃO"),
            new("tipo_pessoa", TipoColuna.TextoFixo, "TIPO DE PESSOA", 1),
            new("documento", TipoColuna.Texto, "CPF OU CNPJ DO SANCIONADO", 20),
            new("nome_sancionado", TipoColuna.Texto, "NOME DO SANCIONADO", 200),
            new("nome_informado_orgao", TipoColuna.Texto, "NOME INFORMADO PELO ÓRGÃO SANCIONADOR", 200),
            new("razao_social", TipoColuna.Texto, "RAZÃO SOCIAL - CADASTRO RECEITA", 200),
            new("nome_fantasia", TipoColuna.Texto, "NOME FANTASIA - CADASTRO RECEITA", 100),
            new("numero_processo", TipoColuna.Texto, "NÚMERO DO PROCESSO", 80),
            new("categoria", TipoColuna.Texto, "CATEGORIA DA SANÇÃO", 120),
            // "VALOR DA MULTA" só existe no CNEP; no CEIS a coluna fica nula.
            new("valor_multa", TipoColuna.Numerico, "VALOR DA MULTA", OpcionalNoCsv: true),
            new("data_inicio", TipoColuna.Data, "DATA INÍCIO SANÇÃO"),
            new("data_fim", TipoColuna.Data, "DATA FINAL SANÇÃO"),
            new("data_publicacao", TipoColuna.Data, "DATA PUBLICAÇÃO"),
            new("publicacao", TipoColuna.Texto, "PUBLICAÇÃO", 150),
            new("detalhamento_publicacao", TipoColuna.TextoLongo, "DETALHAMENTO DO MEIO DE PUBLICAÇÃO"),
            new("data_transito_julgado", TipoColuna.Data, "DATA DO TRÂNSITO EM JULGADO"),
            new("abrangencia", TipoColuna.Texto, "ABRAGÊNCIA DA SANÇÃO", 100),
            new("orgao_sancionador", TipoColuna.Texto, "ÓRGÃO SANCIONADOR", 300),
            new("uf_orgao", TipoColuna.TextoFixo, "UF ÓRGÃO SANCIONADOR", 2),
            new("esfera_orgao", TipoColuna.Texto, "ESFERA ÓRGÃO SANCIONADOR", 20),
            new("fundamentacao_legal", TipoColuna.TextoLongo, "FUNDAMENTAÇÃO LEGAL"),
            new("data_origem", TipoColuna.Data, "DATA ORIGEM INFORMAÇÃO"),
            new("origem_informacao", TipoColuna.Texto, "ORIGEM INFORMAÇÕES", 150),
            new("observacoes", TipoColuna.TextoLongo, "OBSERVAÇÕES"),
            ArquivoOrigem,
            OrigemCarga,
        ],
        ["cadastro", "codigo_sancao"]);

    public static LayoutTabela Peps { get; } = new(
        "peps",
        [
            new("cpf_mascarado", TipoColuna.Texto, "CPF", 20),
            new("cpf_miolo", TipoColuna.Texto, "CPF", 20, Conversores.MioloCpf),
            new("nome", TipoColuna.Texto, "Nome_PEP", 100),
            new("sigla_funcao", TipoColuna.Texto, "Sigla_Função", 10),
            new("descricao_funcao", TipoColuna.Texto, "Descrição_Função", 100),
            new("nivel_funcao", TipoColuna.Texto, "Nível_Função", 10),
            new("nome_orgao", TipoColuna.Texto, "Nome_Órgão", 100),
            new("data_inicio_exercicio", TipoColuna.Data, "Data_Início_Exercício"),
            new("data_fim_exercicio", TipoColuna.Data, "Data_Fim_Exercício"),
            new("data_fim_carencia", TipoColuna.Data, "Data_Fim_Carência"),
            ArquivoOrigem,
            OrigemCarga,
        ],
        ["cpf_mascarado", "sigla_funcao", "data_inicio_exercicio"]);
}
