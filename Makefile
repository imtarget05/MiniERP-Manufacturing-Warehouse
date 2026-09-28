# ============================================================================
# MiniERP-Manufacturing-Warehouse — demo shortcuts (portfolio demo aid).
#
# VERIFIED ON THIS HOST (offline, no `up`, no image pull):
#   * `docker compose config` parses clean — the service / container / image
#     names below are copied from docker-compose.yml, not invented:
#     service `oracle-db`, container `minierp-oracle`, image
#     `gvenzl/oracle-free:slim`.
#   * That Oracle image IS cached locally (`docker images` lists
#     `gvenzl/oracle-free:slim`), so `demo` should not need a pull here.
# NOT VERIFIED HERE (deliberately out of scope / needs minutes + network):
#   * `docker compose up -d oracle-db` was NOT run (first Oracle bootstrap
#     takes several minutes — see scripts/start-db.sh).
#   * `dotnet run` was NOT executed end-to-end here.
# SEED: no new seed script is invented. The existing loader is
#   scripts/run-sql.sh over sql/01..11 (incl. sql/03_seed.sql), guarded by
#   scripts/qa-gate.sh: it REFUSES (exit 78) unless the target is provably
#   disposable. The default `demo` below boots the production-named container
#   (`minierp-oracle`), which the gate treats as production — so for a SEEDED
#   demo use the README QA flow (MINIERP_QA_INSTANCE=... start-db.sh +
#   run-sql.sh with QA vars), or load sql/03_seed.sql manually. `make seed`
#   simply delegates to the existing scripts/run-sql.sh with your environment.
# ============================================================================

.PHONY: help demo seed config-check

help:
	@echo "Targets:"
	@echo "  make demo          oracle-db up, wait healthy, then run the API (foreground)"
	@echo "  make seed          delegate to scripts/run-sql.sh (existing gated loader)"
	@echo "  make config-check  docker compose config (offline validation)"

demo:
	docker compose up -d oracle-db
	@echo "Waiting for minierp-oracle to become healthy (up to 10 min)..."
	@deadline=$$(($$(date +%s) + 600)); \
	while [ "$$(docker inspect -f '{{.State.Health.Status}}' minierp-oracle 2>/dev/null)" != "healthy" ]; do \
		if [ $$(date +%s) -ge $$deadline ]; then \
			echo "Timed out waiting for minierp-oracle health."; \
			docker inspect -f '{{.State.Health.Status}}' minierp-oracle; \
			exit 1; \
		fi; \
		sleep 5; \
	done
	@echo "Oracle is healthy."
	@echo "Seed (once, existing mechanism): MINIERP_QA_DISPOSABLE=1 MINIERP_QA_INSTANCE=<id> \\"
	@echo "  DB_CONTAINER=<qa-container> APP_USER=<qa-schema> bash scripts/run-sql.sh"
	@echo "  (see README + scripts/run-sql.sh header; the gate refuses production names)"
	dotnet run --project src/MiniERP.Api.csproj

seed:
	bash scripts/run-sql.sh

config-check:
	docker compose config > /dev/null
	@echo "compose config: OK"
