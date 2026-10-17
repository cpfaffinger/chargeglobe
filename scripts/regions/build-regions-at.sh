#!/bin/sh
# Rebuilds the region seed src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson from OpenStreetMap:
# the nine Austrian states (ISO 3166-2 AT-1 … AT-9). Needs curl and Docker (see build-regions.sh).
# Run from the repository root:
#   ./scripts/regions/build-regions-at.sh
set -eu
exec "$(dirname "$0")/build-regions.sh" at 9 "AT-1 … AT-9" "${1:-src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson}"
