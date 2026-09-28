# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    dashboard/Dockerfile
# PURPOSE: Nginx image for the Factory Operations Dashboard
#
# The container is the dashboard's front door and does two things:
#   1. serve the static SPA;
#   2. reverse-proxy /api/ to the API container, so the browser only ever talks
#      to one origin and no credential ever travels in a URL.
#
# It runs as the unprivileged `nginx` user. That only works because
# nginx.conf puts every write path under /tmp - see the RED evidence in
# docs (non-root start of the previous image died with
# `mkdir() "/var/cache/nginx/client_temp" failed (13: Permission denied)`).
# ============================================================================

FROM nginx:alpine

# Our own config replaces the stock one; the default server block is removed so
# the SPA is not also served from /etc/nginx/conf.d.
COPY nginx.conf /etc/nginx/nginx.conf
RUN rm -f /etc/nginx/conf.d/default.conf

# Only what the browser loads. The config is copied explicitly rather than with
# `COPY .` so nginx.conf can never end up inside the webroot.
COPY index.html styles.css app.js runtime-config.js /usr/share/nginx/html/

USER nginx

EXPOSE 80

HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=12 \
  CMD wget -q -O /dev/null http://127.0.0.1/healthz || exit 1

CMD ["nginx", "-g", "daemon off;"]
