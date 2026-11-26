#!/bin/sh
# Runs once per "docker compose up" in an Alpine container (see docker-compose.yml).
#  1. A local test CA and a certificate for "localhost", kept in ./certs so they survive restarts and you can trust
#     the CA on your machine. Made only when missing.
#  2. ca-bundle.crt: the usual public CAs plus the test CA, so the apps trust Keycloak and your real services.
#  3. An empty configuration in the config volume, and the admin app's key folder, owned by the apps' user (1654 in
#     the .NET images).
set -eu

apk add --no-cache openssl >/dev/null

if [ ! -f /certs/server.crt ]; then
  echo "Making a local test CA and a certificate for localhost in ./certs"
  openssl req -x509 -newkey rsa:2048 -nodes -days 825 -sha256 \
    -subj "/CN=API Aggregator local test CA" -keyout /certs/ca.key -out /certs/ca.crt \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  openssl req -newkey rsa:2048 -nodes -subj "/CN=localhost" -keyout /certs/server.key -out /tmp/server.csr 2>/dev/null
  printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nbasicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' > /tmp/server.ext
  # 397 days: browsers refuse longer-lived server certificates.
  openssl x509 -req -in /tmp/server.csr -CA /certs/ca.crt -CAkey /certs/ca.key -CAcreateserial \
    -days 397 -sha256 -extfile /tmp/server.ext -out /certs/server.crt 2>/dev/null
  rm -f /certs/ca.srl
fi

cat /etc/ssl/certs/ca-certificates.crt /certs/ca.crt > /certs/ca-bundle.crt
# Keycloak (user 1000) and the apps (user 1654) read the key. Fine for a local test; never do this with a real key.
chmod 644 /certs/server.key /certs/server.crt /certs/ca.crt /certs/ca-bundle.crt
chmod 600 /certs/ca.key

mkdir -p /config/recipes /config/drafts /config/history
[ -f /config/services.json ] || echo '[]' > /config/services.json
[ -f /config/auth-profiles.json ] || echo '[]' > /config/auth-profiles.json
chown -R 1654:1654 /config /admin-keys
echo "Ready"
