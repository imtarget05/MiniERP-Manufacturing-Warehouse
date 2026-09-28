using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Contract between the published OpenAPI document, the endpoints the host
/// actually maps, and the endpoints the shipped dashboard calls.
/// </summary>
public class ApiSurfaceContractTests
{
    private const string SwaggerPath = "/swagger/v1/swagger.json";

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task SwaggerDocument_DeclaresHttpBearerSecurityScheme()
    {
        var document = await FetchSwaggerAsync();

        Assert.True(document.TryGetProperty("components", out var components),
            "swagger.json has no components object");
        Assert.True(components.TryGetProperty("securitySchemes", out var schemes),
            "swagger.json declares no components.securitySchemes, so clients cannot authenticate");
        Assert.True(schemes.TryGetProperty("BearerAuth", out var bearer),
            "components.securitySchemes has no BearerAuth entry; found: " +
            string.Join(",", schemes.EnumerateObject().Select(s => s.Name)));
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task SwaggerDocument_StockAdjustOperation_RequiresBearerAuth()
    {
        var security = await FetchSecurityAsync("/api/automation/stock/adjust", "post");

        Assert.Contains("BearerAuth", security);
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task SwaggerDocument_LoginOperation_StaysPublic()
    {
        var security = await FetchSecurityAsync("/api/auth/login", "post");

        Assert.True(security.Count == 0,
            "public login must not advertise a bearer requirement, found: " + string.Join(",", security));
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task SwaggerDocument_LoginAndHealthOperations_AreNeverBearerProtected()
    {
        var document = await FetchSwaggerAsync();
        var paths = document.GetProperty("paths");

        var operations = paths.EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject()
                .Where(o => o.Name is "get" or "post" or "put" or "patch" or "delete")
                .Select(o => $"{o.Name.ToUpperInvariant()} {p.Name}"))
            .ToArray();
        Assert.Contains("POST /api/auth/login", operations);
        Assert.Contains("GET /api/health", operations);

        foreach (var operation in operations.Where(o => o.EndsWith("/api/auth/login") || o.EndsWith("/api/health")))
        {
            var parts = operation.Split(' ', 2);
            var security = FetchSecurityFrom(paths, parts[1], parts[0].ToLowerInvariant());
            Assert.True(security.Count == 0,
                $"{operation} must stay publicly reachable, found security: {string.Join(",", security)}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task SwaggerOperations_MatchTheEndpointsTheHostMaps()
    {
        var document = await FetchSwaggerAsync();
        // Same /api/ scope as the host-side enumeration below: ops endpoints
        // served outside /api/ (e.g. GET /metrics for Prometheus) are
        // advertised in swagger.json but intentionally excluded from the API
        // surface contract asserted here.
        var swaggerOperations = document.GetProperty("paths").EnumerateObject()
            .Where(p => p.Name.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.EnumerateObject()
                .Where(o => o.Name is "get" or "post" or "put" or "patch" or "delete")
                .Select(o => $"{o.Name.ToUpperInvariant()} {Normalize(p.Name)}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var routeOperations = new List<string>();
        foreach (var source in Factory().Services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var template = endpoint.RoutePattern.RawText;
                if (template is null || !template.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                var verbs = methods is { Count: > 0 }
                    ? methods
                    : new List<string> { "GET" };
                routeOperations.AddRange(verbs.Select(m => $"{m.ToUpperInvariant()} {Normalize(template)}"));
            }
        }
        routeOperations.Sort(StringComparer.Ordinal);

        var missingFromSwagger = routeOperations.Except(swaggerOperations, StringComparer.Ordinal).ToArray();
        var missingFromHost = swaggerOperations.Except(routeOperations, StringComparer.Ordinal).ToArray();

        Assert.True(missingFromSwagger.Length == 0,
            "operations mapped by the host but absent from swagger.json: " + string.Join(" | ", missingFromSwagger));
        Assert.True(missingFromHost.Length == 0,
            "operations advertised by swagger.json but not mapped by the host: " + string.Join(" | ", missingFromHost));
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public void DashboardApiEndpointLiterals_AllResolveToASwaggerPath()
    {
        var document = SwaggerDocument();
        var swaggerPaths = document.GetProperty("paths").EnumerateObject()
            .Select(p => Normalize(p.Name))
            .ToArray();

        var source = File.ReadAllText(TestRepoPaths.Resolve("dashboard/app.js"));
        // Only self-contained literals are asserted: anything concatenated with a
        // runtime value ends in a separator or a placeholder and is normalized
        // through a route template match instead.
        var literals = Regex.Matches(source, "['\"`](?<path>/api/[A-Za-z0-9_{}./-]*?)['\"`]")
            .Select(m => m.Groups["path"].Value)
            .Where(p => !p.EndsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(literals.Length >= 7,
            $"expected the dashboard to call several literal /api/ endpoints, found {literals.Length}");

        var unresolved = literals
            .Where(literal => !swaggerPaths.Any(path => PathTemplateMatches(path, literal)))
            .ToArray();

        Assert.True(unresolved.Length == 0,
            "dashboard calls /api/ literals that no OpenAPI path serves: " + string.Join(" | ", unresolved));
    }

    private static bool PathTemplateMatches(string swaggerPath, string literal) =>
        OpenApiRouteTemplate.Matches(swaggerPath, literal);

    private static string Normalize(string path) => OpenApiRouteTemplate.Normalize(path);

    private static async Task<HashSet<string>> FetchSecurityAsync(string path, string method)
    {
        var paths = (await FetchSwaggerAsync()).GetProperty("paths");
        return FetchSecurityFrom(paths, path, method);
    }

    private static HashSet<string> FetchSecurityFrom(JsonElement paths, string path, string method)
    {
        Assert.True(paths.TryGetProperty(path, out var operations),
            $"swagger.json has no path {path}; published paths: " +
            string.Join(" | ", paths.EnumerateObject().Select(p => p.Name)));
        Assert.True(operations.TryGetProperty(method, out var operation),
            $"swagger.json {path} has no {method} operation; published: " +
            string.Join(",", operations.EnumerateObject().Select(o => o.Name)));

        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!operation.TryGetProperty("security", out var security) || security.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var requirement in security.EnumerateArray())
        {
            names.UnionWith(requirement.EnumerateObject().Select(s => s.Name));
        }
        return names;
    }

    private static readonly SemaphoreSlim DocumentLock = new(1, 1);
    private static JsonElement? _document;

    private static JsonElement SwaggerDocument()
    {
        if (_document is not null) return _document.Value;
        DocumentLock.Wait();
        try
        {
            return _document ??= FetchSwaggerAsync().GetAwaiter().GetResult();
        }
        finally
        {
            DocumentLock.Release();
        }
    }

    private static async Task<JsonElement> FetchSwaggerAsync()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(SwaggerPath);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.OK == response.StatusCode,
            $"GET {SwaggerPath} returned {(int)response.StatusCode}; body: {body[..Math.Min(400, body.Length)]}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    internal static Task<JsonElement> FetchDocumentAsync() => FetchSwaggerAsync();

    private static WebApplicationFactory<Program> Factory() => new ApiSurfaceFactory();

    private sealed class ApiSurfaceFactory : OracleGuardedWebApplicationFactory
    {
        public ApiSurfaceFactory() : base(nameof(ApiSurfaceFactory), null)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Jwt:Issuer", "MiniERP");
            builder.UseSetting("Jwt:Audience", "MiniERP.Api");
            builder.UseSetting("Jwt:SigningKey", "api-surface-contract-test-signing-key-0123456789");
            builder.UseSetting("Jwt:ExpiryMinutes", "30");
        }
    }
}

/// <summary>
/// One copy of the two rules the OpenAPI guards depend on, so the two tests
/// cannot drift apart and silently stop guarding the production copy in
/// src/Services/SwaggerAuthOperationFilter.cs. The production copy is separate
/// because production cannot reference test code; the route<->document
/// equality test is what keeps the two in agreement.
/// </summary>
internal static class OpenApiRouteTemplate
{
    public static string Normalize(string path) =>
        Regex.Replace(path, @"\{(?<name>[A-Za-z0-9_]+)(?::[^}]+)?\}", "{${name}}");

    /// <summary>A {parameter} segment matches any single segment.</summary>
    public static bool Matches(string template, string literal)
    {
        var expected = Normalize(template).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actual = literal.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length != actual.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].StartsWith('{') && expected[i].EndsWith('}')) continue;
            if (!string.Equals(expected[i], actual[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

/// <summary>Locates the repository root from the test output directory.</summary>
internal static class TestRepoPaths
{
    public static string Resolve(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dashboard", "app.js")))
        {
            directory = directory.Parent;
        }
        Assert.True(directory is not null,
            $"could not locate the repository root above {AppContext.BaseDirectory}");

        return relative.Length == 0
            ? directory.FullName
            : Path.Combine(new[] { directory.FullName }.Concat(relative).ToArray());
    }
}
