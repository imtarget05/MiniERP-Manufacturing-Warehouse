using System.Text.RegularExpressions;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// H-06 / Task 8.8. The shipped dashboard must be a *client of the real API*:
/// no seeded demo rows, no invented KPI values, no token in the URL or in
/// localStorage, and no endpoint literal that the host does not actually map.
/// <para>
/// These assertions read the shipped files as text. They deliberately do not
/// boot a web host, so they are DB-free and belong to the no-DB gate.
/// </para>
/// </summary>
public class DashboardAssetContractTests
{
    private static string AppJs => Read("dashboard", "app.js");
    private static string IndexHtml => Read("dashboard", "index.html");
    private static string RuntimeConfig => Read("dashboard", "runtime-config.js");
    private static string NginxConf => Read("dashboard", "nginx.conf");
    private static string ProgramCs => Read("src", "Program.cs");

    // ---------------------------------------------------------------- mocks

    [Theory]
    [InlineData("PO001")]
    [InlineData("MOCK_STOCK")]
    [InlineData("MOCK_BOM")]
    [InlineData("MOCK_ERRORS")]
    [InlineData("MOCK pre-flight")]
    [InlineData("MOCK:")]
    [InlineData("540 / 500")]
    [InlineData("Đế cao su lưu hóa đúc sẵn")]
    public void AppJs_HasNoHardCodedDemoContent(string forbidden)
    {
        Assert.DoesNotContain(forbidden, AppJs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppJs_HardCodedKpiTotalsAreGone()
    {
        Assert.DoesNotContain("100%", AppJs, StringComparison.Ordinal);
        Assert.DoesNotContain("50 đôi", AppJs, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- configuration

    [Fact]
    public void AppJs_ReadsApiBaseFromRuntimeConfig()
    {
        Assert.Contains("__MINI_ERP_CONFIG__", AppJs, StringComparison.Ordinal);
        Assert.Contains("apiBaseUrl", AppJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_ContainsNoBakedApiOrigin()
    {
        Assert.DoesNotContain("http://localhost:5000", AppJs, StringComparison.Ordinal);
        Assert.DoesNotContain("https://localhost", AppJs, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeConfig_PublishesTheApiBaseContract()
    {
        Assert.Contains("__MINI_ERP_CONFIG__", RuntimeConfig, StringComparison.Ordinal);
        Assert.Contains("apiBaseUrl", RuntimeConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeConfig_CarriesNoTokenOrSecret()
    {
        Assert.DoesNotContain("token", RuntimeConfig, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", RuntimeConfig, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- tokens

    [Fact]
    public void AppJs_NeverUsesLocalStorage()
    {
        Assert.DoesNotContain("localStorage", AppJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_NeverPutsACredentialInTheUrl()
    {
        Assert.False(Regex.IsMatch(AppJs, @"[?&](token|access_token|jwt|bearer)=", RegexOptions.IgnoreCase),
            "a credential is read from the query string");
        Assert.DoesNotContain("location.hash", AppJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_KeepsExactlyOneSessionStorageTokenStore()
    {
        Assert.Contains("const ACCESS_KEY = 'minierp.accessToken';", AppJs, StringComparison.Ordinal);
        Assert.Contains("const REFRESH_KEY = 'minierp.refreshToken';", AppJs, StringComparison.Ordinal);

        var handles = Regex.Matches(AppJs, @"sessionStorage\.(?:setItem|getItem|removeItem)\(\s*([A-Za-z_$][\w$]*)")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(handles);
        Assert.All(handles, h => Assert.True(h is "ACCESS_KEY" or "REFRESH_KEY",
            "sessionStorage is addressed through '" + h + "', outside the two declared credential keys"));
        Assert.Equal(new[] { "ACCESS_KEY", "REFRESH_KEY" },
            handles.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AppJs_SendsTheAccessTokenAsABearerHeader()
    {
        Assert.Contains("Authorization", AppJs, StringComparison.Ordinal);
        Assert.Contains("Bearer ", AppJs, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- session

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/logout")]
    public void AppJs_UsesOnlySessionEndpointsTheHostMaps(string path)
    {
        Assert.Contains(path, AppJs, StringComparison.Ordinal);
        Assert.Contains("\"" + path + "\"", ProgramCs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_UsesNoSessionEndpointOutsideTheMappedSet()
    {
        var mapped = Regex.Matches(ProgramCs, @"app\.Map(?:Get|Post|Put|Delete)\(""(/api/auth/[^""]*)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var called = Regex.Matches(AppJs, @"'(/api/auth/[^']*)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

        foreach (var path in called)
        {
            Assert.True(mapped.Contains(path),
                "the dashboard calls " + path + ", which Program.cs does not map; mapped: " +
                string.Join(", ", mapped.OrderBy(p => p, StringComparer.Ordinal)));
        }
    }

    [Fact]
    public void AppJs_RevokesTheRefreshTokenOnLogout()
    {
        var logout = SliceFunction(AppJs, "async function logout");
        Assert.Contains("/api/auth/logout", logout, StringComparison.Ordinal);
        Assert.Contains("refreshToken", logout, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_RefreshesTheAccessTokenWhenTheApiAnswers401()
    {
        // apiFetch delegates to a single-flight refresh helper; the endpoint it
        // calls must be the one the host maps.
        var request = SliceFunction(AppJs, "async function apiFetch");
        Assert.Contains("refreshSession()", request, StringComparison.Ordinal);
        var helper = SliceFunction(AppJs, "function refreshSession");
        Assert.Contains("'/api/auth/refresh'", helper, StringComparison.Ordinal);
        Assert.Contains("refreshInFlight", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_RendersNothingBeforeTheUserIsSignedIn()
    {
        var boot = SliceFunction(AppJs, "function boot");
        Assert.Contains("showLogin", boot, StringComparison.Ordinal);
        Assert.Contains("loadOverview", boot, StringComparison.Ordinal);
        Assert.Matches(@"if\s*\(\s*!\s*accessToken\s*\)", boot);
    }

    [Fact]
    public void AppJs_ReturnsToTheLoginScreenOn401Or403()
    {
        Assert.Contains("showLogin", AppJs, StringComparison.Ordinal);
        var request = SliceFunction(AppJs, "async function apiFetch");
        Assert.Contains("401", request, StringComparison.Ordinal);
        Assert.Contains("403", request, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ loaded vs zero

    [Fact]
    public void AppJs_SeparatesAnUnreadSeriesFromAReadButEmptyOne()
    {
        // A null array means "not read yet" and must render as N/A; [] means the
        // API answered and there is nothing, which is a real 0.
        Assert.Contains("const isLoaded = (rows) => Array.isArray(rows);", AppJs, StringComparison.Ordinal);
        Assert.Contains("warehouses: null,", AppJs, StringComparison.Ordinal);
        Assert.Contains("errors: null,", AppJs, StringComparison.Ordinal);
        Assert.Contains("staleOrders: null,", AppJs, StringComparison.Ordinal);
        Assert.Contains("stockLoaded: false,", AppJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_ShowsZeroForBelowMinimumOnceTheStockHasBeenRead()
    {
        // The defect: gating the KPI on row count, so a fleet whose warehouses
        // all read [] renders N/A - "not read" - instead of 0.
        var overview = SliceFunction(AppJs, "function renderOverview");

        Assert.Contains("data.stockLoaded", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("allStock.length ? String(lowCount) : NA", overview, StringComparison.Ordinal);
        Assert.Matches(@"data\.stockLoaded\s*\?\s*String\(lowCount\)\s*:\s*NA", overview);
    }

    [Fact]
    public void AppJs_DoesNotRaceTheSelectedWarehouseAgainstTheFleetWideRead()
    {
        // Both used to write data.stock and data.stockByWarehouse[code].
        var overview = SliceFunction(AppJs, "async function loadOverview");
        Assert.DoesNotContain("loadStock(), loadAllStock()", overview, StringComparison.Ordinal);

        var select = SliceFunction(AppJs, "function loadStock");
        Assert.DoesNotContain("apiJson(", select, StringComparison.Ordinal);
        Assert.Contains("data.stockByWarehouse[code]", select, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_RefreshesTheBalanceAfterEveryMutationThatMovesIt()
    {
        // The selection helper is cache-only, so a mutation that changes a
        // balance must trigger the fleet read instead - otherwise the panel
        // keeps showing the pre-mutation quantity.
        foreach (var writer in new[] { "async function submitStock",
                                       "async function submitLotReceive",
                                       "async function submitLotMove" })
        {
            var body = SliceFunction(AppJs, writer);
            Assert.Contains("await loadAllStock()", body, StringComparison.Ordinal);
            Assert.DoesNotContain("await loadStock()", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppJs_BumpsTheReadEpochWhenTheSessionEnds()
    {
        Assert.Contains("let readEpoch = 0;", AppJs, StringComparison.Ordinal);
        Assert.Contains("readEpoch += 1;", SliceFunction(AppJs, "function showLogin"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_TheReadGuardDropsAStaleResultBeforeItWrites()
    {
        // The guard is the only thing standing between a late response and the
        // panels, so its shape is pinned: capture the epoch before awaiting, and
        // return before calling apply() if it moved.
        var guard = SliceFunction(AppJs, "async function guardedRead");

        Assert.Contains("const epoch = readEpoch;", guard, StringComparison.Ordinal);
        Assert.Contains("const result = await read();", guard, StringComparison.Ordinal);
        Assert.Matches(@"if\s*\(epoch\s*!==\s*readEpoch\)\s*return false;", guard);
        Assert.Contains("await apply(result);", guard, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("loadAllStock")]
    [InlineData("loadErrors")]
    [InlineData("loadAlerts")]
    [InlineData("loadStaleOrders")]
    [InlineData("loadIncidents")]
    [InlineData("loadApprovals")]
    public void AppJs_EveryReaderDropsAStaleResultRatherThanWriting(string loader)
    {
        // Per-loader, not just stock: a straggler from any of the six would
        // repopulate its own panel behind the login view.
        var body = SliceFunction(AppJs, "async function " + loader);

        Assert.Contains("guardedRead(", body, StringComparison.Ordinal);
        // Nothing may touch the data model outside the guarded callback, so the
        // only writes are the ones indented inside it.
        foreach (var write in new[] { "data.errors =", "data.alerts =", "data.staleOrders =",
                                      "data.incidents =", "data.approvals =", "data.stockLoaded =" })
        {
            if (body.Contains(write, StringComparison.Ordinal))
            {
                Assert.Contains("() =>", body, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("loadHealth")]
    [InlineData("loadWarehouses")]
    [InlineData("loadSession")]
    public void AppJs_TheRemainingReadersAreGuardedToo(string loader)
    {
        Assert.Contains("guardedRead(",
            SliceFunction(AppJs, "async function " + loader), StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_AnUnreadWarehouseListLeavesTheStockKpiUnshown()
    {
        // loadAllStock used to iterate data.warehouses unconditionally, so an
        // unreadable list threw instead of showing N/A.
        var loader = SliceFunction(AppJs, "async function loadAllStock");

        Assert.Contains("if (!Array.isArray(data.warehouses))", loader, StringComparison.Ordinal);
        Assert.Contains("data.stockLoaded = false;", loader, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- endpoints

    [Fact]
    public void AppJs_CallsOnlyEndpointsTheHostActuallyMaps()
    {
        var routes = Regex.Matches(ProgramCs, @"app\.Map(?:Get|Post|Put|Delete)\(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(routes);

        var literals = Regex.Matches(AppJs, @"'(/api/[^']*)'")
            .Select(m => m.Groups[1].Value)
            .Where(p => p.Length > "/api/".Length)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(literals);

        foreach (var literal in literals)
        {
            var normalised = NormaliseDynamic(literal);
            Assert.True(routes.Any(r => RouteMatches(r, normalised)),
                "dashboard/app.js calls '" + literal + "', but Program.cs maps no such route. Mapped: " +
                string.Join(", ", routes.Where(r => r.StartsWith("/api/", StringComparison.Ordinal)).OrderBy(r => r, StringComparer.Ordinal)));
        }
    }

    [Fact]
    public void AppJs_TargetsTheSameOriginApiPrefix()
    {
        Assert.False(Regex.IsMatch(AppJs, "[\'`\"]https?://", RegexOptions.CultureInvariant),
            "app.js must address the API same-origin, not through a baked absolute origin");
    }

    [Fact]
    public void AppJs_MarksTheMissingBomReadContractInsteadOfFakingIt()
    {
        // Not "N/A occurs somewhere": the bom panel itself must carry the
        // statement, name the endpoint that does not exist, and invent no rows.
        var bom = SliceFunction(AppJs, "function renderOverview");

        Assert.Contains("$('bom-list')", bom, StringComparison.Ordinal);
        Assert.Contains("emptyList(", bom, StringComparison.Ordinal);
        Assert.Contains("API chưa có endpoint đọc BOM", bom, StringComparison.Ordinal);
        Assert.Contains("/api/manufacturing/bom/line", bom, StringComparison.Ordinal);
        Assert.DoesNotContain("MAT_", bom, StringComparison.Ordinal);
        Assert.DoesNotContain("FG_", bom, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_RendersAnEmptyStateForEmptyApiPayloads()
    {
        // Every list panel routes a read-but-empty payload through emptyList(),
        // not through a bare "no data" branch, and the CSS gives it a class.
        foreach (var panel in new[] { "error-list", "low-stock-list", "stale-list",
                                      "incident-list", "approval-list" })
        {
            var renderer = SliceFunction(AppJs, "function render" + Panel(panel));
            Assert.Contains("emptyList(", renderer, StringComparison.Ordinal);
            Assert.Contains(panel, renderer, StringComparison.Ordinal);
        }

        Assert.Contains(".empty-state", Read("dashboard", "styles.css"), StringComparison.Ordinal);
    }

    /// <summary>Maps a panel id to the renderer that owns it.</summary>
    private static string Panel(string id) => id switch
    {
        "error-list" => "Errors",
        "low-stock-list" => "Alerts",
        "stale-list" => "StaleOrders",
        "incident-list" => "Incidents",
        _ => "Approvals",
    };

    [Fact]
    public void AppJs_OnlyInjectsLabelMarkupTheServiceHasEncoded()
    {
        // printLabel injects body.rendered straight into the DOM for HTML
        // labels. That is only safe because LabelRenderService HTML-encodes
        // every field; this assertion fails loudly if that coupling is dropped.
        var service = Read("src", "Services", "LabelRenderService.cs");

        Assert.Contains("HtmlEncode", service, StringComparison.Ordinal);
        var printer = SliceFunction(AppJs, "async function printLabel");
        Assert.Contains("body.rendered", printer, StringComparison.Ordinal);
        Assert.Contains("html-encoded", printer, StringComparison.Ordinal);
        Assert.Contains("LabelRenderService", printer, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- html

    [Fact]
    public void IndexHtml_LoadsRuntimeConfigBeforeAppJs()
    {
        var config = IndexHtml.IndexOf("runtime-config.js", StringComparison.Ordinal);
        var app = IndexHtml.IndexOf("app.js", StringComparison.Ordinal);

        Assert.True(config >= 0, "index.html never loads runtime-config.js, so window.__MINI_ERP_CONFIG__ is undefined at boot");
        Assert.True(app >= 0, "index.html does not load app.js");
        Assert.True(config < app, "runtime-config.js must be loaded before app.js");
    }

    [Fact]
    public void IndexHtml_ReferencesNoExternalAsset()
    {
        var external = Regex.Matches(IndexHtml, @"(?:src|href)\s*=\s*""(https?:)?//[^""]+""")
            .Select(m => m.Value)
            .ToArray();
        Assert.Empty(external);
    }

    [Fact]
    public void IndexHtml_ReferencesNoWebFont()
    {
        Assert.DoesNotContain("fonts.googleapis.com", IndexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.gstatic.com", IndexHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexHtml_OffersAUsernameAndPasswordForm()
    {
        Assert.Contains("id=\"login-username\"", IndexHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"login-password\"", IndexHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"login-form\"", IndexHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"logout-btn\"", IndexHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Styles_ActuallyHonourTheHiddenAttribute()
    {
        // .app-layout / .login-view set an explicit display, which beats the UA
        // [hidden] rule, so the two views would render on top of each other.
        var css = Read("dashboard", "styles.css");
        Assert.Contains("[hidden]", css, StringComparison.Ordinal);
        Assert.Matches(@"\[hidden\]\s*\{[^}]*display\s*:\s*none", css);
    }

    [Fact]
    public void IndexHtml_HidesTheDashboardUntilSignedIn()
    {
        Assert.Contains("id=\"app-shell\"", IndexHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"login-view\"", IndexHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexHtml_ShipsNoSeededDemoRow()
    {
        Assert.DoesNotContain("PO001", IndexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("MAT_RUBBER_01", IndexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("540", IndexHtml, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- nginx

    [Fact]
    public void NginxConf_ExistsAndProxiesTheApiSameOrigin()
    {
        Assert.Contains("location /api/", NginxConf, StringComparison.Ordinal);
        Assert.Contains("proxy_pass", NginxConf, StringComparison.Ordinal);
        Assert.Contains("api:5000", NginxConf, StringComparison.Ordinal);
    }

    [Fact]
    public void NginxConf_NeverCachesRuntimeConfig()
    {
        var block = SliceBlock(NginxConf, "location = /runtime-config.js");
        Assert.Contains("no-store", block, StringComparison.Ordinal);
        Assert.Contains("no-cache", block, StringComparison.Ordinal);
    }

    [Fact]
    public void NginxConf_KeepsEveryWritePathOutOfTheImageFilesystem()
    {
        foreach (var path in new[] { "client_body_temp_path", "proxy_temp_path", "fastcgi_temp_path",
                                     "uwsgi_temp_path", "scgi_temp_path" })
        {
            Assert.Contains(path, NginxConf, StringComparison.Ordinal);
            Assert.Matches(Regex.Escape(path) + @"[^;]*\/tmp", NginxConf);
        }
    }

    [Fact]
    public void NginxConf_ServesTheSpaAndTheStaticAssets()
    {
        Assert.Contains("try_files", NginxConf, StringComparison.Ordinal);
        Assert.Contains("root ", NginxConf, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerignore_KeepsTheProxyConfigInTheBuildContext()
    {
        var ignore = Read("dashboard", ".dockerignore");
        Assert.False(ignore.Split('\n').Any(l => l.Trim() == "nginx.conf"),
            "nginx.conf is excluded from the build context, so the image would ship the stock config");
    }

    [Fact]
    public void Dockerfile_InstallsTheProxyConfigAndDropsPrivileges()
    {
        var dockerfile = Read("dashboard", "Dockerfile");

        Assert.Contains("COPY nginx.conf /etc/nginx/nginx.conf", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER nginx", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^USER\s+root", dockerfile);
    }

    [Fact]
    public void Dockerfile_CopiesTheRuntimeAssetsAppJsNeeds()
    {
        var dockerfile = Read("dashboard", "Dockerfile");
        foreach (var asset in new[] { "index.html", "app.js", "styles.css", "runtime-config.js" })
        {
            Assert.Contains("/usr/share/nginx/html/", dockerfile, StringComparison.Ordinal);
            Assert.True(dockerfile.Contains(asset, StringComparison.Ordinal),
                "the image never copies " + asset + " into the webroot");
        }
    }

    // ------------------------------------------------------------- helpers

    private static string Read(params string[] parts) =>
        File.ReadAllText(TestRepoPaths.Resolve(parts));

    private static string SliceFunction(string source, string anchor)
    {
        var start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js has no '" + anchor + "'");

        var next = source.IndexOf("\nfunction ", start, StringComparison.Ordinal);
        var nextAsync = source.IndexOf("\nasync function ", start, StringComparison.Ordinal);
        if (next >= 0 && nextAsync >= 0) next = Math.Min(next, nextAsync);
        if (next < 0) next = source.Length;

        return source[start..next];
    }

    private static string SliceBlock(string source, string anchor)
    {
        var start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, "nginx.conf has no '" + anchor + "' block");

        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private static string NormaliseDynamic(string literal)
    {
        var cut = literal.IndexOf('+');
        var head = cut < 0 ? literal : literal[..cut];
        var normalised = Regex.Replace(head, @"\$\{[^}]*\}", "*");
        if (cut >= 0) return normalised + "*";
        // "'/api/stock/' + code" is captured without its dynamic tail.
        return normalised.EndsWith('/') ? normalised + "*" : normalised;
    }

    private static bool RouteMatches(string template, string normalisedLiteral)
    {
        var t = template.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var l = normalisedLiteral.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // "'/api/stock/' + code" is a static prefix the host extends with a path
        // parameter, so the literal may be shorter than the route template.
        if (l.Length == 0 || t.Length == 0) return false;
        if (l[^1] == "*") l = l[..^1];
        if (l.Length > t.Length) return false;

        for (var i = 0; i < l.Length; i++)
        {
            var star = l[i].IndexOf('*');
            if (star < 0)
            {
                if (!string.Equals(t[i], l[i], StringComparison.Ordinal)) return false;
                continue;
            }

            var prefix = l[i][..star];
            if (prefix.Length == 0)
            {
                if (!t[i].StartsWith('{')) return false;
            }
            else if (!t[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
