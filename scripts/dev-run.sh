#!/usr/bin/env bash
# Starts the mock services, the gateway and the admin app (pages + /admin/* API) locally (macOS/Linux).
# Stop everything with Ctrl+C.
#
#   ./scripts/dev-run.sh          sample data (samples/config), the "Sample data" badge shows in the UI
#   ./scripts/dev-run.sh --real   your own configuration (config/)
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "${1:-}" == "--real" ]]; then
  export Aggregator__Config__Directory="$PWD/config"
  mode="real mode: config/"
else
  export Aggregator__Config__Directory="$PWD/samples/config"
  mode="sample data: samples/config"
fi

dotnet build ApiAggregator.sln -v quiet

pids=()
trap 'kill "${pids[@]}" 2>/dev/null' EXIT INT TERM

dotnet run --no-build --project samples/MockServices          & pids+=($!)
dotnet run --no-build --project src/ApiAggregator.Gateway     & pids+=($!)
dotnet run --no-build --project src/ApiAggregator.Admin       & pids+=($!)

echo
echo "  Mock services  http://localhost:5100/_stats"
echo "  Gateway        http://localhost:5200/bff/openapi.json"
echo "  Admin app      http://localhost:5300   ($mode; sign in with any name, pick author or publisher)"
echo "  Admin API      http://localhost:5300/admin/recipes (bearer token: http://localhost:5300/dev/token)"
echo
wait
