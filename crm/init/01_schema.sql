-- Источник CRM (OLTP). Здесь живут ПДн и медицинские данные пациентов:
-- они остаются в региональном контуре и в OLAP уезжают только в минимально
-- необходимом объёме (см. Task2/README.md, раздел «Хранение и минимизация ПДн»).

CREATE TABLE clients (
    client_id         BIGINT PRIMARY KEY,
    keycloak_username TEXT        NOT NULL UNIQUE,  -- связка с учётной записью в IdP
    full_name         TEXT        NOT NULL,
    email             TEXT        NOT NULL,
    city              TEXT        NOT NULL,
    contract_no       TEXT        NOT NULL,
    registered_at     DATE        NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE prostheses (
    prosthesis_id     BIGINT PRIMARY KEY,
    client_id         BIGINT      NOT NULL REFERENCES clients(client_id),
    model             TEXT        NOT NULL,
    serial_number     TEXT        NOT NULL UNIQUE,
    side              TEXT        NOT NULL CHECK (side IN ('left', 'right')),
    delivered_at      DATE        NOT NULL,
    telemetry_opt_in  BOOLEAN     NOT NULL DEFAULT TRUE,  -- пользователь может отключить сбор
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Инкрементальная выгрузка в ETL идёт по updated_at
CREATE INDEX idx_clients_updated_at    ON clients (updated_at);
CREATE INDEX idx_prostheses_updated_at ON prostheses (updated_at);
CREATE INDEX idx_prostheses_client     ON prostheses (client_id);
