#!/bin/sh
# Rebuilds one region seed (src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-<country>.geojson) from OpenStreetMap
# (ODbL, "© OpenStreetMap contributors"): the states of a country as administrative boundaries (admin_level 4).
# Called by build-regions-at.sh and build-regions-de.sh; arguments: country code (lower case), expected number of
# states, codes text for the attribution, output file.
# Needs curl and Docker; queries the public Overpass API once (OVERPASS_URL selects another instance, OSM_JSON reuses
# a saved answer) and builds the polygons in a throwaway PostGIS container (same image as the db service), which is
# removed afterwards.
set -eu

country=$1
expected=$2
codes=$3
out=$4
here=$(cd "$(dirname "$0")" && pwd)
overpass=${OVERPASS_URL:-https://overpass-api.de/api/interpreter}
container=chargeglobe-regions-build-$country
work=$(mktemp -d)
trap 'docker rm -f "$container" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

if [ -n "${OSM_JSON:-}" ]; then
    echo "Using $OSM_JSON …"
    cp "$OSM_JSON" "$work/osm.json"
else
    echo "Querying $overpass …"
    curl -sS --fail --max-time 1200 --retry 5 --retry-delay 30 --retry-all-errors \
        -A "chargeglobe region seed (https://github.com/cpfaffinger/chargeglobe)" \
        --data-urlencode "data@$here/regions-$country.overpassql" \
        -o "$work/osm.json" "$overpass"
fi

echo "Building polygons in a throwaway PostGIS container …"
docker run -d --rm --name "$container" -e POSTGRES_PASSWORD=regions timescale/timescaledb-ha:pg17 >/dev/null
# The image restarts Postgres once after initializing; wait until it answers three times in a row.
ready=0
while [ "$ready" -lt 3 ]; do
    if docker exec "$container" pg_isready -q -U postgres; then ready=$((ready + 1)); else ready=0; fi
    sleep 1
done
docker cp "$work/osm.json" "$container:/tmp/osm.json"
docker exec -i "$container" psql -q -U postgres -v file=/tmp/osm.json -v expected="$expected" -v codes="$codes" \
    -v script="build-regions-$country.sh" < "$here/build-regions.sql" > "$work/regions.geojson"

mv "$work/regions.geojson" "$out"
echo "Wrote $out ($(wc -c < "$out") bytes)"
