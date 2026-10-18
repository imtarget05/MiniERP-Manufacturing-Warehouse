using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Guards the additive integration-outbox foundation. It deliberately does not
/// assert that any business operation publishes yet: existing PL/SQL routines
/// own their transaction boundaries and must be refactored under Oracle QA
/// before a producer can be made atomic with them.
/// </summary>
public class IntegrationOutboxContractTests
{
    [Fact]
    public void IntegrationOutboxMigration_IsAdditiveAndSupportsSafeReplay()
    {
        var migration = File.ReadAllText(TestRepoPaths.Resolve("sql", "12_integration_outbox_schema.sql"));

        Assert.Contains("CREATE TABLE INTEGRATION_OUTBOX", migration, StringComparison.Ordinal);
        Assert.Contains("EVENT_ID", migration, StringComparison.Ordinal);
        Assert.Contains("EVENT_TYPE", migration, StringComparison.Ordinal);
        Assert.Contains("AGGREGATE_TYPE", migration, StringComparison.Ordinal);
        Assert.Contains("AGGREGATE_ID", migration, StringComparison.Ordinal);
        Assert.Contains("PAYLOAD_JSON", migration, StringComparison.Ordinal);
        Assert.Contains("IDEMPOTENCY_KEY", migration, StringComparison.Ordinal);
        Assert.Contains("UNIQUE", migration, StringComparison.Ordinal);
        Assert.Contains("('PENDING','IN_PROGRESS','PUBLISHED','FAILED','DEAD_LETTER')", migration,
            StringComparison.Ordinal);
        Assert.Contains("IX_IOB_DISPATCH", migration, StringComparison.Ordinal);
        Assert.Contains("WHERE table_name = 'INTEGRATION_OUTBOX'", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlLoader_AppliesIntegrationOutboxAfterExistingSchemas()
    {
        var loader = File.ReadAllText(TestRepoPaths.Resolve("scripts", "run-sql.sh"));

        var helpdesk = loader.IndexOf("10_helpdesk_schema.sql", StringComparison.Ordinal);
        var outbox = loader.IndexOf("12_integration_outbox_schema.sql", StringComparison.Ordinal);

        Assert.True(helpdesk >= 0, "the current Helpdesk schema must remain in the load sequence");
        Assert.True(outbox > helpdesk, "the additive integration-outbox migration must load after existing schemas");
    }
}
