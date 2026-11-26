#!/usr/bin/env bash
# Prepares the production test stack: random passwords and client secrets in .env, and an empty secrets.env for the
# values behind your services' secret:<name> references. Never overwrites either file. Both are git-ignored.
set -euo pipefail
cd "$(dirname "$0")"

random() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-24}"; }

if [ ! -f .env ]; then
  cat > .env <<EOF
# Made by setup.sh. Keycloak reads these on its FIRST start only: to change them later, change them in the Keycloak
# console, or start over with "docker compose down -v".
KEYCLOAK_ADMIN_PASSWORD=$(random)
ADMIN_OIDC_CLIENT_SECRET=$(random 40)
TEST_APP_CLIENT_SECRET=$(random 40)
AUTHOR_PASSWORD=$(random 16)
PUBLISHER_PASSWORD=$(random 16)
ADMIN_PASSWORD=$(random 16)
EOF
  echo "Wrote .env"
fi

if [ ! -f secrets.env ]; then
  cat > secrets.env <<'EOF'
# The values behind secret:<name> references in your sign-ins, one per line. In production the admin app can't store
# secret values; it shows which ones are missing. Add them here, then: docker compose up -d
#   Secrets__crm-password=the-password
EOF
  echo "Wrote secrets.env"
fi

source .env
cat <<EOF

Sign in to the admin app (https://localhost:8300) as:
  author      $AUTHOR_PASSWORD      (author: services, sign-ins, drafts, Try it)
  publisher   $PUBLISHER_PASSWORD      (also publish, roll back, delete)
  admin       $ADMIN_PASSWORD      (also browser access, CORS)
Keycloak console (https://localhost:8443/admin): admin / $KEYCLOAK_ADMIN_PASSWORD

Next: docker compose up --build
EOF
