#!/bin/sh
# Starts the sample demo in one container (Dockerfile.vercel): the mock services on 127.0.0.1:5100, where the
# sample config expects them, and the gateway on the port the host routes to ($PORT, 8080 by default).
# SAMPLE DATA ONLY: the gateway runs in Development, so /dev/token hands out test tokens to anyone.
set -e

(cd /app/mocks && ASPNETCORE_URLS=http://127.0.0.1:5100 exec dotnet MockServices.dll) &

cd /app/gateway
ASPNETCORE_URLS="http://0.0.0.0:${PORT:-8080}" exec dotnet ApiAggregator.Gateway.dll
