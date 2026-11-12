-- =============================================================================
-- Motor de Crédito .NET – movimentação da API (Oracle 21c)
-- Executado pela ferramenta: dotnet run --project src/MotorCredito.Carga -- migrar
-- (como administrador, depois de 01_schema_net.sql e antes de 03_log_erros.sql).
-- Cada comando termina numa linha com "/". Reexecutar não dá problema.
--
-- Uma linha por requisição com chave de API válida (menos /health e /metrics): o que chegou e o
-- que a API respondeu. Chave: (ano, requisicao); o contador volta a 1 em todo ano novo.
-- CPF no conteúdo é gravado mascarado (***.999.999-**) pela própria API.
--
-- A aplicação (MOTOR_APP_NET) não tem privilégio nas tabelas: só executa as duas procedures
-- abaixo (direitos do dono), que numeram, gravam e completam a linha uma única vez.
-- Limpeza (como administrador), ex.: manter 90 dias:
--   DELETE FROM motor_credito_net.log_erros WHERE ocorrido_em < SYSTIMESTAMP - INTERVAL '90' DAY;
--   DELETE FROM motor_credito_net.movimentacao WHERE requisicao_em < SYSTIMESTAMP - INTERVAL '90' DAY;
-- =============================================================================

CREATE TABLE motor_credito_net.contador_requisicao (
    ano     NUMBER(4)   NOT NULL,
    ultimo  NUMBER(12)  NOT NULL,

    CONSTRAINT pk_contador_requisicao        PRIMARY KEY (ano),
    CONSTRAINT ck_contador_requisicao_ultimo CHECK (ultimo >= 1)
)
/
COMMENT ON TABLE motor_credito_net.contador_requisicao IS 'Último número de requisição usado em cada ano (a numeração da movimentação volta a 1 em todo ano novo).'
/
COMMENT ON COLUMN motor_credito_net.contador_requisicao.ano IS 'Ano (horário de Brasília).'
/
COMMENT ON COLUMN motor_credito_net.contador_requisicao.ultimo IS 'Último número entregue neste ano.'
/
CREATE TABLE motor_credito_net.movimentacao (
    ano                  NUMBER(4)      NOT NULL,
    requisicao           NUMBER(12)     NOT NULL,
    metodo               VARCHAR2(10)   NOT NULL,
    rota                 VARCHAR2(200)  NOT NULL,
    requisicao_conteudo  CLOB,
    requisicao_em        TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    situacao_requisicao  CHAR(2),
    status_http          NUMBER(3),
    resposta_conteudo    CLOB,
    resposta_em          TIMESTAMP WITH TIME ZONE,
    situacao_resposta    CHAR(2),

    CONSTRAINT pk_movimentacao                     PRIMARY KEY (ano, requisicao),
    CONSTRAINT ck_movimentacao_situacao_requisicao CHECK (situacao_requisicao IN ('OK', 'ER')),
    CONSTRAINT ck_movimentacao_situacao_resposta   CHECK (situacao_resposta IN ('OK', 'ER')),
    CONSTRAINT ck_movimentacao_status_http         CHECK (status_http BETWEEN 100 AND 599)
)
/
CREATE INDEX motor_credito_net.idx_movimentacao_requisicao_em ON motor_credito_net.movimentacao (requisicao_em)
/
COMMENT ON TABLE motor_credito_net.movimentacao IS 'Requisições recebidas pela API .NET e as respostas dadas. CPF mascarado; sem a chave de API.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.ano IS 'Ano da requisição (horário de Brasília). Chave junto com requisicao.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.requisicao IS 'Número da requisição no ano (começa em 1 a cada ano). Devolvido ao cliente no header X-Requisicao.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.metodo IS 'Método HTTP (POST, GET...).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.rota IS 'Caminho pedido, sem query string (truncado em 200 caracteres).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.requisicao_conteudo IS 'Corpo recebido, como texto, em qualquer formato (até 32 KB, CPF mascarado). Nulo se não houve corpo.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.requisicao_em IS 'Momento em que a requisição chegou.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.situacao_requisicao IS 'OK = aceita (chave e entrada válidas); ER = recusada (respostas 4xx). Nulo enquanto não houver resposta.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.status_http IS 'Status HTTP da resposta.'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.resposta_conteudo IS 'Corpo da resposta, como texto (até 32 KB, CPF mascarado).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.resposta_em IS 'Momento da resposta. Nulo = a API não chegou a responder (ex.: caiu no meio).'
/
COMMENT ON COLUMN motor_credito_net.movimentacao.situacao_resposta IS 'OK = sucesso (2xx); ER = erro (4xx ou 5xx).'
/
-- Numera e grava a chegada. A linha do ano no contador fica travada até o fim da transação (curta):
-- duas requisições ao mesmo tempo nunca recebem o mesmo número.
CREATE OR REPLACE PROCEDURE motor_credito_net.registrar_requisicao (
    p_metodo     IN  VARCHAR2,
    p_rota       IN  VARCHAR2,
    p_conteudo   IN  CLOB,
    p_ano        OUT NUMBER,
    p_requisicao OUT NUMBER
) AUTHID DEFINER AS
BEGIN
    p_ano := EXTRACT(YEAR FROM (SYSTIMESTAMP AT TIME ZONE 'America/Sao_Paulo'));
    UPDATE contador_requisicao SET ultimo = ultimo + 1 WHERE ano = p_ano RETURNING ultimo INTO p_requisicao;
    IF SQL%ROWCOUNT = 0 THEN
        BEGIN
            INSERT INTO contador_requisicao (ano, ultimo) VALUES (p_ano, 1);
            p_requisicao := 1;
        EXCEPTION
            WHEN DUP_VAL_ON_INDEX THEN -- outra requisição abriu o ano ao mesmo tempo
                UPDATE contador_requisicao SET ultimo = ultimo + 1 WHERE ano = p_ano RETURNING ultimo INTO p_requisicao;
        END;
    END IF;
    INSERT INTO movimentacao (ano, requisicao, metodo, rota, requisicao_conteudo)
    VALUES (p_ano, p_requisicao, SUBSTR(p_metodo, 1, 10), SUBSTR(p_rota, 1, 200), p_conteudo);
END;
/
-- Completa a linha com a resposta, uma única vez (não sobrescreve uma resposta já gravada).
CREATE OR REPLACE PROCEDURE motor_credito_net.registrar_resposta (
    p_ano                 IN NUMBER,
    p_requisicao          IN NUMBER,
    p_status_http         IN NUMBER,
    p_conteudo            IN CLOB,
    p_situacao_requisicao IN VARCHAR2,
    p_situacao_resposta   IN VARCHAR2
) AUTHID DEFINER AS
BEGIN
    UPDATE movimentacao
       SET status_http         = p_status_http,
           resposta_conteudo   = p_conteudo,
           resposta_em         = SYSTIMESTAMP,
           situacao_requisicao = p_situacao_requisicao,
           situacao_resposta   = p_situacao_resposta
     WHERE ano = p_ano
       AND requisicao = p_requisicao
       AND resposta_em IS NULL;
END;
/
-- A aplicação só executa as procedures (pelos sinônimos): não lê, não altera nem apaga as tabelas.
GRANT EXECUTE ON motor_credito_net.registrar_requisicao TO motor_app_net
/
GRANT EXECUTE ON motor_credito_net.registrar_resposta TO motor_app_net
/
CREATE OR REPLACE SYNONYM motor_app_net.registrar_requisicao FOR motor_credito_net.registrar_requisicao
/
CREATE OR REPLACE SYNONYM motor_app_net.registrar_resposta FOR motor_credito_net.registrar_resposta
/
