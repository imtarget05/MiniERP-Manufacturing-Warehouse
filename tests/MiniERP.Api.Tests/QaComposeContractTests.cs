using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// A QA compose profile must isolate both the database container and its data
/// volume. Renaming only the container would still allow a QA run to mount the
/// production-shaped external volume from docker-compose.yml.
/// </summary>
public class QaComposeContractTests
{
    [Fact]
    public void QaCompose_OverridesOracleVolumeWithADisposableQaName()
    {
        var compose = File.ReadAllText(TestRepoPaths.Resolve("docker-compose.qa.yml"));

        Assert.Contains("volumes:", compose, StringComparison.Ordinal);
        Assert.Contains("oracle_data:", compose, StringComparison.Ordinal);
        Assert.Contains("name: ${QA_DB_VOLUME:-minierp-qa_oracle_data}", compose, StringComparison.Ordinal);
        Assert.Contains("external: false", compose, StringComparison.Ordinal);
    }
}
