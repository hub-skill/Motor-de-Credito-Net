using Xunit;

namespace MotorCredito.BancosTests;

/// <summary>
/// Classes de teste que mexem na estrutura (migração: COMMENT, GRANT, CREATE OR REPLACE) ou nas linhas da
/// movimentação rodam uma de cada vez: o Oracle responde ORA-00054 (recurso ocupado) a um DDL numa tabela que outra
/// sessão está alterando. As demais classes só leem e seguem em paralelo.
/// </summary>
[CollectionDefinition(Nome, DisableParallelization = false)]
public sealed class ColecaoDoBanco
{
    public const string Nome = "Banco real: migração e movimentação";
}
