using Microsoft.AspNetCore.Mvc;
using MiniERP.Api.Models;
using MiniERP.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() 
    { 
        Title = "Mini ERP API — Manufacturing & Warehouse Execution", 
        Version = "v1",
        Description = "Core REST API connecting ASP.NET Core (.NET 8) to Oracle Database 19c/21c via PL/SQL Stored Procedures."
    });
});

builder.Services.AddSingleton<ErpDbService>();

var app = builder.Build();

// Enable Swagger in development and container mode
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Mini ERP API v1");
    c.RoutePrefix = string.Empty; // Serve Swagger UI at root URL
});

// ==========================================
// 1. Warehouse & Inventory Endpoints
// ==========================================

app.MapGet("/api/warehouse", async (ErpDbService db) =>
{
    var warehouses = await db.GetWarehousesAsync();
    return Results.Ok(warehouses);
})
.WithName("GetWarehouses")
.WithTags("Warehouse");

app.MapGet("/api/stock/{warehouseCode}", async (string warehouseCode, ErpDbService db) =>
{
    var items = await db.GetStockAsync(warehouseCode);
    return Results.Ok(items);
})
.WithName("GetStockByWarehouse")
.WithTags("Warehouse");

app.MapPost("/api/stock/in", async ([FromBody] StockInRequest request, ErpDbService db) =>
{
    try
    {
        await db.ExecuteStockInAsync(request);
        return Results.Ok(new { success = true, message = "Stock-in completed successfully.", referenceNo = request.ReferenceNo });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
})
.WithName("StockIn")
.WithTags("Warehouse");

app.MapPost("/api/stock/out", async ([FromBody] StockOutRequest request, ErpDbService db) =>
{
    try
    {
        await db.ExecuteStockOutAsync(request);
        return Results.Ok(new { success = true, message = "Stock-out completed successfully.", referenceNo = request.ReferenceNo });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
})
.WithName("StockOut")
.WithTags("Warehouse");

// ==========================================
// 2. Manufacturing Endpoints
// ==========================================

app.MapPost("/api/manufacturing/production-order", async ([FromBody] CreateProductionOrderRequest request, ErpDbService db) =>
{
    try
    {
        await db.CreateProductionOrderAsync(request);
        return Results.Ok(new { success = true, message = $"Production order {request.ProductionOrderNo} created.", poNo = request.ProductionOrderNo });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
})
.WithName("CreateProductionOrder")
.WithTags("Manufacturing");

app.MapPost("/api/manufacturing/production-order/{poNo}/complete", async (string poNo, [FromQuery] string user, ErpDbService db) =>
{
    try
    {
        await db.CompleteProductionOrderAsync(poNo, string.IsNullOrEmpty(user) ? "system" : user);
        return Results.Ok(new CompleteProductionOrderResponse(true, poNo, "COMPLETED", $"PO {poNo} completed. Materials consumed & Finished Goods produced."));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, poNo, error = ex.Message, action = "Check /api/support/errors for root cause." });
    }
})
.WithName("CompleteProductionOrder")
.WithTags("Manufacturing");

// ==========================================
// 3. Support & Incident Endpoints
// ==========================================

app.MapGet("/api/support/errors", async ([FromQuery] string? refNo, ErpDbService db) =>
{
    var logs = await db.GetErrorLogsAsync(refNo);
    return Results.Ok(logs);
})
.WithName("GetErrorLogs")
.WithTags("ERP Support");

app.MapPost("/api/support/change-requests", async ([FromBody] CreateChangeRequest request, ErpDbService db) =>
{
    try
    {
        await db.CreateChangeRequestAsync(request);
        return Results.Ok(new { success = true, message = $"Change request {request.ChangeRequestNo} logged successfully." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
})
.WithName("CreateChangeRequest")
.WithTags("ERP Support");

app.Run();
