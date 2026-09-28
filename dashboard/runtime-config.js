'use strict';
/* MiniERP dashboard — runtime configuration.
 *
 * This file is the ONLY place the API origin is decided, and it is served
 * with no-store so an operator can repoint a built image without rebuilding
 * it. It is loaded before app.js (see index.html).
 *
 * apiBaseUrl
 *   ''  -> same origin. The dashboard is then served behind the Nginx image
 *          that reverse-proxies /api/ to the API container. This is the
 *          production/Compose shape.
 *   An absolute origin is only used by a local harness that serves the static
 *          files from a different port than the API (the E2E lab).
 *
 * The value may be overridden by a deployment that mounts its own
 * window.__MINI_ERP_RUNTIME_CONFIG__ before this script; nothing else in the
 * dashboard reads window.location for the API origin.
 *
 * THREAT MODEL: whoever controls this file controls where the bearer is sent.
 * That is deliberate - it is the operator's handle on the origin - and it is
 * bounded by three things: the file is served same-origin with `no-store`, so
 * it is not a cached-payload foothold; an attacker who can already write it
 * can already change index.html or app.js; and shipping an absolute origin
 * here means the API must allow this dashboard's origin explicitly (CORS),
 * which is a second, deliberate act. Do not template an origin into this file
 * from an untrusted source.
 */
(function () {
  var preset = window.__MINI_ERP_RUNTIME_CONFIG__ || {};
  var base = typeof preset.apiBaseUrl === 'string' ? preset.apiBaseUrl : '';
  window.__MINI_ERP_CONFIG__ = {
    apiBaseUrl: base.replace(/\/+$/, ''),
  };
})();
