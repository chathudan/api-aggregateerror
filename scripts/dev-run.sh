#!/usr/bin/env bash
# Starts the mock services, gateway, admin API and admin UI locally (macOS/Linux).
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
dotnet run --no-build --project src/ApiAggregator.Admin.Api   & pids+=($!)
dotnet run --no-build --project src/ApiAggregator.Admin.UI    & pids+=($!)

echo
echo "  Mock services  http://localhost:5100/_stats"
echo "  Gateway        http://localhost:5200/bff/openapi.json"
echo "  Admin API      http://localhost:5210/admin/recipes"
echo "  Admin UI       http://localhost:5300   ($mode)"
echo
wait
