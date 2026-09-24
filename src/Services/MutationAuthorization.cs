using Microsoft.AspNetCore.Authorization;

namespace MiniERP.Api.Services;

/// <summary>Central mutation permission map for legacy and traceability routes.</summary>
public static class MutationAuthorization
{
    public static string? PolicyFor(string method, PathString path)
    {
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) &&
            !HttpMethods.IsPatch(method) && !HttpMethods.IsDelete(method)) return null;
        var p = path.Value ?? string.Empty;
        if (!p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase)) return AuthPolicies.AdminOnly;
        if (p.StartsWith("/api/warehouse/lots/", StringComparison.OrdinalIgnoreCase) &&
            (p.EndsWith("/hold", StringComparison.OrdinalIgnoreCase) ||
             p.EndsWith("/release-hold", StringComparison.OrdinalIgnoreCase)))
            return AuthPolicies.LotHold;
        if (p.StartsWith("/api/procurement/", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.ProcurementMutation;
        if (p.StartsWith("/api/warehouse/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("/api/stock/", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.WarehouseMutation;
        if (p.StartsWith("/api/labels", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.LabelMutation;
        if (p.Contains("/issue-lots", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("/issue-material", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.MaterialIssue;
        if (p.Contains("/complete-traceable", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("/api/manufacturing/", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.ProductionMutation;
        if (p.StartsWith("/api/support/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("/api/integration/helpdesk/", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("/incidents", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("/approvals", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.SupportMutation;
        if (p.Equals("/api/automation/stock/adjust", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.AdminOnly;
        if (p.StartsWith("/api/automation/", StringComparison.OrdinalIgnoreCase))
            return AuthPolicies.OperationsMutation;
        return AuthPolicies.OperationsMutation;
    }
}

public sealed class MutationAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthorizationService _authorization;
    private readonly ILogger<MutationAuthorizationMiddleware> _logger;

    public MutationAuthorizationMiddleware(RequestDelegate next, IAuthorizationService authorization,
        ILogger<MutationAuthorizationMiddleware> logger)
    {
        _next = next;
        _authorization = authorization;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var policy = MutationAuthorization.PolicyFor(context.Request.Method, context.Request.Path);
        if (policy is not null)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    errorCode = "AUTH_REQUIRED",
                    message = "A bearer token is required for this operation."
                });
                return;
            }

            var result = await _authorization.AuthorizeAsync(context.User, null, policy);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Denied {Method} {Path} for {Actor}", context.Request.Method,
                    context.Request.Path, context.User.Identity?.Name);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    errorCode = "AUTH_FORBIDDEN",
                    message = "The authenticated role is not allowed to perform this operation."
                });
                return;
            }
        }

        await _next(context);
    }
}
