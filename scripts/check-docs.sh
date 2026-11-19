#!/usr/bin/env bash
# Runs every example in the guides (docs/**/*.md) and checks it still gives the output the guide shows.
# An example is a "check:" comment followed by a code block, for example:
#
#   <!-- check: run customer-overview --route id=C-10042 -->        (add expect=502 for an error, partial for an excerpt)
#   ```json
#   { "customer": { "id": "C-10042", ... } }
#   ```
#
# The examples run with the real CLI (aggregator run / validate) against the mock services, or through the
# gateway with a dev token. Both start on free ports over a temporary copy of samples/config, so dev-run.sh can
# keep running and nothing in the repository changes. The same check runs in `dotnet test`
# (tests/ApiAggregator.Docs.Tests); this script runs only that project and prints one line per example.
#
# When one fails: if the product changed on purpose, update the guide; otherwise the change broke something.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build -v quiet -nologo tests/ApiAggregator.Docs.Tests
dotnet test tests/ApiAggregator.Docs.Tests --no-build --nologo --logger "console;verbosity=normal" "$@"
