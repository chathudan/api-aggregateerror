#!/usr/bin/env bash
# Prepares the public stack: the three addresses, random passwords and client secrets in .env, and an empty
# secrets.env for the values behind your services' secret:<name> references. Never overwrites either file.
#   ./setup.sh <domain> <email for Let's Encrypt>      e.g. ./setup.sh demo.example.com you@example.com
set -euo pipefail
cd "$(dirname "$0")"

random() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-24}"; }

if [ ! -f .env ]; then
  if [ $# -lt 2 ]; then echo "Usage: ./setup.sh <domain> <email for Let's Encrypt>" >&2; exit 1; fi
  cat > .env <<EOF
# Made by setup.sh. Point these three names at this server (DNS A or AAAA records) before starting.
ADMIN_HOST=admin.$1
API_HOST=api.$1
AUTH_HOST=auth.$1
ACME_EMAIL=$2
# Keycloak reads these on its FIRST start only: later, change them in the Keycloak console.
KEYCLOAK_ADMIN_PASSWORD=$(random)
ADMIN_OIDC_CLIENT_SECRET=$(random 40)
TEST_APP_CLIENT_SECRET=$(random 40)
AUTHOR_PASSWORD=$(random 16)
PUBLISHER_PASSWORD=$(random 16)
ADMIN_PASSWORD=$(random 16)
EOF
  chmod 600 .env
  echo "Wrote .env"
fi

if [ ! -f secrets.env ]; then
  cat > secrets.env <<'EOF'
# The values behind secret:<name> references in your sign-ins, one per line. In production the admin app can't store
# secret values; it shows which ones are missing. Add them here, then: docker compose up -d
#   Secrets__crm-password=the-password
EOF
  chmod 600 secrets.env
  echo "Wrote secrets.env"
fi

source .env
cat <<EOF

Addresses (point all three at this server first):
  https://$ADMIN_HOST   admin app
  https://$API_HOST     gateway
  https://$AUTH_HOST    Keycloak (console: https://$AUTH_HOST/admin, user admin / $KEYCLOAK_ADMIN_PASSWORD)

Built-in sign-ins (keep them for yourself; invite others with ./add-user.sh):
  author      $AUTHOR_PASSWORD
  publisher   $PUBLISHER_PASSWORD
  admin       $ADMIN_PASSWORD

Next: docker compose up --build -d
EOF
