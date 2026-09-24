using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using MiniERP.Api;
using MiniERP.Api.Models;
using MiniERP.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var jwtOptions = JwtOptions.FromConfiguration(builder.Configuration, builder.Environment);
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").GetChildren()
    .Select(x => x.Value)
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Select(x => x!.Trim())
    .ToArray();
if (allowedOrigins.Length == 0)
{
    var envOrigins = builder.Configuration["CORS_ALLOWED_ORIGINS"];
    if (!string.IsNullOrWhiteSpace(envOrigins))
        allowedOrigins = envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
if (allowedOrigins.Length == 0 && builder.Environment.IsDevelopment())
    allowedOrigins = new[] { "http://localhost:5000", "http://localhost:3000" };

// ---------------------------------------------------------------- services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Mini ERP API — Manufacturing & Warehouse Execution",
        Version = "v1",
        Description = "Core REST API connecting ASP.NET Core (.NET 8) to Oracle Database " +
                      "19c/21c/23c through the PL/SQL packages (Dapper micro-ORM)."
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<RefreshTokenStore>();
builder.Services.AddSingleton<ErpDbService>();
var helpdeskOptions = HelpdeskOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(helpdeskOptions);
builder.Services.AddHttpClient<HelpdeskIntegrationService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(helpdeskOptions.TimeoutSeconds);
});
builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, MiniErpBearerHandler>("Bearer", _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.WarehouseMutation,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Warehouse, ErpRoles.CanonicalWarehouse));
    options.AddPolicy(AuthPolicies.ProductionMutation,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Production, ErpRoles.Planner, "PRODUCTION_PLANNER"));
    options.AddPolicy(AuthPolicies.ProcurementMutation,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Procurement, ErpRoles.Warehouse, ErpRoles.CanonicalWarehouse));
    options.AddPolicy(AuthPolicies.MaterialIssue,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Warehouse, ErpRoles.CanonicalWarehouse, ErpRoles.Production, ErpRoles.Planner));
    options.AddPolicy(AuthPolicies.LotHold,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Warehouse, ErpRoles.CanonicalWarehouse, ErpRoles.Support, ErpRoles.CanonicalSupport));
    options.AddPolicy(AuthPolicies.LabelMutation,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Warehouse, ErpRoles.CanonicalWarehouse, ErpRoles.Support, ErpRoles.CanonicalSupport));
    options.AddPolicy(AuthPolicies.SupportMutation,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Support, ErpRoles.CanonicalSupport));
    options.AddPolicy(AuthPolicies.OperationsMutation,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Warehouse, ErpRoles.CanonicalWarehouse, ErpRoles.Production, ErpRoles.Planner, ErpRoles.Support, ErpRoles.CanonicalSupport));
    options.AddPolicy(AuthPolicies.AdminOnly,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin));
    options.AddPolicy(AuthPolicies.SupportDetails,
        p => p.RequireRole(ErpRoles.Admin, ErpRoles.CanonicalAdmin, ErpRoles.Support, ErpRoles.CanonicalSupport));
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<AuditMiddleware>();
app.UseMiddleware<MutationAuthorizationMiddleware>();
app.UseAuthorization();

// ----------------------------------------------------------------- swagger
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Mini ERP API v1");
    c.RoutePrefix = string.Empty; // Swagger UI at root URL
});

// Every business failure is reported with the same JSON contract so clients
// (and the bash smoke test) can branch on `businessCode` instead of parsing
// raw ORA- text. See ErpApiPresenter for the mapping rules.

// ==========================================
// 0. Authentication and health
// ==========================================
app.MapPost("/api/auth/login", async ([FromBody] LoginRequest request,
    ErpDbService db, TokenService tokens, RefreshTokenStore refreshTokens, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) ||
        request.Username.Length > 50 || request.Password.Length > 256)
    {
        return Results.Json(new { success = false, errorCode = "INVALID_LOGIN_INPUT" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        var user = await db.AuthenticateUserAsync(request.Username, request.Password);
        if (user is null)
        {
            await db.WriteAuditEventAsync("AUTH_LOGIN_FAILED", "APP_USER", request.Username.Trim(),
                "anonymous", "{\"reason\":\"invalid_credentials\"}",
                context.Items[CorrelationIdMiddleware.ItemName]?.ToString());
            return Results.Json(new { success = false, errorCode = "INVALID_CREDENTIALS" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var issued = tokens.Issue(user);
        var refreshToken = refreshTokens.CreateToken(user);
        try
        {
            await db.UpdateLastLoginAsync(user.Id);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Could not update LAST_LOGIN_AT for {Actor}", user.Username);
        }
        await db.WriteAuditEventAsync("AUTH_LOGIN", "APP_USER", user.Username, user.Username,
            "{\"success\":true}", context.Items[CorrelationIdMiddleware.ItemName]?.ToString());
        return Results.Ok(new LoginResponse(issued.Token, "Bearer",
            (int)Math.Max(0, (issued.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds),
            issued.ExpiresAtUtc,
            new AuthUserDto(user.Id, user.Username, user.FullName, user.Department, user.Roles),
            refreshToken));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Authentication service unavailable");
        return Results.Json(new { success = false, errorCode = "AUTH_SERVICE_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.AllowAnonymous()
.WithName("Login")
.WithTags("Authentication");

app.MapPost("/api/auth/refresh", ([FromBody] RefreshTokenRequest request,
    TokenService tokens, RefreshTokenStore refreshTokens) =>
{
    if (string.IsNullOrWhiteSpace(request?.RefreshToken))
    {
        return Results.Json(new { success = false, errorCode = "INVALID_REFRESH_TOKEN", message = "Refresh token is required." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var tokenData = refreshTokens.Validate(request.RefreshToken);
    if (tokenData is null)
    {
        return Results.Json(new { success = false, errorCode = "INVALID_REFRESH_TOKEN", message = "Refresh token is invalid, expired, or revoked." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var user = new AuthenticatedUser(tokenData.UserId, tokenData.Username, tokenData.FullName, tokenData.Department, tokenData.Roles);
    var newIssued = tokens.Issue(user);
    refreshTokens.Revoke(request.RefreshToken); // Token rotation
    var newRefreshToken = refreshTokens.CreateToken(user);

    return Results.Ok(new TokenRefreshResponse(newIssued.Token, newRefreshToken, "Bearer",
        (int)Math.Max(0, (newIssued.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds),
        newIssued.ExpiresAtUtc));
})
.AllowAnonymous()
.WithName("RefreshToken")
.WithTags("Authentication");

app.MapPost("/api/auth/revoke", ([FromBody] RevokeTokenRequest? request,
    RefreshTokenStore refreshTokens, HttpContext context) =>
{
    var token = request?.RefreshToken;
    if (!string.IsNullOrWhiteSpace(token))
    {
        refreshTokens.Revoke(token);
    }
    if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(context.User.Identity.Name))
    {
        refreshTokens.RevokeAllForUser(context.User.Identity.Name);
    }
    return Results.Ok(new { success = true, message = "Token revoked successfully." });
})
.AllowAnonymous()
.WithName("RevokeToken")
.WithTags("Authentication");

app.MapPost("/api/auth/logout", ([FromBody] RevokeTokenRequest? request,
    RefreshTokenStore refreshTokens, HttpContext context) =>
{
    var token = request?.RefreshToken;
    if (!string.IsNullOrWhiteSpace(token))
    {
        refreshTokens.Revoke(token);
    }
    if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(context.User.Identity.Name))
    {
        refreshTokens.RevokeAllForUser(context.User.Identity.Name);
    }
    return Results.Ok(new { success = true, message = "Logged out successfully." });
})
.AllowAnonymous()
.WithName("Logout")
.WithTags("Authentication");

app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var roles = context.User.FindAll(System.Security.Claims.ClaimTypes.Role)
        .Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray();
    return Results.Ok(new AuthMeResponse(context.User.Identity?.Name ?? "unknown",
        context.User.FindFirst("full_name")?.Value,
        context.User.FindFirst("department")?.Value, roles));
})
.RequireAuthorization()
.WithName("Me")
.WithTags("Authentication");

app.MapGet("/api/admin/users", async (ErpDbService db) =>
{
    var users = await db.GetUsersAsync();
    return Results.Ok(users);
})
.RequireAuthorization(AuthPolicies.AdminOnly)
.WithName("AdminGetUsers")
.WithTags("Administration");

app.MapPost("/api/admin/users", async ([FromBody] CreateUserRequest request, ErpDbService db) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Json(new { success = false, errorCode = "INVALID_USER_DATA" },
            statusCode: StatusCodes.Status400BadRequest);
    }
    var id = await db.CreateUserAsync(request);
    return Results.Created($"/api/admin/users/{id}", new { success = true, id });
})
.RequireAuthorization(AuthPolicies.AdminOnly)
.WithName("AdminCreateUser")
.WithTags("Administration");

app.MapGet("/api/health", async (ErpDbService db) =>
{
    try
    {
        var probe = await db.ProbeAsync();
        return Results.Ok(new { status = "UP", database = probe });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "DOWN", errorCode = "DATABASE_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Health")
.WithTags("Support");

app.MapGet("/api/health/ready", async (ErpDbService db) =>
{
    try
    {
        var details = await db.GetHealthDetailsAsync();
        return details.Ready
            ? Results.Ok(new { status = "READY", database = details })
            : Results.Json(new { status = "NOT_READY", database = details },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        return Results.Json(new { status = "DOWN", errorCode = "DATABASE_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Readiness")
.WithTags("Support");

app.MapGet("/api/health/details", async (ErpDbService db) =>
{
    try { return Results.Ok(await db.GetHealthDetailsAsync()); }
    catch (Exception)
    {
        return Results.Json(new { ready = false, errorCode = "DATABASE_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.RequireAuthorization(AuthPolicies.SupportDetails)
.WithName("HealthDetails")
.WithTags("Support");

// ==========================================
// 1. Warehouse & Inventory endpoints
// ==========================================
app.MapGet("/api/warehouse", async (ErpDbService db) =>
    Results.Ok(await db.GetWarehousesAsync()))
.WithName("GetWarehouses")
.WithTags("Warehouse");

app.MapGet("/api/stock/{warehouseCode}", async (string warehouseCode, ErpDbService db) =>
    Results.Ok(await db.GetStockAsync(warehouseCode)))
.WithName("GetStockByWarehouse")
.WithTags("Warehouse");

app.MapPost("/api/stock/in", async ([FromBody] StockInRequest request, ErpDbService db) =>
{
    try
    {
        await db.ExecuteStockInAsync(request);
        var stock = await db.GetStockAsync(request.WarehouseCode);
        return Results.Ok(new
        {
            success = true,
            message = "Stock-in completed successfully.",
            referenceNo = request.ReferenceNo,
            itemCode = request.ItemCode,
            balance = stock.FirstOrDefault(s => s.ItemCode == request.ItemCode)?.Quantity
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ReferenceNo);
    }
})
.WithName("StockIn")
.WithTags("Warehouse")
.Produces<ErpErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErpErrorResponse>(StatusCodes.Status400BadRequest);

app.MapPost("/api/stock/out", async ([FromBody] StockOutRequest request, ErpDbService db) =>
{
    try
    {
        await db.ExecuteStockOutAsync(request);
        var stock = await db.GetStockAsync(request.WarehouseCode);
        return Results.Ok(new
        {
            success = true,
            message = "Stock-out completed successfully.",
            referenceNo = request.ReferenceNo,
            itemCode = request.ItemCode,
            balance = stock.FirstOrDefault(s => s.ItemCode == request.ItemCode)?.Quantity
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ReferenceNo);
    }
})
.WithName("StockOut")
.WithTags("Warehouse")
.Produces<ErpErrorResponse>(StatusCodes.Status409Conflict);


// ==========================================
// 2. Manufacturing & BOM endpoints
// ==========================================
app.MapPost("/api/manufacturing/bom/line", async ([FromBody] BomLineRequest request, ErpDbService db) =>
{
    try
    {
        await db.SaveBomLineAsync(request);
        return Results.Ok(new
        {
            success = true,
            message = $"BOM line {request.MaterialCode} = {request.QuantityRequired} saved for " +
                      $"{request.FinishedGoodCode} version {request.Version}.",
            finishedGoodCode = request.FinishedGoodCode,
            version = request.Version
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.FinishedGoodCode);
    }
})
.WithName("SaveBomLine")
.WithTags("Manufacturing");

app.MapPost("/api/manufacturing/production-order", async ([FromBody] CreateProductionOrderRequest request, ErpDbService db) =>
{
    try
    {
        await db.CreateProductionOrderAsync(request);
        var created = await db.GetProductionOrderAsync(request.ProductionOrderNo);
        return Results.Ok(new { success = true, message = $"Production order {request.ProductionOrderNo} created.", poNo = request.ProductionOrderNo, order = created });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ProductionOrderNo);
    }
})
.WithName("CreateProductionOrder")
.WithTags("Manufacturing");

app.MapGet("/api/manufacturing/production-order/{poNo}", async (string poNo, ErpDbService db) =>
{
    var order = await db.GetProductionOrderAsync(poNo);
    return order is null
        ? Results.Problem($"Production order {poNo} not found.", statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(order);
})
.WithName("GetProductionOrder")
.WithTags("Manufacturing");

app.MapPost("/api/manufacturing/production-order/{poNo}/complete", async (string poNo, ErpDbService db) =>
{
    try
    {
        await db.CompleteProductionOrderAsync(poNo, db.ResolveActor());
        var order = await db.GetProductionOrderAsync(poNo);
        return Results.Ok(new CompleteProductionOrderResponse(true, poNo, order?.Status ?? "COMPLETED",
            $"PO {poNo} completed. Materials consumed & Finished Goods produced."));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("CompleteProductionOrder")
.WithTags("Manufacturing")
.Produces<CompleteProductionOrderResponse>(StatusCodes.Status200OK)
.Produces<ErpErrorResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/manufacturing/production-order/{poNo}/cancel", async (string poNo, ErpDbService db) =>
{
    try
    {
        await db.CancelProductionOrderAsync(poNo, db.ResolveActor());
        var order = await db.GetProductionOrderAsync(poNo);
        return Results.Ok(new { success = true, poNo, status = order?.Status, message = $"PO {poNo} cancelled." });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("CancelProductionOrder")
.WithTags("Manufacturing");

// ==========================================
// 3. Procurement endpoints (incident fix path)
// ==========================================
app.MapPost("/api/procurement/purchase-order", async ([FromBody] CreatePurchaseOrderRequest request, ErpDbService db) =>
{
    try
    {
        await db.CreatePurchaseOrderAsync(request);
        return Results.Ok(new { success = true, purchaseOrderNo = request.PurchaseOrderNo, status = "CREATED",
            message = $"Purchase order {request.PurchaseOrderNo} registered and ready to receive." });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message },
            statusCode: StatusCodes.Status400BadRequest);
    }
})
.WithName("CreatePurchaseOrder")
.WithTags("Procurement");

app.MapPost("/api/procurement/purchase-order/{poNo}/receive", async (string poNo, ErpDbService db) =>
{
    try
    {
        await db.ReceivePurchaseOrderAsync(poNo, db.ResolveActor());
        return Results.Ok(new { success = true, purchaseOrderNo = poNo, status = "RECEIVED",
            message = $"Purchase order {poNo} received into stock." });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("ReceivePurchaseOrder")
.WithTags("Procurement");

// ==========================================
// 4. ERP Support / Incident endpoints
// ==========================================
app.MapGet("/api/support/errors", async ([FromQuery] string? refNo, [FromQuery] int? take, ErpDbService db) =>
    Results.Ok(await db.GetErrorLogsAsync(refNo, take ?? 50)))
.WithName("GetErrorLogs")
.WithTags("ERP Support");

app.MapPost("/api/support/change-requests", async ([FromBody] CreateChangeRequest request, ErpDbService db) =>
{
    try
    {
        var created = await db.CreateChangeRequestAsync(request);
        return Results.Ok(new { success = true, changeRequest = created,
            message = $"Change request {request.ChangeRequestNo} logged successfully." });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message },
            statusCode: StatusCodes.Status400BadRequest);
    }
})
.WithName("CreateChangeRequest")
.WithTags("ERP Support");

app.MapGet("/api/support/change-requests/{crNo}", async (string crNo, ErpDbService db) =>
{
    var cr = await db.GetChangeRequestAsync(crNo);
    return cr is null
        ? Results.Problem($"Change request {crNo} not found.", statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(cr);
})
.WithName("GetChangeRequest")
.WithTags("ERP Support");

// ==========================================
// 5. Business automation endpoints (package ERP_AUTOMATION)
// ==========================================
// W1: material availability check -> READY | WAITING_MATERIAL
app.MapPost("/api/automation/production-order/{poNo}/material-check",
    async (string poNo, ErpDbService db) =>
{
    try
    {
        var status = await db.CheckMaterialAvailabilityAsync(poNo, db.ResolveActor());
        var message = status == "READY"
            ? "All materials available for this order."
            : "At least one material is short - see GET /api/automation/replenishment/alerts?status=OPEN.";
        return Results.Ok(new MaterialCheckResponse(true, poNo, status, message));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("MaterialCheck")
.WithTags("Automation");

// W2: soft-reserve all BOM lines (idempotent, ORA-20007 on shortage)
app.MapPost("/api/automation/production-order/{poNo}/reserve",
    async (string poNo, ErpDbService db) =>
{
    try
    {
        await db.ReserveMaterialsAsync(poNo, db.ResolveActor());
        return Results.Ok(new { success = true, poNo,
            message = $"Materials reserved for {poNo}. Order is READY." });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("ReserveMaterials")
.WithTags("Automation");

// W2 undo: release the holds, PO returns to RELEASED
app.MapPost("/api/automation/production-order/{poNo}/release",
    async (string poNo, ErpDbService db) =>
{
    try
    {
        await db.ReleaseReservationsAsync(poNo, db.ResolveActor());
        return Results.Ok(new { success = true, poNo,
            message = $"Reservations released for {poNo}. Order is RELEASED." });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("ReleaseReservations")
.WithTags("Automation");

// W4: transactional completion (reservations + stock movement in ONE tx)
app.MapPost("/api/automation/production-order/{poNo}/complete",
    async (string poNo, ErpDbService db) =>
{
    try
    {
        await db.CompleteReservedOrderAsync(poNo, db.ResolveActor());
        var order = await db.GetProductionOrderAsync(poNo);
        return Results.Ok(new CompleteProductionOrderResponse(true, poNo,
            order?.Status ?? "COMPLETED",
            $"PO {poNo} completed atomically (reservations consumed)."));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("CompleteReservedOrder")
.WithTags("Automation");

// W3: evaluate the replenishment rule for one line
app.MapPost("/api/automation/replenishment/evaluate",
    async ([FromBody] ReplenishmentEvalRequest request, ErpDbService db) =>
{
    try
    {
        var suggested = await db.EvaluateReplenishmentAsync(request.ItemCode,
            request.WarehouseCode,
            string.IsNullOrEmpty(request.Trigger) ? "LOW_STOCK" : request.Trigger,
            db.ResolveActor());
        return Results.Ok(new ReplenishmentEvalResponse(true, request.ItemCode,
            request.WarehouseCode, suggested,
            suggested > 0
                ? $"Suggested replenishment quantity: {suggested}."
                : "Stock position healthy - no replenishment needed."));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ItemCode);
    }
})
.WithName("EvaluateReplenishment")
.WithTags("Automation");

// W3 batch: sweep every RAW material, return OPEN alert count
app.MapPost("/api/automation/replenishment/sweep",
    async (ErpDbService db) =>
{
    try
    {
        var openAlerts = await db.SweepReplenishmentAsync(db.ResolveActor());
        return Results.Ok(new ReplenishmentSweepResponse(true, openAlerts,
            $"Sweep finished - {openAlerts} alert(s) OPEN."));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, null);
    }
})
.WithName("SweepReplenishment")
.WithTags("Automation");

app.MapGet("/api/automation/replenishment/alerts",
    async ([FromQuery] string? status, [FromQuery] int? take, ErpDbService db) =>
        Results.Ok(await db.GetReplenishAlertsAsync(status, take ?? 50)))
.WithName("GetReplenishAlerts")
.WithTags("Automation");

// W5: stale PO detection (count run + list query)
app.MapPost("/api/automation/stale-orders/detect",
    async ([FromQuery] int? days, ErpDbService db) =>
{
    try
    {
        var staleCount = await db.DetectStaleOrdersAsync(days ?? 7, db.ResolveActor());
        return Results.Ok(new { success = true, days = days ?? 7, staleCount,
            message = staleCount > 0
                ? $"{staleCount} order(s) are idle - manual review suggested."
                : "No stale orders found." });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, null);
    }
})
.WithName("DetectStaleOrders")
.WithTags("Automation");

app.MapGet("/api/automation/stale-orders",
    async ([FromQuery] int? days, ErpDbService db) =>
        Results.Ok(await db.GetStaleOrdersAsync(days ?? 7)))
.WithName("GetStaleOrders")
.WithTags("Automation");

// W6: generate a scheduled report + list stored snapshots
app.MapPost("/api/automation/reports/{reportType}",
    async (string reportType, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        ErpDbService db) =>
{
    try
    {
        var normalized = reportType.ToUpperInvariant();
        var reportId = await db.GenerateReportAsync(normalized, from, to, db.ResolveActor());
        var reports = await db.GetReportsAsync(normalized, withPayload: false, take: 1);
        return Results.Ok(new { success = true, reportId, report = reports.FirstOrDefault() });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, reportType);
    }
})
.WithName("GenerateReport")
.WithTags("Automation");

app.MapGet("/api/automation/reports",
    async ([FromQuery] string? type, [FromQuery] int? take,
        [FromQuery] bool withPayload, ErpDbService db) =>
        Results.Ok(await db.GetReportsAsync(type, withPayload, take ?? 20)))
.WithName("GetReports")
.WithTags("Automation");

// Audit trail: every workflow run (SUCCESS / FAILED / MANUAL_REVIEW)
app.MapGet("/api/automation/runs",
    async ([FromQuery] string? workflow, [FromQuery] string? status,
        [FromQuery] int? take, ErpDbService db) =>
        Results.Ok(await db.GetAutomationRunsAsync(workflow, status, take ?? 50)))
.WithName("GetAutomationRuns")
.WithTags("Automation");

// W7: collect incident context (ERROR_LOG + PO state + alerts -> JSON)
app.MapPost("/api/automation/incidents",
    async ([FromBody] CollectIncidentRequest request, ErpDbService db,
        HelpdeskIntegrationService helpdesk, HttpContext context) =>
{
    try
    {
        var incidentId = await db.CollectIncidentContextAsync(request.ReferenceNo,
            string.IsNullOrEmpty(request.ErrorCode) ? "ERR_MATERIAL_SHORTAGE" : request.ErrorCode,
            request.Title ?? string.Empty,
            db.ResolveActor());
        var incident = (await db.GetIncidentsAsync(request.ReferenceNo, 1)).FirstOrDefault();
        var severity = (request.Severity ?? "MEDIUM").Trim().ToUpperInvariant();
        if (severity is not ("HIGH" or "CRITICAL")) severity = "MEDIUM";
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemName, out var cid)
            ? cid?.ToString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        HelpdeskDeliveryResult? delivery = null;
        if (severity is "HIGH" or "CRITICAL")
        {
            var externalRef = $"ERP-INC-{incidentId:D6}";
            var actor = context.User.Identity?.IsAuthenticated == true
                ? context.User.Identity.Name ?? "authenticated" : "system";
            var helpdeskRequest = new HelpdeskIncidentRequest(
                externalRef, "ERP", "Warehouse/Manufacturing", severity,
                incident?.Title ?? request.Title ?? "ERP incident",
                request.Description ?? incident?.Diagnosis ?? "Incident context collected by MiniERP.",
                "SUPPORT_INCIDENT", request.ReferenceNo, correlationId, DateTimeOffset.UtcNow);
            try
            {
                delivery = await helpdesk.SendIncidentAsync(helpdeskRequest, actor);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning("Helpdesk trigger failed softly for {ExternalRef}: {Error}",
                    externalRef, HelpdeskSecurity.SafeError(ex));
                delivery = new HelpdeskDeliveryResult(externalRef, "FAILED", false, false, 0,
                    "HELPDESK_TRIGGER_FAILED", "Helpdesk delivery failed; local incident is retained.");
            }
        }

        return Results.Ok(new IncidentContextResponse(true, incidentId,
            incident?.ErrorCode ?? request.ErrorCode ?? "ERR_MATERIAL_SHORTAGE",
            incident?.RefNo ?? request.ReferenceNo,
            incident?.Status ?? "OPEN",
            incident?.Diagnosis ?? string.Empty,
            incident?.ContextJson,
            delivery));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ReferenceNo);
    }
})
.WithName("CollectIncidentContext")
.WithTags("Automation");

app.MapGet("/api/automation/incidents",
    async ([FromQuery] string? refNo, [FromQuery] int? take, ErpDbService db) =>
        Results.Ok(await db.GetIncidentsAsync(refNo, take ?? 20)))
.WithName("GetIncidents")
.WithTags("Automation");

// Approval gate: request -> decide -> consume (e.g. stock/adjust)
app.MapPost("/api/automation/approvals",
    async ([FromBody] CreateApprovalRequest request, ErpDbService db) =>
{
    try
    {
        await db.RequestApprovalAsync(request.ApprovalNo, request.Action,
            request.ReferenceNo ?? string.Empty, request.Payload ?? string.Empty,
            db.ResolveActor());
        return Results.Ok(new ApprovalResponse(true, request.ApprovalNo,
            request.Action, "PENDING",
            $"Approval {request.ApprovalNo} registered - waiting for a decision."));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ApprovalNo);
    }
})
.WithName("RequestApproval")
.WithTags("Automation");

app.MapPost("/api/automation/approvals/{approvalNo}/decision",
    async (string approvalNo, [FromBody] DecideApprovalRequest request, ErpDbService db) =>
{
    try
    {
        await db.DecideApprovalAsync(approvalNo, request.Decision, db.ResolveActor());
        var approval = (await db.GetApprovalsAsync(null, 100))
            .FirstOrDefault(a => a.ApprovalNo == approvalNo);
        return Results.Ok(new ApprovalResponse(true, approvalNo,
            approval?.Action ?? string.Empty, request.Decision,
            $"Approval {approvalNo} {request.Decision}."));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, approvalNo);
    }
})
.WithName("DecideApproval")
.WithTags("Automation");

app.MapGet("/api/automation/approvals",
    async ([FromQuery] string? status, [FromQuery] int? take, ErpDbService db) =>
        Results.Ok(await db.GetApprovalsAsync(status, take ?? 20)))
.WithName("GetApprovals")
.WithTags("Automation");

// Approval-gated stock override (403 ERR_APPROVAL_REQUIRED without a token)
app.MapPost("/api/automation/stock/adjust",
    async ([FromBody] AdjustStockRequest request, ErpDbService db) =>
{
    try
    {
        await db.AdjustStockAsync(request.WarehouseCode, request.ItemCode,
            request.QuantityDelta, request.ApprovalNo, db.ResolveActor());
        var stock = await db.GetStockAsync(request.WarehouseCode);
        return Results.Ok(new { success = true,
            balance = stock.FirstOrDefault(s => s.ItemCode == request.ItemCode)?.Quantity,
            approvalNo = request.ApprovalNo,
            message = "Stock adjusted under approved token; ledger row ADJUSTMENT written." });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.ApprovalNo);
    }
})
.WithName("AdjustStock")
.WithTags("Automation");

// Actor identity is always resolved from the authenticated bearer principal by
// ErpDbService.ResolveActor; no client-supplied actor parameter is trusted.

// ==========================================
// 6. Traceability: locations / lots / receiving / movement (ERP_TRACEABILITY)
// ==========================================
app.MapGet("/api/warehouse/{warehouseCode}/locations",
    async (string warehouseCode, ErpDbService db) =>
        Results.Ok(await db.GetLocationsAsync(warehouseCode)))
.WithName("GetLocations")
.WithTags("Warehouse");

app.MapPost("/api/warehouse/{warehouseCode}/locations",
    async (string warehouseCode, [FromBody] CreateLocationRequest request,
        ErpDbService db) =>
{
    try
    {
        await db.EnsureLocationAsync(warehouseCode, request, db.ResolveActor());
        var created = (await db.GetLocationsAsync(warehouseCode))
            .FirstOrDefault(l => string.Equals(l.LocationCode,
                request.LocationCode.Trim().ToUpperInvariant(),
                StringComparison.OrdinalIgnoreCase));
        return Results.Ok(new { success = true, location = created });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, warehouseCode);
    }
})
.WithName("EnsureLocation")
.WithTags("Warehouse");

app.MapGet("/api/warehouse/lots/{lotCode}",
    async (string lotCode, ErpDbService db) =>
{
    var lot = await db.GetLotAsync(lotCode);
    return lot is null
        ? Results.Problem($"Lot {lotCode} not found.",
            statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(lot);
})
.WithName("GetLot")
.WithTags("Warehouse");

app.MapGet("/api/warehouse/lots/{lotCode}/stock",
    async (string lotCode, ErpDbService db) =>
        Results.Ok(await db.GetLotStockAsync(lotCode)))
.WithName("GetLotStock")
.WithTags("Warehouse");

app.MapPost("/api/warehouse/receipts/{purchaseOrderNo}/receive",
    async (string purchaseOrderNo, [FromBody] ReceiveLotRequest request,
        ErpDbService db) =>
{
    var actor = db.ResolveActor();
    try
    {
        await db.ReceiveLotAsync(purchaseOrderNo, request, actor);
        var lot = await db.GetLotAsync(request.LotCode);
        var stock = await db.GetLotStockAsync(request.LotCode);
        return Results.Ok(new
        {
            success = true,
            replayed = false,
            lot,
            stock,
            message = $"Lot {request.LotCode} received against {purchaseOrderNo}.",
        });
    }
    catch (ErpBusinessException ex) when (ErpDbService.IsReplay(ex))
    {
        var lot = await db.GetLotAsync(request.LotCode);
        var stock = await db.GetLotStockAsync(request.LotCode);
        return Results.Ok(new
        {
            success = true,
            replayed = true,
            lot,
            stock,
            message = $"Duplicate delivery ignored (idempotency key {request.IdempotencyKey}).",
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, purchaseOrderNo);
    }
})
.WithName("ReceiveLot")
.WithTags("Warehouse");

app.MapPost("/api/warehouse/putaway",
    async ([FromBody] PutawayLotRequest request, ErpDbService db) =>
{
    try
    {
        await db.PutawayLotAsync(request, db.ResolveActor());
        var stock = await db.GetLotStockAsync(request.LotCode);
        return Results.Ok(new
        {
            success = true,
            replayed = false,
            stock,
            message = $"Lot {request.LotCode} moved to {request.ToLocationCode}.",
        });
    }
    catch (ErpBusinessException ex) when (ErpDbService.IsReplay(ex))
    {
        var stock = await db.GetLotStockAsync(request.LotCode);
        return Results.Ok(new
        {
            success = true,
            replayed = true,
            stock,
            message = $"Duplicate put-away ignored (idempotency key {request.IdempotencyKey}).",
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.LotCode);
    }
})
.WithName("PutawayLot")
.WithTags("Warehouse");

app.MapPost("/api/warehouse/move",
    async ([FromBody] MoveLotRequest request, ErpDbService db) =>
{
    try
    {
        await db.MoveLotAsync(request, db.ResolveActor());
        var stock = await db.GetLotStockAsync(request.LotCode);
        return Results.Ok(new
        {
            success = true,
            replayed = false,
            stock,
            message = $"Lot {request.LotCode} moved to " +
                $"{request.ToWarehouseCode}/{request.ToLocationCode}.",
        });
    }
    catch (ErpBusinessException ex) when (ErpDbService.IsReplay(ex))
    {
        var stock = await db.GetLotStockAsync(request.LotCode);
        return Results.Ok(new
        {
            success = true,
            replayed = true,
            stock,
            message = $"Duplicate move ignored (idempotency key {request.IdempotencyKey}).",
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.LotCode);
    }
})
.WithName("MoveLot")
.WithTags("Warehouse");

app.MapPost("/api/warehouse/lots/{lotCode}/hold",
    async (string lotCode, ErpDbService db) =>
{
    try
    {
        await db.SetLotHoldAsync(lotCode, "HOLD", db.ResolveActor());
        return Results.Ok(new { success = true, lotCode, status = "HOLD" });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, lotCode);
    }
})
.WithName("HoldLot")
.WithTags("Warehouse");

app.MapPost("/api/warehouse/lots/{lotCode}/release-hold",
    async (string lotCode, ErpDbService db) =>
{
    try
    {
        await db.SetLotHoldAsync(lotCode, "ACTIVE", db.ResolveActor());
        return Results.Ok(new { success = true, lotCode, status = "ACTIVE" });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, lotCode);
    }
})
.WithName("ReleaseLotHold")
.WithTags("Warehouse");

// 7. Manufacturing genealogy: FEFO allocation / issue / traceable complete / trace
app.MapGet("/api/manufacturing/production-order/{poNo}/allocate-lots",
    async (string poNo, ErpDbService db) =>
{
    try
    {
        return Results.Ok(await db.GetAllocationAsync(poNo));
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("AllocateLots")
.WithTags("Manufacturing");

app.MapPost("/api/manufacturing/production-order/{poNo}/issue-lots",
    async (string poNo, [FromBody] IssueLotsRequest request,
        ErpDbService db) =>
{
    var actor = db.ResolveActor();
    try
    {
        await db.IssueLotsAsync(poNo, request, actor);
        return Results.Ok(new
        {
            success = true,
            replayed = false,
            message = $"Lot materials issued to {poNo}.",
        });
    }
    catch (ErpBusinessException ex) when (ErpDbService.IsReplay(ex))
    {
        return Results.Ok(new
        {
            success = true,
            replayed = true,
            message = $"Duplicate issue ignored (idempotency key {request.IdempotencyKey}).",
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("IssueLots")
.WithTags("Manufacturing");

app.MapPost("/api/manufacturing/production-order/{poNo}/complete-traceable",
    async (string poNo, [FromBody] CompleteTraceableRequest request,
        ErpDbService db) =>
{
    var actor = db.ResolveActor();
    try
    {
        await db.CompleteTraceableAsync(poNo, request, actor);
        var fgLot = string.IsNullOrWhiteSpace(request.FgLot)
            ? "FG-" + poNo + "-001" : request.FgLot;
        return Results.Ok(new
        {
            success = true,
            replayed = false,
            fgLot,
            barcode = "LOT:" + fgLot,
            labelEndpoint = $"/api/labels?entityType=LOT&entityKey={fgLot}",
            outputWarehouseCode = request.OutputWarehouseCode,
            message = $"Order {poNo} completed traceably.",
        });
    }
    catch (ErpBusinessException ex) when (ErpDbService.IsReplay(ex))
    {
        return Results.Ok(new
        {
            success = true,
            replayed = true,
            fgLot = request.FgLot,
            barcode = request.FgLot is null ? null : "LOT:" + request.FgLot,
            labelEndpoint = request.FgLot is null ? null
                : $"/api/labels?entityType=LOT&entityKey={request.FgLot}",
            outputWarehouseCode = request.OutputWarehouseCode,
            message = $"Duplicate completion ignored (idempotency key {request.IdempotencyKey}).",
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, poNo);
    }
})
.WithName("CompleteTraceable")
.WithTags("Manufacturing");

app.MapGet("/api/trace/{lotCode}",
    async (string lotCode, [FromQuery] string? direction, ErpDbService db) =>
{
    string normalized;
    try
    {
        normalized = TraceabilityLogic.NormalizeTraceDirection(direction);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message,
            statusCode: StatusCodes.Status400BadRequest);
    }

    var trace = await db.GetTraceAsync(lotCode, normalized);
    return trace is null
        ? Results.Problem($"Lot {lotCode} not found.",
            statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(new { direction = normalized, trace });
})
.WithName("TraceLot")
.WithTags("Traceability");

// 8. Label workflow: create/print/reprint + audit (no lot or stock side effects)
app.MapPost("/api/labels",
    async ([FromBody] CreateLabelRequest request,
        ErpDbService db) =>
{
    try
    {
        var resp = await db.CreateLabelJobAsync(request, db.ResolveActor());
        return Results.Ok(new
        {
            success = true,
            job = resp.Job,
            rendered = resp.Rendered,
            message = $"Label job {resp.Job.PrintJobId} rendered ({resp.Job.Format}).",
        });
    }
    catch (ErpBusinessException ex)
    {
        return ErpApiPresenter.Problem(ex, request.EntityKey);
    }
})
.WithName("CreateLabelJob")
.WithTags("Labels");

app.MapGet("/api/labels",
    async ([FromQuery] string? entityType, [FromQuery] string? entityKey,
        ErpDbService db) =>
        Results.Ok(await db.GetLabelJobsAsync(entityType, entityKey)))
.WithName("GetLabelJobs")
.WithTags("Labels");

app.MapGet("/api/labels/{id:long}",
    async (long id, ErpDbService db) =>
{
    var job = await db.GetLabelJobAsync(id);
    return job is null
        ? Results.Problem($"Label job {id} not found.",
            statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(job);
})
.WithName("GetLabelJob")
.WithTags("Labels");

app.MapGet("/api/labels/{id:long}/render",
    async (long id, ErpDbService db) =>
{
    var resp = await db.RenderLabelJobAsync(id);
    return resp is null
        ? Results.Problem($"Label job {id} not found.",
            statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(new { job = resp.Job, rendered = resp.Rendered });
})
.WithName("RenderLabelJob")
.WithTags("Labels");

app.MapGet("/api/barcodes/resolve/{code}",
    async (string code, ErpDbService db) =>
{
    (string EntityType, string EntityKey) resolved;
    try
    {
        resolved = TraceabilityLogic.ResolveScan(code);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (resolved.EntityType == "LOT")
    {
        var lot = await db.GetLotAsync(resolved.EntityKey);
        if (lot is null)
        {
            return Results.Problem($"Lot {resolved.EntityKey} not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new BarcodeResolveResponse(
            code, "LOT", lot.LotCode, lot.ItemCode, lot.Status));
    }

    return Results.Ok(new BarcodeResolveResponse(
        code, resolved.EntityType, resolved.EntityKey, null, "ACTIVE"));
})
.WithName("ResolveBarcode")
.WithTags("Warehouse");

// 9. Enterprise Helpdesk integration (fail-soft outbox delivery)
app.MapPost("/api/integration/helpdesk/incidents",
    async ([FromBody] HelpdeskIncidentRequest request,
        HelpdeskIntegrationService helpdesk, HttpContext context) =>
{
    var actor = context.User.Identity?.IsAuthenticated == true
        ? context.User.Identity.Name ?? "authenticated" : "system";
    var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemName, out var cid)
        ? cid?.ToString() ?? request.CorrelationId : request.CorrelationId;
    var normalizedRequest = request with { CorrelationId = correlationId };
    var result = await helpdesk.SendIncidentAsync(normalizedRequest, actor);
    return Results.Ok(result);
})
.RequireAuthorization(AuthPolicies.SupportMutation)
.WithName("ForwardHelpdeskIncident")
.WithTags("Integration");

app.MapGet("/api/integration/helpdesk/deliveries/{externalRef}",
    async (string externalRef, ErpDbService db) =>
{
    var delivery = await db.GetHelpdeskDeliveryAsync(externalRef);
    return delivery is null
        ? Results.Problem($"Helpdesk delivery record {externalRef} not found.",
            statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(delivery);
})
.RequireAuthorization(AuthPolicies.SupportDetails)
.WithName("GetHelpdeskDelivery")
.WithTags("Integration");

app.Run();

/// <summary>
/// Marker so the integration test project can reference the API entry point
/// through WebApplicationFactory&lt;Program&gt; (top-level statements generate an
/// internal Program class otherwise).
/// </summary>
public partial class Program { }
