CREATE DATABASE IF NOT EXISTS bionic;

-- =====================================================================
-- 1. Сырая телеметрия. Пишется Telemetry Gateway напрямую из чипа (4G).
--    Airflow её НЕ переносит — она уже в OLAP; ETL только агрегирует.
-- =====================================================================
CREATE TABLE IF NOT EXISTS bionic.telemetry_events
(
    event_id       UUID,
    prosthesis_id  UInt64,
    event_time     DateTime,
    gesture        LowCardinality(String),
    recognized     UInt8,          -- 1 = движение распознано корректно
    latency_ms     UInt16,         -- время реакции протеза, целевое < 100 мс
    battery_pct    UInt8,
    emg_quality    Float32,        -- качество миосигнала 0..1
    error_code     LowCardinality(String)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(event_time)
ORDER BY (prosthesis_id, event_time)
TTL event_time + INTERVAL 90 DAY;   -- сырьё живёт 90 дней, дальше только витрина

-- =====================================================================
-- 2. Измерения из CRM. Загружаются DAG'ом bionicpro_reports_etl.
--    ReplacingMergeTree — повторный запуск задачи идемпотентен.
-- =====================================================================
CREATE TABLE IF NOT EXISTS bionic.dim_clients
(
    client_id         UInt64,
    keycloak_username String,
    full_name         String,
    city              String,
    contract_no       String,
    registered_at     Date,
    loaded_at         DateTime
)
ENGINE = ReplacingMergeTree(loaded_at)
ORDER BY client_id;

CREATE TABLE IF NOT EXISTS bionic.dim_prostheses
(
    prosthesis_id    UInt64,
    client_id        UInt64,
    model            String,
    serial_number    String,
    side             LowCardinality(String),
    delivered_at     Date,
    telemetry_opt_in UInt8,
    loaded_at        DateTime
)
ENGINE = ReplacingMergeTree(loaded_at)
ORDER BY prosthesis_id;

-- =====================================================================
-- 3. Витрина отчётности. ORDER BY (client_id, report_date) — первичный
--    индекс сразу даёт точечный доступ по пользователю за период:
--    именно это требование «быстрого доступа по пользователям».
--    ReplacingMergeTree(updated_at) делает пересчёт дня идемпотентным.
-- =====================================================================
CREATE TABLE IF NOT EXISTS bionic.report_daily_by_client
(
    client_id           UInt64,
    report_date         Date,
    full_name           String,
    city                String,
    prostheses_count    UInt16,
    events_total        UInt64,
    gestures_recognized UInt64,
    recognition_rate    Float32,   -- доля распознанных движений
    avg_latency_ms      Float32,
    p95_latency_ms      Float32,
    max_latency_ms      UInt16,
    slow_events         UInt64,    -- события с latency >= 100 мс
    battery_avg_pct     Float32,
    battery_min_pct     UInt8,
    emg_quality_avg     Float32,
    errors_count        UInt64,
    active_hours        UInt8,     -- часов с активностью за сутки
    top_gesture         String,    -- самый частый жест ДНЯ
    -- Суммы нужны, чтобы агрегат за период считался взвешенно по числу событий.
    -- Среднее от суточных средних врёт, когда дни неравномерны по нагрузке.
    latency_sum_ms      UInt64,
    battery_sum_pct     UInt64,
    emg_quality_sum     Float64,
    updated_at          DateTime
)
ENGINE = ReplacingMergeTree(updated_at)
PARTITION BY toYYYYMM(report_date)
ORDER BY (client_id, report_date);

-- =====================================================================
-- 4. Водяной знак ETL. Reports API читает его, чтобы не отдавать отчёт
--    за период, который Airflow ещё не обработал.
-- =====================================================================
CREATE TABLE IF NOT EXISTS bionic.etl_watermark
(
    mart           String,
    min_ready_date Date,
    max_ready_date Date,
    updated_at     DateTime
)
ENGINE = ReplacingMergeTree(updated_at)
ORDER BY mart;
