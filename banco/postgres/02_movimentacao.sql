-- =============================================================================
-- Movimentação e log de erros da API (PostgreSQL 15+), versão 2 (ULID, uma gravação por requisição)
-- ANTES de executar, troque credito_net (schema da aplicação, ex.: app) e motor_app_net (o usuário da API).
-- Executar como dono do schema, no banco da aplicação. Pode ser executado de novo: o que existe é mantido.
--
-- Uma linha por requisição com chave de API válida (menos /health e /metrics): o que chegou e o que a API respondeu, gravada de
-- uma vez, em segundo plano, depois da resposta. A chave é o requisicao_id: um ULID de 26 caracteres gerado na API (o mesmo que o
-- cliente recebe em X-Requisicao). Não há contador nem função de numeração. O CPF no conteúdo é gravado mascarado
-- (***.999.999-**) pela própria API.
--
-- A aplicação (motor_app_net) só INSERE em movimentacao e log_erros: não lê, não altera nem apaga.
-- Limpeza (como administrador), ex.: manter 90 dias, apagando primeiro o log de erros:
--   delete from credito_net.log_erros where ocorrido_em < now() - interval '90 days';
--   delete from credito_net.movimentacao where requisicao_em < now() - interval '90 days';
-- Com muito volume, particione movimentacao por requisicao_em (dia ou mês) e descarte as partições antigas.
--
-- MIGRAÇÃO da versão 1 (ano + contador): esta versão NÃO converte os dados. Exporte o que precisar e então remova os objetos antigos:
--   drop table if exists credito_net.log_erros, credito_net.movimentacao, credito_net.contador_requisicao;
--   drop function if exists credito_net.registrar_requisicao(varchar, varchar, text);
--   drop function if exists credito_net.registrar_resposta(smallint, bigint, smallint, text, char, char);
-- =============================================================================

create table if not exists credito_net.movimentacao (
    requisicao_id        varchar(26)   not null,
    metodo               varchar(10)   not null,
    rota                 varchar(200)  not null,
    requisicao_conteudo  text,
    requisicao_em        timestamptz   not null,
    situacao_requisicao  char(2)       not null,
    status_http          smallint      not null,
    resposta_conteudo    text,
    resposta_em          timestamptz   not null,
    situacao_resposta    char(2)       not null,

    constraint pk_movimentacao                     primary key (requisicao_id),
    constraint ck_movimentacao_situacao_requisicao check (situacao_requisicao in ('OK', 'ER')),
    constraint ck_movimentacao_situacao_resposta   check (situacao_resposta in ('OK', 'ER')),
    constraint ck_movimentacao_status_http         check (status_http between 100 and 599)
);

create index if not exists idx_movimentacao_requisicao_em on credito_net.movimentacao (requisicao_em);

comment on table  credito_net.movimentacao                     is 'Requisições recebidas pela API e as respostas dadas. CPF mascarado; sem a chave de API.';
comment on column credito_net.movimentacao.requisicao_id       is 'ULID da requisição (26 caracteres), gerado na API e devolvido ao cliente em X-Requisicao. Carrega a hora de chegada.';
comment on column credito_net.movimentacao.metodo              is 'Método HTTP (POST, GET...).';
comment on column credito_net.movimentacao.rota                is 'Caminho pedido, sem query string (truncado em 200 caracteres).';
comment on column credito_net.movimentacao.requisicao_conteudo is 'Corpo recebido, como texto, em qualquer formato (até 32 KB, CPF mascarado). Nulo se não houve corpo.';
comment on column credito_net.movimentacao.requisicao_em       is 'Momento em que a requisição chegou.';
comment on column credito_net.movimentacao.situacao_requisicao is 'OK = aceita (chave e entrada válidas); ER = recusada (respostas 4xx).';
comment on column credito_net.movimentacao.status_http         is 'Status HTTP da resposta.';
comment on column credito_net.movimentacao.resposta_conteudo   is 'Corpo da resposta, como texto (até 32 KB, CPF mascarado).';
comment on column credito_net.movimentacao.resposta_em         is 'Momento em que a API terminou a resposta.';
comment on column credito_net.movimentacao.situacao_resposta   is 'OK = sucesso (2xx); ER = erro (4xx ou 5xx).';

-- Log de erros: uma linha por resposta 4xx/5xx. Só metadados: NUNCA CPF, CNPJ, nome ou corpo da requisição.
-- É gravado na mesma transação da sua movimentação.
create table if not exists credito_net.log_erros (
    requisicao_id varchar(26)   not null,
    id            bigint        generated always as identity,
    ocorrido_em   timestamptz   not null default now(),
    request_id    varchar(64)   not null,
    metodo        varchar(10)   not null,
    rota          varchar(200)  not null,
    status        smallint      not null,
    codigo        varchar(40)   not null,
    titulo        varchar(200)  not null,
    tipo_excecao  varchar(200),
    duracao_ms    integer,
    ambiente      varchar(20)   not null,

    constraint pk_log_erros               primary key (id),
    constraint fk_log_erros_movimentacao  foreign key (requisicao_id) references credito_net.movimentacao (requisicao_id),
    constraint ck_log_erros_status        check (status between 400 and 599),
    constraint ck_log_erros_duracao       check (duracao_ms >= 0)
);

create index if not exists idx_log_erros_ocorrido_em on credito_net.log_erros (ocorrido_em);
create index if not exists idx_log_erros_requisicao on credito_net.log_erros (requisicao_id);

comment on table  credito_net.log_erros              is 'Respostas de erro (4xx e 5xx) da API. Só metadados, sem dados pessoais.';
comment on column credito_net.log_erros.requisicao_id is 'ULID da requisição na movimentação (o mesmo do header X-Requisicao).';
comment on column credito_net.log_erros.id           is 'Identificador interno.';
comment on column credito_net.log_erros.ocorrido_em  is 'Momento da gravação (alguns milissegundos depois da resposta).';
comment on column credito_net.log_erros.request_id   is 'Mesmo request_id devolvido ao cliente no corpo do erro e no header X-Request-ID.';
comment on column credito_net.log_erros.metodo       is 'Método HTTP (POST, GET...).';
comment on column credito_net.log_erros.rota         is 'Caminho pedido, sem query string (truncado em 200 caracteres).';
comment on column credito_net.log_erros.status       is 'Status HTTP da resposta (400 a 599).';
comment on column credito_net.log_erros.codigo       is 'Campo code do erro (ex.: entrada_invalida, banco_indisponivel).';
comment on column credito_net.log_erros.titulo       is 'Campo title do erro.';
comment on column credito_net.log_erros.tipo_excecao is 'Tipo da exceção, só em erro_interno (500). O stack trace fica no log da aplicação.';
comment on column credito_net.log_erros.duracao_ms   is 'Tempo da requisição até o erro, em milissegundos.';
comment on column credito_net.log_erros.ambiente     is 'Ambiente da API (dev, staging, prod).';

-- A aplicação só insere: os privilégios padrão do schema dariam select/update/delete, retirados aqui.
revoke all on credito_net.movimentacao, credito_net.log_erros from motor_app_net;
grant insert on credito_net.movimentacao, credito_net.log_erros to motor_app_net;
