using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniERP.Api.Models;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Integration tests for the business-process automation layer (package
/// ERP_AUTOMATION): material check, material reservation, transactional
/// completion, replenishment alerts, stale-PO detection, scheduled reports,
/// the approval gate and the ERP_AUTOMATION_RUN audit trail.
///
/// Requirements: the Oracle container must be up and the SQL scripts executed
/// (scripts/run-sql.sh). Without the database the tests fail with a clear
/// message instead of an opaque stack trace.
///
/// Run unit tests only:  dotnet test --filter "Category!=Integration"
/// Run everything:       dotnet test
/// </summary>
[Trait("Category", "Integration")]
public class AutomationIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public AutomationIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        EnsureDatabaseConfigured();
        // All tests share one schema: restore the sql/03_seed.sql baseline so
        // prior runs' completions/holds cannot push availability negative.
        TestStockFixture.Reset(Client());
    }

    private HttpClient RawClient() => _factory.CreateClient();

    private HttpClient Client()
    {
        var client = RawClient();
        var login = client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", "Admin@123")).GetAwaiter().GetResult();
        if (!login.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                "Demo admin login failed. Run scripts/run-sql.sh after the Phase 5 seed: " +
                $"{login.StatusCode} {login.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
        }
        var payload = login.Content.ReadFromJsonAsync<LoginResponse>(Web).GetAwaiter().GetResult()
            ?? throw new Xunit.Sdk.XunitException("Login response was empty.");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", payload.AccessToken);
        return client;
    }

    /// <summary>Fails fast with an actionable message when Oracle is not up.</summary>
    private void EnsureDatabaseConfigured()
    {
        var res = RawClient().GetAsync("/api/health").GetAwaiter().GetResult();
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

    /// <summary>Tiny helper that creates a production order and asserts success.</summary>
    private async Task<ProductionOrderDto> CreateOrderAsync(
        HttpClient client, string poNo, decimal qty, string warehouseCode = "WH_RAW")
    {
        var order = new CreateProductionOrderRequest(poNo, "FG_RUNNER_PRO_42", qty, warehouseCode, "planner01");
        var res = await client.PostAsJsonAsync("/api/manufacturing/production-order", order, Web);
        res.EnsureSuccessStatusCode();

        // GET the created order to verify it's in RELEASED status
        var getRes = await client.GetAsync($"/api/manufacturing/production-order/{poNo}");
        getRes.EnsureSuccessStatusCode();
        var created = await getRes.Content.ReadFromJsonAsync<ProductionOrderDto>(Web);
        Assert.NotNull(created);
        Assert.Equal("RELEASED", created.Status);
        return created;
    }

    /// <summary>W1: material check returns READY when stock is sufficient.</summary>

    /// <summary>W1: material check returns READY when stock is sufficient.</summary>
    [Fact]
    public async Task MaterialCheck_EnoughStock_ReturnsReadyStatus()
    {
        var client = Client();
        var poNo = $"AUT_CK_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 3m);

        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("READY", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>W1: material check returns WAITING_MATERIAL when stock is short.</summary>
    [Fact]
    public async Task MaterialCheck_OverPlannedOrder_ReturnsWaitingMaterialStatus()
    {
        var client = Client();
        var poNo = $"AUT_SHORT_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 10_000m);

        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("WAITING_MATERIAL", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>W1: cannot check material on a completed order.</summary>
    [Fact]
    public async Task MaterialCheck_ReturnsErrorForCompletedOrder()
    {
        var client = Client();
        var poNo = $"AUT_DONE_A_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 3m);

        // Complete the order first
        var completeRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/complete?user=workshop_lead", null);
        completeRes.EnsureSuccessStatusCode();

        // Try to check material on a completed order - should fail
        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ERR_AUTOMATION_STATE", doc.RootElement.GetProperty("businessCode").GetString());
    }

    /// <summary>W1: returns error for unknown order.</summary>
    [Fact]
    public async Task MaterialCheck_ReturnsErrorForUnknownOrder()
    {
        var client = Client();
        var res = await client.PostAsync(
            "/api/automation/production-order/UNKNOWN_ORDER_12345/material-check?user=planner01", null);
        Assert.True(res.StatusCode != HttpStatusCode.OK,
            $"Expected error for unknown PO, got {res.StatusCode}");
    }

    // ---------------------------------------------------------------- W2: reserve / release

    /// <summary>W2: reserve materials when stock is sufficient.</summary>
    [Fact]
    public async Task ReserveMaterials_SufficientStock_ReservesSuccessfully()
    {
        var client = Client();
        var poNo = $"AUT_RES_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 3m);

        // Check material first, then reserve
        var checkRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        checkRes.EnsureSuccessStatusCode();

        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/reserve?user=planner01", null);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        // Cleanup: release the hold so it cannot eat availability for later tests.
        var releaseRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/release?user=planner01", null);
        releaseRes.EnsureSuccessStatusCode();
    }

    /// <summary>W2: reserve materials fails with ORA-20007 when stock is short.</summary>
    [Fact]
    public async Task ReserveMaterials_Shortage_ReturnsError()
    {
        var client = Client();
        var poNo = $"AUT_RES_SHORT_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 10_000m);

        // Reserve should fail due to material shortage
        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/reserve?user=planner01", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ORA-20007", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("ERR_MATERIAL_SHORTAGE", doc.RootElement.GetProperty("businessCode").GetString());
    }

    /// <summary>W2 undo: release reservations returns success.</summary>
    [Fact]
    public async Task ReleaseReservations_Success_ReturnsSuccess()
    {
        var client = Client();
        var poNo = $"AUT_REL_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 3m);

        // Check and reserve first
        var checkRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        checkRes.EnsureSuccessStatusCode();

        var reserveRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/reserve?user=planner01", null);
        reserveRes.EnsureSuccessStatusCode();

        // Then release
        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/release?user=planner01", null);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Reservations released", doc.RootElement.GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------- W4: transactional completion

    /// <summary>W4: complete a reserved order successfully.</summary>
    [Fact]
    public async Task CompleteReservedOrder_HappyPath_CompletesOrder()
    {
        var client = Client();
        var poNo = $"AUT_DONE_B_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 3m);

        // Check material and reserve
        var checkRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        checkRes.EnsureSuccessStatusCode();

        var reserveRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/reserve?user=planner01", null);
        reserveRes.EnsureSuccessStatusCode();

        // Complete the order
        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/complete?user=workshop_lead", null);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("COMPLETED", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>W4: cannot complete an already completed order.</summary>
    [Fact]
    public async Task CompleteReservedOrder_ReturnsErrorForCompletedOrder()
    {
        var client = Client();
        var poNo = $"AUT_DONE2_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        await CreateOrderAsync(client, poNo, 3m);

        // Check and reserve
        var checkRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/material-check?user=planner01", null);
        checkRes.EnsureSuccessStatusCode();

        var reserveRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/reserve?user=planner01", null);
        reserveRes.EnsureSuccessStatusCode();

        // Complete the order
        var completeRes = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/complete?user=workshop_lead", null);
        completeRes.EnsureSuccessStatusCode();

        // Try to complete again - should fail
        var res = await client.PostAsync(
            $"/api/automation/production-order/{poNo}/complete?user=workshop_lead", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ERR_AUTOMATION_STATE", doc.RootElement.GetProperty("businessCode").GetString());
    }
}
