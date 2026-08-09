-- Демонстрационные данные. keycloak_username соответствует пользователям
-- realm reports-realm; client_id продублирован в атрибут пользователя Keycloak
-- (claim bionic_client_id) — по нему Reports API определяет владельца отчёта.

INSERT INTO clients (client_id, keycloak_username, full_name, email, city, contract_no, registered_at) VALUES
    (1001, 'prothetic1', 'Иванов Иван Иванович',    'prothetic1@example.com', 'Москва',          'BP-2024-1001', '2024-03-11'),
    (1002, 'prothetic2', 'Петрова Мария Сергеевна', 'prothetic2@example.com', 'Санкт-Петербург', 'BP-2024-1002', '2024-06-02'),
    (1003, 'prothetic3', 'Сидоров Алексей Павлович','prothetic3@example.com', 'Казань',          'BP-2025-1003', '2025-01-20');

INSERT INTO prostheses (prosthesis_id, client_id, model, serial_number, side, delivered_at, telemetry_opt_in) VALUES
    (5001, 1001, 'BionicPRO Hand X2', 'SN-X2-5001', 'right', '2024-04-25', TRUE),
    (5002, 1001, 'BionicPRO Hand X2', 'SN-X2-5002', 'left',  '2024-09-14', TRUE),
    (5003, 1002, 'BionicPRO Hand X1', 'SN-X1-5003', 'right', '2024-07-19', TRUE),
    (5004, 1003, 'BionicPRO Hand X3', 'SN-X3-5004', 'left',  '2025-02-28', TRUE);
