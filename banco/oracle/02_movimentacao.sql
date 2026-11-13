-- =============================================================================
-- Movimentação e log de erros da API (Oracle 21c), versão 2 (ULID, uma gravação por requisição)
-- ANTES de executar, troque motor_credito_net (schema dono das tabelas, sem login) e motor_app_net (o usuário da API).
-- Executar como DBA no banco plugável. Cada comando termina numa linha com "/". Reexecutar não dá problema.
--
-- Uma linha por requisição com chave de API válida (menos /health e /metrics): o que chegou e o que a API respondeu, gravada de
-- uma vez, em segundo plano, depois da resposta. A chave é o requisicao_id: um ULID de 26 caracteres gerado na API (o mesmo que o
-- cliente recebe em X-Requisicao). Não há contador nem procedure de numeração. O CPF no conteúdo é gravado mascarado
-- (***.999.999-**) pela própria API.
--
-- A aplicação (motor_app_net) só INSERE em movimentacao e log_erros (pelos sinônimos): não lê, não altera nem apaga.
-- Limpeza (como administrador), ex.: manter 90 dias, apagando primeiro o log de erros:
--   DELETE FROM motor_credito_net.log_erros WHERE ocorrido_em < SYSTIMESTAMP - INTERVAL '90' DAY;
--   DELETE FROM motor_credito_net.movimentacao WHERE requisicao_em < SYSTIMESTAMP - INTERVAL '90' DAY;
-- Com muito volume, particione movimentacao por requisicao_em e descarte as partições antigas.
--
-- MIGRAÇÃO da versão 1 (ano + contador): esta versão NÃO converte os dados. Exporte o que precisar e então remova os objetos antigos:
--   DROP TABLE motor_credito_net.log_erros;  DROP TABLE motor_credito_net.movimentacao;  DROP TABLE motor_credito_net.contador_requisicao;
--   DROP PROCEDURE motor_credito_net.registrar_requisicao;  DROP PROCEDURE motor_credito_net.registrar_resposta;
--   (e os sinônimos antigos de motor_app_net: registrar_requisicao, registrar_resposta)
-- =============================================================================

CREATE TABLE motor_credito_net.movimentacao (
    requisicao_id        VARCHAR2(26)   NOT NULL,
    metodo               VARCHAR2(10)   NOT NULL,
    rota                 VARCHAR2(200)  NOT NULL,
    requisicao_conteudo  CLOB,
    requisicao_em        TIMESTAMP WITH TIME ZONE NOT NULL,
    situacao_requisicao  CHAR(2)        NOT NULL,
    status_http          NUMBER(3)      NOT NULL,
    resposta_conteudo    CLOB,
    resposta_em          TIMESTAMP WITH TIME ZONE NOT NULL,
    situacao_resposta    CHAR(2)        NOT NULL,

    CONSTRAINT pk_movimentacao                     PRIMARY KEY (requisicao_id),
    CONSTRAINT ck_movimentacao_situacao_requisicao CHECK (situacao_requisicao IN ('OK', 'ER')),
    CONSTRAINT ck_movimentacao_situacao_resposta   CHECK (situacao_resposta IN ('OK', 'ER')),
    CONSTRAINT ck_movimentacao_status_http         CHECK (status_http BETWEEN 100 AND 599)
)
/
CREATE INDEX motor_credito_net.idx_movimentacao_requisicao_em ON motor_credito_net.movimentacao (requisicao_em)
/
COMMENT ON TABLE motor_credito_net.movimentacao IS 'Requisições recebidas pela API e as respostas dadas. CPF mascarado; sem a chave de API.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.requisicao_id IS 'ULID da requisição (26 caracteres), gerado na API e devolvido ao cliente em X-Requisicao. Carrega a hora de chegada.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.metodo IS 'Método HTTP (POST, GET...).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.rota IS 'Caminho pedido, sem query string (truncado em 200 caracteres).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.requisicao_conteudo IS 'Corpo recebido, como texto, em qualquer formato (até 32 KB, CPF mascarado). Nulo se não houve corpo.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.requisicao_em IS 'Momento em que a requisição chegou.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.situacao_requisicao IS 'OK = aceita (chave e entrada válidas); ER = recusada (respostas 4xx).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.status_http IS 'Status HTTP da resposta.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.resposta_conteudo IS 'Corpo da resposta, como texto (até 32 KB, CPF mascarado).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.resposta_em IS 'Momento em que a API terminou a resposta.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.situacao_resposta IS 'OK = sucesso (2xx); ER = erro (4xx ou 5xx).'
/
CREATE TABLE motor_credito_net.log_erros (
    requisicao_id VARCHAR2(26)   NOT NULL,
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

    CONSTRAINT pk_log_erros              PRIMARY KEY (id),
    CONSTRAINT fk_log_erros_movimentacao FOREIGN KEY (requisicao_id) REFERENCES motor_credito_net.movimentacao (requisicao_id),
    CONSTRAINT ck_log_erros_status       CHECK (status BETWEEN 400 AND 599),
    CONSTRAINT ck_log_erros_duracao      CHECK (duracao_ms >= 0)
)
/
CREATE INDEX motor_credito_net.idx_log_erros_ocorrido_em ON motor_credito_net.log_erros (ocorrido_em)
/
CREATE INDEX motor_credito_net.idx_log_erros_requisicao ON motor_credito_net.log_erros (requisicao_id)
/
COMMENT ON TABLE motor_credito_net.log_erros IS 'Respostas de erro (4xx e 5xx) da API. Só metadados, sem dados pessoais. Gravado na mesma transação da sua movimentação.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.requisicao_id IS 'ULID da requisição na movimentação (o mesmo do header X-Requisicao).'
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
COMMENT ON COLUMN motor_credito_net.log_erros.ambiente IS 'Ambiente da API (dev, staging, prod).'
/
-- A aplicação só insere (sem SELECT, UPDATE ou DELETE) e enxerga as tabelas pelos sinônimos.
GRANT INSERT ON motor_credito_net.movimentacao TO motor_app_net
/
GRANT INSERT ON motor_credito_net.log_erros TO motor_app_net
/
CREATE OR REPLACE SYNONYM motor_app_net.movimentacao FOR motor_credito_net.movimentacao
/
CREATE OR REPLACE SYNONYM motor_app_net.log_erros FOR motor_credito_net.log_erros
/
