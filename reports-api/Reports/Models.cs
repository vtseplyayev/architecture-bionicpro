using System.Text.Json.Serialization;

namespace BionicPro.ReportsApi.Reports;

// JsonPropertyName проставлены явно: ClickHouse отдаёт snake_case, наружу
// сериализуем как есть — контракт API совпадает с именами колонок витрины.

/// <summary>Строка витрины report_daily_by_client — сутки работы протезов клиента.</summary>
public sealed record DailyRow
{
    [JsonPropertyName("report_date")]         public DateOnly ReportDate { get; init; }
    [JsonPropertyName("prostheses_count")]    public int ProsthesesCount { get; init; }
    [JsonPropertyName("events_total")]        public long EventsTotal { get; init; }
    [JsonPropertyName("gestures_recognized")] public long GesturesRecognized { get; init; }
    [JsonPropertyName("recognition_rate")]    public double RecognitionRate { get; init; }
    [JsonPropertyName("avg_latency_ms")]      public double AvgLatencyMs { get; init; }
    [JsonPropertyName("p95_latency_ms")]      public double P95LatencyMs { get; init; }
    [JsonPropertyName("max_latency_ms")]      public int MaxLatencyMs { get; init; }
    [JsonPropertyName("slow_events")]         public long SlowEvents { get; init; }
    [JsonPropertyName("battery_avg_pct")]     public double BatteryAvgPct { get; init; }
    [JsonPropertyName("battery_min_pct")]     public int BatteryMinPct { get; init; }
    [JsonPropertyName("emg_quality_avg")]     public double EmgQualityAvg { get; init; }
    [JsonPropertyName("errors_count")]        public long ErrorsCount { get; init; }
    [JsonPropertyName("active_hours")]        public int ActiveHours { get; init; }
    [JsonPropertyName("top_gesture")]         public string TopGesture { get; init; } = "";
}

/// <summary>Агрегат за период — считается в ClickHouse, не в приложении.</summary>
public sealed record ReportSummary
{
    [JsonPropertyName("days")]                 public long Days { get; init; }
    [JsonPropertyName("events_total")]         public long EventsTotal { get; init; }
    [JsonPropertyName("gestures_recognized")]  public long GesturesRecognized { get; init; }
    [JsonPropertyName("recognition_rate")]     public double? RecognitionRate { get; init; }
    [JsonPropertyName("avg_latency_ms")]       public double? AvgLatencyMs { get; init; }
    [JsonPropertyName("worst_p95_latency_ms")] public double? WorstP95LatencyMs { get; init; }
    [JsonPropertyName("slow_events")]          public long SlowEvents { get; init; }
    [JsonPropertyName("slow_events_share")]    public double? SlowEventsShare { get; init; }
    [JsonPropertyName("battery_avg_pct")]      public double? BatteryAvgPct { get; init; }
    [JsonPropertyName("battery_min_pct")]      public int? BatteryMinPct { get; init; }
    [JsonPropertyName("emg_quality_avg")]      public double? EmgQualityAvg { get; init; }
    [JsonPropertyName("errors_count")]         public long ErrorsCount { get; init; }
    [JsonPropertyName("active_hours_avg")]     public double? ActiveHoursAvg { get; init; }
    [JsonPropertyName("prostheses_count")]     public int ProsthesesCount { get; init; }
    [JsonPropertyName("top_gesture")]          public string TopGesture { get; init; } = "";
}

/// <summary>Граница обработанного Airflow периода.</summary>
public sealed record Watermark
{
    [JsonPropertyName("min_ready_date")] public DateOnly MinReadyDate { get; init; }
    [JsonPropertyName("max_ready_date")] public DateOnly MaxReadyDate { get; init; }
    [JsonPropertyName("updated_at")]     public DateTime UpdatedAt { get; init; }
}

/// <summary>Карточка клиента из измерения dim_clients.</summary>
public sealed record ClientInfo
{
    [JsonPropertyName("client_id")] public long ClientId { get; init; }
    [JsonPropertyName("full_name")] public string FullName { get; init; } = "";
    [JsonPropertyName("city")]      public string City { get; init; } = "";
}

public sealed record ReportPeriod(
    [property: JsonPropertyName("from")]           DateOnly From,
    [property: JsonPropertyName("to")]             DateOnly To,
    [property: JsonPropertyName("requested_from")] DateOnly RequestedFrom,
    [property: JsonPropertyName("requested_to")]   DateOnly RequestedTo,
    [property: JsonPropertyName("clamped")]        bool Clamped,
    [property: JsonPropertyName("note")]           string? Note);

public sealed record ReportResponse(
    [property: JsonPropertyName("client")]             ClientInfo Client,
    [property: JsonPropertyName("period")]             ReportPeriod Period,
    [property: JsonPropertyName("data_ready_through")] DateOnly DataReadyThrough,
    [property: JsonPropertyName("summary")]            ReportSummary Summary,
    [property: JsonPropertyName("daily")]              IReadOnlyList<DailyRow> Daily,
    [property: JsonPropertyName("generated_at")]       DateTime GeneratedAt);

/// <summary>Отчёт невозможен: запрошенный период Airflow ещё не обработал.</summary>
public sealed class PeriodNotReadyException(DateOnly requestedFrom, DateOnly readyThrough)
    : Exception($"Данные за период с {requestedFrom:yyyy-MM-dd} ещё не обработаны. " +
                $"Витрина готова по {readyThrough:yyyy-MM-dd} включительно.")
{
    public DateOnly RequestedFrom { get; } = requestedFrom;
    public DateOnly ReadyThrough { get; } = readyThrough;
}

/// <summary>
/// Запрошенный период целиком раньше начала витрины: данных нет и не появится
/// (сырьё старше TTL уже удалено). Молча вернуть пустой отчёт нельзя — пользователь
/// прочитает его как «протез не работал».
/// </summary>
public sealed class PeriodOutsideDataException(DateOnly requestedFrom, DateOnly requestedTo,
                                               DateOnly minReady, DateOnly maxReady)
    : Exception($"Запрошенный период {requestedFrom:yyyy-MM-dd}…{requestedTo:yyyy-MM-dd} " +
                $"не пересекается с данными витрины: она заполнена " +
                $"с {minReady:yyyy-MM-dd} по {maxReady:yyyy-MM-dd}.");

/// <summary>Витрина пуста — ETL ещё ни разу не отработал.</summary>
public sealed class MartNotReadyException()
    : Exception("Витрина отчётности ещё не построена: дождитесь первого запуска DAG bionicpro_reports_etl.");
