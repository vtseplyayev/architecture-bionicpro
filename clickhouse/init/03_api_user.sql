-- Reports API ходит в OLAP отдельным пользователем и только на чтение:
-- скомпрометированный сервис не сможет изменить или удалить витрину.
-- Запись выполняет Airflow под default.

CREATE USER IF NOT EXISTS reports_api
    IDENTIFIED WITH plaintext_password BY 'reports_api_password'
    -- readonly = 2: только чтение, но настройки запроса менять можно
    -- (клиент передаёт format/timeout в query string).
    SETTINGS max_execution_time = 30, max_result_rows = 100000, readonly = 2;

GRANT SELECT ON bionic.report_daily_by_client TO reports_api;
GRANT SELECT ON bionic.dim_clients            TO reports_api;
GRANT SELECT ON bionic.etl_watermark          TO reports_api;
