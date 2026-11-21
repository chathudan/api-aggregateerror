#!/usr/bin/env bash
# Calls every sample aggregated endpoint through the gateway with a development token, and says which answered.
# Needs scripts/dev-run.sh running (sample data). Exits with 1 if any endpoint didn't answer as expected.
set -euo pipefail
GATEWAY="${GATEWAY:-http://localhost:5200}"

# One token with every scope the sample endpoints need (customer.read, people.read, profile.read).
TOKEN=$(curl -sf "$GATEWAY/dev/token?sub=qa-user&scope=customer.read%20people.read%20profile.read" | sed -E 's/.*"access_token":"([^"]+)".*/\1/') || {
  echo "The gateway isn't answering at $GATEWAY. Start it with ./scripts/dev-run.sh first." >&2
  exit 1
}

failed=0
# call <expected status> <path>: prints the status and the start of the answer.
call() {
  local expected="$1" path="$2" body status
  body=$(mktemp)
  status=$(curl -s -o "$body" -w "%{http_code}" -H "Authorization: Bearer $TOKEN" "$GATEWAY/bff/$path")
  if [[ "$status" == "$expected" ]]; then printf "OK    %s  GET /bff/%s\n" "$status" "$path"
  else printf "FAIL  %s  GET /bff/%s (expected %s)\n" "$status" "$path" "$expected"; failed=1; fi
  printf "      %s\n\n" "$(head -c 300 "$body" | tr '\n' ' ')"
  rm -f "$body"
}

call 200 "customer/C-10042/overview?status=open"
call 200 "customer/C-10042/summary?status=open"
call 200 "customer/C-30003/summary"            # no loyalty record: a partial answer with _errors
call 200 "customer/C-10042/profile"
call 200 "customer/C-10042/loyalty"
call 200 "customer/C-10042/appointments"
call 200 "me/preferences"                      # Service J gets the caller's own token
call 200 "people/directory"                    # combine lists (expected results: samples/README.md)
call 200 "people/newest"
call 200 "contacts/unique"
call 400 "customer/10042/summary"              # a wrong id: refused before any service is called

echo "== Cache: the second call of profile comes from the cache (30 s per caller)"
for _ in 1 2; do
  curl -s -i -H "Authorization: Bearer $TOKEN" "$GATEWAY/bff/customer/C-10042/profile" | grep -iE "^x-aggregator-cache" || echo "x-aggregator-cache: (none)"
done

echo
echo "== Downstream call counts (local mock services only)"
curl -s -m 5 http://localhost:5100/_stats || echo "(the mock services aren't running on this machine)"; echo

if [[ $failed -ne 0 ]]; then echo; echo "Some endpoints didn't answer as expected (FAIL above)."; exit 1; fi
echo; echo "All sample endpoints answered as expected."
