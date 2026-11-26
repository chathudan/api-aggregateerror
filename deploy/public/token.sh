#!/usr/bin/env bash
# Prints an access token from the stack's Keycloak.
#   ./token.sh app            a test app calling /bff/* (client credentials; has the role bff.read)
#   ./token.sh mcp <user>     an AI agent calling /mcp as that person (asks for their password)
#   ./token.sh admin <user>   a script calling /admin/* as that person (asks for their password)
set -euo pipefail
cd "$(dirname "$0")"
source .env

url=https://$AUTH_HOST/realms/aggregator/protocol/openid-connect/token
ask() { read -rsp "Password for $1: " p </dev/tty; echo >&2; printf '%s' "$p"; }

case "${1:-}" in
  app)   args=(-d grant_type=client_credentials -d client_id=test-app --data-urlencode "client_secret=$TEST_APP_CLIENT_SECRET") ;;
  mcp)   [ -n "${2:-}" ] || { echo "Usage: ./token.sh mcp <user>" >&2; exit 1; }
         args=(-d grant_type=password -d client_id=aggregator-agent -d "username=$2" --data-urlencode "password=$(ask "$2")") ;;
  admin) [ -n "${2:-}" ] || { echo "Usage: ./token.sh admin <user>" >&2; exit 1; }
         args=(-d grant_type=password -d client_id=aggregator-scripts -d "username=$2" --data-urlencode "password=$(ask "$2")") ;;
  *) echo "Usage: ./token.sh app | mcp <user> | admin <user>" >&2; exit 1 ;;
esac

answer=$(curl -s "$url" "${args[@]}")
token=$(printf '%s' "$answer" | sed -nE 's/.*"access_token":"([^"]+)".*/\1/p')
if [ -z "$token" ]; then echo "Keycloak refused: $answer" >&2; exit 1; fi
echo "$token"
