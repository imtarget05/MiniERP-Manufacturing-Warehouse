using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

public class SecurityContractTests
{
    private const string TestKey = "security-contract-test-signing-key-01234567890123456789";

    [Fact]
    public void TokenService_IssueAndValidate_RoundTripsClaims()
    {
        var service = Service();
        var issued = service.Issue(new AuthenticatedUser(7, "warehouse01", "Warehouse User", "Warehouse",
            new[] { ErpRoles.Warehouse }));
        Assert.True(service.TryValidate(issued.Token, out var principal, out var error));
        Assert.Null(error);
        Assert.NotNull(principal);
        Assert.Equal("warehouse01", principal!.Identity!.Name);
        Assert.Contains(principal.FindAll(System.Security.Claims.ClaimTypes.Role),
            c => c.Value == ErpRoles.Warehouse);
    }

    [Fact]
    public void TokenService_TamperedToken_IsRejected()
    {
        var service = Service();
        var token = service.Issue(new AuthenticatedUser(1, "viewer01", null, null,
            new[] { ErpRoles.Viewer })).Token;
        var parts = token.Split('.');
        var signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        parts[2] = new string(signature);
        var tampered = string.Join('.', parts);
        Assert.False(service.TryValidate(tampered, out var principal, out var error));
        Assert.Null(principal);
        Assert.NotNull(error);
    }

    [Fact]
    public void TokenService_ExpiredToken_IsRejected()
    {
        var service = new TokenService(new JwtOptions
        {
            Issuer = "MiniERP", Audience = "MiniERP.Api", SigningKey = TestKey, ExpiryMinutes = -1
        });
        var token = service.Issue(new AuthenticatedUser(1, "admin", null, null,
            new[] { ErpRoles.Admin })).Token;
        Assert.False(service.TryValidate(token, out _, out var error));
        Assert.Contains("expired", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MutationPolicy_MapsProtectedOperations()
    {
        Assert.Equal(AuthPolicies.WarehouseMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/warehouse/move")));
        Assert.Equal(AuthPolicies.ProcurementMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/procurement/purchase-order")));
        Assert.Equal(AuthPolicies.LotHold,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/warehouse/lots/L1/hold")));
        Assert.Equal(AuthPolicies.MaterialIssue,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/manufacturing/production-order/PO1/issue-lots")));
        Assert.Equal(AuthPolicies.ProductionMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/manufacturing/production-order/PO1/complete-traceable")));
        Assert.Equal(AuthPolicies.LabelMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/labels")));
        // CR-001 two-person rule: proposing is open to operational roles,
        // deciding is restricted to Admin/Support.
        Assert.Equal(AuthPolicies.OperationsMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/automation/approvals")));
        Assert.Equal(AuthPolicies.SupportMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/automation/approvals/APP-1/decision")));
        Assert.Null(MutationAuthorization.PolicyFor("POST", new PathString("/api/auth/login")));
        Assert.Null(MutationAuthorization.PolicyFor("GET", new PathString("/api/warehouse")));
    }

    [Fact]
    public void CorrelationId_InvalidInputIsReplaced()
    {
        Assert.Equal("abc-123", CorrelationIdMiddleware.Normalize("abc-123"));
        Assert.NotEqual("bad value", CorrelationIdMiddleware.Normalize("bad value"));
        Assert.Equal(32, CorrelationIdMiddleware.Normalize(null).Length);
    }

    internal const string StockAdjustEndpoint = "/api/automation/stock/adjust";

    [Theory]
    [InlineData(StockAdjustEndpoint)]
    [InlineData(StockAdjustEndpoint + "/")]
    public void StockAdjustPolicy_RequiresAdminOnly_ForCanonicalAndTrailingSlashPath(string path)
    {
        var actual = MutationAuthorization.PolicyFor("POST", new PathString(path));
        Assert.True(AuthPolicies.AdminOnly == actual,
            $"H-04 stock-adjust authorization: role=<any authenticated> endpoint=POST {path} " +
            $"expected={AuthPolicies.AdminOnly} actual={actual ?? "<none>"}");
    }

    [Theory]
    [InlineData(StockAdjustEndpoint)]
    [InlineData(StockAdjustEndpoint + "/")]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task StockAdjust_WithoutToken_Returns401(string path)
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();

        var (status, detail) = await PostStockAdjustAsync(client, path, token: null);

        Assert.True(StatusCodes.Status401Unauthorized == status,
            $"H-04 stock-adjust authorization: role=anonymous endpoint=POST {path} " +
            $"expected={StatusCodes.Status401Unauthorized} actual={status} body={detail}");
    }

    // ERP_ADMIN is a seeded role and the only non-canonical one allowed through
    // AuthPolicies.AdminOnly; proving its success path needs Oracle, so this
    // wave asserts only that every non-admin role is stopped before the handler.
    [Theory]
    [InlineData(StockAdjustEndpoint, ErpRoles.Viewer)]
    [InlineData(StockAdjustEndpoint, ErpRoles.Warehouse)]
    [InlineData(StockAdjustEndpoint + "/", ErpRoles.Viewer)]
    [InlineData(StockAdjustEndpoint + "/", ErpRoles.Warehouse)]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task StockAdjust_NonAdminRole_Returns403(string path, string role)
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var token = Service().Issue(new AuthenticatedUser(42, "role-probe", null, null,
            new[] { role })).Token;

        var (status, detail) = await PostStockAdjustAsync(client, path, token);

        Assert.True(StatusCodes.Status403Forbidden == status,
            $"H-04 stock-adjust authorization: role={role} endpoint=POST {path} " +
            $"expected={StatusCodes.Status403Forbidden} actual={status} body={detail}");
    }

    // The second half of the two-person rule: deciding an approval is the
    // approver-only step (AuthPolicies.SupportMutation). A VIEWER and a
    // WAREHOUSE_OPERATOR token must both be stopped at the middleware with
    // 403 before any package call, so this needs no database.
    [Theory]
    [InlineData(ErpRoles.Viewer)]
    [InlineData(ErpRoles.Warehouse)]
    [InlineData(ErpRoles.Production)]
    [InlineData(ErpRoles.Planner)]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task ApprovalDecision_NonApproverRole_Returns403(string role)
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var token = Service().Issue(new AuthenticatedUser(43, "role-probe-approver", null, null,
            new[] { role })).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsJsonAsync(
            "/api/automation/approvals/W5A-ROLE-PROBE/decision", new { decision = "APPROVED" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(StatusCodes.Status403Forbidden == (int)response.StatusCode,
            $"CR-001 two-person rule: role={role} endpoint=POST /api/automation/approvals/{{approvalNo}}/decision " +
            $"expected={StatusCodes.Status403Forbidden} actual={(int)response.StatusCode} body={body}");
        Assert.Equal("AUTH_FORBIDDEN",
            JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    // Proposing is open to operational roles: a warehouse token must not be
    // stopped by MutationAuthorizationMiddleware. This is asserted on the policy
    // map only, never over HTTP, because an HTTP POST that reaches the handler
    // writes a real APPROVAL_REQUEST row into whatever schema the ambient
    // ConnectionStrings__OracleDb points at - including a production database
    // when the suite is run without a disposable one. The end-to-end "warehouse
    // proposes, admin approves" path is covered by the Integration-trait test
    // AutomationIntegrationTests.ApprovalGate_WarehouseRequester_CanPropose_
    // ButCannotDecideOrAdjust, which always runs against a disposable schema.
    [Fact]
    public void ApprovalRequest_OperationalRole_IsAllowedToPropose()
    {
        var policy = MutationAuthorization.PolicyFor("POST",
            new PathString("/api/automation/approvals"));
        Assert.Equal(AuthPolicies.OperationsMutation, policy);
        Assert.NotEqual(AuthPolicies.AdminOnly, policy);
        Assert.NotEqual(AuthPolicies.SupportMutation, policy);
    }

    // The audit sink is best effort: an unreachable Oracle must not turn a
    // rejected mutation into a 500. Removing the swallow in AuditMiddleware is
    // exactly the break this test names.
    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task DeniedMutation_StillReturnsTheAuthContractWhenTheAuditSinkIsUnreachable()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/stock/in", new
        {
            warehouseCode = "WH_RAW", itemCode = "MAT_BOX_01", quantity = 1, referenceNo = "SEC-AUDIT"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTH_REQUIRED", body.GetProperty("errorCode").GetString());
        Assert.False(body.GetProperty("success").GetBoolean());
    }

    // FR-012 audit-row coverage needs the real sink, so these carry the
    // Integration trait and run in W5 (plan Task 5.5: audit-row test deferred to
    // W5). They are excluded from the hermetic no-DB run and the RTM still marks
    // FR-012 "Not yet verified (W5)" until they are actually green.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuditMiddleware_RecordsDeniedMutationInAppAuditEvent()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var lot = UniqueLot("DENIED");
        var correlationId = $"w2-audit-denied-{lot}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/warehouse/lots/{lot}/hold")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var row = await PollAuditEventAsync(factory, "SECURITY_DENIED", lot, correlationId);
        Assert.Equal("HTTP", row.ENTITY_TYPE);
        Assert.Equal(lot, row.ENTITY_KEY);
        Assert.Equal("anonymous", row.ACTOR);
        Assert.Contains("\"status\":401", row.DETAILS_JSON, StringComparison.Ordinal);
        Assert.Equal(correlationId, row.CORRELATION_ID);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuditMiddleware_RecordsAuthenticatedMutationWithItsRouteEntityKey()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await IssueAdminTokenAsync(factory));
        var lot = UniqueLot("MUT");
        var correlationId = $"w2-audit-mutation-{lot}";

        // An unknown lot makes ERP_TRACEABILITY.set_lot_hold roll back, so this
        // exercises the whole mutation path without touching inventory.
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/warehouse/lots/{lot}/hold")
        {
            Content = JsonContent.Create(new { })
        };
        message.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(message);

        Assert.False(response.IsSuccessStatusCode,
            "an unknown lot cannot be held; the subject under test is the audit row, not the status");
        var row = await PollAuditEventAsync(factory, "API_MUTATION", lot, correlationId);
        Assert.Equal("HTTP", row.ENTITY_TYPE);
        Assert.Equal(lot, row.ENTITY_KEY);
        Assert.Equal("admin", row.ACTOR);
        Assert.Contains("\"path\":\"/api/warehouse/lots/", row.DETAILS_JSON, StringComparison.Ordinal);
    }

    private static string UniqueLot(string prefix) =>
        $"LOT-{prefix}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();

    /// <summary>
    /// AuditMiddleware appends the row after the response is produced, so the
    /// insert lands a moment later than the client sees the status.
    /// </summary>
    private static async Task<AuditRow> PollAuditEventAsync(
        AuthFactory factory, string eventType, string entityKey, string correlationId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString("OracleDb");
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "Oracle connection string missing: these are Integration-trait tests and need the seeded database");
        ArgumentNullException.ThrowIfNull(connectionString);

        const string query =
            "SELECT EVENT_TYPE, ENTITY_TYPE, ENTITY_KEY, ACTOR, DETAILS_JSON, CORRELATION_ID " +
            "FROM (SELECT * FROM APP_AUDIT_EVENT WHERE EVENT_TYPE = :t AND ENTITY_KEY = :k " +
            "      AND CORRELATION_ID = :c ORDER BY AUDIT_ID DESC) WHERE ROWNUM = 1";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var connection = new OracleConnection(connectionString);
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
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException(
            $"no APP_AUDIT_EVENT row EVENT_TYPE={eventType} ENTITY_KEY={entityKey} " +
            $"CORRELATION_ID={correlationId} appeared within 20s");
    }

    private static async Task<string> IssueAdminTokenAsync(AuthFactory factory)
    {
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", "Admin@123"));
        Assert.True(login.IsSuccessStatusCode,
            $"seeded admin login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        return payload!.AccessToken;
    }

    private readonly record struct AuditRow(
        string EVENT_TYPE, string? ENTITY_TYPE, string? ENTITY_KEY,
        string ACTOR, string? DETAILS_JSON, string? CORRELATION_ID);

    private static async Task<(int Status, string Detail)> PostStockAdjustAsync(
        HttpClient client, string path, string? token)
    {
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await client.PostAsJsonAsync(path, new
            {
                warehouseCode = "WH_RAW",
                itemCode = "MAT_BOX_01",
                quantityDelta = -1,
                approvalNo = "SEC-H04"
            });
            var body = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, body.Length > 200 ? body[..200] : body);
        }
        catch (Exception ex)
        {
            return (0, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task ProtectedMutation_WithoutToken_Returns401()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/stock/in", new
        {
            warehouseCode = "WH_RAW", itemCode = "MAT_BOX_01", quantity = 1, referenceNo = "SEC-401"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("AUTH_REQUIRED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task ProtectedMutation_ViewerRole_Returns403()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var token = new TokenService(new JwtOptions
        {
            Issuer = "MiniERP", Audience = "MiniERP.Api", SigningKey = TestKey, ExpiryMinutes = 30
        }).Issue(new AuthenticatedUser(2, "viewer01", null, null, new[] { ErpRoles.Viewer })).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PostAsJsonAsync("/api/stock/in", new
        {
            warehouseCode = "WH_RAW", itemCode = "MAT_BOX_01", quantity = 1, referenceNo = "SEC-403"
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTH_FORBIDDEN", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    private static TokenService Service() => new(new JwtOptions
    {
        Issuer = "MiniERP", Audience = "MiniERP.Api", SigningKey = TestKey, ExpiryMinutes = 30
    });

    // Guarded: without an explicitly granted DSN this factory must not start, so
    // the auth-boundary tests can never append APP_AUDIT_EVENT rows to whatever
    // schema src/appsettings.json happens to name (that is how a "no-DB" run
    // wrote 21 rows into the production Oracle in W5a).
    private sealed class AuthFactory : OracleGuardedWebApplicationFactory
    {
        public AuthFactory() : base(nameof(AuthFactory), null)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Jwt:Issuer", "MiniERP");
            builder.UseSetting("Jwt:Audience", "MiniERP.Api");
            builder.UseSetting("Jwt:SigningKey", TestKey);
            builder.UseSetting("Jwt:ExpiryMinutes", "30");
        }
    }
}
