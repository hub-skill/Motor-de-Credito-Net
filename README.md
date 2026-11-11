# Motor de Crédito .NET – Triagem (sanções e PEP)

API em ASP.NET Core (.NET 10) que recebe um CPF ou CNPJ e consulta as listas da CGU gravadas no banco:
**CEIS e CNEP** (sanções) e **PEP** (pessoas expostas politicamente). Devolve `liberar`, `revisar`
ou `bloquear`, sempre com os motivos. Gerada a partir do template .NET da skill `criar-api`.

É a versão .NET do motor feito em Python (repositório `Motor-de-Credito`). Os dois projetos são
**independentes**: o .NET tem schema, usuários e tabelas próprios e nunca altera os do Python.

| Banco | Schema do .NET | Usuário da aplicação | Tabelas do Python (só leitura, na comparação) |
|---|---|---|---|
| PostgreSQL (`motor_credito`) | `credito_net` | `motor_app_net` (só lê e grava dados) | `credito` |
| Oracle (`XEPDB1`) | `MOTOR_CREDITO_NET` (sem login) | `MOTOR_APP_NET` (sinônimos) | `MOTOR_CREDITO` |

Todo registro gravado pelo .NET leva a marca **`origem_carga = 'DOTNET'`**.

## Política de triagem (PROVISÓRIA – validar com risco/compliance)

| Situação | Resultado | Código |
|---|---|---|
| Sanção vigente no CEIS ou CNEP | `bloquear` | S01 |
| Só sanções encerradas | sem efeito (informativo) | S02 |
| PEP em exercício ou dentro da carência | `revisar` (diligência reforçada) | P01 |
| Foi PEP, carência encerrada | sem efeito (informativo) | P02 |
| CPF sem nome informado | PEP não verificado | P03 |

A CGU publica o CPF dos PEPs mascarado (`***.123.456-**`): a busca usa os 6 dígitos visíveis **e** o
nome (sem acentos, maiúsculas). Homônimos ainda são possíveis, por isso PEP vai para revisão humana.

## Estrutura

```
src/MotorCredito.Core    regras puras: CPF/CNPJ (inclusive alfanumérico), triagem, leitura dos CSVs da CGU, .env
src/MotorCredito.Dados   PostgreSQL (Npgsql) e Oracle (ODP.NET): leitura, carga em massa, migração, comparação
src/MotorCredito.Api     POST /api/v1/triagem, health checks, métricas, OpenAPI
src/MotorCredito.Carga   ferramenta: migrar, carregar, comparar, testar-api
banco/                   scripts SQL dos schemas .NET (PostgreSQL e Oracle)
tests/                   UnitTests, IntegrationTests (API em memória), BancosTests (bancos reais)
```

## Configuração

Tudo no `.env` da raiz (nunca versionado). As chaves seguem o padrão do .NET: `__` separa seções
(`Bancos__Postgres__Host` = `Bancos:Postgres:Host`). Variáveis de ambiente já definidas têm prioridade.

| Chave | Uso |
|---|---|
| `Api__ApiKey` | Chave enviada no header `X-API-Key` (mínimo 32 caracteres) |
| `Bancos__Provedor` | `postgres` ou `oracle` |
| `Bancos__Postgres__*`, `Bancos__Oracle__*` | Conexão com o usuário da aplicação .NET |
| `Admin__Postgres__*`, `Admin__Oracle__*` | Só para a ferramenta (`migrar` e `comparar`). A API não usa |

## Uso

```powershell
# 1) Criar usuários, schemas e tabelas (.NET) nos dois bancos
dotnet run --project src/MotorCredito.Carga -- migrar

# 2) Baixar CEIS/CNEP (diários) e PEP (mensal) da CGU e gravar nos dois bancos (marca DOTNET)
dotnet run --project src/MotorCredito.Carga -- carregar
#    ou usar CSVs já baixados:  -- carregar --pasta <pasta>

# 3) Comparar campo a campo com a carga do Python
dotnet run --project src/MotorCredito.Carga -- comparar

# 4) Subir a API e testá-la com chamadas HTTP reais
dotnet run --project src/MotorCredito.Api --urls http://127.0.0.1:5080
dotnet run --project src/MotorCredito.Carga -- testar-api --url http://127.0.0.1:5080
```

Todos os comandos aceitam `--banco postgres|oracle|todos` (padrão: `todos`).
Com `Api__EnableDocs=true`, a documentação fica em `/scalar` e `/openapi/v1.json`.

Exemplo de resposta (resumida), em snake_case como a API Python:
```json
{
  "documento": "19.182.685/0001-62", "tipo": "CNPJ", "resultado": "bloquear",
  "motivos": [{ "codigo": "S01", "texto": "Sanção vigente em CEIS (1 registro(s))." }],
  "sancoes": [{ "cadastro": "CEIS", "categoria": "Declaração de Inidoneidade sem prazo determinado",
                "data_inicio": "2016-06-28", "data_fim": null, "vigente": true }],
  "pep": { "verificado": false, "ativo": false, "funcoes": [] },
  "fonte": { "banco": "postgres", "arquivos": ["202609_PEP.csv", "20261110_CEIS.csv", "20261110_CNEP.csv"] }
}
```

Erros seguem o padrão Problem Details (RFC 9457), o mesmo do Motor Python: `application/problem+json` com exatamente
`title`, `status`, `code` e `request_id`. Códigos: 400 `requisicao_invalida`, 401 `http_401`, 404 `http_404`,
405 `http_405`, 413 `corpo_muito_grande`, 422 `entrada_invalida`, 429 `limite_excedido`,
500 `erro_interno`, 503 `banco_indisponivel`, 504 `timeout` / `banco_timeout`.

## Testes e verificações

```powershell
dotnet format MotorCredito.slnx --verify-no-changes
dotnet build MotorCredito.slnx                      # avisos são erros
dotnet test MotorCredito.slnx --filter "Categoria!=Bancos"   # sem banco (como no CI)
dotnet test MotorCredito.slnx                       # inclui PostgreSQL e Oracle reais
dotnet list MotorCredito.slnx package --vulnerable --include-transitive
```

Os testes de banco precisam do `.env` e dos CSVs da carga do Python
(`MOTOR_CSV_CGU`, padrão: `...\Python\APIs\Motor de credito\dados_cgu`).
