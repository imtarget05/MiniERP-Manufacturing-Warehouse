using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>How a test run authorised the Oracle data source it uses.</summary>
public enum TestDsnSource
{
    /// <summary>The run supplied ConnectionStrings__OracleDb itself.</summary>
    ExplicitRunDsn,

    /// <summary>The run opted in to the ambient (appsettings.json) data source.</summary>
    AmbientOptIn,

    /// <summary>No explicit data source: the host must not start.</summary>
    Refused
}

/// <summary>
/// Fail-closed guard for the test data source. Every test host in this assembly
/// goes through it, so a run can never inherit
/// <c>src/appsettings.json</c> -> <c>Data Source=localhost:1521/FREEPDB1</c>
/// (the developer machine's / production Oracle) by accident.
/// <para>
/// Why this is a hard failure and not a warning: <c>AuditMiddleware</c> appends
/// an <c>APP_AUDIT_EVENT</c> row for every POST/PUT/PATCH/DELETE - including
/// 401/403 - and swallows sink failures, and <see cref="TestStockFixture"/>
/// issues STOCK_IN/STOCK_OUT. So a "no-DB" suite that silently inherits a DSN
/// is a writer, and its verdict is identical whether the DSN points at
/// production, at nothing, or at 127.0.0.1:1. An absent DSN must therefore be
/// an error the operator sees, not a silent no-op.
/// </para>
/// </summary>
public static class TestOracleDsn
{
    /// <summary>Environment variable the test run uses to grant a DSN.</summary>
    public const string DsnVariable = "ConnectionStrings__OracleDb";

    /// <summary>Explicit opt-in that allows the appsettings.json fallback.</summary>
    public const string AmbientOptInVariable = "MINIERP_TEST_ALLOW_AMBIENT_DB";

    public static TestDsnSource Evaluate(string? runDsn, string? ambientOptIn)
    {
        if (!string.IsNullOrWhiteSpace(runDsn)) return TestDsnSource.ExplicitRunDsn;
        return string.Equals(ambientOptIn?.Trim(), "1", StringComparison.Ordinal)
            ? TestDsnSource.AmbientOptIn
            : TestDsnSource.Refused;
    }

    public static TestDsnSource EvaluateCurrentProcess() => Evaluate(
        Environment.GetEnvironmentVariable(DsnVariable),
        Environment.GetEnvironmentVariable(AmbientOptInVariable));

    /// <summary>
    /// True when the given pair grants access. Pure: pass the values you mean,
    /// because null means "not granted", never "read the environment".
    /// </summary>
    public static bool IsGranted(string? runDsn, string? ambientOptIn) =>
        Evaluate(runDsn, ambientOptIn) != TestDsnSource.Refused;

    /// <summary>True when THIS process was granted a DSN by the test run.</summary>
    public static bool IsGrantedForCurrentProcess() =>
        EvaluateCurrentProcess() != TestDsnSource.Refused;

    /// <summary>
    /// Throws unless the data source was granted by the run. <paramref name="owner"/>
    /// names the caller so the failure message points at the class to fix.
    /// </summary>
    public static TestDsnSource Ensure(string owner, Func<string, string?>? readEnv = null)
    {
        var read = readEnv ?? Environment.GetEnvironmentVariable;
        var source = Evaluate(read(DsnVariable), read(AmbientOptInVariable));
        if (source == TestDsnSource.Refused)
        {
            throw new Xunit.Sdk.XunitException(
                $"Test data source refused for '{owner}'. The run did not grant a DSN, so the host " +
                $"would fall back to src/appsettings.json (which points at a real Oracle listener) and " +
                $"every POST in this assembly would write to it. Grant one explicitly:\n" +
                $"  ConnectionStrings__OracleDb='User Id=...;Data Source=<disposable-host>:<port>/FREEPDB1;...' \\\n" +
                $"    dotnet test -c Release\n" +
                $"  # a deliberately unreachable DSN keeps the no-database suite hermetic:\n" +
                $"  ConnectionStrings__OracleDb='User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;' \\\n" +
                $"    dotnet test -c Release --filter 'Category!=Integration'\n" +
                $"  # only for a local container you started on purpose:\n" +
                $"  {AmbientOptInVariable}=1 dotnet test -c Release   # uses src/appsettings.json");
        }

        return source;
    }
}

/// <summary>
/// Base class for every <see cref="WebApplicationFactory{TEntryPoint}"/> in this
/// assembly. The guard runs in the constructor, i.e. before any host is built,
/// so a missing DSN fails the test with an actionable message instead of
/// quietly exercising an ambient database.
/// </summary>
public class OracleGuardedWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _owner;

    /// <param name="owner">Class name used in the failure message.</param>
    /// <param name="readEnv">
    /// Environment reader; overridden by the guard's own regression tests so they
    /// never mutate process-wide state.
    /// </param>
    /// <summary>
    /// Parameterless form used by <c>IClassFixture&lt;&gt;</c>: xUnit can only
    /// activate a fixture whose constructor it can satisfy, so the real path
    /// reads the test run's own environment.
    /// </summary>
    public OracleGuardedWebApplicationFactory()
        : this(null, null)
    {
    }

    /// <summary>
    /// Injected-reader form. Protected on purpose: xUnit refuses to activate a
    /// fixture that exposes more than one public constructor, so only the
    /// parameterless form above may be public - the guard's own tests reach this
    /// one through a derived class.
    /// </summary>
    protected OracleGuardedWebApplicationFactory(
        string? owner, Func<string, string?>? readEnv)
    {
        _owner = string.IsNullOrWhiteSpace(owner) ? GetType().Name : owner!;
        ReadEnv = readEnv;
        DsnSource = TestOracleDsn.Ensure(_owner, readEnv);
    }

    protected Func<string, string?>? ReadEnv { get; }

    /// <summary>How this host's data source was authorised.</summary>
    public TestDsnSource DsnSource { get; }

    /// <summary>Second gate: re-checked when the host is actually created.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        TestOracleDsn.Ensure(_owner, ReadEnv);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
