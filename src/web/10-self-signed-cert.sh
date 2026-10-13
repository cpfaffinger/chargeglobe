#!/bin/sh
# Creates the self-signed certificate for the HTTPS-only port on first start
# (docs/adr/0009). It lives in the "tls" volume so that a browser exception
# survives image rebuilds.
set -eu

dir=/etc/nginx/tls
if [ -s "$dir/tls.crt" ] && [ -s "$dir/tls.key" ]; then
    exit 0
fi

# Host name from PUBLIC_BASE_URL, e.g. https://example.org:5005/x -> example.org
host=$(printf '%s' "${PUBLIC_BASE_URL:-}" | sed -E 's#^[a-zA-Z]+://##; s#[:/].*$##')
san="DNS:localhost,IP:127.0.0.1"
if [ -n "$host" ] && [ "$host" != "localhost" ] && [ "$host" != "127.0.0.1" ]; then
    san="$san,DNS:$host"
fi

mkdir -p "$dir"
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -subj "/CN=${host:-localhost}/O=chargeglobe self-signed" \
    -addext "subjectAltName=$san" \
    -keyout "$dir/tls.key" -out "$dir/tls.crt" 2>/dev/null
chmod 600 "$dir/tls.key"
echo "Created self-signed certificate for: $san"
