using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

public class Phase2RbacTests
{
    private const string TestKey = "security-contract-test-signing-key-01234567890123456789";

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task PlannerRole_CanCreateProductionOrder()
    {
        await using var factory = new RbacTestFactory();
        using var client = factory.CreateClient();

        var token = IssueToken(client, 11, "planner_user", new[] { ErpRoles.Planner });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var poNo = $"PO_PL_{DateTime.UtcNow.Ticks % 1000000}";
        var response = await client.PostAsJsonAsync("/api/manufacturing/production-order", new
        {
            productionOrderNo = poNo,
            finishedGoodCode = "FG_SHOE_01",
            plannedQuantity = 5,
            warehouseCode = "WH_FG"
        });

        // Authorization succeeds: it must not be 401 or 403
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    private sealed class RbacTestFactory : WebApplicationFactory<Program>
    {
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
