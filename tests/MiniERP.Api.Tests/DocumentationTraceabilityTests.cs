using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Keeps the requirement traceability matrix honest: every test symbol it cites
/// must exist in this assembly, every endpoint it cites must be served, and the
/// UI coverage column must use the documented scale instead of a blanket claim.
/// </summary>
public class DocumentationTraceabilityTests
{
    private static readonly string[] AllowedUiCoverage = { "Full", "Partial", "Backend only" };

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> RequirementRows()
    {
        var markdown = File.ReadAllText(
            TestRepoPaths.Resolve("docs", "business-analysis", "07-requirement-traceability-matrix.md"));
        var lines = markdown.Split('\n');
        var header = Array.FindIndex(lines, l => l.StartsWith("| Req ID", StringComparison.Ordinal));
        Assert.True(header >= 0, "the RTM has no '| Req ID' table header");

        var columns = SplitRow(lines[header])
            .Select(c => c.Trim())
            .ToArray();
        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (var i = header + 2; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("| **FR-", StringComparison.Ordinal)) continue;
            var cells = SplitRow(lines[i]);
            Assert.True(columns.Length == cells.Length,
                $"row {i + 1} has {cells.Length} cells but the header declares {columns.Length}; " +
                "a missing or unescaped pipe shifts every later column: " + lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < columns.Length; c++)
            {
                row[columns[c]] = cells[c].Trim();
            }
            var empty = columns.Where(c => row[c].Length == 0).ToArray();
            Assert.True(empty.Length == 0,
                $"row {i + 1} has empty column(s): {string.Join(", ", empty)}");
            rows.Add(row);
        }
        Assert.True(rows.Count >= 15, $"expected the RTM to track at least 15 requirements, found {rows.Count}");
        return rows;
    }

    [Fact]
    public void MarkdownRowParser_SplitsOnUnescapedPipesAndUnescapesTheCellContent()
    {
        var cells = SplitRow("| **FR-008** | Tree | `GET /api/trace/{lotCode}?direction=backward\\|forward` | " +
                             "Dashboard \\rightarrow$ tree | `Phase4Tests.Backward`, `Backward` | \ud83d\udfe2 |");

        Assert.Equal(6, cells.Length);
        Assert.Equal("**FR-008**", cells[0]);
        Assert.Contains("?direction=backward|forward", cells[2], StringComparison.Ordinal);
        Assert.Contains("`Phase4Tests.Backward`, `Backward`", cells[4], StringComparison.Ordinal);
    }

    /// <summary>
    /// Reflects every symbol a set of traceability rows cites. Extracted so the
    /// per-row rules (inherited class prefix, ambiguity, attribute requirement)
    /// can be exercised against synthetic fixtures instead of only the live doc.
    /// </summary>
    internal static (int Cited, IReadOnlyList<string> Missing) ResolveRowSymbols(
        IEnumerable<(string ReqId, string Cell)> rows, Assembly assembly,
        bool requireTestAttribute = true)
    {
        var missing = new List<string>();
        var cited = 0;
        string? inheritedClass = null;

        foreach (var (reqId, cell) in rows)
        {
            // A bare Method token may only inherit from its own row.
            inheritedClass = null;
            foreach (var token in Symbols(cell))
            {
                cited++;
                var (className, methodName) = Split(token, inheritedClass);
                if (className is null)
                {
                    missing.Add($"{reqId}: '{token}' has no class and no preceding Class.Method token in the same cell");
                    continue;
                }
                inheritedClass = className;

                var type = assembly.GetType($"MiniERP.Api.Tests.{className}");
                if (type is null)
                {
                    missing.Add($"{reqId}: test class '{className}' does not exist");
                    continue;
                }
                var candidates = type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => m.Name == methodName)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    missing.Add($"{reqId}: '{className}' has no public method '{methodName}'");
                    continue;
                }
                if (candidates.Length > 1)
                {
                    missing.Add($"{reqId}: '{className}.{methodName}' is ambiguous: " +
                                $"{candidates.Length} public overloads " +
                                $"({string.Join(", ", candidates.Select(m => m.GetParameters().Length + " arg(s)"))})");
                    continue;
                }
                var method = candidates[0];
                if (requireTestAttribute &&
                    !method.GetCustomAttributes().Any(a => a is FactAttribute or TheoryAttribute))
                {
                    missing.Add($"{reqId}: '{className}.{methodName}' exists but carries no [Fact]/[Theory]");
                }
            }
        }

        return (cited, missing);
    }

    [Fact]
    public void RtmTestSymbols_AllResolveToPublicTestMethods()
    {
        var assembly = typeof(DocumentationTraceabilityTests).Assembly;
        var rows = RequirementRows();
        var verifiedRows = rows.Where(r => !IsPending(r)).ToArray();
        var (cited, missing) = ResolveRowSymbols(
            verifiedRows.Select(r => (r["Req ID"], r["Automated Test Class & Method"])), assembly);
        var unquoted = verifiedRows
            .SelectMany(r => SymbolSpans(r["Automated Test Class & Method"]).Unquoted
                .Select(token => $"{r["Req ID"]}: unquoted '{token}'"))
            .ToArray();

        Assert.True(cited >= 15, $"expected the RTM to cite at least 15 test symbols, found {cited}");
        Assert.True(missing.Count == 0,
            "the RTM cites symbols that no test provides: " + string.Join(" | ", missing));
        Assert.True(unquoted.Length == 0,
            "the test column may hold only backticked symbols and separators; these tokens would be " +
            "ignored by the parser: " + string.Join(" | ", unquoted));
    }

    /// <summary>
    /// A row may declare "Not yet verified (W5)" instead of naming a symbol. That
    /// is only honest if the row is not marked implemented, if the scheduled test
    /// really exists, and if the sign-off count excludes the row.
    /// </summary>
    [Fact]
    public void RtmPendingRows_NameARealScheduledTestAndAreNotCountedAsVerified()
    {
        var assembly = typeof(DocumentationTraceabilityTests).Assembly;
        var problems = new List<string>();
        var pending = 0;
        var verified = 0;

        foreach (var row in RequirementRows())
        {
            var req = row["Req ID"];
            var cell = row["Automated Test Class & Method"].Replace("`", string.Empty).Trim();
            if (!IsPending(row))
            {
                verified++;
                if (row["Pending Verification (W5)"].Replace("—", string.Empty).Trim().Length != 0)
                {
                    problems.Add($"{req}: is marked verified but also lists a pending W5 test");
                }
                continue;
            }

            pending++;
            if (!cell.StartsWith("Not yet verified", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{req}: cell '{cell}' is not the 'Not yet verified (W5)' marker");
            }
            if (row["Status"].Contains("Implemented", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{req}: a pending row cannot be marked Implemented");
            }
            var scheduled = row.TryGetValue("Pending Verification (W5)", out var value) ? value : string.Empty;
            var (_, missing) = ResolveRowSymbols(new[] { (req, scheduled) }, assembly);
            if (missing.Count > 0)
            {
                problems.Add($"{req}: scheduled W5 test does not exist -> " + string.Join(" | ", missing));
            }
        }

        Assert.Equal(15, verified + pending);
        // The "at least one row must still be pending" rule was a work-in-progress
        // guard for FR-012. Its two audit-trail tests now run green (W8 full suite
        // 302/302), so the invariant is retired rather than relaxed: the per-row loop
        // above still validates any future pending row, and a row cannot claim
        // Implemented while pending.
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [Fact]
    public void RtmSignOffSummary_CountsOnlyRowsThatHaveACoveringTest()
    {
        var rows = RequirementRows();
        var expected = rows.Count(r => !IsPending(r));
        var summary = Regex.Match(File.ReadAllText(
            TestRepoPaths.Resolve("docs", "business-analysis", "07-requirement-traceability-matrix.md")),
            @"Verified with Automated Tests:\*\*\s*(?<count>\d+)\s*/\s*(?<total>\d+)");

        Assert.True(summary.Success, "the sign-off summary no longer states a verified test count");
        Assert.Equal(rows.Count.ToString(), summary.Groups["total"].Value);
        Assert.Equal(expected.ToString(), summary.Groups["count"].Value);
    }

    private static bool IsPending(IReadOnlyDictionary<string, string> row) =>
        row["Automated Test Class & Method"].Contains("Not yet verified", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void SymbolResolver_ReflectsAPairWhoseNameHasNoUnderscore()
    {
        var (cited, missing) = ResolveRowSymbols(
            new[] { ("FR-FIXTURE", "`RtmSymbolProbe.NoUnderscoreInTheName`") },
            typeof(DocumentationTraceabilityTests).Assembly, requireTestAttribute: false);

        Assert.Equal(1, cited);
        Assert.Empty(missing);
    }

    [Fact]
    public void SymbolResolver_DoesNotInheritAClassFromThePreviousRow()
    {
        var (cited, missing) = ResolveRowSymbols(
            new[]
            {
                ("FR-FIXTURE-A", "`RtmSymbolProbe.NoUnderscoreInTheName`"),
                ("FR-FIXTURE-B", "`NoUnderscoreInTheName`")
            },
            typeof(DocumentationTraceabilityTests).Assembly, requireTestAttribute: false);

        Assert.Equal(2, cited);
        var report = string.Join(" | ", missing);
        Assert.Contains("FR-FIXTURE-B", report, StringComparison.Ordinal);
        Assert.Contains("has no class", report, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolResolver_ReportsAnAmbiguousOverloadInsteadOfThrowing()
    {
        var (cited, missing) = ResolveRowSymbols(
            new[] { ("FR-FIXTURE", "`RtmSymbolProbe.Overloaded`") },
            typeof(DocumentationTraceabilityTests).Assembly, requireTestAttribute: false);

        Assert.Equal(1, cited);
        Assert.Contains("ambiguous", string.Join(" | ", missing), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SymbolResolver_ReportsMissingClassAndMissingMethod()
    {
        var (cited, missing) = ResolveRowSymbols(
            new[]
            {
                ("FR-FIXTURE-C", "`NoSuchTestClass.NoSuchMethod`"),
                ("FR-FIXTURE-D", "`RtmSymbolProbe.Absent`")
            },
            typeof(DocumentationTraceabilityTests).Assembly, requireTestAttribute: false);

        Assert.Equal(2, cited);
        Assert.Equal(2, missing.Count);
        Assert.Contains("does not exist", missing[0], StringComparison.Ordinal);
        Assert.Contains("has no public method 'Absent'", missing[1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task RtmEndpoints_AreServedByTheOpenApiDocument()
    {
        var document = await ApiSurfaceContractTests.FetchDocumentAsync();
        var published = document.GetProperty("paths").EnumerateObject()
            .Select(p => p.Name)
            .ToArray();

        var unresolved = new List<string>();
        foreach (var row in RequirementRows())
        {
            var req = row["Req ID"];
            foreach (var token in row["API Endpoint(s)"].Split(',', StringSplitOptions.TrimEntries))
            {
                var value = token.Replace("`", string.Empty).Trim();
                if (!value.StartsWith('/')) continue; // middleware / table names
                var question = value.IndexOf('?');
                if (question >= 0) value = value[..question];
                if (!published.Any(p => PathTemplateMatches(p, value)))
                {
                    unresolved.Add($"{req}: '{token.Trim()}'");
                }
            }
        }

        Assert.True(unresolved.Count == 0,
            "the RTM cites endpoints that swagger.json does not serve: " + string.Join(" | ", unresolved));
    }

    [Fact]
    [Trait("Category", "Integration")] // needs live Oracle (ci-live acceptance)
    public async Task RtmEndpoints_UseFullPathsWithoutEllipsisShorthand()
    {
        var document = await ApiSurfaceContractTests.FetchDocumentAsync();
        var published = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.True(published.Length >= 50, $"the OpenAPI document unexpectedly shrank to {published.Length} paths");

        var shorthand = RequirementRows()
            .Where(r => r["API Endpoint(s)"].Contains("/.../", StringComparison.Ordinal))
            .Select(r => $"{r["Req ID"]}: {r["API Endpoint(s)"]}")
            .ToArray();

        Assert.True(shorthand.Length == 0,
            "an abbreviated '/.../' endpoint hides which resource is meant: " + string.Join(" | ", shorthand));
    }

    [Fact]
    public void RtmUiCoverage_OnlyUsesTheDocumentedScale()
    {
        var invalid = new List<string>();
        foreach (var row in RequirementRows())
        {
            var req = row["Req ID"];
            var coverage = row.TryGetValue("UI Coverage", out var value) ? value : "<missing column>";
            if (!AllowedUiCoverage.Contains(coverage, StringComparer.Ordinal))
            {
                invalid.Add($"{req}: '{coverage}'");
            }
        }

        Assert.True(invalid.Count == 0,
            "UI coverage must be one of " + string.Join(" / ", AllowedUiCoverage) + "; found " +
            string.Join(" | ", invalid));
    }

    [Fact]
    public void OpenApiPathMatching_TreatsTemplateSegmentsAsWildcardsButRejectsUnknownPaths()
    {
        Assert.True(PathTemplateMatches("/api/stock/{warehouseCode}", "/api/stock/WH_RAW"));
        Assert.True(PathTemplateMatches("/api/labels/{id}/render", "/api/labels/42/render"));
        Assert.False(PathTemplateMatches("/api/stock/{warehouseCode}", "/api/warehouse/WH_RAW"));
        Assert.False(PathTemplateMatches("/api/stock/{warehouseCode}", "/api/stock/WH_RAW/lots"));
    }

    private static (string? ClassName, string MethodName) Split(string token, string? inheritedClass)
    {
        var parts = token.Split('.', 2);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (inheritedClass, parts[0]);
    }

    /// <summary>
    /// Every symbol a cell cites, read from its backticked spans. A cell may hold
    /// nothing but backticked symbols and separators, so an unquoted identifier in
    /// the cell is reported instead of silently ignored: a name-shaped token with
    /// no underscore used to be filtered out and never checked at all.
    /// </summary>
    internal static (IReadOnlyList<string> Symbols, IReadOnlyList<string> Unquoted) SymbolSpans(string cell)
    {
        var symbols = new List<string>();
        foreach (Match span in Regex.Matches(cell, @"`(?<symbol>[^`]+)`"))
        {
            foreach (Match token in Regex.Matches(span.Groups["symbol"].Value,
                         @"[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?"))
            {
                symbols.Add(token.Value);
            }
        }

        var unquoted = Regex.Replace(cell, @"`[^`]+`", string.Empty);
        return (symbols, Regex.Matches(unquoted, @"[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value).ToArray());
    }

    private static IEnumerable<string> Symbols(string cell) => SymbolSpans(cell).Symbols;

    // Splits a Markdown table row on pipes that are not escaped, then restores the
    // escaped ones: a cell such as `?direction=backward\|forward` is one value.
    private static string[] SplitRow(string line) => Regex
        .Split(line.Trim().Trim('|'), @"(?<!\\)\|")
        .Select(cell => cell.Replace("\\|", "|", StringComparison.Ordinal).Trim())
        .ToArray();

    /// <summary>
    /// M-4: the UI column is not a free-text field. Every backticked selector it
    /// names must exist in the shipped dashboard sources, otherwise the coverage
    /// verdict is unfalsifiable.
    /// </summary>
    [Fact]
    public void RtmUiSelectors_ExistInTheShippedDashboard()
    {
        var dashboard = new[] { "index.html", "app.js", "styles.css" }
            .Select(f => TestRepoPaths.Resolve("dashboard", f))
            .ToArray();
        var html = File.ReadAllText(TestRepoPaths.Resolve("dashboard", "index.html"));
        var script = File.ReadAllText(TestRepoPaths.Resolve("dashboard", "app.js"));
        var css = File.ReadAllText(TestRepoPaths.Resolve("dashboard", "styles.css"));

        var missing = RequirementRows()
            .SelectMany(row => RowSelectors(row["UI Component / Tab"])
                .Select(selector => new
                {
                    ReqId = row["Req ID"],
                    Selector = selector,
                    Present = IsDefined(selector, html, script, css)
                }))
            .Where(e => !e.Present)
            .Select(e => $"{e.ReqId}: '{e.Selector}'")
            .ToArray();

        Assert.True(missing.Length == 0,
            "the RTM names UI selectors the dashboard does not define: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// Checks the element the selector names, not the literal CSS text: the
    /// dashboard defines elements as id="api-dot" / class="auth-box" and reaches
    /// them from app.js with $('api-dot').
    /// </summary>
    private static bool IsDefined(string selector, string html, string script, string css) => selector switch
    {
        _ when selector.StartsWith('#') => html.Contains($"id=\"{selector[1..]}\"", StringComparison.Ordinal),
        _ when selector.StartsWith('.') => html.Contains($"class=\"{selector[1..]}\"", StringComparison.Ordinal)
            || Regex.IsMatch(html, $"class=\"[^\"]*\b{Regex.Escape(selector)}\b"),
        _ => html.Contains(selector, StringComparison.Ordinal) || script.Contains(selector, StringComparison.Ordinal)
    };

    private static IEnumerable<string> RowSelectors(string cell) => Regex
        .Matches(cell, @"`(?<selector>[A-Za-z0-9_#.\-]+)`")
        .Select(m => m.Groups["selector"].Value)
        .Where(v => v.StartsWith('#') || v.StartsWith('.') || v.StartsWith("tab-"));

    private static bool PathTemplateMatches(string publishedPath, string citedPath) =>
        OpenApiRouteTemplate.Matches(publishedPath, citedPath);
}

/// <summary>
/// Fixture for the resolver rules: top-level so the guard can resolve it as
/// MiniERP.Api.Tests.RtmSymbolProbe, and without [Fact]/[Theory] so xunit does
/// not collect it as a test class.
/// </summary>
internal sealed class RtmSymbolProbe
{
    public void NoUnderscoreInTheName() { }
    public void Overloaded(int value) { }
    public void Overloaded(string value) { }
}
