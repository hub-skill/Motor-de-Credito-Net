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
| `GET /health/live` | não | A aplicação está no ar: `{"status":"ok"}` (não consulta o banco). Para o orquestrador reiniciar o processo |
| `GET /health/ready` | não | A aplicação **e o banco** respondem: `{"status":"ok","banco":"postgres"}`; `503` com `{"status":"banco_indisponivel"}` se o banco estiver fora. Para o balanceador tirar a instância de rotação |
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
| `X-Requisicao` | Número da requisição na movimentação (ex.: `2026-000123`). **Informe ao suporte** também. Ausente se a chave de API falta ou é inválida, se a rota é `/health` ou `/metrics`, ou se o banco não deu o número a tempo (ver seção 6) |
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

Formato único, **o mesmo do Motor Python**: RFC 9457 (Problem Details), com `Content-Type: application/problem+json`
e exatamente quatro campos: `title` (texto para pessoas), `status` (o mesmo código HTTP), `code` (para o seu programa
decidir o que fazer) e `request_id`. Decida sempre pelo `code` (ou pelo `status`), nunca pelo texto do `title`:

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
| 400 | `requisicao_invalida` | JSON malformado (inclusive `NaN`/`Infinity`, que não existem em JSON) ou aninhado em mais de 64 níveis | Corrigir o corpo |
| 401 | `http_401` | Sem `X-API-Key` ou chave errada | Conferir a chave |
| 404 | `http_404` | Rota inexistente (com chave válida) | Conferir a URL |
| 405 | `http_405` | Método não permitido (a triagem é só `POST`) | Usar `POST` |
| 413 | `corpo_muito_grande` | Corpo acima de 4 KB | Enviar só `documento` e `nome` |
| 422 | `entrada_invalida` | Documento inválido ou nome com caracteres de controle; JSON válido com tipo errado (ex.: número no lugar de texto), corpo vazio ou que não é um objeto | Corrigir a entrada; **não repetir** |
| 429 | `limite_excedido` | Limite de requisições atingido | Esperar `Retry-After` segundos |
| 500 | `erro_interno` | Erro inesperado | Informar o `request_id` ao suporte |
| 503 | `banco_indisponivel` | Banco fora do ar | Tentar de novo depois (com espera crescente) |
| 504 | `banco_timeout` / `timeout` | Banco ou processamento lento demais | Tentar de novo depois |

As mensagens de erro **nunca repetem a entrada** (CPF, nome) nem detalhes internos (SQL, tabelas, senhas).
Sem chave, até rotas inexistentes e métodos errados respondem 401 (com `WWW-Authenticate: APIKey`), como no Motor
Python: a API não revela quais rotas existem.

**Repetir ou não?** Repita só 429, 503 e 504, com espera crescente (ex.: 1 s, 2 s, 4 s). Nunca repita 4xx de
entrada (400, 413, 422): o resultado será o mesmo.

### Movimentação: o que chegou e o que a API respondeu

Cada requisição **com chave de API válida** é registrada na tabela `movimentacao`, menos `/health/*` e `/metrics`
(as sondas chamam a cada poucos segundos): primeiro o que chegou, depois o que a API respondeu, **na mesma linha**.
Chamadas sem chave ou com chave errada (inclusive varreduras de rotas) recebem 401 e **não** são registradas, nem na
movimentação nem na `log_erros`: só aparecem no log da aplicação.

| Banco | Tabelas |
|---|---|
| PostgreSQL | `credito_net.movimentacao` e `credito_net.contador_requisicao` |
| Oracle | `motor_credito_net.movimentacao` e `motor_credito_net.contador_requisicao` |

| Coluna | Conteúdo |
|---|---|
| `ano`, `requisicao` | A chave. `requisicao` é um contador que volta a 1 em todo ano novo (o ano é o do horário de Brasília). O cliente recebe o número no header `X-Requisicao` (ex.: `2026-000123`) |
| `metodo`, `rota` | Método e caminho pedido (sem a query string, até 200 caracteres) |
| `requisicao_conteudo`, `requisicao_em` | O que foi enviado, em qualquer formato (texto), e o momento em que chegou |
| `situacao_requisicao` | `OK` = aceita; `ER` = recusada (respostas 4xx: JSON inválido, entrada inválida, limite excedido...). Vazia até a resposta |
| `status_http`, `resposta_conteudo`, `resposta_em` | Status, corpo e momento da resposta. `resposta_em` vazio = a API não chegou a responder (caiu no meio) ou a gravação da resposta se perdeu |
| `situacao_resposta` | `OK` = sucesso (2xx); `ER` = erro (4xx ou 5xx). Uma falha do servidor (5xx) fica `OK` na requisição e `ER` na resposta |

- **Numeração e chegada** são gravadas na hora, num único comando do banco: duas requisições ao mesmo tempo nunca
  recebem o mesmo número. A API espera o banco por até **1 segundo**; sem número nesse prazo (banco fora ou lento), a
  requisição segue normalmente, **sem movimentação e sem o header `X-Requisicao`** (aviso `requisição sem número na
  movimentação` no log da aplicação).
- **A resposta** completa a linha em segundo plano (fila de até 1000, lotes de 100): o cliente não espera por ela.
  Com o banco fora, a fila enche e as respostas a mais se perdem (aviso no log). Uma resposta já gravada nunca é
  sobrescrita.
- **Conteúdo**: texto em qualquer formato (JSON malformado, XML...), até 32 KB (acima disso é cortado, com aviso no
  fim). Corpo acima do limite da API (4 KB) não é lido: só o tamanho fica gravado. **Todo CPF é mascarado**
  (`***.982.247-**`), em qualquer formato, na requisição e na resposta. A chave de API vai num header e nunca é gravada.
  **O nome enviado é gravado como veio.**
- **Permissão**: o usuário da aplicação (`motor_app_net / MOTOR_APP_NET`) não tem acesso às tabelas; só executa as duas funções/procedures
  (`registrar_requisicao` e `registrar_resposta`). Consultas e limpeza são feitas por um administrador.
- **Consultar** uma requisição relatada pelo cliente: `select * from credito_net.movimentacao where ano = 2026 and requisicao = 123`.
- **Limpeza** (o volume cresce com cada chamada). Exemplo, manter 90 dias, apagando primeiro o log de erros:
  `delete from credito_net.log_erros where ocorrido_em < now() - interval '90 days'` e depois
  `delete from credito_net.movimentacao where requisicao_em < now() - interval '90 days'` (PostgreSQL); no Oracle,
  `... WHERE requisicao_em < SYSTIMESTAMP - INTERVAL '90' DAY` nas tabelas de `motor_credito_net`.
- **Custo**: gravar a chegada de cada requisição é uma escrita no banco dentro do atendimento. Medido em 12/11/2026
  (notebook de 2 núcleos, PostgreSQL, gerador de carga no mesmo computador, antes x depois): a vazão máxima caiu de ~760 para ~530 requisições por segundo (de 762,3 para 529,1 com 16 clientes simultâneos, -31%; com 4 clientes, de 618,3 para 350,2, -43%). O Oracle não foi
  medido. Se a vazão virar problema, as saídas são uma conexão própria só para essas escritas e confirmar a gravação
  sem esperar o disco (`synchronous_commit = off`, que aceita perder as últimas centenas de milissegundos num
  desligamento brusco).

### Log de erros no banco

Toda resposta de erro (4xx e 5xx) é gravada na tabela `log_erros`, no mesmo banco que a API estiver usando:

| Banco | Tabela |
|---|---|
| PostgreSQL | `credito_net.log_erros` |
| Oracle | `motor_credito_net.log_erros` |

Colunas: `ano` e `requisicao` (as duas primeiras: a chave da movimentação do erro, **obrigatórias**), `id`,
`ocorrido_em`, `request_id` (o mesmo devolvido ao cliente), `metodo`, `rota`, `status`, `codigo`, `titulo`,
`tipo_excecao` (só no 500), `duracao_ms` e `ambiente` (`ASPNETCORE_ENVIRONMENT`). **Nunca** grava CPF, CNPJ, nome ou o corpo da
requisição (o corpo, mascarado, fica na movimentação). As tabelas são criadas por `dotnet run --project src/MotorCredito.Carga -- migrar` (scripts `02_movimentacao.sql` e `03_log_erros.sql`).

- **Sem número de requisição, o erro não é gravado aqui**: se a chamada veio sem chave válida (401) ou o banco
  estava fora ou lento na chegada (ver movimentação), o erro aparece só no log da aplicação. A tabela nunca tem linha sem `ano` e `requisicao`.
- A resposta não espera o banco: o erro entra numa fila em memória (até 1000) e é gravado em lotes de até 100.
  Com o banco fora ou lento, a fila enche e os registros a mais são descartados (aviso no log da aplicação);
  a API continua respondendo normalmente. Ao desligar, a API grava o que ainda estiver na fila (até 5 s).
- O usuário da aplicação (`motor_app_net / MOTOR_APP_NET`) só tem **INSERT** nessa tabela: não lê, não altera e não apaga. Consultas e
  limpeza são feitas por um administrador.
- Para achar o erro que um cliente relatou: `select * from credito_net.log_erros where ano = 2026 and requisicao = 123`
  (ou pelo `request_id`), e a requisição completa em `credito_net.movimentacao` com a mesma chave.
- A versão anterior da tabela (sem a chave) ficou guardada como `log_erros_v1`, sem acesso da aplicação: as linhas
  dela não têm movimentação e não foram copiadas. Apague quando quiser (`drop table credito_net.log_erros_v1`).
- Limpeza: ver a da movimentação (o log de erros é apagado primeiro).

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

### Rodar a API em container (Docker)

Validado em 11/11/2026 com Docker Desktop 29.9.3 no Windows, com PostgreSQL e Oracle rodando no próprio
Windows. Imagem final: 258 MB (`aspnet:10.0-noble-chiseled-extra`, sem shell), usuário não root (UID 1654) e zero
vulnerabilidades HIGH/CRITICAL corrigíveis (trivy 0.58.1). A ferramenta de carga não entra na imagem: continua
rodando no Windows.

```powershell
cd "C:\Felipe\Programacao\DotNet\APIs\Motor-de-Credito-Net"

# 1) Construir a imagem (o .dockerignore deixa .env, bin/obj, tests e a Carga fora)
docker build -t motor-credito-net:0.1.0 .

# 2) Arquivo de variáveis do container: o .env sem os administradores e com o banco em host.docker.internal
#    (dentro do container, "localhost" é o próprio container). O .env.docker está no .gitignore.
$l = Get-Content .env -Encoding utf8 | Where-Object { $_.Trim() -and $_ -notmatch '^\s*#' -and $_ -notmatch '^Admin__' } |
  ForEach-Object { $_ -replace '^Bancos__Postgres__Host=.*','Bancos__Postgres__Host=host.docker.internal' -replace '^(Bancos__Oracle__DataSource=)(localhost|127\.0\.0\.1)','${1}host.docker.internal' }
[IO.File]::WriteAllLines("$PWD\.env.docker", [string[]]$l)   # UTF-8 sem BOM (o Set-Content do PowerShell 5.1 grava BOM)

# 3) Subir (porta 8082 do Windows -> 8080 do container)
docker run -d --name motor-net -p 8082:8080 --env-file .env.docker motor-credito-net:0.1.0
#    Para consultar o Oracle: acrescente  -e Bancos__Provedor=oracle

# 4) Conferir
curl.exe http://localhost:8082/health/ready      # {"status":"ok","banco":"postgres"}
dotnet run --project src/MotorCredito.Carga -- testar-api --url http://localhost:8082
docker logs motor-net --tail 20

# Parar / remover
docker rm -f motor-net
```

Observações:

- A imagem chiseled não tem shell nem curl: não use `docker exec ... sh`. Para diagnosticar, veja os logs ou
  rode um container auxiliar (`docker run --rm busybox:1.37 nc -zv host.docker.internal 5432`).
- O Npgsql 10 tenta criptografia GSS (Kerberos) por padrão e, na imagem chiseled, registra `Cannot load library
  libgssapi_krb5.so.2`. A string de conexão usa `GssEncryptionMode=Disable` (não usamos Kerberos).
- Com o banco inacessível, `/health/live` continua 200, `/health/ready` dá 503 e a triagem responde
  `503 banco_indisponivel` em cerca de 0,1 s.
- Não foi preciso mexer no `pg_hba.conf` nem no listener do Oracle nesta máquina. Se der `no pg_hba.conf entry
  for host "x.x.x.x"`, libere a faixa do Docker (192.168.65.0/24) para o usuário da aplicação.

### Rodar a API no Kubernetes

Validado em 11/11/2026 no Kubernetes v1.36.1 do Docker Desktop (modo kind), com os bancos no Windows. Os
manifestos ficam em `k8s/` (Kustomize: `base/` e `overlays/local`, `overlays/producao`). Precisa da imagem do
passo anterior e do `.env.docker`.

```powershell
# 1) Gerar k8s/ (config.env e segredos.env saem do .env.docker; segredos.env está no .gitignore)
& "$HOME\.claude\skills\kubernetes\scripts\preparar-k8s.ps1" -Projeto . -Nome motor-credito-net -Linguagem dotnet -Tag 0.1.1

# 2) Modo kind (nó "desktop-control-plane"): levar a imagem ao nó. Rode no bash (o pipe do PowerShell 5.1 corrompe o tar)
docker save motor-credito-net:0.1.1 | docker exec -i desktop-control-plane ctr -n k8s.io images import --all-platforms -

# 3) Publicar e conferir
kubectl apply -f k8s/overlays/local/namespace.yaml
kubectl apply -k k8s/overlays/local
kubectl -n motor-credito-net rollout status deploy/motor-credito-net --timeout=180s
kubectl -n motor-credito-net get pods                      # 1/1 Running, RESTARTS 0

# 4) Acessar e testar (http://localhost:9082)
kubectl -n motor-credito-net port-forward svc/motor-credito-net 9082:80
dotnet run --project src/MotorCredito.Carga -- testar-api --url http://localhost:9082

# Remover
kubectl delete -k k8s/overlays/local
```

Resultados da validação: triagem real igual à do container e `testar-api` 8/8; `rollout restart` com tráfego
contínuo pelo Service, 622 chamadas e 0 falhas; versão com o banco inacessível nunca fica pronta (readiness 503) e o
pod antigo segue atendendo, sem reinícios.

- Cada mudança de código pede **tag nova**: importe a imagem e troque o `newTag` em
  `k8s/overlays/local/kustomization.yaml` (ou passe `-Tag` ao script).
- `/health/live`, `/health/ready` e `/metrics` ficam **fora do rate limit**. As probes do Kubernetes saem todas do
  IP do nó: com o limite por IP valendo para elas, um 429 na liveness reiniciaria o pod saudável (aconteceu no
  primeiro teste de troca: 169 respostas 429). Há teste automatizado disso.
- O limite `Api__RateLimitPorMinuto` é **por pod** e em memória: com N réplicas o limite real é N vezes maior.
- A imagem chiseled não tem shell: `kubectl exec` não funciona. Diagnostique por `kubectl logs`, `describe` e eventos.
- Conexões no banco = réplicas × pool (5). No overlay `local` há 1 réplica; o HPA (2 a 6) só existe na `base` e no
  `producao`. O Docker Desktop não traz metrics-server nem aplica NetworkPolicy.

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
| PostgreSQL (`motor_credito`) | `credito_net` | `sancoes`, `peps`, `movimentacao` e `contador_requisicao` (só pelas funções), `log_erros` (só INSERT) | `motor_app_net` |
| Oracle (`XEPDB1`) | `MOTOR_CREDITO_NET` (sem login) | `SANCOES`, `PEPS`, `MOVIMENTACAO` e `CONTADOR_REQUISICAO` (só pelas procedures), `LOG_ERROS` (só INSERT) | `MOTOR_APP_NET` (sinônimos) |

Consultas manuais (pgAdmin, SQL Developer) usam o schema na frente: `select * from credito_net.peps` /
`select * from motor_credito_net.peps`. No SQL Developer, conecte por **Nome do Serviço** `XEPDB1`.

---

## 10. Segurança e LGPD

- **Documento por POST**, nunca na URL: não aparece em logs de proxy nem em histórico.
- **CPF sempre mascarado** na resposta. CNPJ é dado público e volta formatado.
- **Logs sem dados pessoais**: nem documento, nem nome, nem detalhes da conexão com o banco.
- **Usuários de banco com privilégio mínimo**: a API só lê as listas (no PostgreSQL a sessão abre como somente
  leitura); o usuário da aplicação não cria tabelas. As únicas escritas são a movimentação (o usuário só executa as
  duas funções, sem acesso às tabelas) e o log de erros (só `INSERT`).
- **A movimentação guarda o que foi enviado**: o CPF é gravado mascarado, mas o **nome** vai como veio, junto com o
  momento da consulta. Defina por quanto tempo guardar (a limpeza está na seção 6), restrinja o acesso às tabelas a
  quem precisa (auditoria e suporte) e registre a finalidade e o prazo de retenção.
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
| Resposta sem o header `X-Requisicao` | Chamada sem chave válida, rota `/health` ou `/metrics`, ou banco fora/lento na chegada (passou de 1 s) | Ver o aviso `requisição sem número na movimentação` no log e conferir o banco |
| `resposta_em` vazio na movimentação | A API caiu antes de responder, ou a gravação da resposta se perdeu | Ver os avisos `fila da movimentação cheia` e `falha ao gravar a resposta na movimentação` |

Ao abrir chamado, informe o **`request_id`** e o **`X-Requisicao`** da resposta (ou o header `X-Request-ID`), a data e a hora.
