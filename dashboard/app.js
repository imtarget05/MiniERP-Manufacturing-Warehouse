'use strict';
/* MiniERP Dashboard — a client of the real API.
 *
 * Rules this file obeys, and why the tests exist:
 *   * No seeded demo rows and no invented KPI. Every number on screen comes
 *     from a response body, or the panel says N/A / empty state.
 *   * No MOCK fallback. If the API is unreachable the dashboard says so.
 *   * Exactly one credential store: sessionStorage, two keys, cleared on
 *     logout. Never a persistent browser store, never the URL.
 *   * The API origin comes from window.__MINI_ERP_CONFIG__ (runtime-config.js),
 *     never from a literal in this file.
 */
const $ = (id) => document.getElementById(id);
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const ACCESS_KEY = 'minierp.accessToken';
const REFRESH_KEY = 'minierp.refreshToken';

/* ---------------------------------------------------------------- session */

let accessToken = sessionStorage.getItem(ACCESS_KEY) || '';
let refreshToken = sessionStorage.getItem(REFRESH_KEY) || '';
let currentUser = null;
let refreshInFlight = null;
/* Bumped whenever the session ends. Any read still in flight when it changes
 * throws its result away instead of repopulating the panels behind the login
 * view. */
let readEpoch = 0;

const config = () => window.__MINI_ERP_CONFIG__ || {};
const apiBase = () => (config().apiBaseUrl || '').replace(/\/+$/, '');
const apiUrl = (path) => apiBase() + path;

function storeTokens(access, refresh) {
  accessToken = access || '';
  refreshToken = refresh || '';
  if (accessToken) sessionStorage.setItem(ACCESS_KEY, accessToken);
  else sessionStorage.removeItem(ACCESS_KEY);
  if (refreshToken) sessionStorage.setItem(REFRESH_KEY, refreshToken);
  else sessionStorage.removeItem(REFRESH_KEY);
}

function forgetTokens() {
  accessToken = '';
  refreshToken = '';
  currentUser = null;
  sessionStorage.removeItem(ACCESS_KEY);
  sessionStorage.removeItem(REFRESH_KEY);
}

function showLogin(message) {
  readEpoch += 1;
  forgetTokens();
  $('app-shell').hidden = true;
  $('login-view').hidden = false;
  const box = $('login-error');
  if (box) {
    box.textContent = message || '';
    box.hidden = !message;
  }
  $('login-username').focus();
}

function showApp(user) {
  currentUser = user;
  $('login-view').hidden = true;
  $('app-shell').hidden = false;
  const who = $('session-user');
  if (who) {
    const roles = user && user.roles && user.roles.length ? ' • ' + user.roles.join(', ') : '';
    who.textContent = user ? user.username + roles : '';
  }
}

/* --------------------------------------------------------------- request */

/* Runs a read and lets it touch the page only if the session that started it is
 * still the current one. Without this, the seven reads that loadOverview fires
 * can resolve after a 401/403 or a logout and repopulate the panels behind the
 * login view. Returns false when the result was dropped. */
async function guardedRead(read, apply) {
  const epoch = readEpoch;
  const result = await read();
  if (epoch !== readEpoch) return false;
  await apply(result);
  return true;
}

/* Single flight: a burst of parallel 401s must produce exactly one refresh. */
function refreshSession() {
  if (!refreshToken) return Promise.resolve(false);
  if (!refreshInFlight) {
    refreshInFlight = fetch(apiUrl('/api/auth/refresh'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
      .then((r) => (r.ok ? r.json() : null))
      .then((body) => {
        if (!body || !body.accessToken) return false;
        storeTokens(body.accessToken, body.refreshToken);
        return true;
      })
      .catch(() => false)
      .finally(() => { refreshInFlight = null; });
  }
  return refreshInFlight;
}

async function apiFetch(path, options = {}) {
  const send = (bearer) => fetch(apiUrl(path), {
    ...options,
    headers: {
      ...(options.headers || {}),
      ...(bearer ? { Authorization: 'Bearer ' + bearer } : {}),
    },
  });

  let response = await send(accessToken);

  if (response.status === 401 && accessToken) {
    if (await refreshSession()) response = await send(accessToken);
  }

  if (response.status === 401 || response.status === 403) {
    showLogin(response.status === 403
      ? 'Tài khoản không có quyền cho thao tác này. Đã đăng xuất để bảo vệ dữ liệu.'
      : 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.');
    renderAll(true);
  }

  return response;
}

async function apiJson(path, options = {}) {
  const response = await apiFetch(path, options);
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = null; }
  return { ok: response.ok, status: response.status, body };
}

function qs(params) {
  const pairs = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => encodeURIComponent(k) + '=' + encodeURIComponent(v));
  return pairs.length ? '?' + pairs.join('&') : '';
}

/* --------------------------------------------------------------- widgets */

function toast(message) {
  const t = $('toast');
  t.textContent = message;
  t.hidden = false;
  clearTimeout(t._h);
  t._h = setTimeout(() => { t.hidden = true; }, 3500);
}

const NA = 'N/A';
const emptyRow = (cols, text) =>
  '<tr class="empty-state"><td colspan="' + cols + '">' + esc(text) + '</td></tr>';
const emptyList = (text) => '<div class="empty-state">' + esc(text) + '</div>';

const num = (v) => (v === null || v === undefined || v === '' ? NA : Number(v).toLocaleString('vi-VN'));
const when = (v) => {
  if (!v) return NA;
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? String(v) : d.toLocaleString('vi-VN');
};

function setKpi(id, value, sub) {
  const valueEl = $(id);
  const subEl = $(id + '-sub');
  if (valueEl) valueEl.textContent = value;
  if (subEl) subEl.textContent = sub;
}

function setApiStatus(state, text) {
  const dot = $('api-dot');
  const label = $('api-status');
  const pill = $('mode-pill');
  if (dot) dot.className = 'status-dot ' + state;
  if (label) label.textContent = text;
  if (pill) {
    pill.textContent = state === 'ok' ? 'LIVE' : 'OFFLINE';
    pill.classList.toggle('live', state === 'ok');
  }
}

/* ------------------------------------------------------------- read model */

/* Every read below is a real endpoint. Where the host has no read contract
 * (BOM), the panel says so instead of inventing rows. */
/* A null array means "not read yet" and renders as N/A; an empty array means
 * "the API answered and there is nothing", which is a real zero. */
const data = {
  warehouses: null,
  stock: null,
  stockByWarehouse: {},
  stockLoaded: false,
  errors: null,
  alerts: null,
  staleOrders: null,
  incidents: null,
  approvals: null,
  productionOrder: null,
};

async function loadHealth() {
  try {
    return await guardedRead(
      () => apiJson('/api/health'),
      ({ ok }) => setApiStatus(ok ? 'ok' : 'bad', ok ? 'API LIVE' : 'API LỖI'));
  } catch {
    if (!accessToken) return false;
    setApiStatus('bad', 'API OFFLINE');
    return false;
  }
}

async function loadWarehouses() {
  return guardedRead(
    () => apiJson('/api/warehouse'),
    ({ ok, body }) => {
      data.warehouses = ok && Array.isArray(body) ? body : null;
      if (!data.warehouses) return;
      renderWarehouseOptions();
      renderStock();
      renderOverview();
    });
}

function renderWarehouseOptions() {
  const select = $('warehouse-select');
  const previous = select.value;
  select.innerHTML = data.warehouses.length
    ? data.warehouses.map((w) =>
        '<option value="' + esc(w.code) + '">' + esc(w.code + ' — ' + w.name) + '</option>').join('')
    : '<option value="">(chưa có kho)</option>';
  if (data.warehouses.some((w) => w.code === previous)) select.value = previous;
}

/* Selection only: the fleet-wide read already fetched every warehouse, so
 * switching warehouses must not issue a second request for the same row set.
 *
 * Consequence, deliberately accepted: the table shows the balance that was read
 * when the overview last loaded. Another operator's movement, or one made
 * through the API directly, is not visible here until "Tải lại" (or any other
 * trigger of loadOverview) re-reads the fleet. The KPI beside it is the same
 * snapshot, so the two always agree with each other. */
function loadStock() {
  const code = $('warehouse-select').value;
  data.stock = code ? (data.stockByWarehouse[code] ?? null) : null;
  renderStock();
  renderOverview();
}

async function loadErrors() {
  return guardedRead(
    () => apiJson('/api/support/errors' + qs({ take: 20 })),
    ({ ok, body }) => {
      data.errors = ok && Array.isArray(body) ? body : null;
      renderErrors();
      renderOverview();
    });
}

async function loadAlerts() {
  return guardedRead(
    () => apiJson('/api/automation/replenishment/alerts' + qs({ status: 'OPEN', take: 20 })),
    ({ ok, body }) => {
      data.alerts = ok && Array.isArray(body) ? body : null;
      renderAlerts();
    });
}

async function loadStaleOrders() {
  return guardedRead(
    () => apiJson('/api/automation/stale-orders' + qs({ days: 7 })),
    ({ ok, body }) => {
      data.staleOrders = ok && Array.isArray(body) ? body : null;
      renderStaleOrders();
      renderOverview();
    });
}

async function loadIncidents() {
  return guardedRead(
    () => apiJson('/api/automation/incidents' + qs({ take: 20 })),
    ({ ok, body }) => {
      data.incidents = ok && Array.isArray(body) ? body : null;
      renderIncidents();
    });
}

async function loadApprovals() {
  return guardedRead(
    () => apiJson('/api/automation/approvals' + qs({ status: 'PENDING', take: 20 })),
    ({ ok, body }) => {
      data.approvals = ok && Array.isArray(body) ? body : null;
      renderApprovals();
    });
}

/* The "below minimum" KPI is a fleet-wide number, so every warehouse is read -
 * not just the one the operator happens to have selected. */
async function loadAllStock() {
  /* M8: the warehouse list itself may be unreadable. An unread list means no
   * stock was read either, so the KPI must stay N/A - not a count, not a crash. */
  if (!Array.isArray(data.warehouses)) {
    data.stockLoaded = false;
    loadStock();
    return true;
  }

  return guardedRead(
    () => Promise.all(data.warehouses.map((w) =>
      apiJson('/api/stock/' + encodeURIComponent(w.code))
        .then(({ ok, body }) => [w.code, ok && Array.isArray(body) ? body : null]))),
    (settled) => {
      for (const [code, rows] of settled) data.stockByWarehouse[code] = rows;
      /* Read - and an empty fleet is a real zero, not a missing value. Only a
       * warehouse list we could not read leaves the KPI unshown. */
      data.stockLoaded = data.warehouses.length > 0 &&
        settled.some(([, rows]) => Array.isArray(rows));
      loadStock();
    });
}

/* Confirms the session on every refresh cycle. Most ERP reads are anonymous on
 * the host, so this is the request that proves the bearer is still good - and
 * therefore the one that triggers a refresh when it is not. */
async function loadSession() {
  return guardedRead(
    () => apiJson('/api/auth/me'),
    ({ ok, body }) => {
      if (ok && body && body.username) {
        const roles = body.roles && body.roles.length ? ' • ' + body.roles.join(', ') : '';
        const who = $('session-user');
        if (who) who.textContent = body.username + roles;
      }
    });
}

async function loadOverview() {
  await loadHealth();
  await loadSession();
  await loadWarehouses();
  await Promise.all([loadAllStock(), loadErrors(), loadAlerts(), loadStaleOrders(),
                     loadIncidents(), loadApprovals()]);
}

/* --------------------------------------------------------------- renderers */

function renderStock(query) {
  const q = (query !== undefined ? query : ($('stock-search').value || '')).toLowerCase();
  const rows = Array.isArray(data.stock) ? data.stock : null;
  const filtered = rows
    ? rows.filter((s) => !q || String(s.itemCode + ' ' + s.itemName).toLowerCase().includes(q))
    : [];

  $('stock-tbody').innerHTML = filtered.length
    ? filtered.map((s) => {
        const low = s.isBelowMinStock === true ||
          (Number(s.minStock) > 0 && Number(s.quantity) < Number(s.minStock));
        return '<tr><td><strong>' + esc(s.itemCode) + '</strong></td>' +
          '<td>' + esc(s.itemName) + '</td><td>' + esc(s.itemType) + '</td>' +
          '<td>' + esc(s.uom) + '</td><td><strong>' + esc(num(s.quantity)) + '</strong></td>' +
          '<td>' + esc(num(s.minStock)) + '</td>' +
          '<td><span class="badge ' + (low ? 'badge-red' : 'badge-green') + '">' +
          (low ? 'Dưới min' : 'Đạt') + '</span></td></tr>';
      }).join('')
    : emptyRow(7, rows === null ? NA + ' — chưa đọc được tồn kho của kho này.'
        : (rows.length ? 'Không có vật tư khớp bộ lọc.' : 'Kho này chưa có bản ghi tồn kho.'));

  $('stock-count').textContent = filtered.length + ' vật tư • nguồn: GET /api/stock/' +
    ($('warehouse-select').value || '-');
}

const isLoaded = (rows) => Array.isArray(rows);

function renderOverview() {
  const allStock = Object.values(data.stockByWarehouse).filter(Array.isArray).flat();
  const loadedWarehouses = Object.values(data.stockByWarehouse).filter(Array.isArray).length;
  const lowCount = allStock.filter((s) =>
    s.isBelowMinStock === true ||
    (Number(s.minStock) > 0 && Number(s.quantity) < Number(s.minStock))).length;

  setKpi('kpi-warehouses', isLoaded(data.warehouses) ? String(data.warehouses.length) : NA,
    isLoaded(data.warehouses) ? 'GET /api/warehouse' : 'chưa đọc được danh sách kho');
  setKpi('kpi-low', data.stockLoaded ? String(lowCount) : NA,
    data.stockLoaded ? lowCount + '/' + allStock.length + ' SKU dưới định mức' : 'chưa đọc được tồn kho');
  setKpi('kpi-po', isLoaded(data.staleOrders) ? String(data.staleOrders.length) : NA,
    'lệnh sản xuất không đổi trạng thái ≥ 7 ngày');
  setKpi('kpi-errors', isLoaded(data.errors) ? String(data.errors.length) : NA,
    isLoaded(data.errors)
      ? (data.errors.length ? 'ghi nhận gần nhất: ' + when(data.errors[0].createdAt) : 'không có lỗi gần đây')
      : 'chưa đọc được nhật ký lỗi');

  // BOM: the host exposes POST /api/manufacturing/bom/line only. There is no
  // read contract, so this panel states that rather than rendering invented
  // consumption bars.
  $('bom-list').innerHTML = emptyList(
    NA + ' — API chưa có endpoint đọc BOM (chỉ có POST /api/manufacturing/bom/line). ' +
    'Xem vật tư thực tế tại tab Kho.');
}

function renderErrors() {
  $('error-list').innerHTML = !isLoaded(data.errors)
    ? emptyList('Chưa đọc được nhật ký lỗi ERP.')
    : data.errors.length
    ? data.errors.map((e) =>
        '<div class="stock-row"><span><strong>' + esc(e.errorCode) + '</strong>' +
        (e.referenceNo ? ' • ' + esc(e.referenceNo) : '') + ' — ' + esc(e.message) + '</span>' +
        '<span class="text-muted">' + esc(when(e.createdAt)) + '</span></div>').join('')
    : emptyList('Không có lỗi ERP nào trong 20 bản ghi gần nhất.');
}

function renderAlerts() {
  $('low-stock-list').innerHTML = !isLoaded(data.alerts)
    ? emptyList('Chưa đọc được cảnh báo tồn kho.')
    : data.alerts.length
    ? data.alerts.map((a) =>
        '<div class="stock-row"><span><strong>' + esc(a.itemCode) + '</strong> @ ' +
        esc(a.warehouseCode) + ' — còn ' + esc(num(a.quantityAvailable)) +
        ', đề xuất nhập ' + esc(num(a.quantitySuggested)) + '</span>' +
        '<span class="badge badge-red">' + esc(a.status) + '</span></div>').join('')
    : emptyList('Không có cảnh báo tồn kho mở.');
}

function renderStaleOrders() {
  $('stale-list').innerHTML = !isLoaded(data.staleOrders)
    ? emptyList('Chưa đọc được danh sách lệnh kẹt.')
    : data.staleOrders.length
    ? data.staleOrders.map((o) =>
        '<div class="stock-row"><span><strong>' + esc(o.productionOrderNo) + '</strong> — ' +
        esc(o.finishedGoodCode) + ' • ' + esc(o.status) + '</span>' +
        '<span class="text-muted">đứng yên ' + esc(o.daysIdle) + ' ngày</span></div>').join('')
    : emptyList('Không có lệnh sản xuất bị kẹt.');
}

function renderIncidents() {
  $('incident-list').innerHTML = !isLoaded(data.incidents)
    ? emptyList('Chưa đọc được danh sách sự cố.')
    : data.incidents.length
    ? data.incidents.map((i) =>
        '<div class="stock-row"><span><strong>' + esc(i.errorCode) + '</strong>' +
        (i.refNo ? ' • ' + esc(i.refNo) : '') + ' — ' + esc(i.title) + '</span>' +
        '<span class="text-muted">' + esc(i.status) + ' • ' + esc(when(i.createdAt)) + '</span></div>').join('')
    : emptyList('Chưa ghi nhận sự cố hỗ trợ nào.');
}

function renderApprovals() {
  $('approval-list').innerHTML = !isLoaded(data.approvals)
    ? emptyList('Chưa đọc được danh sách phê duyệt.')
    : data.approvals.length
    ? data.approvals.map((a) =>
        '<div class="stock-row"><span><strong>' + esc(a.approvalNo) + '</strong> — ' +
        esc(a.action) + (a.refNo ? ' • ' + esc(a.refNo) : '') + '</span>' +
        '<span class="text-muted">' + esc(a.status) + '</span></div>').join('')
    : emptyList('Không có phê duyệt nào đang chờ.');
}

function renderAll(reset) {
  if (reset) {
    data.warehouses = null;
    data.stock = null;
    data.errors = null;
    data.alerts = null;
    data.staleOrders = null;
    data.incidents = null;
    data.approvals = null;
    data.stockByWarehouse = {};
    data.stockLoaded = false;
    data.productionOrder = null;
  }
  renderOverview();
  renderStock();
  renderErrors();
  renderAlerts();
  renderStaleOrders();
  renderIncidents();
  renderApprovals();
  renderProductionOrder();
}

/* ------------------------------------------------------------- production */

/* There is no "list production orders" endpoint, so the operator names the
 * order. Nothing about a specific order is pre-filled. */
function renderProductionOrder() {
  const po = data.productionOrder;
  const badge = $('po-status');
  const detail = $('po-detail');
  const number = $('po-number');

  number.textContent = po ? po.productionOrderNo : '—';
  if (po) {
    badge.textContent = po.status;
    badge.className = 'badge ' + (po.status === 'COMPLETED' ? 'badge-green' : 'badge-orange');
    detail.textContent = po.finishedGoodCode + ' • kế hoạch ' + num(po.plannedQuantity) +
      ' • đã làm ' + num(po.doneQuantity) + ' • kho ' + po.warehouseCode +
      ' • tạo ' + when(po.createdAt);
  } else {
    badge.textContent = NA;
    badge.className = 'badge';
    detail.textContent = 'Chưa nạp lệnh sản xuất. Nhập mã lệnh rồi bấm "Tải lệnh".';
  }
}

async function loadProductionOrder() {
  const poNo = $('po-input').value.trim();
  if (!poNo) { data.productionOrder = null; renderProductionOrder(); return; }
  const { ok, body } = await apiJson('/api/manufacturing/production-order/' + encodeURIComponent(poNo));
  data.productionOrder = ok && body ? body : null;
  renderProductionOrder();
  $('mfg-msg').textContent = data.productionOrder
    ? 'Đã tải lệnh ' + poNo + ' từ API.'
    : 'Không tìm thấy lệnh ' + poNo + ' trên hệ thống.';
}

async function runProductionAction(kind) {
  const poNo = $('po-input').value.trim();
  if (!poNo) { $('mfg-msg').textContent = 'Nhập mã lệnh sản xuất trước.'; return; }
  if (!accessToken) { showLogin(); return; }

  const path = kind === 'check'
    ? '/api/automation/production-order/' + encodeURIComponent(poNo) + '/material-check'
    : '/api/automation/production-order/' + encodeURIComponent(poNo) + '/complete';

  const { ok, body } = await apiJson(path, { method: 'POST' });
  $('mfg-msg').textContent = ok
    ? (body.message || 'Thành công.')
    : ((body && (body.message || body.errorCode)) || 'Lệnh thất bại (HTTP).');
  if (kind === 'complete' && ok) await loadProductionOrder();
}

/* -------------------------------------------------------------- mutations */

async function submitStock(event) {
  event.preventDefault();
  if (!accessToken) { showLogin(); return; }

  const type = $('s-type').value;
  const payload = {
    warehouseCode: $('warehouse-select').value,
    itemCode: $('s-item').value.trim(),
    quantity: Number($('s-qty').value),
    referenceNo: $('s-ref').value.trim() || 'MANUAL',
  };
  const { ok, body } = await apiJson('/api/stock/' + type, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });

  $('stock-msg').textContent = ok
    ? ((body && body.message) || 'Ghi nhận thành công.') +
      (body && body.balance != null ? ' • Tồn mới: ' + body.balance : '')
    : ((body && (body.message || body.errorCode)) || 'Lệnh thất bại (HTTP).');
  if (ok) { await loadAllStock(); await loadAlerts(); }
}

async function submitLotReceive(event) {
  event.preventDefault();
  if (!accessToken) { showLogin(); return; }
  const payload = {
    itemCode: $('l-item').value.trim(),
    qty: Number($('l-qty').value),
    lotCode: $('l-lot').value.trim(),
    receivingLocationCode: $('l-loc').value.trim(),
    idempotencyKey: $('l-idem').value.trim(),
  };
  const { ok, body } = await apiJson(
    '/api/warehouse/receipts/' + encodeURIComponent($('l-po').value.trim()) + '/receive', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
  $('lot-msg').textContent = ok
    ? ((body && body.replayed ? '[REPLAY] ' : '') + (body.message || 'Đã nhận lot.'))
    : ((body && (body.message || body.errorCode)) || 'Nhận hàng thất bại (HTTP).');
  if (ok) await loadAllStock();
}

async function resolveScan() {
  const code = $('m-lot').value.trim();
  const box = $('scan-result');
  if (!code) { box.innerHTML = emptyList('Nhập mã lot cần tra cứu.'); return; }
  const { ok, body } = await apiJson('/api/barcodes/resolve/' + encodeURIComponent(code));
  box.innerHTML = ok
    ? '<div class="stock-row ok"><span><strong>' + esc(body.entityType) + '</strong> • ' +
      esc(body.entityKey) + (body.itemCode ? ' • ' + esc(body.itemCode) : '') + '</span>' +
      '<span class="text-muted">' + esc(body.status || '') + '</span></div>'
    : '<div class="stock-row danger"><span>Lỗi tra cứu: ' +
      esc((body && (body.title || body.message || body.errorCode)) || 'không tìm thấy') + '</span></div>';
}

async function submitLotMove(event) {
  event.preventDefault();
  if (!accessToken) { showLogin(); return; }
  const sameWarehouse = $('m-fromwh').value.trim() === $('m-towh').value.trim();
  const lot = $('m-lot').value.replace(/^LOT:/i, '').trim();
  const payload = sameWarehouse
    ? { lotCode: lot, fromLocationCode: $('m-fromloc').value.trim(),
        toLocationCode: $('m-toloc').value.trim(), qty: Number($('m-qty').value),
        idempotencyKey: $('m-idem').value.trim() }
    : { lotCode: lot, fromWarehouseCode: $('m-fromwh').value.trim(),
        fromLocationCode: $('m-fromloc').value.trim(), toWarehouseCode: $('m-towh').value.trim(),
        toLocationCode: $('m-toloc').value.trim(), qty: Number($('m-qty').value),
        idempotencyKey: $('m-idem').value.trim() };

  const { ok, body } = await apiJson(sameWarehouse ? '/api/warehouse/putaway' : '/api/warehouse/move', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  $('move-msg').textContent = ok
    ? ((body && body.replayed ? '[REPLAY] ' : '') + (body.message || 'Đã di chuyển lot.'))
    : ((body && (body.message || body.errorCode)) || 'Di chuyển thất bại (HTTP).');
  if (ok) await loadAllStock();
}

async function loadAllocation(event) {
  event.preventDefault();
  const poNo = $('po-input').value.trim();
  const box = $('alloc-result');
  if (!poNo) { box.innerHTML = emptyList('Nhập mã lệnh sản xuất trước.'); return; }

  const { ok, body } = await apiJson(
    '/api/manufacturing/production-order/' + encodeURIComponent(poNo) + '/allocate-lots');
  if (!ok) {
    box.innerHTML = emptyList('Không phân bổ được: ' +
      ((body && (body.message || body.title || body.errorCode)) || 'lỗi HTTP'));
    return;
  }
  const lines = (body && body.lines) || [];
  box.innerHTML = lines.length
    ? lines.map((l) =>
        '<div class="stock-row' + (l.isShort ? ' danger' : ' ok') + '"><span><strong>' +
        esc(l.itemCode) + '</strong> • ' + esc(l.traceMode) + '</span><span>cần ' +
        esc(num(l.qtyRequired)) + ' • sẵn ' + esc(num(l.qtyAvailableIssuable)) +
        (l.suggestedLots && l.suggestedLots.length
          ? ' • lô: ' + l.suggestedLots.map((s) => esc(s.lotCode)).join(', ') : '') +
        (l.isShort ? ' • THIẾU' : '') + '</span></div>').join('')
    : emptyList('Lệnh này không có dòng định mức để phân bổ.');
}

function renderTraceNode(node) {
  const bits = (node.itemCode ? ' • ' + node.itemCode : '') +
    (node.qty !== null && node.qty !== undefined ? ' • qty ' + num(node.qty) : '');
  let html = '<div class="stock-row"><span><strong>' + esc(node.kind) + '</strong> ' +
    esc(node.key) + esc(bits) + '</span></div>';
  if (node.children && node.children.length) {
    html += node.children.map((c) => '<div style="margin-left:18px">' +
      renderTraceNode(c) + '</div>').join('');
  }
  return html;
}

async function lookupTrace(event) {
  event.preventDefault();
  const box = $('trace-result');
  const lot = $('t-lot').value.trim();
  if (!lot) { box.innerHTML = emptyList('Nhập mã lot cần truy vết.'); return; }

  const { ok, body } = await apiJson('/api/trace/' + encodeURIComponent(lot) +
    qs({ direction: $('t-dir').value }));
  box.innerHTML = ok
    ? '<div class="stock-row ok"><span>Hướng tra cứu: ' + esc(body.direction) + '</span></div>' +
      renderTraceNode(body.trace)
    : emptyList('Không truy vết được: ' +
      ((body && (body.title || body.message || body.errorCode)) || 'lỗi HTTP'));
}

async function printLabel(event) {
  event.preventDefault();
  if (!accessToken) { showLogin(); return; }
  const box = $('label-result');
  const payload = {
    entityType: 'LOT',
    entityKey: $('lb-lot').value.trim(),
    labelType: $('lb-type').value,
    copies: Number($('lb-copies').value) || 1,
    format: $('lb-format').value,
  };
  const { ok, body } = await apiJson('/api/labels', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  if (!ok) {
    box.innerHTML = emptyList('Không tạo được nhãn: ' +
      ((body && (body.message || body.errorCode)) || 'lỗi HTTP'));
    return;
  }
  const job = body.job || {};
  /* ZPL is escaped here; the HTML branch is not, because body.rendered is
   // html-encoded field by field by LabelRenderService (see
   // AppJs_OnlyInjectsLabelMarkupTheServiceHasEncoded). Do not inject raw markup
   // here without that guarantee. */
  box.innerHTML = '<div class="stock-row ok"><span>Job #' + esc(job.printJobId) + ' • ' +
    esc(job.format) + ' × ' + esc(job.copies) + '</span></div>' +
    (job.format === 'ZPL'
      ? '<pre class="label-pre">' + esc(body.rendered) + '</pre>'
      : '<div class="stock-row">' + body.rendered + '</div>');
}

/* ---------------------------------------------------------------- session */

async function login() {
  const username = $('login-username').value.trim();
  const password = $('login-password').value;
  if (!username || !password) {
    $('login-error').textContent = 'Nhập tài khoản và mật khẩu.';
    $('login-error').hidden = false;
    return;
  }

  const button = $('login-submit');
  button.disabled = true;
  try {
    const response = await fetch(apiUrl('/api/auth/login'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
    const body = await response.json().catch(() => null);
    if (!response.ok || !body || !body.accessToken) {
      showLogin((body && (body.message || body.errorCode)) ||
        'Đăng nhập thất bại (HTTP ' + response.status + ').');
      return;
    }
    storeTokens(body.accessToken, body.refreshToken);
    $('login-password').value = '';
    showApp(body.user);
    toast('Đã đăng nhập ' + username);
    await loadOverview();
  } catch {
    showLogin('Không kết nối được API. Kiểm tra runtime-config.js.');
  } finally {
    button.disabled = false;
  }
}

async function logout() {
  /* Revoke server-side first, then drop the local copy. The bearer is sent so
   * the host can revoke every session for this actor. */
  if (accessToken || refreshToken) {
    try {
      await fetch(apiUrl('/api/auth/logout'), {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(accessToken ? { Authorization: 'Bearer ' + accessToken } : {}),
        },
        body: JSON.stringify({ refreshToken }),
      });
    } catch { /* the local session is dropped regardless */ }
  }
  forgetTokens();
  renderAll(true);
  showLogin('Đã đăng xuất.');
  toast('Đã đăng xuất');
}

/* ------------------------------------------------------------------- boot */

function setupNav() {
  const titles = {
    'tab-overview': 'Tổng quan vận hành',
    'tab-stock': 'Kho & Tồn kho',
    'tab-mfg': 'Sản xuất',
    'tab-lots': 'Nhận hàng & Lots',
    'tab-incident': 'Giám sát & Sự cố',
  };
  const select = (button) => {
    document.querySelectorAll('.nav-item').forEach((x) => x.classList.toggle('active', x === button));
    document.querySelectorAll('.tab-pane').forEach((p) => p.classList.toggle('active', p.id === button.dataset.tab));
    $('page-title').textContent = titles[button.dataset.tab] || '';
    $('breadcrumb-tab').textContent = titles[button.dataset.tab] || '';
  };
  document.querySelectorAll('.nav-item').forEach((b) => b.addEventListener('click', () => select(b)));
  document.querySelectorAll('[data-goto]').forEach((b) => b.addEventListener('click', () => {
    document.querySelector('[data-tab="' + b.dataset.goto + '"]')?.click();
  }));
  $('theme-toggle').addEventListener('click', () => {
    const root = document.documentElement;
    const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    $('theme-toggle').textContent = next === 'dark' ? '○ Chế độ sáng' : '◐ Chế độ tối';
  });
}

async function boot() {
  if (!accessToken) {
    showLogin();
    return;
  }
  const { ok, body } = await apiJson('/api/auth/me');
  if (!ok || !body) {
    showLogin();
    return;
  }
  showApp({ username: body.username, roles: body.roles });
  await loadOverview();
}

document.addEventListener('DOMContentLoaded', () => {
  setupNav();
  renderAll();
  $('login-form').addEventListener('submit', (e) => { e.preventDefault(); login(); });
  $('logout-btn').addEventListener('click', logout);
  $('btn-reload').addEventListener('click', loadOverview);
  $('warehouse-select').addEventListener('change', loadStock);
  $('stock-search').addEventListener('input', () => renderStock());
  $('btn-reload-stock').addEventListener('click', loadAllStock);
  $('btn-load-po').addEventListener('click', loadProductionOrder);
  $('po-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); loadProductionOrder(); }
  });
  $('btn-check-material').addEventListener('click', () => runProductionAction('check'));
  $('btn-complete-po').addEventListener('click', () => runProductionAction('complete'));
  $('stock-form').addEventListener('submit', submitStock);
  $('lot-form').addEventListener('submit', submitLotReceive);
  $('move-form').addEventListener('submit', submitLotMove);
  $('btn-resolve').addEventListener('click', resolveScan);
  $('m-lot').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); resolveScan(); }
  });
  $('alloc-form').addEventListener('submit', loadAllocation);
  $('trace-form').addEventListener('submit', lookupTrace);
  $('label-form').addEventListener('submit', printLabel);
  boot();
});
