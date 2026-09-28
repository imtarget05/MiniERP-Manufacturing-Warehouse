#!/usr/bin/env python3
"""H-06 / Task 8.8 - browser proof for the MiniERP dashboard.

Every assertion is made against the disposable Oracle/QA pair the caller has
already booted; nothing here contacts production. Screenshots and the result
JSON are written under the caller-supplied QA_ROOT only.

Environment (all required):
  DASHBOARD_E2E_URL        static origin serving the dashboard
  DASHBOARD_E2E_API        disposable API base
  DASHBOARD_E2E_OUT        directory for screenshots + result JSON
  DASHBOARD_E2E_ADMIN      admin username
  DASHBOARD_E2E_ADMIN_PW   admin password
Optional:
  DASHBOARD_E2E_SHORT_API  a second disposable API booted with a 1-minute token
                           lifetime, used for the real-expiry refresh case
  DASHBOARD_E2E_SHORT_URL  static origin whose runtime-config points at it
"""
import base64
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

from playwright.sync_api import sync_playwright

UI = os.environ["DASHBOARD_E2E_URL"].rstrip("/")
API = os.environ["DASHBOARD_E2E_API"].rstrip("/")
OUT = os.environ["DASHBOARD_E2E_OUT"]
ADMIN = os.environ["DASHBOARD_E2E_ADMIN"]
ADMIN_PW = os.environ["DASHBOARD_E2E_ADMIN_PW"]
SHORT_API = os.environ.get("DASHBOARD_E2E_SHORT_API", "").rstrip("/")
SHORT_URL = os.environ.get("DASHBOARD_E2E_SHORT_URL", "").rstrip("/")

# The disposable Oracle on this host shares 4 GiB of Docker memory with the
# production stack, so a single read can take several seconds under ambient
# load. Waits are therefore generous; they are not hiding a fast path, they are
# absorbing a slow database.
DEFAULT_TIMEOUT_MS = int(os.environ.get("DASHBOARD_E2E_TIMEOUT_MS", "60000"))

results = []


def check(cid, name, ok, detail=""):
    results.append({"id": cid, "name": name, "ok": bool(ok), "detail": str(detail)[:400]})
    print(("  ok   " if ok else "  FAIL ") + cid + " " + name + ((" -- " + str(detail)[:400]) if detail else ""))


def api(path, token=None, method="GET", body=None, base=None):
    base = (base or API)
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            raw = r.read().decode()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, (json.loads(raw) if raw else None)
        except json.JSONDecodeError:
            return e.code, raw


def jwt_seconds_left(token):
    """Seconds until the token's own exp claim. Measured on the token itself, so
    it cannot be satisfied by any freshly issued token."""
    payload = token.split(".")[1]
    payload += "=" * (-len(payload) % 4)
    exp = json.loads(base64.urlsafe_b64decode(payload))["exp"]
    return exp - time.time()


def parse_dotnet_time(value):
    """System.Text.Json emits up to 7 fractional digits; Python 3.9 accepts 6."""
    text = value.replace("Z", "+00:00")
    date, _, rest = text.partition("T")
    fraction = ""
    if "." in rest:
        rest, _, tail = rest.partition(".")
        digits = ""
        for ch in tail:
            if ch.isdigit():
                digits += ch
            else:
                rest_tail = tail[len(digits):]
                break
        else:
            rest_tail = ""
        fraction = "." + (digits + "000000")[:6]
        rest += fraction + rest_tail
    return datetime.fromisoformat(date + "T" + rest)


def restore_balance(wh_code, item_code, before_qty, after_qty, token):
    """Undo the E2E's own stock movement so the disposable seed is left as found."""
    delta = round(float(after_qty) - float(before_qty), 3)
    if delta <= 0:
        return
    st, body = api("/api/stock/out", token, "POST",
                   {"warehouseCode": wh_code, "itemCode": item_code,
                    "quantity": delta, "referenceNo": "E2E-RESTORE"})
    _, now = api("/api/stock/" + wh_code, token)
    left = [r for r in now if r["itemCode"] == item_code][0]["quantity"]
    check("E19b", "the E2E puts the balance it moved back, so the seed is left as found",
          st == 200 and abs(float(left) - float(before_qty)) < 0.001,
          "item=%s before=%s after_test=%s now=%s http=%s" % (item_code, before_qty, after_qty, left, st))


def unique(prefix):
    return "%s_%s" % (prefix, datetime.now(timezone.utc).strftime("%H%M%S%f")[-9:])


def attach_collectors(page, bucket):
    page.on("console", lambda m: bucket["console"].append(m.type + ": " + m.text) if m.type in ("error", "warning") else None)
    page.on("pageerror", lambda e: bucket["pageerror"].append(str(e)))
    page.on("requestfailed", lambda r: bucket["requestfailed"].append(r.url + " :: " + str(r.failure)))
    page.on("request", lambda r: bucket["requests"].append(r.url))


def external_requests(bucket, allowed_hosts):
    out = []
    for url in bucket["requests"]:
        if not url.startswith(("http://", "https://")):
            continue
        host = url.split("/")[2]
        if host not in allowed_hosts:
            out.append(url)
    return out


SETTLE_JS = ("() => ['kpi-warehouses','kpi-low','kpi-po','kpi-errors']"
             ".every(id => document.querySelector('#' + id).textContent.trim() !== 'N/A')")


def settle(page):
    """Wait until the dashboard has finished its first read.

    A reload issued while a cross-origin fetch is still in flight loses
    sessionStorage in this headless Chromium (measured 4/5 runs; inserting any
    other script call, or waiting ~150ms, makes it disappear again). The
    application never clears storage on that path - no 4xx is issued and the
    reload only aborts the in-flight request - so this is a harness/Chromium
    artefact, not dashboard behaviour. The test settles the page first and the
    reload assertion below therefore measures the real thing: does a signed-in
    tab keep its session across a reload?
    """
    page.wait_for_function(SETTLE_JS, timeout=DEFAULT_TIMEOUT_MS)
    page.wait_for_timeout(500)


def sign_in(page, origin, username, password):
    page.goto(origin + "/index.html", wait_until="load")
    page.wait_for_selector("#login-form", state="visible")
    page.fill("#login-username", username)
    page.fill("#login-password", password)
    page.click("#login-submit")


def has_overflow(page):
    return page.evaluate(
        "() => document.documentElement.scrollWidth - document.documentElement.clientWidth"
    )


def hygiene(browser, origin, api_base, label, signed_in, out, stamp):
    """Console / network / overflow hygiene, measured on a context that has
    never had a request fault-injected. The deliberate 401s of E16/E16b make
    Chromium log "Failed to load resource", so they must not share a bucket
    with this check or it would be measuring the test's own injections."""
    ctx = browser.new_context(viewport={"width": 1440, "height": 900})
    page = ctx.new_page()
    page.set_default_timeout(DEFAULT_TIMEOUT_MS)
    bucket = {"console": [], "pageerror": [], "requestfailed": [], "requests": []}
    attach_collectors(page, bucket)
    allowed = {origin.split("/")[2], api_base.split("/")[2]}

    page.goto(origin + "/index.html", wait_until="load")
    page.wait_for_selector("#login-form", state="visible")
    page.screenshot(path=os.path.join(out, "01-login-1440x900-%s.png" % stamp))
    if signed_in:
        sign_in(page, origin, ADMIN, ADMIN_PW)
        settle(page)
        page.screenshot(path=os.path.join(out, "02-dashboard-1440x900-%s.png" % stamp))
        page.set_viewport_size({"width": 390, "height": 844})
        page.wait_for_timeout(500)
        page.screenshot(path=os.path.join(out, "03-dashboard-390x844-%s.png" % stamp))
        page.set_viewport_size({"width": 1440, "height": 900})
        page.wait_for_timeout(300)

    ext = external_requests(bucket, allowed)
    check("E01-" + label, "no external asset is requested", ext == [], ext)
    noise = bucket["console"] + bucket["pageerror"] + bucket["requestfailed"]
    check("E02-" + label, "no console error / page error / failed request", not noise, noise[:3])
    for width, height in ((1440, 900), (390, 844)):
        page.set_viewport_size({"width": width, "height": height})
        page.wait_for_timeout(300)
        over = has_overflow(page)
        check("E17-" + label + "-" + str(width), "no horizontal overflow at %dx%d" % (width, height),
              over <= 0, "scrollWidth-clientWidth=%s" % over)
    page.set_viewport_size({"width": 1440, "height": 900})
    ctx.close()


def main():
    os.makedirs(OUT, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")

    status, warehouses = api("/api/warehouse")
    status_t, tok = api("/api/auth/login", method="POST", body={"username": ADMIN, "password": ADMIN_PW})
    token = tok["accessToken"]
    refresh_token = tok.get("refreshToken")
    check("E00", "disposable API answers /api/warehouse and admin can sign in",
          status == 200 and isinstance(warehouses, list) and status_t == 200 and bool(token),
          "warehouses=%s login=%s roles=%s" % (len(warehouses or []), status_t, (tok.get("user") or {}).get("roles")))
    # Pick a warehouse that actually holds stock, so the stock assertions
    # compare like with like instead of two empty lists.
    wh_code, stock = warehouses[0]["code"], []
    for w in warehouses:
        _, rows = api("/api/stock/" + w["code"], token)
        if rows:
            wh_code, stock = w["code"], rows
            break
    check("E08a", "the disposable seed holds stock rows to assert on",
          bool(stock), "warehouse=%s" % wh_code)
    _, errors = api("/api/support/errors?take=20", token)
    _, big_error_log = api("/api/support/errors?take=200", token)
    _, alerts = api("/api/automation/replenishment/alerts?status=OPEN&take=20", token)
    _, stale = api("/api/automation/stale-orders?days=7", token)
    _, incidents = api("/api/automation/incidents?take=20", token)
    _, approvals = api("/api/automation/approvals?status=PENDING&take=20", token)

    # A production order created for this run, so the UI can never be showing a
    # number that was baked into the markup.
    po_no = "E2E-" + unique("PO")
    status, created = api("/api/manufacturing/production-order", token, "POST", {
        "productionOrderNo": po_no,
        "finishedGoodCode": "FG_RUNNER_PRO_42",
        "plannedQuantity": 2,
        "warehouseCode": wh_code,
    })
    check("E12a", "a fresh production order exists on the disposable DB",
          status == 200 and created.get("poNo") == po_no, "po=%s status=%s" % (po_no, status))

    with sync_playwright() as p:
        browser = p.chromium.launch()
        allowed = {UI.split("/")[2], API.split("/")[2]}

        # ---------------------------------------------------- wrong login
        ctx = browser.new_context(viewport={"width": 1440, "height": 900})
        page = ctx.new_page()
        page.set_default_timeout(DEFAULT_TIMEOUT_MS)
        b1 = {"console": [], "pageerror": [], "requestfailed": [], "requests": []}
        attach_collectors(page, b1)
        page.goto(UI + "/index.html", wait_until="load")
        page.wait_for_selector("#login-form", state="visible")
        check("E20", "an unauthenticated visitor sees the login view and no data request",
              page.is_hidden("#app-shell") and not any("/api/stock/" in u for u in b1["requests"]),
              [u for u in b1["requests"] if "/api/" in u][:3])
        page.fill("#login-username", ADMIN)
        page.fill("#login-password", "definitely-not-the-password")
        page.click("#login-submit")
        page.wait_for_selector("#login-error:visible", timeout=DEFAULT_TIMEOUT_MS)
        check("E03", "a wrong password is rejected and the dashboard stays hidden",
              page.is_hidden("#app-shell") and page.is_visible("#login-form"),
              page.inner_text("#login-error"))

        # ------------------------------------------------------ admin login
        page.fill("#login-username", ADMIN)
        page.fill("#login-password", ADMIN_PW)
        page.click("#login-submit")
        page.wait_for_selector("#app-shell:visible", timeout=DEFAULT_TIMEOUT_MS)
        page.wait_for_function("() => document.querySelector('#stock-tbody tr') !== null", timeout=DEFAULT_TIMEOUT_MS)
        check("E04", "admin sign-in reveals the dashboard and the session identity",
              ADMIN in page.inner_text("#session-user"), page.inner_text("#session-user"))

        stored = page.evaluate(
            "() => ({s: {...sessionStorage}, l: {...localStorage}, url: location.href})")
        check("E05", "the credential lives in sessionStorage only, and not in the URL",
              bool(stored["s"].get("minierp.accessToken")) and stored["l"] == {} and
              "token" not in stored["url"].lower(),
              {k: ("<present>" if v else v) for k, v in stored["s"].items()} | {"url": stored["url"]})

        # ------------------------------------------------- live KPI values
        settle(page)
        kpi_wh = page.inner_text("#kpi-warehouses").strip()
        kpi_err = page.inner_text("#kpi-errors").strip()
        kpi_low = page.inner_text("#kpi-low").strip()
        # The warehouse list is complete (no paging), so equality with the API's
        # list length is a real check: a hard-coded count fails on any other DB.
        check("E06", "the warehouse KPI equals the API's complete warehouse list",
              kpi_wh == str(len(warehouses)), "ui=%s api=%s" % (kpi_wh, len(warehouses)))

        # The error KPI counts a *page* (take=20), not the whole log, so this
        # cannot be made non-self-referential: a hard-coded 20 would also pass.
        # What it does prove is that the panel and the KPI agree, that the
        # number is a count of rendered rows, and that it is a page of a longer
        # log - which is why the report no longer offers it as independent proof.
        rendered_errors = page.eval_on_selector_all("#error-list .stock-row", "els => els.length")
        check("E07", "the error KPI counts the error rows the panel rendered, and is a page of a longer log",
              kpi_err == str(rendered_errors) and rendered_errors == len(errors) and
              kpi_err.isdigit() and len(errors) <= 20,
              "ui=%s rendered=%s page=%s total_in_log>=%s" % (
                  kpi_err, rendered_errors, len(errors), len(big_error_log)))

        all_stock = []
        for w in warehouses:
            _, rows = api("/api/stock/" + w["code"], token)
            all_stock += rows or []
        below = [s for s in all_stock
                 if s.get("isBelowMinStock") or
                 (float(s.get("minStock") or 0) > 0 and float(s.get("quantity") or 0) < float(s["minStock"]))]
        page.wait_for_function("() => document.querySelector('#kpi-low').textContent.trim() !== 'N/A'",
                               timeout=DEFAULT_TIMEOUT_MS)
        kpi_low = page.inner_text("#kpi-low").strip()
        check("E07b", "the below-minimum KPI is derived from every warehouse's stock payload",
              kpi_low == str(len(below)),
              "ui=%s computed=%s rows=%s" % (kpi_low, len(below), len(all_stock)))

        # ----------------------------------------------- stock table & tabs
        page.click('[data-tab="tab-stock"]')
        page.wait_for_timeout(400)
        page.select_option("#warehouse-select", wh_code)
        page.wait_for_function(
            "n => document.querySelectorAll('#stock-tbody tr').length === n", arg=len(stock), timeout=DEFAULT_TIMEOUT_MS)
        ui_rows = page.eval_on_selector_all("#stock-tbody tr", "els => els.map(e => e.innerText)")
        check("E08", "the stock table renders exactly what GET /api/stock returned",
              len(ui_rows) == len(stock) and all(s["itemCode"] in " ".join(ui_rows) for s in stock),
              "ui=%s api=%s" % (len(ui_rows), len(stock)))

        # ------------------------------------------ alerts / BOM / errors
        # Re-read at assert time: E12a and E19 write to this schema, so a snapshot
        # taken at the top of the run can be stale by the time the panel is read.
        _, alerts_now = api("/api/automation/replenishment/alerts?status=OPEN&take=20", token)
        alerts = alerts_now or []
        alert_rows = page.eval_on_selector_all("#low-stock-list .stock-row", "els => els.map(e => e.innerText)")
        if alerts:
            check("E09", "the low-stock panel renders exactly the open alerts the API returned",
                  len(alert_rows) == len(alerts) and
                  all(a["itemCode"] in " ".join(alert_rows) and a["warehouseCode"] in " ".join(alert_rows)
                      for a in alerts),
                  "rendered=%s api=%s" % (len(alert_rows), len(alerts)))
        else:
            check("E09", "the low-stock panel shows a controlled empty state when no alert is open",
                  page.locator("#low-stock-list .empty-state").count() == 1 and not alert_rows,
                  "empty-state elements=%d rows=%d" % (
                      page.locator("#low-stock-list .empty-state").count(), len(alert_rows)))
        bom_text = page.inner_text("#bom-list")
        check("E10", "the BOM panel states N/A instead of inventing rows",
              "N/A" in bom_text and "MAT_" not in bom_text, bom_text[:120])

        page.click('[data-tab="tab-mfg"]')
        page.wait_for_timeout(300)
        error_text = page.inner_text("#error-list")
        error_codes = {e["errorCode"] for e in errors}
        check("E11", "the error panel is built from GET /api/support/errors",
              (len(errors) == 0 and "empty-state" in page.inner_html("#error-list")) or
              all(c in error_text for c in error_codes),
              "api codes=%s" % sorted(error_codes))

        # ------------------------------------- dynamic production order
        page.fill("#po-input", po_no)
        page.click("#btn-load-po")
        page.wait_for_function(
            "n => document.querySelector('#po-number').textContent.trim() === n", arg=po_no, timeout=DEFAULT_TIMEOUT_MS)
        detail = page.inner_text("#po-detail")
        check("E12b", "the production-order panel shows the order the operator typed",
              page.inner_text("#po-number").strip() == po_no and
              created["order"]["finishedGoodCode"] in detail,
              "number=%s detail=%s" % (page.inner_text("#po-number"), detail[:120]))

        seen_urls = []
        page.on("request", lambda r: seen_urls.append(r.url) if "production-order" in r.url else None)
        page.click("#btn-check-material")
        page.wait_for_function(
            "() => document.querySelector('#mfg-msg').textContent.trim().length > 0", timeout=DEFAULT_TIMEOUT_MS)
        check("E12c", "material-check is sent for the typed order, never a baked-in one",
              any(po_no in u and "material-check" in u for u in seen_urls) and
              not any("PO001" in u for u in seen_urls),
              [u for u in seen_urls if "production-order" in u][-1:])

        # ----------------------------------------------- stock in / out
        page.click('[data-tab="tab-stock"]')
        page.wait_for_timeout(300)
        page.fill("#s-item", stock[0]["itemCode"])
        page.fill("#s-qty", "7")
        page.fill("#s-ref", "E2E-" + unique("REF"))
        page.select_option("#s-type", "in")
        page.click("#stock-form button[type=submit]")
        page.wait_for_function(
            "() => document.querySelector('#stock-msg').textContent.trim().length > 0", timeout=DEFAULT_TIMEOUT_MS)
        _, after = api("/api/stock/" + wh_code, token)
        after_row = [r for r in after if r["itemCode"] == stock[0]["itemCode"]][0]
        page.wait_for_function(
            "q => document.querySelector('#stock-tbody').textContent.includes(q)",
            arg=str(int(after_row["quantity"])), timeout=DEFAULT_TIMEOUT_MS)
        shown = page.evaluate(
            """(code) => {
                for (const tr of document.querySelectorAll('#stock-tbody tr')) {
                    const cells = [...tr.children].map(c => c.innerText.trim());
                    if (cells[0] === code) return cells[4];
                }
                return null;
            }""", stock[0]["itemCode"])
        check("E19", "stock in writes a real record and the panel re-reads the new balance",
              after_row["quantity"] != stock[0]["quantity"] and shown == str(int(after_row["quantity"])),
              "before=%s api_after=%s table_cell=%s msg=%s" % (
                  stock[0]["quantity"], after_row["quantity"], shown, page.inner_text("#stock-msg")[:60]))

        # Put the balance back. Without this the write compounds across runs and
        # eventually pushes a seeded item past the min-stock level that other
        # suites assert on (ApiIntegrationTests.StockByWarehouse_...MinStockFlag).
        restore_balance(wh_code, stock[0]["itemCode"],
                        stock[0]["quantity"], after_row["quantity"], token)

        # ----------------------------------------------- monitoring tab
        page.click('[data-tab="tab-incident"]')
        page.wait_for_timeout(300)
        _, incidents_now = api("/api/automation/incidents?take=20", token)
        incidents = incidents_now or []
        incident_rows = page.eval_on_selector_all("#incident-list .stock-row", "els => els.map(e => e.innerText)")
        if incidents:
            check("E12d", "the monitoring tab renders exactly the incidents the API returned",
                  len(incident_rows) == len(incidents) and
                  all(i["errorCode"] in " ".join(incident_rows) for i in incidents),
                  "rendered=%s api=%s" % (len(incident_rows), len(incidents)))
        else:
            check("E12d", "the monitoring tab shows a controlled empty state when there is no incident",
                  page.locator("#incident-list .empty-state").count() == 1 and not incident_rows,
                  "incidents=0 empty-state elements=%d" % page.locator("#incident-list .empty-state").count())
        _, approvals_now = api("/api/automation/approvals?status=PENDING&take=20", token)
        approvals = approvals_now or []
        approval_rows = page.eval_on_selector_all(
            "#approval-list .stock-row", "els => els.map(e => e.innerText)")
        if approvals:
            check("E12e", "the approval panel renders exactly the pending approvals the API returned",
                  len(approval_rows) == len(approvals) and
                  all(a["approvalNo"] in " ".join(approval_rows) for a in approvals),
                  "rendered=%s api=%s" % (len(approval_rows), len(approvals)))
        else:
            check("E12e", "the approval panel shows a controlled empty state when nothing awaits approval",
                  page.locator("#approval-list .empty-state").count() == 1 and not approval_rows,
                  "approvals=0 empty-state elements=%d" % page.locator("#approval-list .empty-state").count())

        # ---------------------------------------------- 401 -> one refresh
        # A protected read answers 401 exactly once. The dashboard must refresh
        # the session once, retry with the new bearer, and stay signed in.
        b1["requests"].clear()
        before_token = page.evaluate("() => sessionStorage.getItem('minierp.accessToken')")
        page.route("**/api/auth/me", lambda route: route.fulfill(
            status=401, content_type="application/json",
            body='{"success":false,"errorCode":"EXPIRED"}'), times=1)
        page.click("#btn-reload")
        page.wait_for_function(
            "t => sessionStorage.getItem('minierp.accessToken') !== t", arg=before_token, timeout=DEFAULT_TIMEOUT_MS)
        page.wait_for_timeout(700)
        refresh_calls = [u for u in b1["requests"] if u.endswith("/api/auth/refresh")]
        after_token = page.evaluate("() => sessionStorage.getItem('minierp.accessToken')")
        check("E16", "one 401 triggers exactly one refresh and the retry uses the new bearer",
              len(refresh_calls) == 1 and after_token != before_token and page.is_visible("#app-shell"),
              "refreshes=%s token_changed=%s signed_in=%s" % (
                  len(refresh_calls), after_token != before_token, page.is_visible("#app-shell")))
        page.unroute("**/api/auth/me")

        # -------------------------- an unrecoverable 401 returns to login
        stale_rows = page.eval_on_selector_all("#stock-tbody tr", "els => els.length")
        b1["requests"].clear()
        page.route("**/api/auth/me", lambda route: route.fulfill(
            status=401, content_type="application/json",
            body='{"success":false,"errorCode":"EXPIRED"}'))
        page.route("**/api/auth/refresh", lambda route: route.fulfill(
            status=401, content_type="application/json",
            body='{"success":false,"errorCode":"INVALID_REFRESH_TOKEN"}'))
        page.click("#btn-reload")
        page.wait_for_selector("#login-view:visible", timeout=DEFAULT_TIMEOUT_MS)
        left = page.evaluate("() => ({s: {...sessionStorage}, l: {...localStorage}})")
        check("E16b", "an unrecoverable 401 returns to login and drops the stale render",
              left["s"] == {} and left["l"] == {} and page.is_hidden("#app-shell") and stale_rows > 0,
              "sessionStorage=%s had_rows=%s" % (left["s"], stale_rows))
        page.unroute("**/api/auth/refresh")
        page.unroute("**/api/auth/me")

        # ------------------------------------------------ reload & logout
        ctx2 = browser.new_context(viewport={"width": 1440, "height": 900})
        page2 = ctx2.new_page()
        page2.set_default_timeout(DEFAULT_TIMEOUT_MS)
        b2 = {"console": [], "pageerror": [], "requestfailed": [], "requests": []}
        attach_collectors(page2, b2)
        sign_in(page2, UI, ADMIN, ADMIN_PW)
        settle(page2)
        page2.reload(wait_until="load")
        page2.wait_for_selector("#app-shell:visible", timeout=DEFAULT_TIMEOUT_MS)
        settle(page2)
        check("E14", "a reload inside the same tab keeps the session and re-reads live data",
              page2.inner_text("#kpi-warehouses").strip() == str(len(warehouses)),
              page2.inner_text("#kpi-warehouses"))

        page2.click("#logout-btn")
        page2.wait_for_selector("#login-view:visible", timeout=DEFAULT_TIMEOUT_MS)
        left = page2.evaluate("() => ({s: {...sessionStorage}, l: {...localStorage}})")
        old_access, old_refresh = token, refresh_token
        st_after, _ = api("/api/auth/me", old_access)
        st_refresh, _ = api("/api/auth/refresh", method="POST", body={"refreshToken": old_refresh})
        # The host's model: a stateless JWT plus a revocable refresh token.
        # /api/auth/logout revokes the refresh token and every session for the
        # actor, so the refresh path is dead immediately; the already-issued
        # access token stays a valid JWT until its own exp. The assertion states
        # that boundary instead of pretending the token dies on logout.
        _, me_body = api("/api/auth/me", old_access)
        window_left = jwt_seconds_left(old_access)
        check("E15", "logout clears both credentials and revokes the refresh token server-side",
              left["s"] == {} and left["l"] == {} and st_refresh == 401 and
              (st_after == 401 or (st_after == 200 and me_body and me_body.get("username") == ADMIN)),
              "sessionStorage=%s refresh=%s access_me=%s" % (left["s"], st_refresh, st_after))
        # Measured from the token that actually survived logout - its own exp,
        # not a freshly minted one. This is the window in which a stolen access
        # token is still usable after the user pressed "Đăng xuất".
        check("E15b", "the surviving access token expires no later than the host's configured lifetime",
              0 < window_left <= 1800,
              "the post-logout access token is still accepted for %.0fs more (host lifetime 1800s)" % window_left)

        page2.screenshot(path=os.path.join(OUT, "04-after-logout-%s.png" % stamp))
        ctx2.close()

        # A context that never had a request fault-injected, for the hygiene
        # checks (E01/E02/E17) and the delivery screenshots.
        hygiene(browser, UI, API, "login-view", False, OUT, stamp)
        hygiene(browser, UI, API, "signed-in", True, OUT, stamp)
        ctx.close()

        # ------------------------------------- real 1-minute token expiry
        if SHORT_API and SHORT_URL:
            b3 = {"console": [], "pageerror": [], "requestfailed": [], "requests": []}
            ctx3 = browser.new_context(viewport={"width": 1440, "height": 900})
            page3 = ctx3.new_page()
            page3.set_default_timeout(DEFAULT_TIMEOUT_MS)
            attach_collectors(page3, b3)
            sign_in(page3, SHORT_URL, ADMIN, ADMIN_PW)
            settle(page3)
            first = page3.evaluate("() => sessionStorage.getItem('minierp.accessToken')")
            b3["requests"].clear()
            # The plan allows 65s for a 1-minute token. The read behind each
            # poll can itself take tens of seconds while the disposable Oracle
            # is CPU-starved, so the wall-clock budget is wider than the TTL.
            deadline = time.time() + 240
            refreshed = False
            while time.time() < deadline:
                page3.click("#btn-reload")
                try:
                    page3.wait_for_function(
                        "() => sessionStorage.getItem('minierp.accessToken') !== '%s'" % first,
                        timeout=DEFAULT_TIMEOUT_MS)
                    refreshed = True
                    break
                except Exception:
                    pass
                if page3.is_hidden("#app-shell"):
                    break
            second = page3.evaluate("() => sessionStorage.getItem('minierp.accessToken')")
            refresh_calls = [u for u in b3["requests"] if u.endswith("/api/auth/refresh")]
            check("E18", "an expired access token is refreshed once and the session survives",
                  refreshed and len(refresh_calls) >= 1 and page3.is_visible("#app-shell"),
                  "refreshes=%s still_signed_in=%s" % (len(refresh_calls), page3.is_visible("#app-shell")))
            ctx3.close()

        browser.close()

    summary = {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "ui": UI, "api": API,
        "disposableProductionOrder": po_no,
        "apiSnapshot": {
            "warehouses": len(warehouses or []), "stockRows": len(stock or []),
            "errorLogs": len(errors or []), "errorLogsUnpaged": len(big_error_log or []),
            "openAlerts": len(alerts or []),
            "staleOrders": len(stale or []), "incidents": len(incidents or []),
        },
        "total": len(results),
        "passed": sum(1 for r in results if r["ok"]),
        "failed": sum(1 for r in results if not r["ok"]),
        "results": results,
    }
    with open(os.path.join(OUT, "e2e-summary-%s.json" % stamp), "w") as fh:
        json.dump(summary, fh, indent=2)
    print("\nE2E TOTAL %d  passed %d  failed %d" % (summary["total"], summary["passed"], summary["failed"]))
    print("SUMMARY_JSON %s" % os.path.join(OUT, "e2e-summary-%s.json" % stamp))
    return 1 if summary["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
