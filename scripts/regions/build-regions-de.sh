#!/bin/sh
# Rebuilds the region seed src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-de.geojson from OpenStreetMap:
# the 16 German states (ISO 3166-2 DE-BW … DE-TH). Needs curl and Docker (see build-regions.sh).
# Run from the repository root:
#   ./scripts/regions/build-regions-de.sh
set -eu
exec "$(dirname "$0")/build-regions.sh" de 16 "DE-BW … DE-TH" "${1:-src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-de.geojson}"
