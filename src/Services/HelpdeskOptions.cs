namespace MiniERP.Api.Services;

public sealed class HelpdeskOptions
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public string IntegrationKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 5;
    public int MaxAttempts { get; init; } = 3;
    public int RetryDelayMs { get; init; } = 250;
    public string IncidentPath { get; init; } = "/api/incidents";

    public static HelpdeskOptions FromConfiguration(IConfiguration configuration)
    {
        var enabledValue = Environment.GetEnvironmentVariable("HELPDESK_INTEGRATION_ENABLED")
            ?? configuration["HELPDESK_INTEGRATION_ENABLED"]
            ?? configuration["Helpdesk:Enabled"];
        var enabled = bool.TryParse(enabledValue, out var value) && value;
        var baseUrl = Environment.GetEnvironmentVariable("HELPDESK_BASE_URL")
            ?? configuration["HELPDESK_BASE_URL"]
            ?? configuration["Helpdesk:BaseUrl"] ?? string.Empty;
        var key = Environment.GetEnvironmentVariable("HELPDESK_INTEGRATION_KEY")
            ?? configuration["HELPDESK_INTEGRATION_KEY"]
            ?? configuration["Helpdesk:IntegrationKey"] ?? string.Empty;
        var timeoutValue = Environment.GetEnvironmentVariable("HELPDESK_TIMEOUT_SECONDS")
            ?? configuration["HELPDESK_TIMEOUT_SECONDS"]
            ?? configuration["Helpdesk:TimeoutSeconds"];
        var timeout = int.TryParse(timeoutValue, out var t) ? t : 5;
        var attemptsValue = Environment.GetEnvironmentVariable("HELPDESK_MAX_ATTEMPTS")
            ?? configuration["HELPDESK_MAX_ATTEMPTS"]
            ?? configuration["Helpdesk:MaxAttempts"];
        var attempts = int.TryParse(attemptsValue, out var a) ? a : 3;
        var delayValue = Environment.GetEnvironmentVariable("HELPDESK_RETRY_DELAY_MS")
            ?? configuration["HELPDESK_RETRY_DELAY_MS"]
            ?? configuration["Helpdesk:RetryDelayMs"];
        var delay = int.TryParse(delayValue, out var d) ? d : 250;
        var path = Environment.GetEnvironmentVariable("HELPDESK_INCIDENT_PATH")
            ?? configuration["HELPDESK_INCIDENT_PATH"]
            ?? configuration["Helpdesk:IncidentPath"] ?? "/api/incidents";
        return new HelpdeskOptions
        {
            Enabled = enabled,
            BaseUrl = baseUrl.Trim().TrimEnd('/'),
            IntegrationKey = key,
            TimeoutSeconds = Math.Clamp(timeout, 1, 60),
            MaxAttempts = Math.Clamp(attempts, 1, 5),
            RetryDelayMs = Math.Clamp(delay, 0, 5000),
            IncidentPath = string.IsNullOrWhiteSpace(path) ? "/api/incidents" : path
        };
    }
}

public static class HelpdeskSecurity
{
    private static readonly string[] SensitiveFragments =
    {
        "bearer ", "bearer", "eyj", "password", "passwd", "token", "secret", "integration_key", "connectionstring",
        "user id=", "data source=", "jdbc:", "select ", "insert ", "update ", "delete "
    };

    public static string Sanitize(string? value, int maxLength = 2000)
    {
        var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        foreach (var fragment in SensitiveFragments)
        {
            var index = text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var start = Math.Max(0, index - 32);
                var end = Math.Min(text.Length, index + fragment.Length + 32);
                text = text.Remove(index, end - index).Insert(index, "[REDACTED]");
                index = text.IndexOf(fragment, index + 10, StringComparison.OrdinalIgnoreCase);
            }
        }
        text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public static string? SafeError(Exception exception) =>
        Sanitize(exception.Message, 500);
}
