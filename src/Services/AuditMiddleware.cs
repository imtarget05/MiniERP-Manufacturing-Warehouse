namespace MiniERP.Api.Services;

/// <summary>
/// Best-effort append-only audit middleware. The business transaction remains
/// authoritative; if the audit sink is unavailable the API logs the failure
/// instead of corrupting an already committed inventory result.
/// </summary>
public sealed class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ErpDbService _db;
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(RequestDelegate next, ErpDbService db, ILogger<AuditMiddleware> logger)
    {
        _next = next;
        _db = db;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);
        if (!HttpMethods.IsPost(context.Request.Method) &&
            !HttpMethods.IsPut(context.Request.Method) &&
            !HttpMethods.IsPatch(context.Request.Method) &&
            !HttpMethods.IsDelete(context.Request.Method))
        {
            return;
        }

        var actor = context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ?? "unknown"
            : "anonymous";
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemName, out var value)
            ? value?.ToString() ?? "unknown" : "unknown";
        var entityKey = context.Request.RouteValues.Values
            .Select(v => v?.ToString())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? context.Request.Path.Value;
        var eventType = context.Response.StatusCode is 401 or 403
            ? "SECURITY_DENIED" : "API_MUTATION";
        try
        {
            await _db.WriteAuditEventAsync(eventType, "HTTP", entityKey, actor,
                $"{{\"method\":\"{context.Request.Method}\",\"path\":\"{context.Request.Path}\"," +
                $"\"status\":{context.Response.StatusCode}}}", correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not append audit event for {Path}", context.Request.Path);
        }
    }
}
