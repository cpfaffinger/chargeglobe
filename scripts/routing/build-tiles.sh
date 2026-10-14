#!/bin/sh
# Builds the Valhalla routing tiles for Austria from OpenStreetMap (ODbL) in a throwaway container, for hosts whose
# memory does not suffice to build them in the routing service itself (docs/02, Routing). Needs Docker and about
# 3 GB of free memory and 3 GB of disk for 20–40 minutes. Keeps only what the routing service needs to serve:
# valhalla_tiles.tar, valhalla.json, admins.sqlite, timezones.sqlite and file_hashes.txt.
# Run from the repository root, then copy the directory to ./data/routing of the target installation, e.g.:
#   ./scripts/routing/build-tiles.sh /tmp/routing
#   rsync -a /tmp/routing/ <host>:<installation>/data/routing/
# and start the service there with COMPOSE_PROFILES=routing (docker compose up -d).
set -eu

out=${1:-data/routing-build}
extract=${EXTRACT_URL:-https://download.geofabrik.de/europe/austria-latest.osm.pbf}
image=ghcr.io/valhalla/valhalla-scripted:3.9.1
mkdir -p "$out"
out=$(cd "$out" && pwd)

echo "Building tiles for $extract into $out …"
docker run --rm --name chargeglobe-routing-build -v "$out:/custom_files" \
    -e tile_urls="$extract" -e serve_tiles=False -e build_tar=True -e build_elevation=False \
    -e build_admins=True -e build_time_zones=True -e use_tiles_ignore_pbf=True -e force_rebuild=True \
    "$image"

# The extract and the loose tiles are only needed for building; the service serves the tar.
docker run --rm -v "$out:/custom_files" --entrypoint sh "$image" -c \
    'rm -rf /custom_files/*.osm.pbf /custom_files/valhalla_tiles && chmod -R a+rX /custom_files'
ls -lh "$out"
