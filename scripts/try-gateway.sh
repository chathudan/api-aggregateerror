#!/usr/bin/env bash
# Gets a development token and calls the sample aggregated endpoints. Needs scripts/dev-run.sh running.
set -euo pipefail
TOKEN=$(curl -s "http://localhost:5200/dev/token?sub=qa-user&scope=customer.read" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')

echo "== GET /bff/customer/C-10042/overview"
curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:5200/bff/customer/C-10042/overview?status=open"
echo; echo
echo "== GET /bff/customer/C-10042/profile (cached for 30 s per caller)"
curl -s -i -H "Authorization: Bearer $TOKEN" "http://localhost:5200/bff/customer/C-10042/profile" | grep -iE "^HTTP|x-aggregator-cache"
curl -s -i -H "Authorization: Bearer $TOKEN" "http://localhost:5200/bff/customer/C-10042/profile" | grep -iE "^HTTP|x-aggregator-cache"
echo
echo "== Downstream call counts"
curl -s http://localhost:5100/_stats; echo
