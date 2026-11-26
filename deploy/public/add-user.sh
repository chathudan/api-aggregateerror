#!/usr/bin/env bash
# Invites a person: makes their Keycloak account with a one-time password (they choose their own when they first sign
# in) and the role you pick.
#   ./add-user.sh <username> <author|publisher|admin> "<Full Name>" [email]
#     author     services, sign-ins, drafts, Try it
#     publisher  also publish, roll back, delete
#     admin      also browser access (CORS)
set -euo pipefail
cd "$(dirname "$0")"
source .env

if [ $# -lt 3 ]; then echo "Usage: ./add-user.sh <username> <author|publisher|admin> \"<Full Name>\" [email]" >&2; exit 1; fi
user=$1 role=$2 full=$3 email=${4:-}
case "$role" in
  author)    roles=(aggregator.author) ;;
  publisher) roles=(aggregator.author aggregator.publisher) ;;
  admin)     roles=(aggregator.author aggregator.publisher aggregator.admin) ;;
  *) echo "The role must be author, publisher or admin." >&2; exit 1 ;;
esac
first=${full%% *}; last=${full#* }; [ "$last" = "$full" ] && last="-"
password=$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 16)

kc() { docker compose exec -T keycloak /opt/keycloak/bin/kcadm.sh "$@"; }
kc config credentials --server http://localhost:8080 --realm master --user admin --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null
fields=(-s "username=$user" -s enabled=true -s "firstName=$first" -s "lastName=$last")
[ -n "$email" ] && fields+=(-s "email=$email" -s emailVerified=true)
kc create users -r aggregator "${fields[@]}" >/dev/null
kc set-password -r aggregator --username "$user" --new-password "$password" --temporary
for r in "${roles[@]}"; do kc add-roles -r aggregator --uusername "$user" --rolename "$r"; done

cat <<EOF
Invited $full ($role). Send them, through a private channel:
  Address:           https://$ADMIN_HOST
  Username:          $user
  One-time password: $password   (they choose their own when they first sign in)
EOF
