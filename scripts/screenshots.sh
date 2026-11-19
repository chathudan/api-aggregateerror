#!/usr/bin/env bash
# Refreshes the screenshots in docs/images that the guides use. Runs the admin UI tests against a temporary
# copy of the sample data (nothing in samples/config changes) with UPDATE_SCREENSHOTS=1.
# Needs the browsers once: ./scripts/install-playwright.sh
set -euo pipefail
cd "$(dirname "$0")/.."
# The screenshots show addresses, so they're taken on the usual ports (5100, 5200, 5300): stop dev-run.sh first.
for port in 5100 5200 5300; do
  if lsof -iTCP:$port -sTCP:LISTEN -t >/dev/null 2>&1; then echo "Port $port is in use (is dev-run.sh running?). Stop it, then run this again."; exit 1; fi
done
UI_TESTS_STANDARD_PORTS=1 UPDATE_SCREENSHOTS=1 dotnet test tests/ApiAggregator.UI.Tests --filter "FullyQualifiedName~Screenshots_of_every_page"
echo "Updated:"; ls -1 docs/images/*.png
