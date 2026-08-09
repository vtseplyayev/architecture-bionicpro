using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace BionicPro.ReportsApi.Auth;

public static class KeycloakAuth
{
    public const string ReportsPolicy = "reports:read";
    public const string ProstheticRole = "prothetic_user";

    /// <summary>Claim с идентификатором клиента BionicPRO. Кладётся protocol mapper'ом Keycloak.</summary>
    public const string ClientIdClaim = "bionic_client_id";

    public static IServiceCollection AddKeycloakAuth(this IServiceCollection services,
                                                     IConfiguration configuration)
    {
        var section = configuration.GetSection("Keycloak");
        var validIssuers = section.GetSection("ValidIssuers").Get<string[]>() ?? [];
        var audience = section["Audience"] ?? "reports-api";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Метаданные тянем по внутреннему адресу (контейнерная сеть), а issuer
                // в токене — тот, по которому в Keycloak ходил браузер. Отсюда список
                // допустимых issuer'ов вместо одного значения.
                options.MetadataAddress = section["MetadataAddress"]!;
                options.RequireHttpsMetadata = section.GetValue("RequireHttpsMetadata", false);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = validIssuers,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = ClaimTypes.Role
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        MapRealmRoles(context.Principal);
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Auth")
                            .LogWarning("Токен отклонён: {Message}", context.Exception.Message);
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(ReportsPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ProstheticRole));

        return services;
    }

    /// <summary>
    /// Keycloak кладёт роли в realm_access.roles — ASP.NET о такой структуре не знает,
    /// поэтому разворачиваем её в обычные role-claims.
    /// </summary>
    private static void MapRealmRoles(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity identity) return;

        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess)) return;

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (!document.RootElement.TryGetProperty("roles", out var roles)) return;
            foreach (var role in roles.EnumerateArray())
            {
                var value = role.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, value));
                }
            }
        }
        catch (JsonException)
        {
            // Некорректный realm_access — просто не даём ролей, доступ закроется политикой.
        }
    }

    /// <summary>Идентификатор клиента берётся ТОЛЬКО из токена, никогда из запроса.</summary>
    public static long? ResolveClientId(this ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst(ClientIdClaim)?.Value, out var clientId) ? clientId : null;

    public static string UserName(this ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirst("sub")?.Value ?? "unknown";
}
