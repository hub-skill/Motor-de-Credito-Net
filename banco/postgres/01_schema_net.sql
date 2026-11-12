-- =============================================================================
-- Motor de Crédito .NET – schema credito_net (PostgreSQL 15+)
-- Executado pela ferramenta: dotnet run --project src/MotorCredito.Carga -- migrar
-- (como administrador, no banco motor_credito). Pode ser executado de novo.
--
-- As tabelas do projeto Python ficam no schema "credito" e NÃO são alteradas aqui.
-- Todo registro gravado pelo .NET leva a marca origem_carga = 'DOTNET'.
-- O usuário motor_app_net é criado antes, pela ferramenta, com a senha do .env.
-- =============================================================================

create schema if not exists credito_net;
comment on schema credito_net is 'Motor de crédito .NET: listas da CGU gravadas pela aplicação .NET.';

-- -----------------------------------------------------------------------------
-- Sanções: CEIS (inidôneas e suspensas) + CNEP (punidas), uma linha por sanção
-- -----------------------------------------------------------------------------
create table if not exists credito_net.sancoes (
    id                       bigint generated always as identity,
    cadastro                 varchar(10)    not null,
    codigo_sancao            bigint         not null,
    tipo_pessoa              char(1),
    documento                varchar(20)    not null,
    nome_sancionado          varchar(200),
    nome_informado_orgao     varchar(200),
    razao_social             varchar(200),
    nome_fantasia            varchar(100),
    numero_processo          varchar(80),
    categoria                varchar(120),
    valor_multa              numeric(15,2),
    data_inicio              date,
    data_fim                 date,
    data_publicacao          date,
    publicacao               varchar(150),
    detalhamento_publicacao  text,
    data_transito_julgado    date,
    abrangencia              varchar(100),
    orgao_sancionador        varchar(300),
    uf_orgao                 char(2),
    esfera_orgao             varchar(20),
    fundamentacao_legal      text,
    data_origem              date,
    origem_informacao        varchar(150),
    observacoes              text,
    arquivo_origem           varchar(60)    not null,
    origem_carga             varchar(10)    not null default 'DOTNET'
        constraint ck_sancoes_origem_carga check (origem_carga = 'DOTNET'),
    carregado_em             timestamptz    not null default now(),

    constraint pk_sancoes                    primary key (id),
    constraint uk_sancoes_cadastro_codigo    unique (cadastro, codigo_sancao),
    constraint ck_sancoes_cadastro           check (cadastro in ('CEIS', 'CNEP')),
    constraint ck_sancoes_tipo_pessoa        check (tipo_pessoa in ('F', 'J')),
    constraint ck_sancoes_valor_multa        check (valor_multa >= 0)
);

create index if not exists idx_sancoes_documento on credito_net.sancoes (documento);

comment on table  credito_net.sancoes                         is 'Sanções do CEIS e do CNEP (CGU/Portal da Transparência). Recarregada a cada arquivo diário.';
comment on column credito_net.sancoes.id                      is 'Identificador interno.';
comment on column credito_net.sancoes.cadastro                is 'Lista de origem: CEIS ou CNEP.';
comment on column credito_net.sancoes.codigo_sancao           is 'Código da sanção na CGU. Único por cadastro.';
comment on column credito_net.sancoes.tipo_pessoa             is 'F = pessoa física, J = pessoa jurídica. Nulo quando a CGU não informa.';
comment on column credito_net.sancoes.documento               is 'CPF ou CNPJ do sancionado, só dígitos, como veio no arquivo (pode haver documentos malformados).';
comment on column credito_net.sancoes.nome_sancionado         is 'Nome do sancionado.';
comment on column credito_net.sancoes.nome_informado_orgao    is 'Nome informado pelo órgão sancionador.';
comment on column credito_net.sancoes.razao_social            is 'Razão social no cadastro da Receita (PJ).';
comment on column credito_net.sancoes.nome_fantasia           is 'Nome fantasia no cadastro da Receita (PJ).';
comment on column credito_net.sancoes.numero_processo         is 'Número do processo administrativo ou judicial.';
comment on column credito_net.sancoes.categoria               is 'Categoria da sanção (impedimento, suspensão, inidoneidade, multa...).';
comment on column credito_net.sancoes.valor_multa             is 'Valor da multa em reais (somente CNEP).';
comment on column credito_net.sancoes.data_inicio             is 'Início da sanção.';
comment on column credito_net.sancoes.data_fim                is 'Fim da sanção. Nulo = sem prazo determinado.';
comment on column credito_net.sancoes.data_publicacao         is 'Data de publicação da sanção.';
comment on column credito_net.sancoes.publicacao              is 'Meio de publicação (ex.: Diário Oficial).';
comment on column credito_net.sancoes.detalhamento_publicacao is 'Detalhamento do meio de publicação.';
comment on column credito_net.sancoes.data_transito_julgado   is 'Data do trânsito em julgado, quando houver.';
comment on column credito_net.sancoes.abrangencia             is 'Abrangência da sanção.';
comment on column credito_net.sancoes.orgao_sancionador       is 'Órgão que aplicou a sanção.';
comment on column credito_net.sancoes.uf_orgao                is 'UF do órgão sancionador.';
comment on column credito_net.sancoes.esfera_orgao            is 'Esfera do órgão: FEDERAL, ESTADUAL ou MUNICIPAL.';
comment on column credito_net.sancoes.fundamentacao_legal     is 'Fundamentação legal da sanção.';
comment on column credito_net.sancoes.data_origem             is 'Data da informação na origem.';
comment on column credito_net.sancoes.origem_informacao       is 'Órgão de origem da informação.';
comment on column credito_net.sancoes.observacoes             is 'Observações da CGU.';
comment on column credito_net.sancoes.arquivo_origem          is 'Nome do arquivo de onde a linha foi carregada (ex.: 20261110_CEIS.csv).';
comment on column credito_net.sancoes.carregado_em            is 'Momento da carga.';

-- -----------------------------------------------------------------------------
-- PEP: pessoas expostas politicamente (CPF mascarado pela CGU)
-- -----------------------------------------------------------------------------
create table if not exists credito_net.peps (
    id                     bigint generated always as identity,
    cpf_mascarado          varchar(20)    not null,
    cpf_miolo              varchar(20)    not null,
    nome                   varchar(100)   not null,
    sigla_funcao           varchar(10),
    descricao_funcao       varchar(100),
    nivel_funcao           varchar(10),
    nome_orgao             varchar(100),
    data_inicio_exercicio  date,
    data_fim_exercicio     date,
    data_fim_carencia      date,
    arquivo_origem         varchar(60)    not null,
    origem_carga             varchar(10)    not null default 'DOTNET'
        constraint ck_peps_origem_carga check (origem_carga = 'DOTNET'),
    carregado_em           timestamptz    not null default now(),

    constraint pk_peps primary key (id)
);

create index if not exists idx_peps_cpf_miolo_nome on credito_net.peps (cpf_miolo, nome);

comment on table  credito_net.peps                       is 'Pessoas expostas politicamente (CGU/Portal da Transparência). Recarregada a cada arquivo mensal.';
comment on column credito_net.peps.id                    is 'Identificador interno.';
comment on column credito_net.peps.cpf_mascarado         is 'CPF como publicado pela CGU, mascarado: ***.999.999-**.';
comment on column credito_net.peps.cpf_miolo             is 'Os 6 dígitos visíveis do CPF. Cruzamento com clientes: cpf_miolo + nome.';
comment on column credito_net.peps.nome                  is 'Nome da pessoa exposta politicamente.';
comment on column credito_net.peps.sigla_funcao          is 'Sigla da função (ex.: VEREAD, PREFEI).';
comment on column credito_net.peps.descricao_funcao      is 'Descrição da função exercida.';
comment on column credito_net.peps.nivel_funcao          is 'Nível da função, quando informado.';
comment on column credito_net.peps.nome_orgao            is 'Órgão de exercício.';
comment on column credito_net.peps.data_inicio_exercicio is 'Início do exercício da função.';
comment on column credito_net.peps.data_fim_exercicio    is 'Fim do exercício. Nulo = não informada pela CGU.';
comment on column credito_net.peps.data_fim_carencia     is 'Fim da carência (a pessoa continua PEP até esta data). Nulo = não informada.';
comment on column credito_net.peps.arquivo_origem        is 'Nome do arquivo de onde a linha foi carregada (ex.: 202609_PEP.csv).';
comment on column credito_net.peps.carregado_em          is 'Momento da carga.';

comment on column credito_net.sancoes.origem_carga is 'Aplicação que gravou o registro. Sempre DOTNET neste schema.';
comment on column credito_net.peps.origem_carga    is 'Aplicação que gravou o registro. Sempre DOTNET neste schema.';

-- -----------------------------------------------------------------------------
-- Acesso da aplicação .NET: lê e grava dados, não cria objetos
-- -----------------------------------------------------------------------------
grant connect on database motor_credito to motor_app_net;
grant usage on schema credito_net to motor_app_net;
grant select, insert, update, delete on all tables in schema credito_net to motor_app_net;
grant usage, select on all sequences in schema credito_net to motor_app_net;
alter default privileges in schema credito_net
    grant select, insert, update, delete on tables to motor_app_net;
alter default privileges in schema credito_net
    grant usage, select on sequences to motor_app_net;
alter role motor_app_net in database motor_credito set search_path = credito_net;

-- -----------------------------------------------------------------------------
-- Log de erros da API .NET: uma linha por resposta 4xx/5xx. Só metadados: NUNCA CPF, CNPJ,
-- nome ou corpo da requisição. A aplicação (motor_app_net) só INSERE; não lê, altera nem apaga.
-- Limpeza (como administrador), ex.: manter 90 dias:
--   delete from credito_net.log_erros where ocorrido_em < now() - interval '90 days';
-- -----------------------------------------------------------------------------
create table if not exists credito_net.log_erros (
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

    constraint pk_log_erros          primary key (id),
    constraint ck_log_erros_status   check (status between 400 and 599),
    constraint ck_log_erros_duracao  check (duracao_ms >= 0)
);

create index if not exists idx_log_erros_ocorrido_em on credito_net.log_erros (ocorrido_em);

comment on table  credito_net.log_erros              is 'Respostas de erro (4xx e 5xx) da API .NET. Só metadados, sem dados pessoais.';
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
comment on column credito_net.log_erros.ambiente     is 'Ambiente da API .NET (ASPNETCORE_ENVIRONMENT: Development, Staging, Production).';

-- A aplicação só insere. Os privilégios padrão do schema dariam select/update/delete: retirados aqui.
revoke all on credito_net.log_erros from motor_app_net;
grant insert on credito_net.log_erros to motor_app_net;
