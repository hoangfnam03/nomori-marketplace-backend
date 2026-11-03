FROM caddy:2-alpine

# Azure Container Apps names; override them with environment variables on the container app if yours differ.
ENV SITE_ADDRESS=:80 \
    API_UPSTREAM=nomori-api:8080 \
    WEB_UPSTREAM=nomori-web:4000

COPY deploy/Caddyfile /etc/caddy/Caddyfile
