using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniERP.Api.Models;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Integration tests: they boot the real ASP.NET Core pipeline through
/// WebApplicationFactory&lt;Program&gt; and reach Oracle through the connection
/// string in src/appsettings.json.
///
/// Requirements: the Oracle container must be up and the SQL scripts executed
/// (scripts/run-sql.sh). If the database is not configured, the tests report a
/// clear failure naming the missing prerequisite instead of an opaque stack.
///
/// Run unit tests only:  dotnet test --filter "Category!=Integration"
/// Run everything:       dotnet test
/// </summary>
[Trait("Category", "Integration")]
public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        EnsureDatabaseConfigured();
        // Shared Oracle schema: restore the sql/03_seed.sql baseline before
        // each test so stock/FG assertions stay deterministic across runs.
        TestStockFixture.Reset(Client());
    }

    private HttpClient Client() => _factory.CreateClient();

    /// <summary>Fails fast with an actionable message when Oracle is not up.</summary>
    private void EnsureDatabaseConfigured()
    {
        var res = Client().GetAsync("/api/health").GetAwaiter().GetResult();
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new Xunit.Sdk.XunitException(
                "Oracle Database is not reachable from the API. Start it and load the schema first:\n" +
                "  docker compose up -d && bash scripts/run-sql.sh\n" +
                $"health payload: {body}");
        }
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Health_ReportsDatabaseUpAndPackageValid()
    {
        var res = await Client().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("UP", doc.RootElement.GetProperty("status").GetString());
        var db = doc.RootElement.GetProperty("database");
        Assert.Contains("Oracle", db.GetProperty("banner").GetString());
        Assert.Equal("VALID", db.GetProperty("packageStatus").GetString());
        Assert.True(db.GetProperty("tableCount").GetInt32() >= 20,
            "expected at least 20 tables (13 core + 7 automation)");
    }

    [Fact]
    public async Task Warehouses_ReturnsTheThreeSeededPlants()
    {
        var res = await Client().GetAsync("/api/warehouse");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var list = await res.Content.ReadFromJsonAsync<List<WarehouseDto>>(Web);
        Assert.NotNull(list);
        Assert.Contains(list!, w => w.Code == "WH_RAW" && w.IsActive);
        Assert.Contains(list!, w => w.Code == "WH_FG");
        Assert.Contains(list!, w => w.Code == "WH_WIP");
    }

    [Fact]
    public async Task StockByWarehouse_ExposesRawMaterialAndMinStockFlag()
    {
        var res = await Client().GetAsync("/api/stock/WH_RAW");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var rows = await res.Content.ReadFromJsonAsync<List<StockItemDto>>(Web);
        Assert.NotNull(rows);
        var rubber = Assert.Single(rows!, r => r.ItemCode == "MAT_RUBBER_01");
        Assert.Equal("RAW", rubber.ItemType);
        Assert.Equal("PAIR", rubber.Uom);
        Assert.True(rubber.Quantity >= 0);
        // The footwear plant keeps far less than the 500-pair min-stock level,
        // which is exactly why the shortage incident can happen.
        Assert.True(rubber.IsBelowMinStock);
    }

    [Fact]
    public async Task StockOfUnknownWarehouse_ReturnsEmptyListNotError()
    {
        var res = await Client().GetAsync("/api/stock/WH_NOT_A_REAL_WAREHOUSE");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var rows = await res.Content.ReadFromJsonAsync<List<StockItemDto>>(Web);
        Assert.NotNull(rows);
        Assert.Empty(rows!);
    }

    [Fact]
    public async Task UnknownProductionOrder_Returns404()
    {
        var res = await Client().GetAsync("/api/manufacturing/production-order/DOES_NOT_EXIST_XYZ");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ErrorLog_FilteredByRefNo_OnlyReturnsThatReference()
    {
        var res = await Client().GetAsync("/api/support/errors?refNo=PO001");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var logs = await res.Content.ReadFromJsonAsync<List<ErrorLogDto>>(Web);
        Assert.NotNull(logs);
        Assert.All(logs!, l => Assert.Equal("PO001", l.ReferenceNo));
    }

    [Fact]
    public async Task StockOut_InsufficientQuantity_IsRejectedWithBusinessContract()
    {
        var payload = new StockOutRequest("WH_RAW", "MAT_THREAD_01", 9_999_999m, "DOTNET_TEST_NEGATIVE");
        var res = await Client().PostAsJsonAsync("/api/stock/out", payload, Web);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ORA-20003", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("ERR_INSUFFICIENT_STOCK", doc.RootElement.GetProperty("businessCode").GetString());
        Assert.Equal("create_stock_out", doc.RootElement.GetProperty("procedureName").GetString());
    }

    [Fact]
    public async Task StockIn_UnknownItem_Returns404()
    {
        var payload = new StockInRequest("WH_RAW", "ITEM_DOES_NOT_EXIST", 5m, "DOTNET_TEST_404");
        var res = await Client().PostAsJsonAsync("/api/stock/in", payload, Web);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ERR_NOT_FOUND", doc.RootElement.GetProperty("businessCode").GetString());
    }

    [Fact]
    public async Task StockIn_NonPositiveQuantity_Returns400()
    {
        var payload = new StockInRequest("WH_RAW", "MAT_BOX_01", 0m, "DOTNET_TEST_400");
        var res = await Client().PostAsJsonAsync("/api/stock/in", payload, Web);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ERR_INVALID_QTY", doc.RootElement.GetProperty("businessCode").GetString());
    }

    [Fact]
    public async Task CompleteOrder_MaterialShortage_ReportsOra20007AndKeepsAutonomousLog()
    {
        var client = Client();
        var tag = "DN" + DateTime.Now.ToString("HHmmss") + "_" + Random.Shared.Next(1000, 9999);
        var poNo = "OPS_" + tag;

        // 1. create an order that can never be covered by real stock
        var create = new CreateProductionOrderRequest(poNo, "FG_RUNNER_PRO_42", 5_000_000m, "WH_RAW", "tester");
        var createRes = await client.PostAsJsonAsync("/api/manufacturing/production-order", create, Web);
        createRes.EnsureSuccessStatusCode();

        // 2. completing it must fail with the documented shortage contract
        var completeRes = await client.PostAsync(
            $"/api/manufacturing/production-order/{poNo}/complete?user=tester", content: null);
        Assert.Equal(HttpStatusCode.Conflict, completeRes.StatusCode);
        using var doc = JsonDocument.Parse(await completeRes.Content.ReadAsStringAsync());
        Assert.Equal("ORA-20007", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("ERR_MATERIAL_SHORTAGE", doc.RootElement.GetProperty("businessCode").GetString());

        // 3. PRAGMA AUTONOMOUS_TRANSACTION: the log must survive the ROLLBACK
        var logs = await client.GetAsync($"/api/support/errors?refNo={poNo}");
        logs.EnsureSuccessStatusCode();
        var rows = await logs.Content.ReadFromJsonAsync<List<ErrorLogDto>>(Web);
        Assert.NotEmpty(rows!);
        Assert.All(rows!, r => Assert.Equal(poNo, r.ReferenceNo));
        Assert.Contains(rows!, r => r.ProcedureName == "complete_production_order");

        // 4. ACID: the aborted attempt must not have produced any finished good
        var fg = await client.GetAsync("/api/stock/WH_RAW");
        fg.EnsureSuccessStatusCode();
        var stock = await fg.Content.ReadFromJsonAsync<List<StockItemDto>>(Web);
        var runner = Assert.Single(stock!, s => s.ItemCode == "FG_RUNNER_PRO_42");
        Assert.True(runner.Quantity < 1_000m,
            $"a rolled-back completion must not create finished goods (found {runner.Quantity})");
    }
}


