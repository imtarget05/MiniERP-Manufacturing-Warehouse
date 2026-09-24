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

    [Fact]
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

    private sealed class AuthFactory : WebApplicationFactory<Program>
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
