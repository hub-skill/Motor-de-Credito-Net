using MotorCredito.Core.Cgu;

namespace MotorCredito.Dados;

/// <summary>Linhas a gravar numa tabela. Com FiltroCadastro, substitui só as linhas daquele cadastro (CEIS ou CNEP).</summary>
public sealed record CargaTabela(LayoutTabela Layout, string? FiltroCadastro, IReadOnlyList<object?[]> Linhas)
{
    public string Rotulo => FiltroCadastro is null ? Layout.Tabela : $"{Layout.Tabela} ({FiltroCadastro})";
}

public sealed record ResultadoCarga(string Rotulo, int Linhas, TimeSpan Duracao);
