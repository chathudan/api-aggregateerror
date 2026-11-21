#!/bin/sh
# Starts the sample demo in one container (Dockerfile.vercel): the mock services on 127.0.0.1:5100 (where the sample
# config expects them), the gateway on 127.0.0.1:5200, the admin app on 127.0.0.1:5300, and Caddy on the port the
# host routes to ($PORT, 8080 by default), which sends the gateway's paths to the gateway and the rest to the admin app.
# SAMPLE DATA ONLY: everything runs in Development, so /dev/token and the admin sign-in are open to anyone.
set -e

(cd /app/mocks && ASPNETCORE_URLS=http://127.0.0.1:5100 exec dotnet MockServices.dll) &
(cd /app/gateway && ASPNETCORE_URLS=http://127.0.0.1:5200 exec dotnet ApiAggregator.Gateway.dll) &
(cd /app/admin && ASPNETCORE_URLS=http://127.0.0.1:5300 exec dotnet ApiAggregator.Admin.dll) &

exec caddy run --config /app/Caddyfile --adapter caddyfile
