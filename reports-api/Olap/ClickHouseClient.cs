using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BionicPro.ReportsApi.Olap;

public sealed class ClickHouseOptions
{
    public string Url { get; set; } = "http://clickhouse:8123/";
    public string Database { get; set; } = "bionic";
    public string User { get; set; } = "default";
    public string Password { get; set; } = "";
}

/// <summary>
/// Тонкий клиент к ClickHouse поверх HTTP-интерфейса.
/// Значения передаются серверными параметрами ({name:Type} + param_name=...),
/// то есть подстановки в текст запроса не происходит и SQL-инъекция невозможна.
/// </summary>
public sealed class ClickHouseClient(HttpClient http, IOptions<ClickHouseOptions> options,
                                     ILogger<ClickHouseClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ClickHouseOptions _opt = options.Value;

    public async Task<List<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, string>? parameters,
                                             CancellationToken ct)
    {
        var query = new List<string>
        {
            $"database={Uri.EscapeDataString(_opt.Database)}",
            "default_format=JSONEachRow",
            // Без этого ClickHouse отдаёт 64-битные целые строками.
            "output_format_json_quote_64bit_integers=0",
            "max_execution_time=15"
        };
        foreach (var (name, value) in parameters ?? new Dictionary<string, string>())
        {
            query.Add($"param_{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }

        var url = _opt.Url.TrimEnd('/') + "/?" + string.Join('&', query);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(sql, Encoding.UTF8, "text/plain")
        };
        if (!string.IsNullOrEmpty(_opt.User))
        {
            request.Headers.Add("X-ClickHouse-User", _opt.User);
            request.Headers.Add("X-ClickHouse-Key", _opt.Password);
        }

        using var response = await http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("ClickHouse вернул {Status}: {Body}", (int)response.StatusCode,
                            payload.Length > 500 ? payload[..500] : payload);
            throw new ClickHouseException($"ClickHouse вернул {(int)response.StatusCode}");
        }

        var rows = new List<T>();
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var row = JsonSerializer.Deserialize<T>(line, JsonOptions);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var rows = await QueryAsync<Dictionary<string, JsonElement>>("SELECT 1 AS ok", null, ct);
            return rows.Count == 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ClickHouse недоступен");
            return false;
        }
    }
}

public sealed class ClickHouseException(string message) : Exception(message);
