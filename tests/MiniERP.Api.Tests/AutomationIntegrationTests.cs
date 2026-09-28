using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// IMPORTANT - the DSN is never inherited. Every host in this assembly goes
/// through TestOracleDsn, so a run without an explicit ConnectionStrings__OracleDb
/// fails instead of falling back to src/appsettings.json (a real listener) and
/// writing to it. Integration-trait runs need a seeded disposable schema:
///   ConnectionStrings__OracleDb='User Id=...;Data Source=127.0.0.1:<port>/FREEPDB1;...' \
///     dotnet test -c Release
/// The no-database subset is hermetic and only needs an unreachable DSN:
///   ConnectionStrings__OracleDb='User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;' \
///     dotnet test -c Release --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class AutomationIntegrationTests : IClassFixture<OracleGuardedWebApplicationFactory>
{
    private readonly OracleGuardedWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public AutomationIntegrationTests(OracleGuardedWebApplicationFactory factory)
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

    /// <summary>
    /// The approval gate must round-trip through the read model: once a request
    /// has been decided, GET /api/automation/approvals has to materialize the row.
    /// Regression guard - a nullable DATE column (DECIDED_AT) cannot be mapped by
    /// a positional Dapper record, which used to return HTTP 500 on every read.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_DecidedRequestIsReadable()
    {
        var client = Client();
        // Two identities: the requester proposes, a different approver decides
        // (CR-001 two-person rule - the requester may not approve itself).
        using var requester = ClientAs("warehouse01", "Warehouse@123");
        var approvalNo = $"AUT_APP_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

        var create = await requester.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_AUT",
                ScopePayload(RawWarehouse, AdjustItem, -1m)));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<ApprovalResponse>(Web);
        Assert.NotNull(created);
        Assert.Equal("PENDING", created!.Status);

        var decision = await client.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decision.EnsureSuccessStatusCode();

        var listRes = await client.GetAsync("/api/automation/approvals?status=APPROVED&take=100");
        listRes.EnsureSuccessStatusCode();
        var rows = await listRes.Content.ReadFromJsonAsync<List<ApprovalDto>>(Web);
        Assert.NotNull(rows);
        var row = Assert.Single(rows!, r => r.ApprovalNo == approvalNo);
        Assert.Equal("INVENTORY_ADJUST", row.Action);
        Assert.Equal("CYCLE_AUT", row.RefNo);
        Assert.Equal("APPROVED", row.Status);
        Assert.NotNull(row.DecidedAt);
    }

    // ---------------------------------------------------------------- round 4
    // The approved scope must be a deterministic, session-independent string.
    // TO_CHAR(n) without a format model is not: a decimal delta renders as
    // "|.5" under one session and "|,5" under another, and request_approval and
    // adjust_stock can run on different pooled connections.

    /// <summary>A positive delta is approvable, executable and stored canonically.</summary>
    [Fact]
    public async Task ApprovalGate_PositiveDelta_IsApprovedExecutedAndStoredCanonically()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR4_POS");
        var before = await StockOfAsync(admin, AdjustItem);

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR4",
                ScopePayload(RawWarehouse, AdjustItem, 5m)));
        create.EnsureSuccessStatusCode();
        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();

        // The stored scope is the canonical string: no padding, no space, the
        // codes upper-cased and the delta in a fixed 3-decimal form.
        var scope = await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", approvalNo);
        Assert.Equal($"{RawWarehouse}|{AdjustItem}|5.000", scope);
        Assert.Equal(scope!.Trim(), scope);
        Assert.DoesNotContain(" ", scope);
        Assert.Equal(3, scope.Split('|')[2].Split('.').Last().Length);

        using var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 5m, approvalNo));
        adjust.EnsureSuccessStatusCode();
        Assert.Equal(before + 5m, await StockOfAsync(admin, AdjustItem));
        Assert.Equal("EXECUTED", (await ApprovalRowAsync(admin, approvalNo)).Status);
    }

    /// <summary>A positive approval may only execute that exact positive amount.</summary>
    [Fact]
    public async Task ApprovalGate_PositiveApproval_RefusesAnotherPositiveDelta()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR4_POSBAD");
        var before = await StockOfAsync(admin, AdjustItem);

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR4",
                ScopePayload(RawWarehouse, AdjustItem, 5m)));
        create.EnsureSuccessStatusCode();
        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();

        using var bigger = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 6m, approvalNo));
        Assert.False(bigger.IsSuccessStatusCode,
            "an approval for +5 must not execute +6: " + await bigger.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, bigger.StatusCode);

        using var smaller = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 1m, approvalNo));
        Assert.False(smaller.IsSuccessStatusCode,
            "an approval for +5 must not execute +1: " + await smaller.Content.ReadAsStringAsync());

        Assert.Equal(before, await StockOfAsync(admin, AdjustItem));
        Assert.Equal("APPROVED", (await ApprovalRowAsync(admin, approvalNo)).Status);
    }

    /// <summary>A zero delta is not an adjustment: refused at creation and at execution.</summary>
    [Fact]
    public async Task ApprovalGate_ZeroDelta_IsRejectedAtCreationAndExecution()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var zeroNo = Tag("W5AR4_ZERO");

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(zeroNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR4",
                ScopePayload(RawWarehouse, AdjustItem, 0m)));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var createBody = await create.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(createBody))
        {
            Assert.Equal("ERR_INVALID_INPUT", doc.RootElement.GetProperty("businessCode").GetString());
        }
        // The API contract, not a raw constraint violation: docs/03-api-spec.md
        // documents errorCode "ORA-20012" beside businessCode, but ORA-2290 (a
        // CHECK violation) must never be what the caller sees.
        Assert.DoesNotContain("ORA-2290", createBody, StringComparison.OrdinalIgnoreCase);

        // Even with a hand-inserted token, a zero delta is refused.
        var forcedNo = Tag("W5AR4_ZERO2");
        await InsertApprovalRowAsync(forcedNo, ScopePayload(RawWarehouse, AdjustItem, 3m));
        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{forcedNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();
        var before = await StockOfAsync(admin, AdjustItem);
        using var zeroAdjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 0m, forcedNo));
        Assert.Equal(HttpStatusCode.BadRequest, zeroAdjust.StatusCode);
        var zeroBody = await zeroAdjust.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(zeroBody))
        {
            Assert.Equal("ERR_INVALID_INPUT", doc.RootElement.GetProperty("businessCode").GetString());
        }
        Assert.DoesNotContain("ORA-2290", zeroBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await StockOfAsync(admin, AdjustItem));
    }

    /// <summary>Negative deltas keep working: the canonical form is not negative-only.</summary>
    [Fact]
    public async Task ApprovalGate_NegativeDelta_RemainsSupported()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR4_NEG");
        var before = await StockOfAsync(admin, AdjustItem);

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR4",
                ScopePayload(RawWarehouse, AdjustItem, -4m)));
        create.EnsureSuccessStatusCode();
        var scope = await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", approvalNo);
        Assert.Equal($"{RawWarehouse}|{AdjustItem}|-4.000", scope);

        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();
        using var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -4m, approvalNo));
        adjust.EnsureSuccessStatusCode();
        Assert.Equal(before - 4m, await StockOfAsync(admin, AdjustItem));
    }

    /// <summary>
    /// The row-level backstop and the package guard must apply the SAME rule:
    /// upper-cased and trimmed. A decision taken through the package with
    /// ' warehouse01 ' for requester 'warehouse01' is refused, and so is the same
    /// row written directly as APPROVER = ' WAREHOUSE01 '.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_SelfDecisionBackstopMatchesPackageGuard()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR4_NORM");
        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR4",
                ScopePayload(RawWarehouse, AdjustItem, -1m)));
        create.EnsureSuccessStatusCode();

        // (a) package guard, called directly with a padded lower-case spelling
        //     of the requester: the canonical rule is UPPER(TRIM(...)), so this
        //     is the same person and must be refused.
        var packageError = await DirectDecideErrorAsync(approvalNo, " warehouse01 ");
        Assert.NotNull(packageError);
        // ODP.NET surfaces the unsigned ORA number
        Assert.Equal(20011, Math.Abs(packageError!.Value));
        Assert.Equal("PENDING", (await ApprovalRowAsync(admin, approvalNo)).Status);

        // (b) the same rule as a CHECK constraint: a direct UPDATE is refused
        var constraintError = await DirectSelfApproveUpdateErrorAsync(approvalNo);
        Assert.NotNull(constraintError);
        Assert.Equal(2290, constraintError!.Value);

        // (c) a genuinely different approver still decides
        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();
        Assert.Equal("APPROVED", (await ApprovalRowAsync(admin, approvalNo)).Status);
    }

    private async Task<string?> ScalarStringAsync(string sql, string bindName, string bindValue)
    {
        using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        command.Parameters.Add($":{bindName}", bindValue);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private async Task InsertApprovalRowAsync(string approvalNo, string payload)
    {
        using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO APPROVAL_REQUEST (APPROVAL_NO, ACTION, REF_NO, PAYLOAD, APPROVED_SCOPE, STATUS, REQUESTER) " +
            "VALUES (:no, 'INVENTORY_ADJUST', 'W5AR4_FORCE', :payload, :scope, 'PENDING', 'warehouse01')";
        command.BindByName = true;
        command.Parameters.Add(":no", approvalNo);
        command.Parameters.Add(":payload", payload);
        command.Parameters.Add(":scope", ScopeOf(RawWarehouse, AdjustItem, 3m));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int?> DirectDecideErrorAsync(string approvalNo, string approver)
    {
        try
        {
            using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText =
                "BEGIN ERP_AUTOMATION.decide_approval(:no, 'APPROVED', :who); END;";
            command.Parameters.Add(":no", approvalNo);
            command.Parameters.Add(":who", approver);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (Oracle.ManagedDataAccess.Client.OracleException ex)
        {
            return ex.Number;
        }
    }

    private async Task<int?> DirectSelfApproveUpdateErrorAsync(string approvalNo)
    {
        try
        {
            using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            // Same identity, different spelling: ' WAREHOUSE01 ' vs 'warehouse01'.
            // The backstop must apply the same UPPER(TRIM(...)) rule as the package.
            command.CommandText =
                "UPDATE APPROVAL_REQUEST SET APPROVER = ' WAREHOUSE01 ' WHERE APPROVAL_NO = :no";
            command.Parameters.Add(":no", approvalNo);
            await command.ExecuteNonQueryAsync();
            connection.Rollback();
            return null;
        }
        catch (Oracle.ManagedDataAccess.Client.OracleException ex)
        {
            return ex.Number;
        }
    }

    // ---------------------------------------------------------------- B-02
    // Two-person rule, identity half. CR-001 is titled "two-person", and a
    // control where the requester also presses the approve button is one person.
    // warehouse01 proposes and admin decides everywhere else, so this only
    // changes the self-approval path.

    /// <summary>
    /// RED for B-02: the requester must not be able to approve their own
    /// request. The approval must stay PENDING, keep APPROVER NULL, and no
    /// stock may move.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_RequesterCannotApproveOwnRequest()
    {
        using var admin = Client();
        var approvalNo = Tag("W5AR3_SELF");
        var payload = ScopePayload(RawWarehouse, AdjustItem, -1m);

        using var create = await admin.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR3", payload));
        create.EnsureSuccessStatusCode();
        var before = await StockOfAsync(admin, AdjustItem);

        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        var body = await decide.Content.ReadAsStringAsync();

        Assert.False(decide.IsSuccessStatusCode,
            "CR-001 two-person rule: the requester must not be able to approve their own request. " +
            $"status={(int)decide.StatusCode} body={body}");
        Assert.Equal(HttpStatusCode.Conflict, decide.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ERR_AUTOMATION_STATE", doc.RootElement.GetProperty("businessCode").GetString());

        var row = await ApprovalRowAsync(admin, approvalNo);
        Assert.Equal("PENDING", row.Status);
        Assert.Null(row.Approver);
        Assert.Null(row.DecidedAt);
        Assert.Equal("admin", row.Requester);
        Assert.Equal(before, await StockOfAsync(admin, AdjustItem));
    }

    /// <summary>
    /// The same request must still be decidable by a *different* approver, so
    /// the control refuses the identity, not the workflow.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_SelfApprovalBlocked_ButAnotherApproverCanDecide()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR3_TWO");
        var payload = ScopePayload(RawWarehouse, AdjustItem, -2m);

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR3", payload));
        create.EnsureSuccessStatusCode();
        var before = await StockOfAsync(admin, AdjustItem);

        // the requester may not decide, and an unknown approver may not either
        using var byRequester = await warehouse.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        Assert.Equal(HttpStatusCode.Forbidden, byRequester.StatusCode);

        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();

        var row = await ApprovalRowAsync(admin, approvalNo);
        Assert.Equal("APPROVED", row.Status);
        Assert.Equal("warehouse01", row.Requester);
        Assert.Equal("admin", row.Approver);

        using var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -2m, approvalNo));
        adjust.EnsureSuccessStatusCode();
        Assert.Equal(before - 2m, await StockOfAsync(admin, AdjustItem));
    }

    // ---------------------------------------------------------------- B-03
    // An approval is granted for one specific adjustment. Before this control
    // the token was looked up by number only, so a token approved for -1 could
    // execute -37 (and any magnitude the stock allowed): CR-001's "+/- 50 units
    // and flagged as critical material" scoping had no teeth.

    /// <summary>
    /// The canonical INVENTORY_ADJUST payload must be machine-checkable:
    /// warehouseCode + itemCode + quantityDelta, and a token may only execute
    /// exactly that adjustment.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_ApprovedToken_IsBoundToItsPayloadOnFirstUse()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR3_BIND");
        var approvedForOne = ScopePayload(RawWarehouse, AdjustItem, -1m);
        var before = await StockOfAsync(admin, AdjustItem);

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR3", approvedForOne));
        create.EnsureSuccessStatusCode();
        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();

        // first use, wrong magnitude
        using var wrongDelta = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -37m, approvalNo));
        var wrongDeltaBody = await wrongDelta.Content.ReadAsStringAsync();
        Assert.False(wrongDelta.IsSuccessStatusCode,
            "an approval granted for -1 must not execute -37 on first use: " + wrongDeltaBody);
        Assert.Equal(HttpStatusCode.Forbidden, wrongDelta.StatusCode);
        using var wrongDeltaDoc = JsonDocument.Parse(wrongDeltaBody);
        Assert.Equal("ERR_APPROVAL_REQUIRED",
            wrongDeltaDoc.RootElement.GetProperty("businessCode").GetString());
        Assert.Equal(before, await StockOfAsync(admin, AdjustItem));

        // first use, wrong item
        using var wrongItem = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, "MAT_THREAD_01", -1m, approvalNo));
        Assert.False(wrongItem.IsSuccessStatusCode,
            "an approval granted for MAT_BOX_01 must not adjust MAT_THREAD_01: " +
            await wrongItem.Content.ReadAsStringAsync());

        // first use, wrong warehouse
        using var wrongWh = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest("WH_WIP", AdjustItem, -1m, approvalNo));
        Assert.False(wrongWh.IsSuccessStatusCode,
            "an approval granted for WH_RAW must not adjust WH_WIP: " +
            await wrongWh.Content.ReadAsStringAsync());

        // the token is still APPROVED after the refusals, and the correct
        // adjustment still succeeds exactly once
        var row = await ApprovalRowAsync(admin, approvalNo);
        Assert.Equal("APPROVED", row.Status);
        using var correct = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -1m, approvalNo));
        correct.EnsureSuccessStatusCode();
        Assert.Equal(before - 1m, await StockOfAsync(admin, AdjustItem));
        Assert.Equal("EXECUTED", (await ApprovalRowAsync(admin, approvalNo)).Status);

        // ... and only once: the same token cannot be replayed afterwards
        using var replay = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -1m, approvalNo));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(before - 1m, await StockOfAsync(admin, AdjustItem));
    }

    /// <summary>
    /// A payload that is not the canonical INVENTORY_ADJUST contract must be
    /// refused at creation, so an unbound token can never exist.
    /// </summary>
    [Theory]
    [InlineData("Cycle count discrepancy -10 MAT_RUBBER_01 in WH_RAW")]
    [InlineData("{\"qty\":-1}")]
    [InlineData("{\"warehouseCode\":\"WH_RAW\"}")]
    [InlineData("{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"MAT_BOX_01\"}")]
    [InlineData("{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"MAT_BOX_01\",\"quantityDelta\":0}")]
    public async Task ApprovalRequest_NonCanonicalInventoryAdjustPayload_IsRejected(string payload)
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        var approvalNo = Tag("W5AR3_BADPL");

        using var response = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR3", payload));

        Assert.False(response.IsSuccessStatusCode,
            $"non-canonical INVENTORY_ADJUST payload accepted: {payload}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ERR_INVALID_INPUT", doc.RootElement.GetProperty("businessCode").GetString());

        // nothing was persisted
        using var list = await warehouse.GetAsync("/api/automation/approvals?take=200");
        list.EnsureSuccessStatusCode();
        var rows = await list.Content.ReadFromJsonAsync<List<ApprovalDto>>(Web);
        Assert.DoesNotContain(rows!, r => r.ApprovalNo == approvalNo);
    }

    /// <summary>
    /// The canonical payload is stored and compared case-insensitively on codes:
    /// approving "wh_raw"/"mat_box_01" must still authorise "WH_RAW"/"MAT_BOX_01".
    /// </summary>
    [Fact]
    public async Task ApprovalGate_CanonicalPayload_MatchesCodesCaseInsensitively()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5AR3_CASE");
        var before = await StockOfAsync(admin, AdjustItem);

        using var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR3",
                ScopePayload("wh_raw", "mat_box_01", -3m)));
        create.EnsureSuccessStatusCode();
        using var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decide.EnsureSuccessStatusCode();

        using var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest("WH_RAW", "MAT_BOX_01", -3m, approvalNo));
        adjust.EnsureSuccessStatusCode();
        Assert.Equal(before - 3m, await StockOfAsync(admin, AdjustItem));
    }

    // ---------------------------------------------------------------- CR-001
    // Two-person approval gate end to end: propose -> decide -> consume.
    // The role split is the rule the spec declares (FR-005 in
    // docs/business-analysis/04-functional-requirements.md, CR-001 in
    // docs/project-management/change-request-log.md, UAT-APP-001 in
    // docs/uat/uat-test-cases.md): warehouse staff may only *propose*, the
    // Admin/Manager decides, and only an Admin/Manager posts the ledger move.

    private const string RawWarehouse = "WH_RAW";
    private const string AdjustItem = "MAT_BOX_01";

    private HttpClient ClientAs(string username, string password)
    {
        var client = RawClient();
        var login = client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(username, password)).GetAwaiter().GetResult();
        if (!login.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"Demo login failed for '{username}': {login.StatusCode} " +
                $"{login.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
        }
        var payload = login.Content.ReadFromJsonAsync<LoginResponse>(Web).GetAwaiter().GetResult()
            ?? throw new Xunit.Sdk.XunitException("Login response was empty.");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", payload.AccessToken);
        return client;
    }

    private static string Tag(string prefix) =>
        $"{prefix}_{DateTime.Now:HHmmss}_{Random.Shared.Next(1000, 9999)}";

    /// <summary>
    /// The canonical INVENTORY_ADJUST approval payload. CR-001 grants an approval
    /// for one specific adjustment, so the payload has to be machine-checkable:
    /// exactly these three members, and adjust_stock only accepts a token whose
    /// stored scope equals the adjustment it is asked to perform.
    /// </summary>
    /// <summary>
    /// Mirrors ERP_AUTOMATION.approval_scope so a test can assert the stored
    /// value without repeating the format model: WH|ITEM|DELTA with 3 decimals.
    /// </summary>
    private static string ScopeOf(string warehouseCode, string itemCode, decimal delta) =>
        $"{warehouseCode.ToUpperInvariant()}|{itemCode.ToUpperInvariant()}|{delta.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";

    private static string ScopePayload(string warehouseCode, string itemCode, decimal delta) =>
        $"{{\"warehouseCode\":\"{warehouseCode}\",\"itemCode\":\"{itemCode}\",\"quantityDelta\":{delta.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    private async Task<decimal> StockOfAsync(HttpClient client, string itemCode)
    {
        var res = await client.GetAsync($"/api/stock/{RawWarehouse}");
        res.EnsureSuccessStatusCode();
        var rows = await res.Content.ReadFromJsonAsync<List<StockItemDto>>(Web);
        var row = Assert.Single(rows!, r => r.ItemCode == itemCode);
        return row.Quantity;
    }

    private async Task<ApprovalDto> ApprovalRowAsync(HttpClient client, string approvalNo)
    {
        var res = await client.GetAsync("/api/automation/approvals?take=200");
        res.EnsureSuccessStatusCode();
        var rows = await res.Content.ReadFromJsonAsync<List<ApprovalDto>>(Web);
        return Assert.Single(rows!, r => r.ApprovalNo == approvalNo);
    }

    private string ConnectionString()
    {
        var connectionString = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
            .GetConnectionString("OracleDb");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new Xunit.Sdk.XunitException(
                "Oracle connection string missing: this is an Integration-trait test and needs the seeded database")
            : connectionString;
    }

    /// <summary>
    /// Two-person rule, role half: the requester (warehouse staff) can register
    /// a PENDING approval but is stopped before the decision and before the
    /// ledger write, both with 403 AUTH_FORBIDDEN from MutationAuthorizationMiddleware.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_WarehouseRequester_CanPropose_ButCannotDecideOrAdjust()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        var approvalNo = Tag("W5A_2P");

        var request = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5A", ScopePayload(RawWarehouse, AdjustItem, -1m)));
        request.EnsureSuccessStatusCode();
        var created = await request.Content.ReadFromJsonAsync<ApprovalResponse>(Web);
        Assert.NotNull(created);
        Assert.Equal("PENDING", created!.Status);

        var row = await ApprovalRowAsync(warehouse, approvalNo);
        Assert.Equal("warehouse01", row.Requester);
        Assert.Null(row.Approver);

        using var decide = await warehouse.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        Assert.Equal(HttpStatusCode.Forbidden, decide.StatusCode);

        using var adjust = await warehouse.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -1m, approvalNo));
        Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);

        var after = await ApprovalRowAsync(warehouse, approvalNo);
        Assert.Equal("PENDING", after.Status);
        Assert.Null(after.Approver);
    }

    /// <summary>
    /// An admin (the role that passes AuthPolicies.AdminOnly) is still refused
    /// when the approval token is missing or not yet APPROVED, and the balance
    /// must not move. This is the "no approval, no ledger change" guarantee.
    /// </summary>
    [Fact]
    public async Task StockAdjust_WithoutApprovedToken_IsRejectedAndLeavesStockUnchanged()
    {
        using var admin = Client();
        var before = await StockOfAsync(admin, AdjustItem);

        using var unknown = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -1m, Tag("W5A_MISSING")));
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        using var unknownDoc = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal("ERR_APPROVAL_REQUIRED", unknownDoc.RootElement.GetProperty("businessCode").GetString());

        // A PENDING (not decided) token is equally refused.
        var approvalNo = Tag("W5A_PENDING");
        using var request = await admin.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5A", ScopePayload(RawWarehouse, AdjustItem, -1m)));
        request.EnsureSuccessStatusCode();

        using var pending = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -1m, approvalNo));
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
        using var pendingDoc = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Equal("ERR_APPROVAL_REQUIRED", pendingDoc.RootElement.GetProperty("businessCode").GetString());

        Assert.Equal(before, await StockOfAsync(admin, AdjustItem));
    }

    /// <summary>
    /// Happy path with two distinct principals: warehouse01 proposes, admin
    /// decides, admin consumes. The balance must move by exactly the delta, the
    /// token must end EXECUTED with both actors recorded, and the signed
    /// ADJUSTMENT ledger row must reference the approval number.
    /// </summary>
    [Fact]
    public async Task StockAdjust_RequesterWarehouse_ApproverAdmin_AppliesExactDeltaOnce()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5A_OK");
        const decimal delta = -3m;
        var before = await StockOfAsync(admin, AdjustItem);

        using var request = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5A", ScopePayload(RawWarehouse, AdjustItem, -3m)));
        request.EnsureSuccessStatusCode();

        using var decision = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decision.EnsureSuccessStatusCode();

        using var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, delta, approvalNo));
        adjust.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await adjust.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(before + delta, doc.RootElement.GetProperty("balance").GetDecimal());

        Assert.Equal(before + delta, await StockOfAsync(admin, AdjustItem));

        var row = await ApprovalRowAsync(admin, approvalNo);
        Assert.Equal("EXECUTED", row.Status);
        Assert.Equal("warehouse01", row.Requester);
        Assert.Equal("admin", row.Approver);
        Assert.NotNull(row.DecidedAt);

        var ledger = await ScalarDecimalAsync(
            "SELECT NVL(MAX(QTY), -999999) FROM INVENTORY_TRANSACTION " +
            "WHERE TXN_TYPE = 'ADJUSTMENT' AND REF_NO = :ref", "ref", approvalNo);
        Assert.Equal(delta, ledger);
    }

    /// <summary>
    /// A consumed approval is one-shot: reusing the same token for a different
    /// adjustment (other item, other delta) must be refused and must not move
    /// the second item.
    /// </summary>
    [Fact]
    public async Task StockAdjust_ReusingConsumedApproval_IsBlocked()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5A_REUSE");
        const decimal delta = -2m;

        using var request = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5A", ScopePayload(RawWarehouse, AdjustItem, -2m)));
        request.EnsureSuccessStatusCode();
        using var decision = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decision.EnsureSuccessStatusCode();
        using var first = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, delta, approvalNo));
        first.EnsureSuccessStatusCode();

        var otherBefore = await StockOfAsync(admin, "MAT_THREAD_01");
        using var reuse = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, "MAT_THREAD_01", -50m, approvalNo));
        Assert.False(reuse.IsSuccessStatusCode,
            "an EXECUTED approval token must not authorize a second adjustment");
        Assert.Equal(HttpStatusCode.Forbidden, reuse.StatusCode);
        using var reuseDoc = JsonDocument.Parse(await reuse.Content.ReadAsStringAsync());
        Assert.Equal("ERR_APPROVAL_REQUIRED", reuseDoc.RootElement.GetProperty("businessCode").GetString());
        Assert.Equal(otherBefore, await StockOfAsync(admin, "MAT_THREAD_01"));
    }

    /// <summary>
    /// The consumed mutation must be auditable: APP_AUDIT_EVENT carries the
    /// actor, the route entity key and the correlation id the caller sent.
    /// </summary>
    [Fact]
    public async Task StockAdjust_WritesAuditEventWithActorAndCorrelationId()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var approvalNo = Tag("W5A_AUDIT");
        var correlationId = Tag("w5a-audit-adjust").ToLowerInvariant();

        using var request = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(approvalNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5A", ScopePayload(RawWarehouse, AdjustItem, -1m)));
        request.EnsureSuccessStatusCode();
        using var decision = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{approvalNo}/decision", new DecideApprovalRequest("APPROVED"));
        decision.EnsureSuccessStatusCode();

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/automation/stock/adjust")
        {
            Content = JsonContent.Create(
                new AdjustStockRequest(RawWarehouse, AdjustItem, -1m, approvalNo), options: Web)
        };
        message.Headers.Add("X-Correlation-ID", correlationId);
        using var adjust = await admin.SendAsync(message);
        adjust.EnsureSuccessStatusCode();

        var row = await PollAuditEventAsync("API_MUTATION", "/api/automation/stock/adjust", correlationId);
        Assert.Equal("HTTP", row.ENTITY_TYPE);
        Assert.Equal("admin", row.ACTOR);
        Assert.Contains("\"path\":\"/api/automation/stock/adjust\"", row.DETAILS_JSON, StringComparison.Ordinal);
        Assert.Contains("\"status\":200", row.DETAILS_JSON, StringComparison.Ordinal);
    }

    private async Task<decimal> ScalarDecimalAsync(string sql, string bindName, string bindValue)
    {
        using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        command.Parameters.Add($":{bindName}", bindValue);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToDecimal(value);
    }

    /// <summary>
    /// AuditMiddleware appends the row after the response is produced, so the
    /// insert lands a moment later than the client sees the status.
    /// </summary>
    private async Task<AuditRow> PollAuditEventAsync(string eventType, string entityKey, string correlationId)
    {
        var connectionString = ConnectionString();
        const string query =
            "SELECT EVENT_TYPE, ENTITY_TYPE, ENTITY_KEY, ACTOR, DETAILS_JSON, CORRELATION_ID " +
            "FROM (SELECT * FROM APP_AUDIT_EVENT WHERE EVENT_TYPE = :t AND ENTITY_KEY = :k " +
            "      AND CORRELATION_ID = :c ORDER BY AUDIT_ID DESC) WHERE ROWNUM = 1";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            command.BindByName = true;
            command.Parameters.Add(":t", eventType);
            command.Parameters.Add(":k", entityKey);
            command.Parameters.Add(":c", correlationId);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new AuditRow(
                    reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException(
            $"no APP_AUDIT_EVENT row EVENT_TYPE={eventType} ENTITY_KEY={entityKey} " +
            $"CORRELATION_ID={correlationId} appeared within 20s");
    }

    private readonly record struct AuditRow(
        string EVENT_TYPE, string? ENTITY_TYPE, string? ENTITY_KEY,
        string ACTOR, string? DETAILS_JSON, string? CORRELATION_ID);

    /// <summary>
    /// The automation read models (runs / reports / alerts) must materialize too:
    /// every one of them contains at least one nullable DATE column.
    /// </summary>
    [Fact]
    public async Task AutomationReadModels_Materialize()
    {
        var client = Client();

        var runs = await client.GetAsync("/api/automation/runs?take=5");
        runs.EnsureSuccessStatusCode();
        await runs.Content.ReadFromJsonAsync<List<AutomationRunDto>>(Web);

        var alerts = await client.GetAsync("/api/automation/replenishment/alerts?take=5");
        alerts.EnsureSuccessStatusCode();
        await alerts.Content.ReadFromJsonAsync<List<ReplenishAlertDto>>(Web);

        var reports = await client.GetAsync("/api/automation/reports?withPayload=false&take=5");
        reports.EnsureSuccessStatusCode();
        await reports.Content.ReadFromJsonAsync<List<AutomationReportDto>>(Web);
    }

    // ---------------------------------------------------------------- round 5
    // The round-4 upgrade proof ran against an EMPTY APPROVAL_REQUEST, so it
    // could not fail: the two UPDATE ... SET REQUESTER/APPROVER = UPPER(TRIM())
    // normalisations matched 0 rows and the three ADD CONSTRAINT statements were
    // validated against nothing. sql/05_automation_schema.sql 5.8.1 now adds the
    // three checks ENABLE NOVALIDATE and only upgrades them to VALIDATE when the
    // existing rows allow it, so a populated legacy table cannot abort the
    // migration - and no new INSERT or UPDATE can escape the checks.
    //
    // The behaviour half is what matters: APPROVED_SCOPE is deliberately NOT
    // backfilled, because no scope was ever approved for those rows, so a legacy
    // token stays inert. It is refused with a business error before any stock row
    // is touched, and it cannot even be promoted to APPROVED.

    /// <summary>
    /// A legacy INVENTORY_ADJUST token carries no approved scope, so it must not
    /// be able to move stock for ANY payload - including the exact one stored in
    /// its own PAYLOAD, which is the case a backfill would have "fixed".
    /// </summary>
    [Fact]
    public async Task ApprovalGate_LegacyUnboundToken_CannotExecuteAnyPayload()
    {
        using var admin = Client();
        var approvalNo = Tag("W5AR5_LEGACY");
        var legacyPayload = ScopePayload(RawWarehouse, AdjustItem, -3m);
        var before = await StockOfAsync(admin, AdjustItem);

        await InsertLegacyApprovalRowAsync(approvalNo, legacyPayload,
            "APPROVED", "warehouse01", "admin");

        // The row really is the legacy shape: approved, but no recorded scope.
        Assert.Null(await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", approvalNo));
        Assert.Equal("APPROVED", (await ApprovalRowAsync(admin, approvalNo)).Status);

        try
        {
            var attempts = new (string Wh, string Item, decimal Delta, string Why)[]
            {
                (RawWarehouse, AdjustItem, -3m, "the exact adjustment its own payload asks for"),
                (RawWarehouse, AdjustItem, -37m, "a different magnitude"),
                (RawWarehouse, "MAT_THREAD_01", -3m, "a different item"),
                ("WH_WIP", AdjustItem, -3m, "a different warehouse"),
                (RawWarehouse, AdjustItem, 3m, "the same magnitude in the other direction"),
            };

            foreach (var (wh, item, delta, why) in attempts)
            {
                using var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
                    new AdjustStockRequest(wh, item, delta, approvalNo));
                var body = await adjust.Content.ReadAsStringAsync();
                Assert.False(adjust.IsSuccessStatusCode,
                    $"a legacy unbound token executed {wh}/{item}/{delta} ({why}): {body}");
                Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("ERR_APPROVAL_REQUIRED",
                    doc.RootElement.GetProperty("businessCode").GetString());
                // A constraint violation would be the unmapped failure mode here.
                Assert.DoesNotContain("ORA-2290", body, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(before, await StockOfAsync(admin, AdjustItem));
            Assert.Equal("APPROVED", (await ApprovalRowAsync(admin, approvalNo)).Status);
        }
        finally
        {
            await ExecDirectAsync(
                "DELETE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", ("no", approvalNo));
            await RestoreScopeConstraintAsync();
        }
    }

    /// <summary>
    /// A legacy unbound token cannot be promoted either. Without the package
    /// guard the NOVALIDATE CK_APV_SCOPE_REQUIRED would fire on the UPDATE and
    /// the caller would see an unmapped ORA-02290 for a legitimate business
    /// refusal.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_LegacyUnboundToken_CannotBeDecided()
    {
        using var admin = Client();
        var approvalNo = Tag("W5AR5_LEGPEND");

        await InsertLegacyApprovalRowAsync(approvalNo,
            ScopePayload(RawWarehouse, AdjustItem, -5m), "PENDING", "warehouse01", null);

        try
        {
            // (a) the package, called directly: a business error, not ORA-2290
            var packageError = await DirectDecideErrorAsync(approvalNo, "admin");
            Assert.NotNull(packageError);
            Assert.Equal(20011, Math.Abs(packageError!.Value));

            var row = await ApprovalRowAsync(admin, approvalNo);
            Assert.Equal("PENDING", row.Status);
            Assert.Null(row.Approver);
            Assert.Null(row.DecidedAt);

            // (b) the same thing through the API, which must stay on the
            //     automation-state contract instead of surfacing a constraint
            using var decide = await admin.PostAsJsonAsync(
                $"/api/automation/approvals/{approvalNo}/decision",
                new DecideApprovalRequest("APPROVED"));
            var body = await decide.Content.ReadAsStringAsync();
            Assert.False(decide.IsSuccessStatusCode,
                "a legacy unbound PENDING token was decided: " + body);
            Assert.Equal(HttpStatusCode.Conflict, decide.StatusCode);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("ERR_AUTOMATION_STATE", doc.RootElement.GetProperty("businessCode").GetString());
            Assert.DoesNotContain("ORA-2290", body, StringComparison.OrdinalIgnoreCase);

            // (c) rejecting it is equally refused: the token is inert, not merely
            //     stuck in one direction, and the queue cannot be drained.
            using var reject = await admin.PostAsJsonAsync(
                $"/api/automation/approvals/{approvalNo}/decision",
                new DecideApprovalRequest("REJECTED"));
            Assert.False(reject.IsSuccessStatusCode,
                "a legacy unbound PENDING token was rejected: " + await reject.Content.ReadAsStringAsync());
            Assert.Equal("PENDING", (await ApprovalRowAsync(admin, approvalNo)).Status);
        }
        finally
        {
            await ExecDirectAsync(
                "DELETE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", ("no", approvalNo));
            await RestoreScopeConstraintAsync();
        }
    }

    /// <summary>
    /// ENABLE NOVALIDATE grandfathers the rows that already exist and nothing
    /// else: a new INSERT and a new UPDATE must both still be refused, otherwise
    /// the migration policy would have silently dropped the control.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_NovalidateScopeConstraint_StillRefusesNewWrites()
    {
        var insertNo = Tag("W5AR5_NOSCOPE");
        var insertError = await DirectErrorAsync(
            "INSERT INTO APPROVAL_REQUEST (APPROVAL_NO, ACTION, REF_NO, PAYLOAD, STATUS, REQUESTER) " +
            "VALUES (:no, 'INVENTORY_ADJUST', 'CYCLE_COUNT_W5AR5', :payload, 'PENDING', 'warehouse01')",
            ("no", insertNo), ("payload", ScopePayload(RawWarehouse, AdjustItem, -1m)));
        Assert.NotNull(insertError);
        Assert.Equal(2290, insertError!.Value);
        Assert.Null(await ScalarStringAsync(
            "SELECT APPROVAL_NO FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", insertNo));

        // ... and on UPDATE, against a row that exists and is otherwise legal
        var targetNo = Tag("W5AR5_UPD");
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using (var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(targetNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR5",
                ScopePayload(RawWarehouse, AdjustItem, -1m))))
        {
            create.EnsureSuccessStatusCode();
        }

        var updateError = await DirectErrorAsync(
            "UPDATE APPROVAL_REQUEST SET APPROVED_SCOPE = 'WH_RAW|MAT_BOX_01|not-a-number' " +
            "WHERE APPROVAL_NO = :no", ("no", targetNo));
        Assert.NotNull(updateError);
        Assert.Equal(2290, updateError!.Value);
        Assert.Equal($"{RawWarehouse}|{AdjustItem}|-1.000", await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", targetNo));
    }

    /// <summary>
    /// The canonical scope must not follow the session, in either direction: it
    /// is created on a session whose decimal character is a COMMA and then
    /// executed on a pooled connection whose decimal character is a POINT. The
    /// bare TO_CHAR this replaced produced "...|,500" and a false rejection.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_CanonicalScope_IsIndependentOfSessionNls()
    {
        using var admin = Client();
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        var commaNo = Tag("W5AR5_NLSCMA");
        var pointNo = Tag("W5AR5_NLSPT");
        var before = await StockOfAsync(admin, AdjustItem);

        // One direct connection, decimal character flipped to ','. Both the
        // creation and the read-back happen on that session, so a session-
        // dependent builder would be visible right here.
        using (var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString()))
        {
            await connection.OpenAsync();
            await using (var alter = connection.CreateCommand())
            {
                alter.CommandText = "ALTER SESSION SET NLS_NUMERIC_CHARACTERS = ',.'";
                await alter.ExecuteNonQueryAsync();
            }

            await using var call = connection.CreateCommand();
            call.BindByName = true;
            call.CommandText = @"BEGIN
                ERP_AUTOMATION.request_approval(:no, 'INVENTORY_ADJUST', 'CYCLE_COUNT_W5AR5',
                  :payload, 'warehouse01');
                ERP_AUTOMATION.decide_approval(:no, 'APPROVED', 'admin');
            END;";
            call.Parameters.Add(":no", commaNo);
            call.Parameters.Add(":payload", ScopePayload(RawWarehouse, AdjustItem, 0.5m));
            await call.ExecuteNonQueryAsync();

            await using var read = connection.CreateCommand();
            read.BindByName = true;
            read.CommandText = "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no";
            read.Parameters.Add(":no", commaNo);
            var commaScope = Convert.ToString(await read.ExecuteScalarAsync());
            Assert.Equal($"{RawWarehouse}|{AdjustItem}|0.500", commaScope);
            Assert.DoesNotContain(",", commaScope!);
        }

        // The same adjustment created through the API, on the default session.
        using (var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(pointNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR5",
                ScopePayload(RawWarehouse, AdjustItem, 5m))))
        {
            create.EnsureSuccessStatusCode();
        }
        Assert.Equal($"{RawWarehouse}|{AdjustItem}|5.000", await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", pointNo));

        // Both execute on a pooled connection whose NLS is the default, which is
        // the cross-session match the round-4 defect broke.
        using (var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{pointNo}/decision", new DecideApprovalRequest("APPROVED")))
        {
            decide.EnsureSuccessStatusCode();
        }
        using (var adjustComma = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 0.5m, commaNo)))
        {
            adjustComma.EnsureSuccessStatusCode();
        }
        using (var adjustPoint = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 5m, pointNo)))
        {
            adjustPoint.EnsureSuccessStatusCode();
        }

        Assert.Equal(before + 5.5m, await StockOfAsync(admin, AdjustItem));
        Assert.Equal("EXECUTED", (await ApprovalRowAsync(admin, commaNo)).Status);
        Assert.Equal("EXECUTED", (await ApprovalRowAsync(admin, pointNo)).Status);
    }

    /// <summary>
    /// Fractional deltas are the case the canonical form exists for: a positive
    /// and a negative fraction, each stored with exactly three decimals and each
    /// executing exactly once for exactly the approved amount.
    /// </summary>
    [Fact]
    public async Task ApprovalGate_FractionalDelta_IsCanonicalAndExecutesExactly()
    {
        using var warehouse = ClientAs("warehouse01", "Warehouse@123");
        using var admin = Client();
        var before = await StockOfAsync(admin, AdjustItem);

        var upNo = Tag("W5AR5_FRUP");
        using (var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(upNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR5",
                ScopePayload(RawWarehouse, AdjustItem, 0.5m))))
        {
            create.EnsureSuccessStatusCode();
        }
        Assert.Equal($"{RawWarehouse}|{AdjustItem}|0.500", await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", upNo));
        using (var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{upNo}/decision", new DecideApprovalRequest("APPROVED")))
        {
            decide.EnsureSuccessStatusCode();
        }
        // 0.501 and -0.5 are both outside the approved +0.5
        using (var tooPrecise = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 0.501m, upNo)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, tooPrecise.StatusCode);
        }
        using (var wrongSign = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -0.5m, upNo)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongSign.StatusCode);
        }
        Assert.Equal(before, await StockOfAsync(admin, AdjustItem));
        using (var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, 0.5m, upNo)))
        {
            adjust.EnsureSuccessStatusCode();
        }
        Assert.Equal(before + 0.5m, await StockOfAsync(admin, AdjustItem));

        var downNo = Tag("W5AR5_FRDOWN");
        using (var create = await warehouse.PostAsJsonAsync("/api/automation/approvals",
            new CreateApprovalRequest(downNo, "INVENTORY_ADJUST", "CYCLE_COUNT_W5AR5",
                ScopePayload(RawWarehouse, AdjustItem, -0.25m))))
        {
            create.EnsureSuccessStatusCode();
        }
        Assert.Equal($"{RawWarehouse}|{AdjustItem}|-0.250", await ScalarStringAsync(
            "SELECT APPROVED_SCOPE FROM APPROVAL_REQUEST WHERE APPROVAL_NO = :no", "no", downNo));
        using (var decide = await admin.PostAsJsonAsync(
            $"/api/automation/approvals/{downNo}/decision", new DecideApprovalRequest("APPROVED")))
        {
            decide.EnsureSuccessStatusCode();
        }
        using (var adjust = await admin.PostAsJsonAsync("/api/automation/stock/adjust",
            new AdjustStockRequest(RawWarehouse, AdjustItem, -0.25m, downNo)))
        {
            adjust.EnsureSuccessStatusCode();
        }
        Assert.Equal(before + 0.25m, await StockOfAsync(admin, AdjustItem));
        Assert.Equal("EXECUTED", (await ApprovalRowAsync(admin, downNo)).Status);
    }

    /// <summary>
    /// Builds the row shape a pre-round-3 writer produced: an INVENTORY_ADJUST
    /// token with no recorded scope. The current CK_APV_SCOPE_REQUIRED makes
    /// that row impossible, so the constraint is disabled for the insert.
    /// <para>
    /// The restore is ENABLE NOVALIDATE, never ENABLE VALIDATE, and that is
    /// deliberate: re-validating while the legacy row is still there is exactly
    /// the ORA-02293 this round exists to stop, and an ENABLE VALIDATE that threw
    /// out of a finally block would leave the constraint DISABLED and turn every
    /// later approval assertion in this class into a false green. NOVALIDATE
    /// cannot fail, still governs all new DML, and the test's own finally block
    /// re-validates once the row is gone.
    /// </para>
    /// </summary>
    private async Task InsertLegacyApprovalRowAsync(
        string approvalNo, string payload, string status, string requester, string? approver)
    {
        await ExecDirectAsync("ALTER TABLE APPROVAL_REQUEST DISABLE CONSTRAINT CK_APV_SCOPE_REQUIRED");
        try
        {
            await ExecDirectAsync(
                "INSERT INTO APPROVAL_REQUEST " +
                "(APPROVAL_NO, ACTION, REF_NO, PAYLOAD, STATUS, REQUESTER, APPROVER, DECIDED_AT) " +
                "VALUES (:no, 'INVENTORY_ADJUST', 'CYCLE_COUNT_W5AR5', :payload, :status, :requester, :approver, " +
                "CASE WHEN :status2 = 'PENDING' THEN NULL ELSE SYSDATE END)",
                ("no", approvalNo), ("payload", payload), ("status", status),
                ("requester", requester), ("approver", approver), ("status2", status));
        }
        finally
        {
            await ExecDirectAsync(
                "ALTER TABLE APPROVAL_REQUEST ENABLE NOVALIDATE CONSTRAINT CK_APV_SCOPE_REQUIRED");
        }
    }

    /// <summary>
    /// Puts CK_APV_SCOPE_REQUIRED back the way the migration left it, and fails
    /// loudly rather than leaving the table without the control.
    /// </summary>
    private async Task RestoreScopeConstraintAsync()
    {
        await ExecDirectAsync(
            "ALTER TABLE APPROVAL_REQUEST ENABLE NOVALIDATE CONSTRAINT CK_APV_SCOPE_REQUIRED");
        var wasValidated = (await ConstraintStateAsync("CK_APV_SCOPE_REQUIRED")) == "ENABLED/VALIDATED";
        if (wasValidated)
        {
            await ExecDirectAsync(
                "ALTER TABLE APPROVAL_REQUEST ENABLE VALIDATE CONSTRAINT CK_APV_SCOPE_REQUIRED");
        }

        var state = await ConstraintStateAsync("CK_APV_SCOPE_REQUIRED");
        Assert.True(state.StartsWith("ENABLED/", StringComparison.Ordinal),
            $"CK_APV_SCOPE_REQUIRED was left without the control by a test (state={state})");
        if (wasValidated)
        {
            Assert.Equal("ENABLED/VALIDATED", state);
        }
    }

    private async Task<string> ConstraintStateAsync(string constraintName)
    {
        using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText =
            "SELECT STATUS || '/' || VALIDATED FROM user_constraints " +
            "WHERE table_name = 'APPROVAL_REQUEST' AND constraint_name = :name";
        command.Parameters.Add(":name", constraintName);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToString(value) ?? throw new Xunit.Sdk.XunitException(
            $"constraint {constraintName} does not exist on APPROVAL_REQUEST");
    }

    private async Task ExecDirectAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(":" + name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int?> DirectErrorAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            await ExecDirectAsync(sql, parameters);
            return null;
        }
        catch (Oracle.ManagedDataAccess.Client.OracleException ex)
        {
            return ex.Number;
        }
    }

}
