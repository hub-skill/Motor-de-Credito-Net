-- =============================================================================
-- Motor de Crédito .NET – movimentação da API (PostgreSQL)
-- Executado pela ferramenta: dotnet run --project src/MotorCredito.Carga -- migrar
-- (como administrador, depois de 01_schema_net.sql e antes de 03_log_erros.sql). Pode ser executado de novo.
--
-- Uma linha por requisição com chave de API válida (menos /health e /metrics): o que chegou e o
-- que a API respondeu. Chave: (ano, requisicao); o contador volta a 1 em todo ano novo.
-- CPF no conteúdo é gravado mascarado (***.999.999-**) pela própria API.
--
-- A aplicação (motor_app_net) não tem privilégio nas tabelas: só executa as duas funções
-- abaixo (SECURITY DEFINER), que numeram, gravam e completam a linha uma única vez.
-- Limpeza (como administrador), ex.: manter 90 dias:
--   delete from credito_net.log_erros where ocorrido_em < now() - interval '90 days';
--   delete from credito_net.movimentacao where requisicao_em < now() - interval '90 days';
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Contador: último número usado em cada ano
-- -----------------------------------------------------------------------------
create table if not exists credito_net.contador_requisicao (
    ano     smallint  not null,
    ultimo  bigint    not null,

    constraint pk_contador_requisicao         primary key (ano),
    constraint ck_contador_requisicao_ultimo  check (ultimo >= 1)
);

comment on table  credito_net.contador_requisicao        is 'Último número de requisição usado em cada ano (a numeração da movimentação volta a 1 em todo ano novo).';
comment on column credito_net.contador_requisicao.ano    is 'Ano (horário de Brasília).';
comment on column credito_net.contador_requisicao.ultimo is 'Último número entregue neste ano.';

-- -----------------------------------------------------------------------------
-- Movimentação: requisição e resposta
-- -----------------------------------------------------------------------------
create table if not exists credito_net.movimentacao (
    ano                  smallint      not null,
    requisicao           bigint        not null,
    metodo               varchar(10)   not null,
    rota                 varchar(200)  not null,
    requisicao_conteudo  text,
    requisicao_em        timestamptz   not null default now(),
    situacao_requisicao  char(2),
    status_http          smallint,
    resposta_conteudo    text,
    resposta_em          timestamptz,
    situacao_resposta    char(2),

    constraint pk_movimentacao                      primary key (ano, requisicao),
    constraint ck_movimentacao_situacao_requisicao  check (situacao_requisicao in ('OK', 'ER')),
    constraint ck_movimentacao_situacao_resposta    check (situacao_resposta in ('OK', 'ER')),
    constraint ck_movimentacao_status_http          check (status_http between 100 and 599)
);

create index if not exists idx_movimentacao_requisicao_em on credito_net.movimentacao (requisicao_em);

comment on table  credito_net.movimentacao                     is 'Requisições recebidas pela API .NET e as respostas dadas. CPF mascarado; sem a chave de API.';
comment on column credito_net.movimentacao.ano                 is 'Ano da requisição (horário de Brasília). Chave junto com requisicao.';
comment on column credito_net.movimentacao.requisicao          is 'Número da requisição no ano (começa em 1 a cada ano). Devolvido ao cliente no header X-Requisicao.';
comment on column credito_net.movimentacao.metodo              is 'Método HTTP (POST, GET...).';
comment on column credito_net.movimentacao.rota                is 'Caminho pedido, sem query string (truncado em 200 caracteres).';
comment on column credito_net.movimentacao.requisicao_conteudo is 'Corpo recebido, como texto, em qualquer formato (até 32 KB, CPF mascarado). Nulo se não houve corpo.';
comment on column credito_net.movimentacao.requisicao_em       is 'Momento em que a requisição chegou.';
comment on column credito_net.movimentacao.situacao_requisicao is 'OK = aceita (chave e entrada válidas); ER = recusada (respostas 4xx). Nulo enquanto não houver resposta.';
comment on column credito_net.movimentacao.status_http         is 'Status HTTP da resposta.';
comment on column credito_net.movimentacao.resposta_conteudo   is 'Corpo da resposta, como texto (até 32 KB, CPF mascarado).';
comment on column credito_net.movimentacao.resposta_em         is 'Momento da resposta. Nulo = a API não chegou a responder (ex.: caiu no meio).';
comment on column credito_net.movimentacao.situacao_resposta   is 'OK = sucesso (2xx); ER = erro (4xx ou 5xx).';

-- -----------------------------------------------------------------------------
-- Funções usadas pela aplicação
-- -----------------------------------------------------------------------------
-- Numera e grava a chegada num único comando: duas requisições ao mesmo tempo nunca recebem o mesmo
-- número (a linha do ano no contador fica travada até o fim da transação, que é curta).
create or replace function credito_net.registrar_requisicao(p_metodo varchar, p_rota varchar, p_conteudo text)
returns table (ano smallint, requisicao bigint)
language sql
security definer
set search_path = credito_net, pg_temp
as $$
    with numero as (
        insert into credito_net.contador_requisicao as c (ano, ultimo)
        values (extract(year from now() at time zone 'America/Sao_Paulo')::smallint, 1)
        on conflict on constraint pk_contador_requisicao do update set ultimo = c.ultimo + 1
        returning c.ano, c.ultimo
    )
    insert into credito_net.movimentacao (ano, requisicao, metodo, rota, requisicao_conteudo)
    select numero.ano, numero.ultimo, left(p_metodo, 10), left(p_rota, 200), p_conteudo
      from numero
    returning movimentacao.ano, movimentacao.requisicao;
$$;

-- Completa a linha com a resposta, uma única vez (não sobrescreve uma resposta já gravada).
create or replace function credito_net.registrar_resposta(
    p_ano smallint, p_requisicao bigint, p_status_http smallint, p_conteudo text,
    p_situacao_requisicao char, p_situacao_resposta char)
returns void
language sql
security definer
set search_path = credito_net, pg_temp
as $$
    update credito_net.movimentacao
       set status_http         = p_status_http,
           resposta_conteudo   = p_conteudo,
           resposta_em         = now(),
           situacao_requisicao = p_situacao_requisicao,
           situacao_resposta   = p_situacao_resposta
     where ano = p_ano
       and requisicao = p_requisicao
       and resposta_em is null;
$$;

comment on function credito_net.registrar_requisicao(varchar, varchar, text) is 'Numera a requisição no ano e grava a chegada. Devolve (ano, requisicao).';
comment on function credito_net.registrar_resposta(smallint, bigint, smallint, text, char, char) is 'Completa a movimentação com a resposta (uma vez só).';

-- A aplicação só executa as funções: não lê, não altera nem apaga as tabelas diretamente.
revoke all on credito_net.contador_requisicao, credito_net.movimentacao from motor_app_net;
revoke all on function credito_net.registrar_requisicao(varchar, varchar, text) from public;
revoke all on function credito_net.registrar_resposta(smallint, bigint, smallint, text, char, char) from public;
grant execute on function credito_net.registrar_requisicao(varchar, varchar, text) to motor_app_net;
grant execute on function credito_net.registrar_resposta(smallint, bigint, smallint, text, char, char) to motor_app_net;
