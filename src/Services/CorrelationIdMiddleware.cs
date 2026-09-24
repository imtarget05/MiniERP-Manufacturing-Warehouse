using System.Diagnostics;

namespace MiniERP.Api.Services;

/// <summary>Adds a validated correlation ID to every request.</summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemName = "MiniERP.CorrelationId";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Normalize(context.Request.Headers[HeaderName].FirstOrDefault());
        context.Items[ItemName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        var started = Stopwatch.GetTimestamp();
        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId,
            ["actor"] = context.User.Identity?.Name ?? "anonymous",
            ["route"] = context.Request.Path.Value,
        }))
        {
            await _next(context);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _logger.LogInformation("HTTP {Method} {Path} completed with {StatusCode} in {ElapsedMs:F1} ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("N");
        var trimmed = value.Trim();
        return trimmed.Length is > 0 and <= 60 &&
            trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            ? trimmed
            : Guid.NewGuid().ToString("N");
    }
}
