using BionicPro.ReportsApi.Olap;

namespace BionicPro.ReportsApi.Reports;

/// <summary>
/// Сборка отчёта из витрины OLAP. В реальном времени ничего не вычисляется:
/// и суточные строки, и агрегат за период читаются из report_daily_by_client,
/// который подготовил Airflow.
/// </summary>
public sealed class ReportService(ClickHouseClient clickHouse, ILogger<ReportService> logger)
{
    private const string Mart = "report_daily_by_client";
    private const int DefaultPeriodDays = 30;
    private const int MaxPeriodDays = 366;

    public async Task<Watermark> GetWatermarkAsync(CancellationToken ct)
    {
        var rows = await clickHouse.QueryAsync<Watermark>(
            """
            SELECT min_ready_date,
                   max_ready_date,
                   -- %i = минуты (в ClickHouse %M — название месяца)
                   formatDateTime(updated_at, '%Y-%m-%dT%H:%i:%S') AS updated_at
            FROM bionic.etl_watermark FINAL
            WHERE mart = {mart:String}
            """,
            new Dictionary<string, string> { ["mart"] = Mart }, ct);

        return rows.Count > 0 ? rows[0] : throw new MartNotReadyException();
    }

    public async Task<ReportResponse> BuildAsync(long clientId, DateOnly? from, DateOnly? to,
                                                 CancellationToken ct)
    {
        var watermark = await GetWatermarkAsync(ct);

        // Умолчания: обе границы заданы — нормализуем порядок; задана одна —
        // достраиваем окно в 30 суток от неё; не задано ничего — последние 30 суток
        // готовых данных. Достраивать «to» водяным знаком нельзя: запрос ?from=<будущее>
        // тогда вывернулся бы в валидный диапазон вместо честного 409.
        DateOnly requestedFrom, requestedTo;
        switch (from, to)
        {
            case (not null, not null):
                (requestedFrom, requestedTo) = from <= to ? (from.Value, to.Value)
                                                          : (to.Value, from.Value);
                break;
            case (not null, null):
                requestedFrom = from.Value;
                requestedTo = from.Value.AddDays(DefaultPeriodDays - 1);
                break;
            case (null, not null):
                requestedTo = to.Value;
                requestedFrom = to.Value.AddDays(-(DefaultPeriodDays - 1));
                break;
            default:
                requestedTo = watermark.MaxReadyDate;
                requestedFrom = requestedTo.AddDays(-(DefaultPeriodDays - 1));
                break;
        }

        // Ключевое ограничение: витрина знает только про обработанные Airflow сутки.
        // Полностью «будущий» запрос — честная ошибка, частично — усечение с пометкой.
        if (requestedFrom > watermark.MaxReadyDate)
        {
            throw new PeriodNotReadyException(requestedFrom, watermark.MaxReadyDate);
        }
        // Зеркальный случай: период целиком раньше начала витрины. Без этой проверки
        // усечение даёт вывернутый диапазон (from > to) и пустой отчёт без объяснения.
        if (requestedTo < watermark.MinReadyDate)
        {
            throw new PeriodOutsideDataException(requestedFrom, requestedTo,
                                                 watermark.MinReadyDate, watermark.MaxReadyDate);
        }

        var effectiveTo = Min(requestedTo, watermark.MaxReadyDate);
        var effectiveFrom = Max(requestedFrom, watermark.MinReadyDate);
        if (effectiveTo.DayNumber - effectiveFrom.DayNumber + 1 > MaxPeriodDays)
        {
            effectiveFrom = effectiveTo.AddDays(-(MaxPeriodDays - 1));
        }

        var clamped = effectiveFrom != requestedFrom || effectiveTo != requestedTo;
        var note = clamped
            ? $"Период усечён до готовых данных: витрина заполнена с {watermark.MinReadyDate:yyyy-MM-dd} " +
              $"по {watermark.MaxReadyDate:yyyy-MM-dd} (обновлена {watermark.UpdatedAt:yyyy-MM-dd HH:mm:ss})."
            : null;

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId.ToString(),
            ["date_from"] = effectiveFrom.ToString("yyyy-MM-dd"),
            ["date_to"] = effectiveTo.ToString("yyyy-MM-dd")
        };

        var daily = await clickHouse.QueryAsync<DailyRow>(
            $$"""
              SELECT report_date, prostheses_count, events_total, gestures_recognized,
                     recognition_rate, avg_latency_ms, p95_latency_ms, max_latency_ms,
                     slow_events, battery_avg_pct, battery_min_pct, emg_quality_avg,
                     errors_count, active_hours, top_gesture
              FROM bionic.{{Mart}} FINAL
              WHERE client_id = {client_id:UInt64}
                AND report_date BETWEEN {date_from:Date} AND {date_to:Date}
              ORDER BY report_date
              """, parameters, ct);

        var summaryRows = await clickHouse.QueryAsync<ReportSummary>(
            // Агрегаты считаются во вложенном запросе под своими именами: иначе
            // псевдоним затеняет одноимённую колонку и ClickHouse видит
            // «агрегат внутри агрегата» (ILLEGAL_AGGREGATION).
            // Средние за период — взвешенные (сумма/сумма), а не среднее суточных
            // средних: иначе день с 10 событиями весит столько же, сколько день с 10 000.
            $$"""
              SELECT a.days                                              AS days,
                     a.s_events                                          AS events_total,
                     a.s_recognized                                      AS gestures_recognized,
                     round(a.s_recognized / nullIf(a.s_events, 0), 4)    AS recognition_rate,
                     round(a.s_latency  / nullIf(a.s_events, 0), 2)      AS avg_latency_ms,
                     round(a.w_p95, 2)                                   AS worst_p95_latency_ms,
                     a.s_slow                                            AS slow_events,
                     round(a.s_slow / nullIf(a.s_events, 0), 4)          AS slow_events_share,
                     round(a.s_battery / nullIf(a.s_events, 0), 2)       AS battery_avg_pct,
                     a.m_battery                                         AS battery_min_pct,
                     round(a.s_emg / nullIf(a.s_events, 0), 3)           AS emg_quality_avg,
                     a.s_errors                                          AS errors_count,
                     round(a.a_hours, 1)                                 AS active_hours_avg,
                     a.m_prostheses                                      AS prostheses_count,
                     a.t_gesture                                         AS top_gesture
              FROM
              (
                  SELECT count()                        AS days,
                         sum(events_total)              AS s_events,
                         sum(gestures_recognized)       AS s_recognized,
                         sum(latency_sum_ms)            AS s_latency,
                         max(p95_latency_ms)            AS w_p95,
                         sum(slow_events)               AS s_slow,
                         sum(battery_sum_pct)           AS s_battery,
                         toUInt8(min(battery_min_pct))  AS m_battery,
                         sum(emg_quality_sum)           AS s_emg,
                         sum(errors_count)              AS s_errors,
                         avg(active_hours)              AS a_hours,
                         toUInt16(max(prostheses_count)) AS m_prostheses,
                         -- жест, чаще всего лидировавший по дням (точная гистограмма
                         -- жестов в витрине не хранится — см. ограничения в Task2)
                         topK(1)(top_gesture)[1]        AS t_gesture
                  FROM bionic.{{Mart}} FINAL
                  WHERE client_id = {client_id:UInt64}
                    AND report_date BETWEEN {date_from:Date} AND {date_to:Date}
              ) AS a
              """, parameters, ct);

        var client = await GetClientAsync(clientId, ct);

        logger.LogInformation(
            "Отчёт собран: client_id={ClientId}, период {From}..{To}, суток={Days}",
            clientId, effectiveFrom, effectiveTo, daily.Count);

        return new ReportResponse(
            client,
            new ReportPeriod(effectiveFrom, effectiveTo, requestedFrom, requestedTo, clamped, note),
            watermark.MaxReadyDate,
            summaryRows.Count > 0 ? summaryRows[0] : new ReportSummary(),
            daily,
            DateTime.UtcNow);
    }

    private async Task<ClientInfo> GetClientAsync(long clientId, CancellationToken ct)
    {
        var rows = await clickHouse.QueryAsync<ClientInfo>(
            """
            SELECT client_id, full_name, city
            FROM bionic.dim_clients FINAL
            WHERE client_id = {client_id:UInt64}
            """,
            new Dictionary<string, string> { ["client_id"] = clientId.ToString() }, ct);

        return rows.Count > 0
            ? rows[0]
            : new ClientInfo { ClientId = clientId, FullName = "", City = "" };
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
