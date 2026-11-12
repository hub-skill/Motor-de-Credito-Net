-- =============================================================================
-- Motor de Crédito .NET – log de erros da API (PostgreSQL)
-- Executado pela ferramenta: dotnet run --project src/MotorCredito.Carga -- migrar
-- (como administrador, depois de 02_movimentacao.sql). Pode ser executado de novo.
--
-- Uma linha por resposta de erro (4xx e 5xx) da API. Só metadados: NUNCA CPF, CNPJ, nome
-- ou corpo da requisição (o corpo, mascarado, fica na movimentação). A aplicação (motor_app_net)
-- só INSERE; não lê, altera nem apaga.
-- (ano, requisicao) são as primeiras colunas, obrigatórias, e apontam para a movimentação do
-- erro: sem número de requisição (banco fora na chegada), o erro vai só para o log da aplicação.
-- Limpeza (como administrador), ex.: manter 90 dias:
--   delete from credito_net.log_erros where ocorrido_em < now() - interval '90 days';
-- =============================================================================

-- Versão anterior (sem ano/requisicao): as linhas dela não têm movimentação e não são copiadas.
-- A tabela é guardada como log_erros_v1, sem acesso da aplicação; apague quando quiser:
--   drop table credito_net.log_erros_v1;
do $$
begin
    if to_regclass('credito_net.log_erros') is not null and not exists (
        select 1 from information_schema.columns
         where table_schema = 'credito_net' and table_name = 'log_erros' and column_name = 'ano'
    ) then
        alter table credito_net.log_erros rename to log_erros_v1;
        alter table credito_net.log_erros_v1 rename constraint pk_log_erros to pk_log_erros_v1;
        alter table credito_net.log_erros_v1 rename constraint ck_log_erros_status to ck_log_erros_v1_status;
        alter table credito_net.log_erros_v1 rename constraint ck_log_erros_duracao to ck_log_erros_v1_duracao;
        alter index credito_net.idx_log_erros_ocorrido_em rename to idx_log_erros_v1_ocorrido_em;
        revoke all on credito_net.log_erros_v1 from motor_app_net;
        comment on table credito_net.log_erros_v1 is 'Versão anterior da log_erros (sem ano/requisicao). Não é mais usada pela API.';
    end if;
end
$$;

create table if not exists credito_net.log_erros (
    ano           smallint      not null,
    requisicao    bigint        not null,
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
    constraint fk_log_erros_movimentacao  foreign key (ano, requisicao) references credito_net.movimentacao (ano, requisicao),
    constraint ck_log_erros_status        check (status between 400 and 599),
    constraint ck_log_erros_duracao       check (duracao_ms >= 0)
);

create index if not exists idx_log_erros_ocorrido_em on credito_net.log_erros (ocorrido_em);
create index if not exists idx_log_erros_requisicao on credito_net.log_erros (ano, requisicao);

comment on table  credito_net.log_erros              is 'Respostas de erro (4xx e 5xx) da API .NET. Só metadados, sem dados pessoais.';
comment on column credito_net.log_erros.ano          is 'Ano da requisição na movimentação (chave junto com requisicao).';
comment on column credito_net.log_erros.requisicao   is 'Número da requisição na movimentação (o mesmo do header X-Requisicao).';
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
comment on column credito_net.log_erros.ambiente     is 'Ambiente da API .NET (Development, Staging, Production).';

-- A aplicação só insere. Os privilégios padrão do schema dariam select/update/delete: retirados aqui.
revoke all on credito_net.log_erros from motor_app_net;
grant insert on credito_net.log_erros to motor_app_net;
