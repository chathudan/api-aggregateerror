#!/usr/bin/env bash
# Benchmark: load, faults, abuse, config churn, soak and memory checks for the gateway (docs/guides/benchmarking.md).
#
#   ./scripts/bench.sh                         # default profile (about 15 min): 2,000 endpoints, 50 services, 5,000 callers
#   ./scripts/bench.sh --profile smoke         # about 4 min, for a quick check or CI
#   ./scripts/bench.sh --profile soak          # adds a 30-minute soak for the memory verdict
#   ./scripts/bench.sh --baseline bench-results/<earlier run>/report.json     # compare with an earlier run
#   ./scripts/bench.sh micro                   # only the in-process hot-path timings
#
# Starts its own mocks, gateway and admin app on free ports (your dev-run.sh can keep running). Builds in Release first,
# without shared build servers. Results: bench-results/<time>-<profile>/report.md (git-ignored). Exit code 1 if a check fails.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "Building (Release)..."
dotnet build -c Release --disable-build-servers -nologo -v quiet -clp:ErrorsOnly

command="run"
if [[ "${1:-}" == "micro" || "${1:-}" == "generate" ]]; then command="$1"; shift; fi
exec dotnet tools/ApiAggregator.Bench/bin/Release/net10.0/bench.dll "$command" "$@"
