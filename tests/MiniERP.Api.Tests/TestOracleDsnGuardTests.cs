using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Regression tests for the hermetic-data-source guard (C-1). Without these the
/// guard can be silently bypassed later, which is exactly how the W5a "no-DB"
/// run ended up appending rows to the production Oracle: the suite's verdict was
/// identical whether the ambient DSN was production, absent, or 127.0.0.1:1.
/// The decision logic is exercised through an injected reader, so no test
/// mutates process-wide environment state.
/// </summary>
public class TestOracleDsnGuardTests
{
    private static Func<string, string?> Reader(string? dsn, string? optIn) => name => name switch
    {
        TestOracleDsn.DsnVariable => dsn,
        TestOracleDsn.AmbientOptInVariable => optIn,
        _ => null
    };

    [Fact]
    public void Evaluate_ExplicitRunDsn_IsAccepted()
    {
        Assert.Equal(TestDsnSource.ExplicitRunDsn,
            TestOracleDsn.Evaluate("User Id=x;Data Source=127.0.0.1:1/FREEPDB1;", null));
    }

    [Fact]
    public void Evaluate_MissingDsn_WithoutOptIn_IsRefused()
    {
        Assert.Equal(TestDsnSource.Refused, TestOracleDsn.Evaluate(null, null));
        Assert.Equal(TestDsnSource.Refused, TestOracleDsn.Evaluate("   ", null));
        Assert.Equal(TestDsnSource.Refused, TestOracleDsn.Evaluate(null, "0"));
        Assert.Equal(TestDsnSource.Refused, TestOracleDsn.Evaluate(null, "true"));
        Assert.False(TestOracleDsn.IsGranted(null, null));
        Assert.False(TestOracleDsn.IsGranted("", ""));
    }

    [Fact]
    public void Evaluate_ExplicitOptIn_IsAccepted()
    {
        Assert.Equal(TestDsnSource.AmbientOptIn, TestOracleDsn.Evaluate(null, "1"));
        Assert.Equal(TestDsnSource.AmbientOptIn, TestOracleDsn.Evaluate(null, " 1 "));
        Assert.True(TestOracleDsn.IsGranted(null, "1"));
        Assert.True(TestOracleDsn.IsGranted("Data Source=127.0.0.1:1/FREEPDB1", null));
    }

    /// <summary>
    /// The appsettings.json fallback must be blocked too: the file really does
    /// carry a connection string, yet the guard only trusts the environment, so
    /// an ungranted run still refuses.
    /// </summary>
    [Fact]
    public void Ensure_AppsettingsFallbackPresent_ButRunGrantedNothing_StillRefuses()
    {
        var appsettings = Path.Combine(RepoRoot(), "src", "appsettings.json");
        Assert.True(File.Exists(appsettings), $"expected {appsettings} to exist");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appsettings));
        var ambient = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("OracleDb").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ambient),
            "appsettings.json is expected to carry the ambient DSN this guard must ignore");

        var error = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            TestOracleDsn.Ensure("guard-self-test", Reader(null, null)));

        Assert.Contains("Test data source refused", error.Message, StringComparison.Ordinal);
        Assert.Contains(TestOracleDsn.DsnVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains(TestOracleDsn.AmbientOptInVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_ExplicitDsn_AndOptIn_DoNotThrow()
    {
        Assert.Equal(TestDsnSource.ExplicitRunDsn,
            TestOracleDsn.Ensure("guard-self-test", Reader("Data Source=127.0.0.1:1/FREEPDB1", null)));
        Assert.Equal(TestDsnSource.AmbientOptIn,
            TestOracleDsn.Ensure("guard-self-test", Reader(null, "1")));
    }

    /// <summary>
    /// Host startup, not just a helper call: constructing the shared factory with
    /// an ungranted reader must throw before any host is built.
    /// </summary>
    [Fact]
    public void GuardedFactory_WithoutGrantedDsn_FailsHostStartup()
    {
        var error = Assert.Throws<Xunit.Sdk.XunitException>(() => new RefusingFactory());

        Assert.Contains(nameof(RefusingFactory), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task GuardedFactory_WithGrantedDsn_StartsAndServesTheRealPipeline()
    {
        // The DSN is forced to an unreachable one on the host as well, so the
        // verdict cannot depend on whatever the surrounding run exported.
        using var factory = new BlackholeFactory();
        Assert.Equal(TestDsnSource.ExplicitRunDsn, factory.DsnSource);
        using var client = factory.CreateClient();

        // The host really booted (it answers), and it answers DOWN because the
        // granted DSN is unreachable - never UP from an ambient database.
        using var response = await client.GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DOWN", body.RootElement.GetProperty("status").GetString());
    }

    private const string BlackholeDsn =
        "User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;Pooling=false;Connection Timeout=1;";

    /// <summary>A factory whose run granted nothing: construction must throw.</summary>
    private sealed class RefusingFactory : OracleGuardedWebApplicationFactory
    {
        public RefusingFactory() : base(nameof(RefusingFactory), Reader(null, null))
        {
        }
    }

    private sealed class BlackholeFactory : OracleGuardedWebApplicationFactory
    {
        public BlackholeFactory() : base(nameof(BlackholeFactory), Reader(BlackholeDsn, null))
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:OracleDb", BlackholeDsn);
        }
    }

    /// <summary>
    /// Every factory in this assembly must derive from the guarded base, otherwise
    /// a future class can reintroduce the ambient-DSN hole.
    /// </summary>
    [Fact]
    public void EveryWebApplicationFactoryInTheAssembly_IsGuarded()
    {
        var assembly = typeof(TestOracleDsnGuardTests).Assembly;
        var unguarded = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>)
                .IsAssignableFrom(t))
            .Where(t => !typeof(OracleGuardedWebApplicationFactory).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unguarded.Length == 0,
            "these WebApplicationFactory implementations bypass the DSN guard: " +
            string.Join(", ", unguarded));
    }

    /// <summary>TestStockFixture tops stock up, so it must refuse too.</summary>
    [Fact]
    public void TestStockFixture_WithoutGrantedDsn_RefusesToTouchStock()
    {
        var error = Assert.Throws<Xunit.Sdk.XunitException>(() =>
        {
            var previous = Environment.GetEnvironmentVariable(TestOracleDsn.DsnVariable);
            var previousOptIn = Environment.GetEnvironmentVariable(TestOracleDsn.AmbientOptInVariable);
            try
            {
                Environment.SetEnvironmentVariable(TestOracleDsn.DsnVariable, null);
                Environment.SetEnvironmentVariable(TestOracleDsn.AmbientOptInVariable, null);
                TestStockFixture.Reset(new HttpClient());
            }
            finally
            {
                Environment.SetEnvironmentVariable(TestOracleDsn.DsnVariable, previous);
                Environment.SetEnvironmentVariable(TestOracleDsn.AmbientOptInVariable, previousOptIn);
            }
        });

        Assert.Contains(nameof(TestStockFixture), error.Message, StringComparison.Ordinal);
    }

    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "appsettings.json")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null,
            $"could not locate the repository root above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
