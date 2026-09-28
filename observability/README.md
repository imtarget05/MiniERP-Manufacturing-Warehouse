# Observability - MiniERP Manufacturing & Warehouse

Self-contained Prometheus + Grafana + Alertmanager stack for this repo. It lives
here rather than in a shared folder, so cloning **this** repository is enough to
see the whole runtime picture - which is what a reviewer, a demo or a debugging
session actually needs.

## Quickstart

```bash
cd MiniERP-Manufacturing-Warehouse/observability
docker compose up -d
docker compose config                     # validate without starting
```

| Service | Port | URL |
|---|---|---|
| Prometheus | 9106 | http://localhost:9106 (`Status -> Targets`) |
| Grafana | 3206 | http://localhost:3206 (admin / `$GF_SECURITY_ADMIN_PASSWORD`, default `admin`) |
| Alertmanager | 9306 | http://localhost:9306 |

Reload a config change without a restart:

```bash
curl -XPOST http://localhost:9106/-/reload
```

## Scrape targets

| Job | Metrics path | Port | Service |
|---|---|---|---|
| `minierp` | `/metrics` | 5000 | MiniERP Manufacturing & Warehouse |

## Dashboards

- `project-minierp.json` - MiniERP - DB reachability + process liveness
- `golden-signals.json` - Golden signals - QPS / error rate / P95 / saturation per scrape job

## Metrics this repo exposes

| Endpoint | Service | Series |
|---|---|---|
| `GET /metrics` | MiniERP.Api | `minierp_db_up`, `minierp_process_up` |

Deliberately minimal and unauthenticated: DB reachability and process liveness only.
Lot quantities, stock levels and customer data never leave the API, because a scraper
has no way to authenticate.

## Alerts

`prometheus/alerts.yml` has the `services` group: any scrape target down for 2m. This repo does not vendor the LLM gateway, so the `gateway` group is intentionally absent.

## SLOs

`slo.yaml` holds the machine-readable SLI / target / window / error-budget table.

## Operational notes

- Grafana `admin` + a default password is fine for a local demo. For anything
  shared, set `GF_SECURITY_ADMIN_PASSWORD` and keep anonymous access off.
- Every `/metrics` endpoint is aggregate-only and unauthenticated, because a
  Prometheus scraper carries no session cookie. Business detail stays behind the
  existing auth-protected endpoints.
- A target that is not running shows `DOWN`; it never blocks the other jobs.
- Ports are offset per project (Prometheus 9106) so several portfolios can run
  at the same time. Override with `PROMETHEUS_PORT` / `GRAFANA_PORT` /
  `ALERTMANAGER_PORT`.
- Docker is required. CI asserts these configs parse; it does not start the stack.
