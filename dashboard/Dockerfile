# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    dashboard/Dockerfile
# PURPOSE: Lightweight Nginx container for the Factory Operations Dashboard
# ============================================================================

FROM nginx:alpine

# Copy static frontend assets into standard Nginx webroot
COPY . /usr/share/nginx/html

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
