using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Phase 4 unit tests (no database): label validation/normalization contract,
/// print-job DTO shape, and deterministic (reprint-identical) rendering.
/// </summary>
public class TraceabilityPhase4Tests
{
    [Theory]
    [InlineData(null, "HTML")]
    [InlineData("", "HTML")]
    [InlineData("html", "HTML")]
    [InlineData(" zpl ", "ZPL")]
    public void NormalizeLabelFormat_DefaultsAndNormalizes(string? input, string expected)
    {
        Assert.Equal(expected, TraceabilityLogic.NormalizeLabelFormat(input));
    }

    [Fact]
    public void NormalizeLabelFormat_Unknown_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TraceabilityLogic.NormalizeLabelFormat("PDF"));
    }

    [Theory]
    [InlineData("LOT", true)]
    [InlineData("lot", true)]
    [InlineData("ITEM", true)]
    [InlineData("LOCATION", true)]
    [InlineData("PRODUCTION_ORDER", true)]
    [InlineData("CARRIER", false)]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    public void IsValidLabelEntityType_Cases(string? input, bool expected)
    {
        Assert.Equal(expected, TraceabilityLogic.IsValidLabelEntityType(input));
    }

    [Theory]
    [InlineData("RAW_MATERIAL", true)]
    [InlineData("finished_good", true)]
    [InlineData("LOCATION", true)]
    [InlineData("PALLET", false)]
    [InlineData(null, false)]
    public void IsValidLabelType_Cases(string? input, bool expected)
    {
        Assert.Equal(expected, TraceabilityLogic.IsValidLabelType(input));
    }

    [Fact]
    public void LabelJobDto_RoundTripsPrintJobShape()
    {
        var job = new LabelJobDto(7, "RAW_MATERIAL", "LOT",
            "RM001-DEMO-001", "DEFAULT", 2, "ZPL", "PRINTED");
        Assert.Equal(7, job.PrintJobId);
        Assert.Equal("ZPL", job.Format);
        Assert.Equal("PRINTED", job.Status);
    }

    [Fact]
    public void RenderHtml_ReprintIsDeterministic()
    {
        static string Render() => LabelRenderService.RenderHtml(
            "MiniERP Demo", "MAT_RUBBER_01", "Caos su", "RM001-001", 100m,
            "PAIR", "WH_RAW/RCV-01", new DateTime(2026, 1, 1), null);
        // Same entity rendered twice must be byte-identical: a reprint only
        // appends a LABEL_PRINT_JOB row, it never regenerates label content.
        Assert.Equal(Render(), Render());
    }

    [Fact]
    public void RenderZpl_ReprintIsDeterministic_AndCarriesBarcode()
    {
        static string Render() => LabelRenderService.RenderZpl(
            "MAT_RUBBER_01", "RM001-001", 100m, "PAIR");
        var a = Render();
        Assert.Equal(a, Render());
        Assert.StartsWith("^XA", a);
        Assert.Contains("^XZ", a);
        Assert.Contains("RM001-001", a);
    }

    [Fact]
    public void LabelPrintResponse_HoldsJobAndRenderedPayload()
    {
        var job = new LabelJobDto(1, "FINISHED_GOOD", "LOT",
            "FG001-001", "DEFAULT", 1, "HTML", "PRINTED");
        var resp = new LabelPrintResponse(job, "<div class=\"erp-label\"></div>");
        Assert.Equal(job, resp.Job);
        Assert.Contains("erp-label", resp.Rendered);
    }

    [Fact]
    public void CreateLabelService_BindsOracleOutputParameter()
    {
        var source = ReadTraceabilityService();
        Assert.Contains("p.Add(\"p_job_id\", dbType: DbType.Decimal", source, StringComparison.Ordinal);
        Assert.Contains("direction: ParameterDirection.Output", source, StringComparison.Ordinal);
        Assert.Contains("PRINT_JOB_ID AS PrintJobId", source, StringComparison.Ordinal);
        Assert.Contains("LABEL_TYPE AS LabelType", source, StringComparison.Ordinal);
        Assert.Contains("private sealed record LabelJobRow", source, StringComparison.Ordinal);
        Assert.Contains("return row is null ? null : MapLabel(row)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceService_UsesDedicatedForwardOutputRecord()
    {
        var source = ReadTraceabilityService();
        Assert.Contains("private sealed record TraceOutputLotRow", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<TraceOutputLotRow>", source, StringComparison.Ordinal);
    }

    private static string ReadTraceabilityService()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "src", "Services", "ErpDbService.Traceability.cs"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Program.cs")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}