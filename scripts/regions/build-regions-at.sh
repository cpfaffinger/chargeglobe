#!/bin/sh
# Rebuilds the region seed src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson from OpenStreetMap
# (ODbL, "© OpenStreetMap contributors"): the nine Austrian states as administrative boundaries (admin_level 4).
# Needs curl and Docker; queries the public Overpass API once (OVERPASS_URL selects another instance) and builds
# the polygons in a throwaway PostGIS container (same image as the db service), which is removed afterwards.
# Run from the repository root:
#   ./scripts/regions/build-regions-at.sh
set -eu

here=$(cd "$(dirname "$0")" && pwd)
out=${1:-src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson}
overpass=${OVERPASS_URL:-https://overpass-api.de/api/interpreter}
container=chargeglobe-regions-build
work=$(mktemp -d)
trap 'docker rm -f "$container" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

echo "Querying $overpass …"
curl -sS --fail --max-time 600 --retry 5 --retry-delay 30 \
    -A "chargeglobe region seed (https://github.com/cpfaffinger/chargeglobe)" \
    --data-urlencode "data@$here/regions-at.overpassql" \
    -o "$work/osm.json" "$overpass"

echo "Building polygons in a throwaway PostGIS container …"
docker run -d --rm --name "$container" -e POSTGRES_PASSWORD=regions timescale/timescaledb-ha:pg17 >/dev/null
# The image restarts Postgres once after initializing; wait until it answers three times in a row.
ready=0
while [ "$ready" -lt 3 ]; do
    if docker exec "$container" pg_isready -q -U postgres; then ready=$((ready + 1)); else ready=0; fi
    sleep 1
done
docker cp "$work/osm.json" "$container:/tmp/osm.json"
docker exec -i "$container" psql -q -U postgres -v file=/tmp/osm.json < "$here/build-regions.sql" > "$work/regions.geojson"

mv "$work/regions.geojson" "$out"
echo "Wrote $out ($(wc -c < "$out") bytes)"
