-- =============================================================================
-- Motor de Crédito .NET – log de erros da API (Oracle 21c)
-- Executado pela ferramenta: dotnet run --project src/MotorCredito.Carga -- migrar
-- (como administrador, depois de 02_movimentacao.sql).
-- Cada comando termina numa linha com "/". Reexecutar não dá problema.
--
-- Uma linha por resposta de erro (4xx e 5xx) da API. Só metadados: NUNCA CPF, CNPJ, nome
-- ou corpo da requisição (o corpo, mascarado, fica na movimentação). A aplicação (MOTOR_APP_NET)
-- só INSERE; não lê, altera nem apaga.
-- (ano, requisicao) são as primeiras colunas, obrigatórias, e apontam para a movimentação do
-- erro: sem número de requisição (banco fora na chegada), o erro vai só para o log da aplicação.
-- Limpeza (como administrador), ex.: manter 90 dias:
--   DELETE FROM motor_credito_net.log_erros WHERE ocorrido_em < SYSTIMESTAMP - INTERVAL '90' DAY;
-- =============================================================================

-- Versão anterior (sem ano/requisicao): as linhas dela não têm movimentação e não são copiadas.
-- A tabela é guardada como LOG_ERROS_V1, sem acesso da aplicação; apague quando quiser:
--   DROP TABLE motor_credito_net.log_erros_v1 PURGE;
DECLARE
    v_existe  NUMBER;
    v_tem_ano NUMBER;
BEGIN
    SELECT COUNT(*) INTO v_existe FROM all_tables WHERE owner = 'MOTOR_CREDITO_NET' AND table_name = 'LOG_ERROS';
    SELECT COUNT(*) INTO v_tem_ano
      FROM all_tab_columns
     WHERE owner = 'MOTOR_CREDITO_NET' AND table_name = 'LOG_ERROS' AND column_name = 'ANO';
    IF v_existe = 1 AND v_tem_ano = 0 THEN
        EXECUTE IMMEDIATE 'ALTER TABLE motor_credito_net.log_erros RENAME TO log_erros_v1';
        EXECUTE IMMEDIATE 'ALTER TABLE motor_credito_net.log_erros_v1 RENAME CONSTRAINT pk_log_erros TO pk_log_erros_v1';
        EXECUTE IMMEDIATE 'ALTER INDEX motor_credito_net.pk_log_erros RENAME TO pk_log_erros_v1';
        EXECUTE IMMEDIATE 'ALTER TABLE motor_credito_net.log_erros_v1 RENAME CONSTRAINT ck_log_erros_status TO ck_log_erros_v1_status';
        EXECUTE IMMEDIATE 'ALTER TABLE motor_credito_net.log_erros_v1 RENAME CONSTRAINT ck_log_erros_duracao TO ck_log_erros_v1_duracao';
        EXECUTE IMMEDIATE 'ALTER INDEX motor_credito_net.idx_log_erros_ocorrido_em RENAME TO idx_log_erros_v1_ocorrido_em';
        EXECUTE IMMEDIATE 'REVOKE INSERT ON motor_credito_net.log_erros_v1 FROM motor_app_net';
        EXECUTE IMMEDIATE q'[COMMENT ON TABLE motor_credito_net.log_erros_v1 IS 'Versão anterior da LOG_ERROS (sem ano/requisicao). Não é mais usada pela API.']';
    END IF;
END;
/
CREATE TABLE motor_credito_net.log_erros (
    ano           NUMBER(4)      NOT NULL,
    requisicao    NUMBER(12)     NOT NULL,
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
    CONSTRAINT fk_log_erros_movimentacao FOREIGN KEY (ano, requisicao) REFERENCES motor_credito_net.movimentacao (ano, requisicao),
    CONSTRAINT ck_log_erros_status       CHECK (status BETWEEN 400 AND 599),
    CONSTRAINT ck_log_erros_duracao      CHECK (duracao_ms >= 0)
)
/
CREATE INDEX motor_credito_net.idx_log_erros_ocorrido_em ON motor_credito_net.log_erros (ocorrido_em)
/
CREATE INDEX motor_credito_net.idx_log_erros_requisicao ON motor_credito_net.log_erros (ano, requisicao)
/
COMMENT ON TABLE motor_credito_net.log_erros IS 'Respostas de erro (4xx e 5xx) da API .NET. Só metadados, sem dados pessoais.'
/
COMMENT ON COLUMN motor_credito_net.log_erros.ano IS 'Ano da requisição na movimentação (chave junto com requisicao).'
/
COMMENT ON COLUMN motor_credito_net.log_erros.requisicao IS 'Número da requisição na movimentação (o mesmo do header X-Requisicao).'
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
COMMENT ON COLUMN motor_credito_net.log_erros.ambiente IS 'Ambiente da API .NET (Development, Staging, Production).'
/
-- A aplicação só insere (sem SELECT, UPDATE ou DELETE) e enxerga a tabela pelo sinônimo.
GRANT INSERT ON motor_credito_net.log_erros TO motor_app_net
/
CREATE OR REPLACE SYNONYM motor_app_net.log_erros FOR motor_credito_net.log_erros
/
