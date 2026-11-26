#!/usr/bin/env bash
# Prints an access token from the stack's Keycloak.
#   ./token.sh app            a server calling /bff/* (client credentials; has the role bff.read)
#   ./token.sh mcp [user]     an AI agent calling /mcp as that person (default: author)
#   ./token.sh admin [user]   a script calling /admin/* as that person (default: publisher)
set -euo pipefail
cd "$(dirname "$0")"
source .env

url=https://localhost:8443/realms/aggregator/protocol/openid-connect/token
password_for() { case "$1" in author) echo "$AUTHOR_PASSWORD";; publisher) echo "$PUBLISHER_PASSWORD";; admin) echo "$ADMIN_PASSWORD";; *) echo "Unknown user '$1': use author, publisher or admin" >&2; exit 1;; esac; }

case "${1:-}" in
  app)   args=(-d grant_type=client_credentials -d client_id=test-app --data-urlencode "client_secret=$TEST_APP_CLIENT_SECRET") ;;
  mcp)   user=${2:-author};    args=(-d grant_type=password -d client_id=aggregator-agent -d "username=$user" --data-urlencode "password=$(password_for "$user")") ;;
  admin) user=${2:-publisher}; args=(-d grant_type=password -d client_id=aggregator-scripts -d "username=$user" --data-urlencode "password=$(password_for "$user")") ;;
  *) echo "Usage: ./token.sh app | mcp [user] | admin [user]" >&2; exit 1 ;;
esac

answer=$(curl -s --cacert certs/ca.crt "$url" "${args[@]}")
token=$(printf '%s' "$answer" | sed -nE 's/.*"access_token":"([^"]+)".*/\1/p')
if [ -z "$token" ]; then echo "Keycloak refused: $answer" >&2; exit 1; fi
echo "$token"
