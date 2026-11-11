# Motor de Crédito .NET – Documentação de uso

Versão 0.1 · 11/11/2026

## Sumário

1. [Para que serve](#1-para-que-serve)
2. [Como funciona](#2-como-funciona)
3. [Começando em 5 minutos](#3-começando-em-5-minutos)
4. [Referência da API](#4-referência-da-api)
5. [Regras de decisão](#5-regras-de-decisão)
6. [Erros](#6-erros)
7. [Exemplos de integração](#7-exemplos-de-integração)
8. [Operação: ferramenta de carga](#8-operação-ferramenta-de-carga)
9. [Configuração](#9-configuração)
10. [Segurança e LGPD](#10-segurança-e-lgpd)
11. [Limitações](#11-limitações)
12. [Solução de problemas](#12-solução-de-problemas)

---

## 1. Para que serve

A API verifica um **CPF ou CNPJ** nas listas públicas da Controladoria-Geral da União (CGU) e responde, em
milissegundos, se o documento pode seguir no processo de crédito:

| Resultado | Significado | O que fazer |
|---|---|---|
| `liberar` | Nenhuma restrição nas listas consultadas | Seguir para as próximas etapas (score, limite) |
| `revisar` | Pessoa exposta politicamente (PEP) | Encaminhar para análise humana (diligência reforçada) |
| `bloquear` | Sanção vigente no CEIS ou no CNEP | Não conceder crédito |

Toda resposta traz os **motivos** da decisão, em código e texto, o que atende ao direito de explicação das
decisões automatizadas (LGPD, art. 20).

### Utilidades

- **Regra eliminatória do motor de crédito**: primeira etapa da análise, antes de consultas pagas (birôs).
  Um documento bloqueado aqui não precisa gerar custo de consulta à Serasa.
- **Prevenção à lavagem de dinheiro (PLD/KYC)**: identificação de pessoas expostas politicamente, exigida para
  diligência reforçada.
- **Cadastro de clientes e fornecedores**: checar empresas e pessoas inidôneas, suspensas ou punidas pelo poder
  público antes de fechar negócio.
- **Validação de documento**: rejeita CPF e CNPJ com dígito verificador errado, inclusive o **CNPJ
  alfanumérico** da Receita Federal.

### Listas consultadas

| Lista | Conteúdo | Atualização na fonte |
|---|---|---|
| **CEIS** – Cadastro de Empresas Inidôneas e Suspensas | Pessoas e empresas impedidas de contratar com o poder público | Diária |
| **CNEP** – Cadastro Nacional de Empresas Punidas | Empresas punidas pela Lei Anticorrupção (multas, publicação da decisão) | Diária |
| **PEP** – Pessoas Expostas Politicamente | Cargos eletivos e de alto escalão, e período de carência | Mensal |

Fonte: Portal da Transparência (`portaldatransparencia.gov.br/download-de-dados`). Dados públicos e gratuitos.

---

## 2. Como funciona

```
                     carga diária                                   consulta
  Portal da    ──────────────────────►  PostgreSQL ou Oracle  ◄────────────────  API  ◄──── cliente
 Transparência   ferramenta MotorCredito.Carga   (credito_net /     POST /api/v1/triagem
  (CSV zipado)   (baixa, valida, grava)          MOTOR_CREDITO_NET)
```

1. A **ferramenta de carga** baixa os arquivos da CGU, valida cada linha e grava as listas no banco. A gravação
   é atômica: se um arquivo vier com problema, nada é gravado e os dados anteriores continuam valendo.
2. A **API** recebe o documento, consulta as tabelas (só leitura) e aplica as regras de decisão.
3. As consultas são locais, sem chamar o governo a cada requisição: resposta típica entre **3 e 15 ms**.

A API funciona com **PostgreSQL ou Oracle**, à escolha na configuração (`Bancos__Provedor`). Os dois bancos têm
as mesmas tabelas e os mesmos dados.

---

## 3. Começando em 5 minutos

Pré-requisitos: .NET SDK 10, PostgreSQL e/ou Oracle acessíveis, e o arquivo `.env` preenchido
(ver [Configuração](#9-configuração)).

```powershell
cd C:\Felipe\Programacao\DotNet\APIs\Motor-de-Credito-Net

# 1) Uma vez: cria usuários, schemas e tabelas nos bancos
dotnet run --project src/MotorCredito.Carga -- migrar

# 2) Carrega as listas da CGU (repita diariamente)
dotnet run --project src/MotorCredito.Carga -- carregar

# 3) Sobe a API
dotnet run --project src/MotorCredito.Api --urls http://127.0.0.1:5080
```

Primeira consulta (PowerShell), lendo a chave do `.env`:

```powershell
$chave = ((Get-Content .env | Where-Object { $_ -like 'Api__ApiKey=*' }) -replace '^Api__ApiKey=','')
Invoke-RestMethod -Method Post http://127.0.0.1:5080/api/v1/triagem `
  -Headers @{ 'X-API-Key' = $chave } -ContentType 'application/json' `
  -Body '{"documento": "19.182.685/0001-62"}'
```

Resultado esperado: `resultado = bloquear`, motivo `S01` (empresa com declaração de inidoneidade no CEIS).

Para conferir tudo de uma vez com chamadas reais:

```powershell
dotnet run --project src/MotorCredito.Carga -- testar-api --url http://127.0.0.1:5080
```

---

## 4. Referência da API

### Autenticação

Toda chamada (exceto health checks e documentação) exige o header:

```
X-API-Key: <valor de Api__ApiKey>
```

A chave é comparada em tempo constante. Sem chave ou com chave errada: **401**.

### `POST /api/v1/triagem`

Verifica um CPF ou CNPJ nas listas.

**Requisição** (`Content-Type: application/json`, corpo de até 4 KB):

| Campo | Tipo | Obrigatório | Regras |
|---|---|---|---|
| `documento` | texto | sim | CPF ou CNPJ, com ou sem pontuação (`.`, `-`, `/`, espaços). 11 a 20 caracteres. Dígitos verificadores conferidos. Aceita CNPJ alfanumérico (`12.ABC.345/01DE-35`) |
| `nome` | texto | não | Até 200 caracteres, sem caracteres de controle. **Necessário para verificar PEP em CPF** (ver [Limitações](#11-limitações)). Ignorado para CNPJ |

```json
{ "documento": "529.982.247-25", "nome": "Maria da Silva" }
```

**Resposta 200** (campos em snake_case):

| Campo | Descrição |
|---|---|
| `documento` | CPF **mascarado** (`***.982.247-**`) ou CNPJ formatado (`19.182.685/0001-62`) |
| `tipo` | `CPF` ou `CNPJ` |
| `resultado` | `liberar`, `revisar` ou `bloquear` |
| `motivos[]` | `{ codigo, texto }` – sempre ao menos um (ver [Regras de decisão](#5-regras-de-decisão)) |
| `sancoes[]` | Todas as sanções do documento, vigentes e encerradas: `cadastro` (CEIS/CNEP), `categoria`, `orgao_sancionador`, `uf_orgao`, `data_inicio`, `data_fim` (nulo = sem prazo), `numero_processo`, `vigente` |
| `pep` | `verificado` (se a checagem de PEP foi feita), `ativo` (se é PEP hoje), `funcoes[]` (ex.: `PREFEITO`) |
| `consultado_em` | Data da consulta (`AAAA-MM-DD`) |
| `fonte` | `banco` (`postgres`/`oracle`), `arquivos` da CGU carregados e `carregado_em` (data e hora da carga) |

Exemplo – empresa sancionada:

```json
{
  "documento": "19.182.685/0001-62",
  "tipo": "CNPJ",
  "resultado": "bloquear",
  "motivos": [ { "codigo": "S01", "texto": "Sanção vigente em CEIS (1 registro(s))." } ],
  "sancoes": [
    {
      "cadastro": "CEIS",
      "categoria": "Declaração de Inidoneidade sem prazo determinado",
      "orgao_sancionador": "Prefeitura Municipal de Riqueza (SC)",
      "uf_orgao": "SC",
      "data_inicio": "2016-06-28",
      "data_fim": null,
      "numero_processo": "104/2014",
      "vigente": true
    }
  ],
  "pep": { "verificado": false, "ativo": false, "funcoes": [] },
  "consultado_em": "2026-11-11",
  "fonte": {
    "banco": "postgres",
    "arquivos": [ "202609_PEP.csv", "20261110_CEIS.csv", "20261110_CNEP.csv" ],
    "carregado_em": "2026-11-11T06:23:57.652-03:00"
  }
}
```

Exemplo – pessoa exposta politicamente:

```json
{
  "documento": "***.225.903-**",
  "tipo": "CPF",
  "resultado": "revisar",
  "motivos": [ { "codigo": "P01", "texto": "Pessoa exposta politicamente (PREFEITO)." } ],
  "sancoes": [],
  "pep": { "verificado": true, "ativo": true, "funcoes": [ "PREFEITO" ] },
  "consultado_em": "2026-11-11",
  "fonte": { "banco": "oracle", "arquivos": [ "..." ], "carregado_em": "..." }
}
```

### Outros endpoints

| Endpoint | Autenticação | Uso |
|---|---|---|
| `GET /health/live` | não | A aplicação está no ar (não consulta o banco). Para o orquestrador reiniciar o processo |
| `GET /health/ready` | não | A aplicação **e o banco** respondem. `503` se o banco estiver fora. Para o balanceador tirar a instância de rotação |
| `GET /metrics` | não | Métricas Prometheus (latência, requisições, erros). **Exponha só na rede interna** |
| `GET /openapi/v1.json` | não | Contrato OpenAPI. Só existe com `Api__EnableDocs=true` |
| `GET /scalar` | não | Documentação interativa (testar chamadas pelo navegador). Só com `Api__EnableDocs=true` |

### Limites

| Limite | Valor padrão | Configuração |
|---|---|---|
| Requisições por IP | 60 por minuto | `Api__RateLimitPorMinuto` |
| Tempo total da requisição | 10 s | `Api__RequestTimeoutSeconds` |
| Tempo de cada consulta ao banco | 5 s | `Bancos__Postgres__TimeoutSegundos` / `Bancos__Oracle__TimeoutSegundos` |
| Tamanho do corpo | 4 KB | fixo |

### Headers de resposta

| Header | Valor |
|---|---|
| `X-Request-ID` | Identificador da requisição. **Informe ao suporte** em caso de erro |
| `Cache-Control` | `no-store` (a resposta contém dados pessoais) |
| `X-Content-Type-Options` / `X-Frame-Options` / `Strict-Transport-Security` | Proteções do navegador |
| `Retry-After` | Nas respostas 429: segundos até poder tentar de novo |

---

## 5. Regras de decisão

> **Política PROVISÓRIA.** Deve ser validada pela área de risco/compliance antes de uso em produção.

| Código | Situação | Efeito no resultado |
|---|---|---|
| `S01` | Sanção **vigente** no CEIS ou no CNEP | `bloquear` |
| `S02` | Só sanções **encerradas** | nenhum (informativo) |
| `P01` | PEP **em exercício** ou **dentro da carência** | `revisar` |
| `P02` | Foi PEP, carência encerrada | nenhum (informativo) |
| `P03` | CPF enviado **sem nome**: PEP não verificado | nenhum (informativo) |
| `OK` | Nenhuma restrição | `liberar` |

- Quando há mais de um motivo, vale o **mais grave**: `bloquear` > `revisar` > `liberar`.
- **Sanção vigente**: já começou (ou sem data de início) e não terminou. Sem data final = sem prazo, vigente.
- **PEP ativo**: enquanto exerce a função e até o fim da carência. Datas não informadas pela CGU contam como
  ativas (a favor da cautela).
- **PEP não bloqueia**: ser PEP não impede crédito; exige análise reforçada.
- A checagem de PEP **só existe para CPF**. Para CNPJ, avalie os sócios separadamente.

---

## 6. Erros

Formato único (RFC 7807 – ProblemDetails), sempre com `code` e `request_id`:

```json
{
  "title": "Dados de entrada inválidos.",
  "status": 422,
  "code": "entrada_invalida",
  "request_id": "0HNH1K8V3R2LQ:00000001"
}
```

| HTTP | `code` | Quando | O cliente deve |
|---|---|---|---|
| 400 | `requisicao_invalida` | JSON malformado | Corrigir o corpo |
| 401 | `http_401` | Sem `X-API-Key` ou chave errada | Conferir a chave |
| 404 | `http_404` | Rota inexistente (com chave válida) | Conferir a URL |
| 413 | `corpo_muito_grande` | Corpo acima de 4 KB | Enviar só `documento` e `nome` |
| 422 | `entrada_invalida` | Documento inválido ou nome com caracteres de controle | Corrigir a entrada; **não repetir** |
| 429 | `http_429` | Limite de requisições atingido | Esperar `Retry-After` segundos |
| 500 | `erro_interno` | Erro inesperado | Informar o `request_id` ao suporte |
| 503 | `banco_indisponivel` | Banco fora do ar | Tentar de novo depois (com espera crescente) |
| 504 | `banco_timeout` / `timeout` | Banco ou processamento lento demais | Tentar de novo depois |

As mensagens de erro **nunca repetem a entrada** (CPF, nome) nem detalhes internos (SQL, tabelas, senhas).
Sem chave, até rotas inexistentes respondem 401: a API não revela quais rotas existem.

**Repetir ou não?** Repita só 429, 503 e 504, com espera crescente (ex.: 1 s, 2 s, 4 s). Nunca repita 4xx de
entrada (400, 413, 422): o resultado será o mesmo.

---

## 7. Exemplos de integração

### curl

```bash
curl -X POST http://127.0.0.1:5080/api/v1/triagem \
  -H "X-API-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"documento": "529.982.247-25", "nome": "Maria da Silva"}'
```

### C# (.NET)

```csharp
using System.Net.Http.Json;
using System.Text.Json;

var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5080"), Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.Add("X-API-Key", Environment.GetEnvironmentVariable("MOTOR_API_KEY"));

var resposta = await http.PostAsJsonAsync("/api/v1/triagem", new { documento = "529.982.247-25", nome = "Maria da Silva" });
if (resposta.IsSuccessStatusCode)
{
    var triagem = await resposta.Content.ReadFromJsonAsync<Triagem>(
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    Console.WriteLine($"{triagem!.Resultado}: {string.Join("; ", triagem.Motivos.Select(m => m.Texto))}");
}

record Motivo(string Codigo, string Texto);
record Triagem(string Documento, string Tipo, string Resultado, List<Motivo> Motivos);
```

### Uso típico no fluxo de crédito

```
pedido → validar entrada → POST /api/v1/triagem
           bloquear → recusar (registrar motivos S01)
           revisar  → fila de análise manual (registrar P01)
           liberar  → consultar birô / calcular score / limite
```

---

## 8. Operação: ferramenta de carga

Projeto `src/MotorCredito.Carga`. Todos os comandos aceitam `--banco postgres|oracle|todos` (padrão: `todos`).

| Comando | O que faz | Quando |
|---|---|---|
| `migrar` | Cria os usuários de aplicação, os schemas e as tabelas. Pode ser executado de novo sem efeito colateral | Na instalação e após mudanças de schema |
| `carregar` | Baixa CEIS, CNEP e PEP do Portal da Transparência e substitui as tabelas | **Diariamente** |
| `carregar --pasta <dir>` | Usa CSVs já baixados | Reprocessar ou testar |
| `comparar` | Compara campo a campo as tabelas do .NET com as do projeto Python | Auditoria da carga |
| `testar-api --url <endereço>` | Faz chamadas HTTP reais e confere as respostas | Após publicar a API |

### Carga diária

- O Portal mantém **só o arquivo mais recente** (os anteriores respondem 403). A ferramenta procura o arquivo do
  dia e recua até 7 dias (CEIS/CNEP) ou 3 meses (PEP).
- Cada registro gravado leva `origem_carga = 'DOTNET'`, `arquivo_origem` (nome do CSV) e `carregado_em`.
- A carga é atômica por banco: se algo falhar, os dados do dia anterior continuam.
- Tempo típico: cerca de 10 s no PostgreSQL e 15 a 45 s no Oracle (160 mil linhas).

Agendamento sugerido (Agendador de Tarefas do Windows), todo dia às 7h:

```powershell
$acao = New-ScheduledTaskAction -Execute "dotnet" `
  -Argument "run --project src/MotorCredito.Carga -- carregar" `
  -WorkingDirectory "C:\Felipe\Programacao\DotNet\APIs\Motor-de-Credito-Net"
Register-ScheduledTask -TaskName "MotorCredito-CargaCGU" -Action $acao `
  -Trigger (New-ScheduledTaskTrigger -Daily -At 7am)
```

A API não precisa ser reiniciada após a carga. A origem informada em `fonte` é atualizada em até 5 minutos.

---

## 9. Configuração

Arquivo `.env` na raiz do projeto (**nunca versionado**). Variáveis de ambiente já definidas têm prioridade.
No .NET, `__` separa seções: `Bancos__Postgres__Host` equivale a `Bancos:Postgres:Host`.

| Variável | Padrão | Descrição |
|---|---|---|
| `Api__ApiKey` | – (obrigatória) | Chave dos clientes, mínimo 32 caracteres. A API não sobe sem ela |
| `Api__AllowedHosts__0`, `__1`... | `localhost`, `127.0.0.1` | Hosts aceitos no header `Host` |
| `Api__AllowedOrigins__0`... | nenhum | Origens liberadas para CORS (navegador) |
| `Api__EnableDocs` | `false` | Liga `/openapi/v1.json` e `/scalar`. **Desligue em produção** |
| `Api__RequestTimeoutSeconds` | `10` | Tempo máximo da requisição |
| `Api__RateLimitPorMinuto` | `60` | Requisições por IP por minuto |
| `Bancos__Provedor` | `postgres` | `postgres` ou `oracle` |
| `Bancos__Postgres__Host` / `__Porta` / `__Banco` | `127.0.0.1` / `5432` / `motor_credito` | Servidor PostgreSQL |
| `Bancos__Postgres__Usuario` / `__Senha` | `motor_app_net` / – | Usuário da aplicação (só lê e grava dados) |
| `Bancos__Postgres__SslMode` | `Prefer` | Use `Require` ou `VerifyFull` em servidor remoto |
| `Bancos__Oracle__DataSource` | `localhost:1521/XEPDB1` | Banco plugável (nunca o CDB `XE`) |
| `Bancos__Oracle__Usuario` / `__Senha` | `MOTOR_APP_NET` / – | Usuário da aplicação (acessa por sinônimos) |
| `Admin__Postgres__*`, `Admin__Oracle__*` | – | Só para `migrar` e `comparar`. **A API não usa** |

### Onde ficam os dados

| Banco | Schema | Tabelas | Usuário da aplicação |
|---|---|---|---|
| PostgreSQL (`motor_credito`) | `credito_net` | `sancoes`, `peps` | `motor_app_net` |
| Oracle (`XEPDB1`) | `MOTOR_CREDITO_NET` (sem login) | `SANCOES`, `PEPS` | `MOTOR_APP_NET` (sinônimos) |

Consultas manuais (pgAdmin, SQL Developer) usam o schema na frente: `select * from credito_net.peps` /
`select * from motor_credito_net.peps`. No SQL Developer, conecte por **Nome do Serviço** `XEPDB1`.

---

## 10. Segurança e LGPD

- **Documento por POST**, nunca na URL: não aparece em logs de proxy nem em histórico.
- **CPF sempre mascarado** na resposta. CNPJ é dado público e volta formatado.
- **Logs sem dados pessoais**: nem documento, nem nome, nem detalhes da conexão com o banco.
- **Usuários de banco com privilégio mínimo**: a API só lê (no PostgreSQL a sessão abre como somente leitura);
  o usuário da aplicação não cria tabelas.
- **Segredos só no `.env`** ou em variáveis de ambiente; nunca no código ou no repositório.
- **Finalidade**: use a API para análise de crédito, prevenção a fraude e PLD. Os dados vêm de cadastros
  públicos, mas a combinação CPF + nome é dado pessoal: registre a base legal do tratamento.
- **Revisão humana**: a pessoa pode pedir revisão da decisão automatizada (LGPD, art. 20). Os `motivos` da
  resposta e o `request_id` permitem reconstruir a decisão.

---

## 11. Limitações

- **PEP depende do nome.** A CGU publica o CPF dos PEPs mascarado (`***.123.456-**`). A API compara os 6 dígitos
  visíveis **e** o nome (sem acentos, maiúsculas). Sem nome, o PEP não é verificado (`P03`). Homônimos com os
  mesmos 6 dígitos são raros, mas possíveis: por isso PEP leva a `revisar`, não a `bloquear`.
- **O nome precisa ser o completo**, como no registro civil. Abreviações ("Maria S.") não são encontradas.
- **Dados com até 1 dia de atraso** (CEIS/CNEP) e até 1 mês (PEP), conforme a publicação da CGU e o horário da
  carga.
- **Não é análise de crédito completa.** A API não traz score, dívidas, negativações nem renda. É a etapa de
  regras eliminatórias; as demais dependem de birôs (ex.: Serasa) e da política de crédito.
- **Só listas federais da CGU.** Sanções estaduais e municipais aparecem quando o órgão as informou à CGU.
- **CPF dos sancionados no CEIS/CNEP** vem completo da CGU; documentos malformados na fonte são gravados como
  vieram e não casam com consultas válidas.

---

## 12. Solução de problemas

| Sintoma | Causa provável | Solução |
|---|---|---|
| A API não sobe: erro de validação de `ApiSettings` | `Api__ApiKey` vazia ou com menos de 32 caracteres | Preencher no `.env` |
| `401` em todas as chamadas | Chave errada ou header ausente | Header `X-API-Key` com o valor de `Api__ApiKey` |
| `503 banco_indisponivel` e `/health/ready` em 503 | Banco parado, porta errada ou senha do usuário de aplicação errada | Conferir o serviço do banco e o `.env` |
| `400` ou host recusado | Header `Host` fora de `Api__AllowedHosts` | Incluir o host usado pelos clientes |
| `resultado = liberar` para alguém que deveria ser PEP | Nome não enviado, abreviado ou diferente do cadastro | Enviar o nome completo; conferir `pep.verificado` |
| `fonte.arquivos` com datas antigas | Carga diária não executada | Rodar `carregar` e conferir o agendamento |
| `carregar` falha com "colunas ausentes" | A CGU mudou o layout do arquivo | Ajustar `LayoutsCgu.cs`; os dados anteriores continuam valendo |
| `ORA-00942` no SQL Developer | Conexão no CDB (`SID xe`) | Conectar por **Nome do Serviço** `XEPDB1` |
| `relation "peps" does not exist` no pgAdmin | Consulta sem o schema | Usar `credito_net.peps` |
| Build falha com `NU1902`/`NU1903` | Pacote com vulnerabilidade conhecida | Atualizar a versão no `Directory.Packages.props` (nunca desligar o aviso) |
| Build falha com `MSB4014`/`MSB4236` (workload manifest) | Atualização do .NET SDK em andamento | Esperar o Windows Update terminar e tentar de novo |

Ao abrir chamado, informe o **`request_id`** da resposta (ou o header `X-Request-ID`), a data e a hora.
