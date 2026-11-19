#!/usr/bin/env bash
# Installs the browser the admin UI tests need (Chromium for Playwright, ADR 0013). Run once per machine
# and after Microsoft.Playwright is upgraded. Uses the Node runtime that ships inside the Playwright
# package, so it needs neither PowerShell nor npm. Browsers go to Playwright's usual folder
# (~/Library/Caches/ms-playwright on macOS, ~/.cache/ms-playwright on Linux, or PLAYWRIGHT_BROWSERS_PATH).
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build tests/ApiAggregator.UI.Tests -v quiet
driver="tests/ApiAggregator.UI.Tests/bin/Debug/net10.0/.playwright"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)  platform=darwin-arm64 ;;
  Darwin-x86_64) platform=darwin-x64 ;;
  Linux-aarch64) platform=linux-arm64 ;;
  Linux-x86_64)  platform=linux-x64 ;;
  *) echo "Unsupported platform $(uname -s)-$(uname -m). On Windows run: pwsh $driver/../playwright.ps1 install chromium"; exit 1 ;;
esac

node="$driver/node/$platform/node"
[[ -x "$node" ]] || { echo "Playwright's Node runtime wasn't found at $node. Is the build up to date?"; exit 1; }

# On Linux CI agents add --with-deps to also install the system libraries Chromium needs (needs sudo).
"$node" "$driver/package/cli.js" install chromium "$@"
echo "Done. Run the UI tests with: dotnet test tests/ApiAggregator.UI.Tests"
