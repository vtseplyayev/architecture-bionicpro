-- Имитация потока Telemetry Gateway: 90 дней телеметрии по 4 протезам.
-- В проде эти строки пишет сам шлюз; здесь генерируем детерминированно
-- (cityHash64 вместо rand — чтобы стенд был воспроизводимым).
-- Профили моделей отличаются: X1 (5003) медленнее, X3 (5004) точнее.

INSERT INTO bionic.telemetry_events
    (event_id, prosthesis_id, event_time, gesture, recognized, latency_ms, battery_pct, emg_quality, error_code)
SELECT
    generateUUIDv4() AS event_id,
    prosthesis_id,
    ts AS event_time,
    ['grip', 'pinch', 'point', 'open', 'rotate', 'wrist_turn'][(h1 % 6) + 1] AS gesture,
    if((h2 % 1000) < (930 + model_accuracy), 1, 0) AS recognized,
    toUInt16(42 + (h3 % 48) + model_latency + if((h3 % 100) < 5, 65, 0)) AS latency_ms,
    toUInt8(least(100, greatest(5,
        100 - intDiv((toHour(ts) * 3600 + toMinute(ts) * 60 - 21600) * 70, 57600) + (h4 % 7) - 3
    ))) AS battery_pct,
    toFloat32(round(0.60 + (h5 % 380) / 1000.0, 3)) AS emg_quality,
    multiIf(
        (h6 % 1000) < 12, 'EMG_NOISE',
        (h6 % 1000) < 18, 'BATT_LOW',
        (h6 % 1000) < 21, 'ACT_OVERHEAT',
        ''
    ) AS error_code
FROM
(
    SELECT
        prosthesis_id,
        -- активность с 06:00 до 22:00
        toDateTime(today() - d) + toIntervalSecond(21600 + (cityHash64(prosthesis_id, d, e, 'ts') % 57600)) AS ts,
        cityHash64(prosthesis_id, d, e, 'g') AS h1,
        cityHash64(prosthesis_id, d, e, 'r') AS h2,
        cityHash64(prosthesis_id, d, e, 'l') AS h3,
        cityHash64(prosthesis_id, d, e, 'b') AS h4,
        cityHash64(prosthesis_id, d, e, 'q') AS h5,
        cityHash64(prosthesis_id, d, e, 'x') AS h6,
        multiIf(prosthesis_id = 5003, 18, prosthesis_id = 5004, -6, 0) AS model_latency,
        multiIf(prosthesis_id = 5003, -25, prosthesis_id = 5004, 45, 0) AS model_accuracy
    FROM (SELECT arrayJoin([5001, 5002, 5003, 5004]) AS prosthesis_id) AS pr
    CROSS JOIN (SELECT arrayJoin(range(90)) AS d) AS days
    CROSS JOIN (SELECT arrayJoin(range(180)) AS e) AS evt
) AS gen;
