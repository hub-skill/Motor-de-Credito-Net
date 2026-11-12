-- =============================================================================
-- Motor de Crédito .NET – schema MOTOR_CREDITO_NET (Oracle 21c, banco plugável XEPDB1)
-- Executado pela ferramenta: dotnet run --project src/MotorCredito.Carga -- migrar
-- (como administrador). Cada comando termina numa linha com "/". Reexecutar é seguro.
--
-- As tabelas do projeto Python ficam em MOTOR_CREDITO e NÃO são alteradas aqui.
-- Todo registro gravado pelo .NET leva a marca origem_carga = 'DOTNET'.
-- Os usuários MOTOR_CREDITO_NET (sem login) e MOTOR_APP_NET são criados antes, pela ferramenta.
-- =============================================================================

CREATE TABLE motor_credito_net.sancoes (
    id                       NUMBER GENERATED ALWAYS AS IDENTITY,
    cadastro                 VARCHAR2(10)   NOT NULL,
    codigo_sancao            NUMBER(12)     NOT NULL,
    tipo_pessoa              CHAR(1),
    documento                VARCHAR2(20)   NOT NULL,
    nome_sancionado          VARCHAR2(200),
    nome_informado_orgao     VARCHAR2(200),
    razao_social             VARCHAR2(200),
    nome_fantasia            VARCHAR2(100),
    numero_processo          VARCHAR2(80),
    categoria                VARCHAR2(120),
    valor_multa              NUMBER(15,2),
    data_inicio              DATE,
    data_fim                 DATE,
    data_publicacao          DATE,
    publicacao               VARCHAR2(150),
    detalhamento_publicacao  CLOB,
    data_transito_julgado    DATE,
    abrangencia              VARCHAR2(100),
    orgao_sancionador        VARCHAR2(300),
    uf_orgao                 CHAR(2),
    esfera_orgao             VARCHAR2(20),
    fundamentacao_legal      CLOB,
    data_origem              DATE,
    origem_informacao        VARCHAR2(150),
    observacoes              CLOB,
    arquivo_origem           VARCHAR2(60)   NOT NULL,
    origem_carga             VARCHAR2(10)   DEFAULT 'DOTNET' NOT NULL
        CONSTRAINT ck_sancoes_origem_carga CHECK (origem_carga = 'DOTNET'),
    carregado_em             TIMESTAMP      DEFAULT SYSTIMESTAMP NOT NULL,

    CONSTRAINT pk_sancoes                 PRIMARY KEY (id),
    CONSTRAINT uk_sancoes_cadastro_codigo UNIQUE (cadastro, codigo_sancao),
    CONSTRAINT ck_sancoes_cadastro        CHECK (cadastro IN ('CEIS', 'CNEP')),
    CONSTRAINT ck_sancoes_tipo_pessoa     CHECK (tipo_pessoa IN ('F', 'J')),
    CONSTRAINT ck_sancoes_valor_multa     CHECK (valor_multa >= 0)
)
/
CREATE INDEX motor_credito_net.idx_sancoes_documento ON motor_credito_net.sancoes (documento)
/
COMMENT ON TABLE motor_credito_net.sancoes IS 'Sanções do CEIS e do CNEP (CGU/Portal da Transparência). Recarregada a cada arquivo diário.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.id IS 'Identificador interno.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.cadastro IS 'Lista de origem: CEIS ou CNEP.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.codigo_sancao IS 'Código da sanção na CGU. Único por cadastro.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.tipo_pessoa IS 'F = pessoa física, J = pessoa jurídica. Nulo quando a CGU não informa.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.documento IS 'CPF ou CNPJ do sancionado, só dígitos, como veio no arquivo (pode haver documentos malformados).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.nome_sancionado IS 'Nome do sancionado.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.nome_informado_orgao IS 'Nome informado pelo órgão sancionador.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.razao_social IS 'Razão social no cadastro da Receita (PJ).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.nome_fantasia IS 'Nome fantasia no cadastro da Receita (PJ).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.numero_processo IS 'Número do processo administrativo ou judicial.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.categoria IS 'Categoria da sanção (impedimento, suspensão, inidoneidade, multa...).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.valor_multa IS 'Valor da multa em reais (somente CNEP).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.data_inicio IS 'Início da sanção.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.data_fim IS 'Fim da sanção. Nulo = sem prazo determinado.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.data_publicacao IS 'Data de publicação da sanção.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.publicacao IS 'Meio de publicação (ex.: Diário Oficial).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.detalhamento_publicacao IS 'Detalhamento do meio de publicação.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.data_transito_julgado IS 'Data do trânsito em julgado, quando houver.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.abrangencia IS 'Abrangência da sanção.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.orgao_sancionador IS 'Órgão que aplicou a sanção.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.uf_orgao IS 'UF do órgão sancionador.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.esfera_orgao IS 'Esfera do órgão: FEDERAL, ESTADUAL ou MUNICIPAL.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.fundamentacao_legal IS 'Fundamentação legal da sanção.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.data_origem IS 'Data da informação na origem.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.origem_informacao IS 'Órgão de origem da informação.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.observacoes IS 'Observações da CGU.'
/
COMMENT ON COLUMN motor_credito_net.sancoes.arquivo_origem IS 'Nome do arquivo de onde a linha foi carregada (ex.: 20261110_CEIS.csv).'
/
COMMENT ON COLUMN motor_credito_net.sancoes.carregado_em IS 'Momento da carga.'
/
CREATE TABLE motor_credito_net.peps (
    id                     NUMBER GENERATED ALWAYS AS IDENTITY,
    cpf_mascarado          VARCHAR2(20)   NOT NULL,
    cpf_miolo              VARCHAR2(20)   NOT NULL,
    nome                   VARCHAR2(100)  NOT NULL,
    sigla_funcao           VARCHAR2(10),
    descricao_funcao       VARCHAR2(100),
    nivel_funcao           VARCHAR2(10),
    nome_orgao             VARCHAR2(100),
    data_inicio_exercicio  DATE,
    data_fim_exercicio     DATE,
    data_fim_carencia      DATE,
    arquivo_origem         VARCHAR2(60)   NOT NULL,
    origem_carga             VARCHAR2(10)   DEFAULT 'DOTNET' NOT NULL
        CONSTRAINT ck_peps_origem_carga CHECK (origem_carga = 'DOTNET'),
    carregado_em           TIMESTAMP      DEFAULT SYSTIMESTAMP NOT NULL,

    CONSTRAINT pk_peps PRIMARY KEY (id)
)
/
CREATE INDEX motor_credito_net.idx_peps_cpf_miolo_nome ON motor_credito_net.peps (cpf_miolo, nome)
/
COMMENT ON TABLE motor_credito_net.peps IS 'Pessoas expostas politicamente (CGU/Portal da Transparência). Recarregada a cada arquivo mensal.'
/
COMMENT ON COLUMN motor_credito_net.peps.id IS 'Identificador interno.'
/
COMMENT ON COLUMN motor_credito_net.peps.cpf_mascarado IS 'CPF como publicado pela CGU, mascarado: ***.999.999-**.'
/
COMMENT ON COLUMN motor_credito_net.peps.cpf_miolo IS 'Os 6 dígitos visíveis do CPF. Cruzamento com clientes: cpf_miolo + nome.'
/
COMMENT ON COLUMN motor_credito_net.peps.nome IS 'Nome da pessoa exposta politicamente.'
/
COMMENT ON COLUMN motor_credito_net.peps.sigla_funcao IS 'Sigla da função (ex.: VEREAD, PREFEI).'
/
COMMENT ON COLUMN motor_credito_net.peps.descricao_funcao IS 'Descrição da função exercida.'
/
COMMENT ON COLUMN motor_credito_net.peps.nivel_funcao IS 'Nível da função, quando informado.'
/
COMMENT ON COLUMN motor_credito_net.peps.nome_orgao IS 'Órgão de exercício.'
/
COMMENT ON COLUMN motor_credito_net.peps.data_inicio_exercicio IS 'Início do exercício da função.'
/
COMMENT ON COLUMN motor_credito_net.peps.data_fim_exercicio IS 'Fim do exercício. Nulo = não informada pela CGU.'
/
COMMENT ON COLUMN motor_credito_net.peps.data_fim_carencia IS 'Fim da carência (a pessoa continua PEP até esta data). Nulo = não informada.'
/
COMMENT ON COLUMN motor_credito_net.peps.arquivo_origem IS 'Nome do arquivo de onde a linha foi carregada (ex.: 202609_PEP.csv).'
/
COMMENT ON COLUMN motor_credito_net.peps.carregado_em IS 'Momento da carga.'
/
-- Acesso da aplicação: privilégios de objeto + sinônimos privados (sem digitar o schema)
GRANT SELECT, INSERT, UPDATE, DELETE ON motor_credito_net.sancoes TO motor_app_net
/
GRANT SELECT, INSERT, UPDATE, DELETE ON motor_credito_net.peps TO motor_app_net
/
CREATE OR REPLACE SYNONYM motor_app_net.sancoes FOR motor_credito_net.sancoes
/
CREATE OR REPLACE SYNONYM motor_app_net.peps FOR motor_credito_net.peps
/
COMMENT ON COLUMN motor_credito_net.sancoes.origem_carga IS 'Aplicação que gravou o registro. Sempre DOTNET neste schema.'
/
COMMENT ON COLUMN motor_credito_net.peps.origem_carga IS 'Aplicação que gravou o registro. Sempre DOTNET neste schema.'
/
-- -----------------------------------------------------------------------------
-- Log de erros da API .NET: uma linha por resposta 4xx/5xx. Só metadados: NUNCA CPF, CNPJ,
-- nome ou corpo da requisição. A aplicação (MOTOR_APP_NET) só INSERE; não lê, altera nem apaga.
-- Limpeza (como administrador), ex.: manter 90 dias:
--   DELETE FROM motor_credito_net.log_erros WHERE ocorrido_em < SYSTIMESTAMP - INTERVAL '90' DAY;
-- -----------------------------------------------------------------------------
CREATE TABLE motor_credito_net.log_erros (
    id            NUMBER GENERATED ALWAYS AS IDENTITY,
    ocorrido_em   TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    request_id    VARCHAR2(64)   NOT NULL,
    metodo        VARCHAR2(10)   NOT NULL,
    rota          VARCHAR2(200)  NOT NULL,
    status        NUMBER(3)      NOT NULL,
    codigo        VARCHAR2(40)   NOT NULL,
    titulo        VARCHAR2(200)  NOT NULL,
    tipo_excecao  VARCHAR2(200),
    duracao_ms    NUMBER(10),
    ambiente      VARCHAR2(20)   NOT NULL,

    CONSTRAINT pk_log_erros         PRIMARY KEY (id),
    CONSTRAINT ck_log_erros_status  CHECK (status BETWEEN 400 AND 599),
    CONSTRAINT ck_log_erros_duracao CHECK (duracao_ms >= 0)
)
/
CREATE INDEX motor_credito_net.idx_log_erros_ocorrido_em ON motor_credito_net.log_erros (ocorrido_em)
/
COMMENT ON TABLE motor_credito_net.log_erros IS 'Respostas de erro (4xx e 5xx) da API .NET. Só metadados, sem dados pessoais.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.id IS 'Identificador interno.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.ocorrido_em IS 'Momento da gravação (alguns milissegundos depois da resposta).'
/
COMMENT ON COLUMN motor_credito_net.log_erros.request_id IS 'Mesmo request_id devolvido ao cliente no corpo do erro e no header X-Request-ID.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.metodo IS 'Método HTTP (POST, GET...).'
/
COMMENT ON COLUMN motor_credito_net.log_erros.rota IS 'Caminho pedido, sem query string (truncado em 200 caracteres).'
/
COMMENT ON COLUMN motor_credito_net.log_erros.status IS 'Status HTTP da resposta (400 a 599).'
/
COMMENT ON COLUMN motor_credito_net.log_erros.codigo IS 'Campo code do erro (ex.: entrada_invalida, banco_indisponivel).'
/
COMMENT ON COLUMN motor_credito_net.log_erros.titulo IS 'Campo title do erro.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.tipo_excecao IS 'Tipo da exceção, só em erro_interno (500). O stack trace fica no log da aplicação.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.duracao_ms IS 'Tempo da requisição até o erro, em milissegundos.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.ambiente IS 'Ambiente da API .NET (ASPNETCORE_ENVIRONMENT: Development, Staging, Production).'
/
-- A aplicação só insere (sem SELECT, UPDATE ou DELETE) e enxerga a tabela pelo sinônimo.
GRANT INSERT ON motor_credito_net.log_erros TO motor_app_net
/
CREATE OR REPLACE SYNONYM motor_app_net.log_erros FOR motor_credito_net.log_erros
/
