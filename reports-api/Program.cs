using System.Security.Claims;
using BionicPro.ReportsApi.Auth;
using BionicPro.ReportsApi.Olap;
using BionicPro.ReportsApi.Reports;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClickHouseOptions>(builder.Configuration.GetSection("ClickHouse"));
builder.Services.AddHttpClient<ClickHouseClient>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddProblemDetails();
builder.Services.AddKeycloakAuth(builder.Configuration);

const string CorsPolicy = "spa";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .WithMethods("GET")
    .WithHeaders("Authorization", "Content-Type")));

var app = builder.Build();

// Ошибки предметной области → внятные коды ответа.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title, detail) = error switch
    {
        PeriodNotReadyException ex => (StatusCodes.Status409Conflict,
            "Данные за период ещё не готовы", ex.Message),
        PeriodOutsideDataException ex => (StatusCodes.Status409Conflict,
            "Период вне диапазона витрины", ex.Message),
        MartNotReadyException ex => (StatusCodes.Status503ServiceUnavailable,
            "Витрина отчётности не готова", ex.Message),
        ClickHouseException => (StatusCodes.Status502BadGateway,
            "OLAP-хранилище недоступно", "Не удалось получить данные из ClickHouse."),
        _ => (StatusCodes.Status500InternalServerError,
            "Внутренняя ошибка", "Обратитесь в поддержку.")
    };

    await Results.Problem(title: title, detail: detail, statusCode: status)
                 .ExecuteAsync(context);
}));

app.UseStatusCodePages();
app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (ClickHouseClient clickHouse, CancellationToken ct) =>
        await clickHouse.IsHealthyAsync(ct)
            ? Results.Ok(new { status = "ok", olap = "up" })
            : Results.Json(new { status = "degraded", olap = "down" }, statusCode: 503))
   .AllowAnonymous();

// Все отчётные эндпоинты требуют аутентификации и роли prothetic_user.
var reports = app.MapGroup("/reports").RequireAuthorization(KeycloakAuth.ReportsPolicy);

// Граница готовности данных: UI показывает её и не даёт запросить «будущее».
reports.MapGet("/meta", async (ReportService service, CancellationToken ct) =>
{
    var watermark = await service.GetWatermarkAsync(ct);
    return Results.Ok(new
    {
        data_ready_from = watermark.MinReadyDate,
        data_ready_through = watermark.MaxReadyDate,
        mart_updated_at = watermark.UpdatedAt
    });
});

// Основной эндпоинт: отчёт по СЕБЕ. Идентификатор клиента приходит из токена,
// параметра «чей отчёт» здесь нет в принципе — IDOR структурно невозможен.
reports.MapGet("", async (ClaimsPrincipal user, ReportService service,
                          DateOnly? from, DateOnly? to, CancellationToken ct) =>
{
    var clientId = user.ResolveClientId();
    if (clientId is null)
    {
        return Results.Problem(
            title: "Учётная запись не связана с клиентом BionicPRO",
            detail: "В токене нет claim bionic_client_id — отчёт строить не по кому.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(await service.BuildAsync(clientId.Value, from, to, ct));
});

// Явная проверка ограничения из Задачи 4: чужой отчёт недоступен.
reports.MapGet("/{clientId:long}", async (long clientId, ClaimsPrincipal user, ReportService service,
                                          ILoggerFactory loggerFactory,
                                          DateOnly? from, DateOnly? to, CancellationToken ct) =>
{
    var ownClientId = user.ResolveClientId();
    if (ownClientId is null || ownClientId != clientId)
    {
        loggerFactory.CreateLogger("Audit").LogWarning(
            "Отказ в доступе: пользователь {User} (client_id={Own}) запросил отчёт client_id={Requested}",
            user.UserName(), ownClientId?.ToString() ?? "-", clientId);

        return Results.Problem(
            title: "Доступ запрещён",
            detail: "Отчёт доступен только в отношении собственных протезов.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(await service.BuildAsync(clientId, from, to, ct));
});

app.Run();
