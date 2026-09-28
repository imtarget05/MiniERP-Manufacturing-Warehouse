using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

public class Phase2RbacTests
{
    private const string TestKey = "security-contract-test-signing-key-01234567890123456789";

    // Requires Oracle: the login endpoint validates PBKDF2 credentials in APP_USER.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "Admin@123"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.Equal("admin", body.User.Username);
        Assert.Contains(body.User.Roles, r => r is "ADMIN" or "ERP_ADMIN");
    }

    // Requires Oracle: the login endpoint validates PBKDF2 credentials in APP_USER.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_InvalidPassword_Returns401()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "WrongPass123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_CREDENTIALS", error.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task WarehouseRole_CannotCreateProductionOrder()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var token = IssueToken(client, 10, "wh_user", new[] { ErpRoles.CanonicalWarehouse });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/manufacturing/production-order", new
        {
            productionOrderNo = "PO_TEST_RBAC_WH",
            finishedGoodCode = "FG_SHOE_01",
            plannedQuantity = 10,
            warehouseCode = "WH_FG"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTH_FORBIDDEN", body.GetProperty("errorCode").GetString());
    }

    // FR-011: the warehouse role keeps its own mutation surface but must not
    // reach the approval-gated stock adjustment or the admin surface.
    //
    // The two ALLOWED endpoints are asserted on the policy map, never over HTTP:
    // calling them means reaching ERP_TRACEABILITY against whatever schema is
    // configured, and "not 401/403" is satisfied by a 500 as well - a vacuous
    // verdict that also made this test a production writer. The real 2xx
    // behaviour lives in
    // WarehouseOperator_CanMutateWarehouse_AgainstSeedData_ButNotStockAdjustOrAdmin
    // (Integration trait, disposable schema only). The two DENIED endpoints stop
    // inside MutationAuthorizationMiddleware, so their 403 needs no database.
    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task WarehouseOperator_CanMutateWarehouse_ButNotStockAdjustOrAdmin()
    {
        // Allowed half: the warehouse policy covers the two warehouse mutations
        // and the admin/stock-adjust surfaces are reserved for ADMIN.
        Assert.Equal(AuthPolicies.WarehouseMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/warehouse/putaway")));
        Assert.Equal(AuthPolicies.WarehouseMutation,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/warehouse/move")));
        Assert.Equal(AuthPolicies.AdminOnly,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/automation/stock/adjust")));
        Assert.Equal(AuthPolicies.AdminOnly,
            MutationAuthorization.PolicyFor("POST", new PathString("/api/admin/users")));

        // Denied half: real HTTP, real 403 contract, no handler invocation.
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();
        var token = IssueToken(client, 13, "wh_operator", new[] { ErpRoles.Warehouse });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var adjust = await client.PostAsJsonAsync("/api/automation/stock/adjust", new
        {
            warehouseCode = "WH_RAW",
            itemCode = "MAT_BOX_01",
            quantityDelta = -1,
            approvalNo = "RBAC-WH-1"
        });
        using var admin = await client.PostAsJsonAsync("/api/admin/users", new
        {
            username = "rbac_wh_created", password = "WhCreated@2026", fullName = "WH", department = "OPS"
        });

        Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);
        Assert.Equal("AUTH_FORBIDDEN",
            (await adjust.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.Equal("AUTH_FORBIDDEN",
            (await admin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    // Planner may create a production order: the policy map is the no-DB proof,
    // and the 2xx/RELEASED verdict is proved against seeded data below.
    [Fact]
    public void PlannerRole_CanCreateProductionOrder()
    {
        var policy = MutationAuthorization.PolicyFor("POST",
            new PathString("/api/manufacturing/production-order"));
        Assert.Equal(AuthPolicies.ProductionMutation, policy);
        Assert.NotEqual(AuthPolicies.AdminOnly, policy);
        Assert.NotEqual(AuthPolicies.WarehouseMutation, policy);
    }

    // Requires Oracle: the same two claims as above, but exercised for real
    // against the seeded disposable schema (Integration trait, never in the
    // no-database run).
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PlannerRole_CanCreateProductionOrder_AgainstSeedData()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var token = IssueToken(client, 11, "planner_user", new[] { ErpRoles.Planner });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var poNo = $"PO_PL_{DateTime.UtcNow.Ticks % 1000000}";
        using var response = await client.PostAsJsonAsync("/api/manufacturing/production-order", new
        {
            productionOrderNo = poNo,
            // FG_RUNNER_PRO_42 is the finished good seeded by sql/03_seed.sql;
            // FG_SHOE_01 does not exist, so a foreign key used to turn this call
            // into a 500 that the old "not 401/403" assertion happily accepted.
            finishedGoodCode = "FG_RUNNER_PRO_42",
            plannedQuantity = 5,
            warehouseCode = "WH_FG"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = created.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(poNo, root.GetProperty("poNo").GetString());
        // The endpoint nests the created row under "order" (Program.cs:406).
        var order = root.GetProperty("order");
        Assert.Equal(poNo, order.GetProperty("productionOrderNo").GetString());
        Assert.Equal("RELEASED", order.GetProperty("status").GetString());
        Assert.Equal("FG_RUNNER_PRO_42", order.GetProperty("finishedGoodCode").GetString());
        Assert.Equal(5, order.GetProperty("plannedQuantity").GetInt32());
    }

    // Requires Oracle: the warehouse half of
    // WarehouseOperator_CanMutateWarehouse_ButNotStockAdjustOrAdmin, exercised
    // for real. Uses locations that sql/03_seed.sql actually creates, so a 2xx is
    // a business success instead of a 500 that only "is not 401/403".
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WarehouseOperator_CanMutateWarehouse_AgainstSeedData_ButNotStockAdjustOrAdmin()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();
        var token = IssueToken(client, 13, "wh_operator_seed", new[] { ErpRoles.Warehouse });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // sql/09_traceability_seed.sql opens MAT_RUBBER_01 as lot OPEN-MAT_RUBBER_01
        // in WH_RAW / A-01-01, and seeds A-01-02 (WH_RAW) plus LINE-01 (WH_WIP).
        // Same call shape scripts/test-traceability.sh proves, so a 200 is a real
        // business success - and the move back restores the seeded position.
        var run = Guid.NewGuid().ToString("N")[..10];
        using var putaway = await client.PostAsJsonAsync("/api/warehouse/putaway", new
        {
            lotCode = "OPEN-MAT_RUBBER_01",
            fromLocationCode = "A-01-01",
            toLocationCode = "A-01-02",
            qty = 1,
            idempotencyKey = $"RBAC-WH-PUT-{run}"
        });
        using var outMove = await client.PostAsJsonAsync("/api/warehouse/move", new
        {
            lotCode = "OPEN-MAT_RUBBER_01",
            fromWarehouseCode = "WH_RAW",
            fromLocationCode = "A-01-02",
            toWarehouseCode = "WH_WIP",
            toLocationCode = "LINE-01",
            qty = 1,
            idempotencyKey = $"RBAC-WH-OUT-{run}"
        });
        using var backMove = await client.PostAsJsonAsync("/api/warehouse/move", new
        {
            lotCode = "OPEN-MAT_RUBBER_01",
            fromWarehouseCode = "WH_WIP",
            fromLocationCode = "LINE-01",
            toWarehouseCode = "WH_RAW",
            toLocationCode = "A-01-02",
            qty = 1,
            idempotencyKey = $"RBAC-WH-BACK-{run}"
        });

        foreach (var (name, response) in new[]
                 {
                     ("putaway", putaway), ("move-out", outMove), ("move-back", backMove)
                 })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(payload);
            Assert.True(body.RootElement.GetProperty("success").GetBoolean(), $"{name}: {payload}");
            Assert.False(body.RootElement.GetProperty("replayed").GetBoolean(),
                $"{name} must be a fresh mutation, not an idempotent replay: {payload}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task ExpiredToken_Returns401()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var expiredTokenService = new TokenService(new JwtOptions
        {
            Issuer = "MiniERP", Audience = "MiniERP.Api", SigningKey = TestKey, ExpiryMinutes = -1
        });
        var expiredToken = expiredTokenService.Issue(new AuthenticatedUser(1, "admin", null, null, new[] { "ADMIN" })).Token;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);
        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task MissingToken_Returns401()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var mutationResponse = await client.PostAsJsonAsync("/api/stock/in", new
        {
            warehouseCode = "WH_RAW", itemCode = "MAT_BOX_01", quantity = 1, referenceNo = "NO_TOKEN"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, mutationResponse.StatusCode);
    }

    // Requires Oracle: /api/admin/users reads the user list from the database.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Admin_CanManageUsers()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        // 1. Non-admin is rejected (403)
        var plannerToken = IssueToken(client, 12, "planner_only", new[] { ErpRoles.Planner });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plannerToken);
        var forbiddenResponse = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        // 2. Admin is allowed (200)
        var adminToken = IssueToken(client, 1, "admin", new[] { ErpRoles.CanonicalAdmin });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var okResponse = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);

        var users = await okResponse.Content.ReadFromJsonAsync<List<UserSummaryDto>>();
        Assert.NotNull(users);
        Assert.Contains(users!, u => u.Username.Equals("admin", StringComparison.OrdinalIgnoreCase));
    }

    // Requires Oracle: refresh-token rotation re-validates the user in the database.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshToken_Valid_ReturnsNewToken()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        // Login to get valid refresh token
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "Admin@123"));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(loginBody?.RefreshToken);

        // Exchange refresh token for new access token
        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(loginBody!.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var refreshBody = await refreshResponse.Content.ReadFromJsonAsync<TokenRefreshResponse>();
        Assert.NotNull(refreshBody);
        Assert.False(string.IsNullOrWhiteSpace(refreshBody!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshBody.RefreshToken));
        // Token rotation should generate a new refresh token
        Assert.NotEqual(loginBody.RefreshToken, refreshBody.RefreshToken);
    }

    // Requires Oracle: login + refresh rotation both rely on the database user record.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshToken_Revoked_Returns401()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        // Login to get valid refresh token
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "Admin@123"));
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        var refreshToken = loginBody!.RefreshToken!;

        // Explicitly revoke the token
        var revokeResponse = await client.PostAsJsonAsync("/api/auth/revoke", new RevokeTokenRequest(refreshToken));
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // Attempting to refresh with the revoked token must return 401
        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(refreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);

        var error = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REFRESH_TOKEN", error.GetProperty("errorCode").GetString());
    }

    private static string IssueToken(HttpClient client, int userId, string username, string[] roles)
    {
        var tokenService = new TokenService(new JwtOptions
        {
            Issuer = "MiniERP", Audience = "MiniERP.Api", SigningKey = TestKey, ExpiryMinutes = 30
        });
        return tokenService.Issue(new AuthenticatedUser(userId, username, username, "TestDept", roles)).Token;
    }

    // Guarded: see TestOracleDsn. Without an explicit run DSN the host refuses
    // to start instead of inheriting the real Oracle in src/appsettings.json.
    private sealed class RbacTestFactory : OracleGuardedWebApplicationFactory
    {
        public RbacTestFactory() : base(nameof(RbacTestFactory), null)
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
