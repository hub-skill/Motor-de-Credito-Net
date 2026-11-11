using MotorCredito.Core.Documentos;
using MotorCredito.Core.Listas;
using MotorCredito.Core.Regras;
using Xunit;

namespace MotorCredito.UnitTests;

public sealed class MotorDeTriagemTests
{
    private static readonly DateOnly Hoje = new(2026, 11, 10);

    private static Sancao NovaSancao(DateOnly? inicio = null, DateOnly? fim = null, string cadastro = "CEIS", bool semInicio = false) =>
        new(cadastro, 1, "Suspensão", "Órgão X", "SP", semInicio ? null : inicio ?? new DateOnly(2025, 1, 1), fim, "123/2025");

    private static RegistroPep NovoPep(string nome = "MARIA DA SILVA", DateOnly? fimExercicio = null, DateOnly? fimCarencia = null) =>
        new(nome, "VEREADOR", "CAMARA", new DateOnly(2021, 1, 1), fimExercicio, fimCarencia);

    [Fact]
    public void Regras_de_sancao_vigente()
    {
        Assert.True(MotorDeTriagem.SancaoVigente(NovaSancao(), Hoje));                                  // sem prazo
        Assert.True(MotorDeTriagem.SancaoVigente(NovaSancao(fim: Hoje), Hoje));                         // termina hoje
        Assert.False(MotorDeTriagem.SancaoVigente(NovaSancao(fim: new DateOnly(2026, 11, 9)), Hoje));   // terminou ontem
        Assert.False(MotorDeTriagem.SancaoVigente(NovaSancao(inicio: new DateOnly(2026, 12, 1)), Hoje)); // não começou
        Assert.True(MotorDeTriagem.SancaoVigente(NovaSancao(semInicio: true), Hoje));                   // sem início
    }

    [Fact]
    public void Regras_de_pep_ativo()
    {
        Assert.True(MotorDeTriagem.PepAtivo(NovoPep(), Hoje));
        Assert.True(MotorDeTriagem.PepAtivo(NovoPep(fimExercicio: new DateOnly(2025, 1, 1), fimCarencia: new DateOnly(2030, 1, 1)), Hoje));
        Assert.False(MotorDeTriagem.PepAtivo(NovoPep(fimExercicio: new DateOnly(2020, 1, 1), fimCarencia: new DateOnly(2025, 1, 1)), Hoje));
        Assert.False(MotorDeTriagem.PepAtivo(NovoPep(fimExercicio: new DateOnly(2020, 1, 1)), Hoje));
    }

    [Theory]
    [InlineData("  José   da  Conceição ", "JOSE DA CONCEICAO")]
    [InlineData("maria\tda silva", "MARIA DA SILVA")]
    public void Normaliza_nome(string entrada, string esperado) =>
        Assert.Equal(esperado, MotorDeTriagem.NormalizarNome(entrada));

    [Fact]
    public void Sem_restricoes_libera()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cpf, "Fulano", [], [], Hoje);
        Assert.Equal(Decisao.Liberar, r.Decisao);
        Assert.Equal(["OK"], r.Motivos.Select(m => m.Codigo));
        Assert.True(r.PepVerificado);
        Assert.False(r.PepAtivo);
    }

    [Fact]
    public void Sancao_vigente_bloqueia()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cnpj, null, [NovaSancao(), NovaSancao(cadastro: "CNEP")], [], Hoje);
        Assert.Equal(Decisao.Bloquear, r.Decisao);
        Assert.Equal("S01", r.Motivos[0].Codigo);
        Assert.Contains("CEIS, CNEP", r.Motivos[0].Texto, StringComparison.Ordinal);
    }

    [Fact]
    public void Sancao_encerrada_so_informa()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cnpj, null, [NovaSancao(fim: new DateOnly(2024, 1, 1))], [], Hoje);
        Assert.Equal(Decisao.Liberar, r.Decisao);
        Assert.Equal("S02", r.Motivos[0].Codigo);
    }

    [Fact]
    public void Pep_ativo_manda_para_revisao_mesmo_com_acento_e_minusculas()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cpf, "Maria da Sílva", [], [NovoPep()], Hoje);
        Assert.Equal(Decisao.Revisar, r.Decisao);
        Assert.True(r.PepAtivo);
        Assert.Equal("P01", r.Motivos[0].Codigo);
        Assert.Contains("VEREADOR", r.Motivos[0].Texto, StringComparison.Ordinal);
    }

    [Fact]
    public void Mesmo_miolo_com_outro_nome_nao_e_pep()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cpf, "João Souza", [], [NovoPep()], Hoje);
        Assert.Equal(Decisao.Liberar, r.Decisao);
        Assert.Empty(r.Peps);
    }

    [Fact]
    public void Pep_com_carencia_encerrada_so_informa()
    {
        var antigo = NovoPep(fimExercicio: new DateOnly(2020, 1, 1), fimCarencia: new DateOnly(2021, 1, 1));
        var r = MotorDeTriagem.Triar(TipoDocumento.Cpf, "Maria da Silva", [], [antigo], Hoje);
        Assert.Equal(Decisao.Liberar, r.Decisao);
        Assert.Equal("P02", r.Motivos[0].Codigo);
    }

    [Fact]
    public void Cpf_sem_nome_nao_verifica_pep()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cpf, null, [], [], Hoje);
        Assert.False(r.PepVerificado);
        Assert.Equal("P03", r.Motivos[0].Codigo);
    }

    [Fact]
    public void Bloqueio_prevalece_sobre_revisao()
    {
        var r = MotorDeTriagem.Triar(TipoDocumento.Cpf, "Maria da Silva", [NovaSancao()], [NovoPep()], Hoje);
        Assert.Equal(Decisao.Bloquear, r.Decisao);
        Assert.Equal(["P01", "S01"], r.Motivos.Select(m => m.Codigo).Order(StringComparer.Ordinal));
    }
}
