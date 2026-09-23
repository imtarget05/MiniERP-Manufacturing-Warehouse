using Microsoft.AspNetCore.Mvc;
using MiniERP.Api;
using MiniERP.Api.Models;
using MiniERP.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Mini ERP API — Manufacturing & Warehouse Execution",
        Version = "v1",
        Description = "Core REST API connecting ASP.NET Core (.NET 8) to Oracle Database " +
                      "19c/21c/23c through the PL/SQL package ERP_OPERATIONS (Dapper micro-ORM)."
    });
});

builder.Services.AddSingleton<ErpDbService>();

var app = builder.Build();

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
// 0. Health / connectivity
// ==========================================
app.MapGet("/api/health", async (ErpDbService db) =>
{
    try
    {
        var probe = await db.ProbeAsync();
        return Results.Ok(new { status = "UP", database = probe });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "DOWN", error = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Health")
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

app.MapPost("/api/manufacturing/production-order/{poNo}/complete", async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        await db.CompleteProductionOrderAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
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

app.MapPost("/api/manufacturing/production-order/{poNo}/cancel", async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        await db.CancelProductionOrderAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
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

app.MapPost("/api/procurement/purchase-order/{poNo}/receive", async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        await db.ReceivePurchaseOrderAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
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
    async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        var status = await db.CheckMaterialAvailabilityAsync(
            poNo, string.IsNullOrEmpty(user) ? "system" : user);
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
    async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        await db.ReserveMaterialsAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
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
    async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        await db.ReleaseReservationsAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
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
    async (string poNo, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        await db.CompleteReservedOrderAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
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
            string.IsNullOrEmpty(request.User) ? "system" : request.User);
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
    async ([FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        var openAlerts = await db.SweepReplenishmentAsync(
            string.IsNullOrEmpty(user) ? "system" : user);
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
    async ([FromQuery] int? days, [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        var staleCount = await db.DetectStaleOrdersAsync(
            days ?? 7, string.IsNullOrEmpty(user) ? "system" : user);
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
        [FromQuery] string? user, ErpDbService db) =>
{
    try
    {
        var normalized = reportType.ToUpperInvariant();
        var reportId = await db.GenerateReportAsync(normalized, from, to,
            string.IsNullOrEmpty(user) ? "system" : user);
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
    async ([FromBody] CollectIncidentRequest request, ErpDbService db) =>
{
    try
    {
        var incidentId = await db.CollectIncidentContextAsync(request.ReferenceNo,
            string.IsNullOrEmpty(request.ErrorCode) ? "ERR_MATERIAL_SHORTAGE" : request.ErrorCode,
            request.Title ?? string.Empty,
            string.IsNullOrEmpty(request.User) ? "system" : request.User);
        var incident = (await db.GetIncidentsAsync(request.ReferenceNo, 1)).FirstOrDefault();
        return Results.Ok(new IncidentContextResponse(true, incidentId,
            incident?.ErrorCode ?? request.ErrorCode ?? "ERR_MATERIAL_SHORTAGE",
            incident?.RefNo ?? request.ReferenceNo,
            incident?.Status ?? "OPEN",
            incident?.Diagnosis ?? string.Empty,
            incident?.ContextJson));
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
            string.IsNullOrEmpty(request.Requester) ? "system" : request.Requester);
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
        await db.DecideApprovalAsync(approvalNo, request.Decision,
            string.IsNullOrEmpty(request.Approver) ? "system" : request.Approver);
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
            request.QuantityDelta, request.ApprovalNo,
            string.IsNullOrEmpty(request.User) ? "system" : request.User);
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

app.Run();

/// <summary>
/// Marker so the integration test project can reference the API entry point
/// through WebApplicationFactory&lt;Program&gt; (top-level statements generate an
/// internal Program class otherwise).
/// </summary>
public partial class Program { }
