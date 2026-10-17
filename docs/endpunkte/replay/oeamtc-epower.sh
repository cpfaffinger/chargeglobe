#!/usr/bin/env bash
# ÖAMTC ePower: POI-Suche "efuel" im gis-Backend, Seiten per count/start.
# OEAMTC_BASIC = kompletter Authorization-Wert ("Basic …"), fest in at.oeamtc.android.smartrouting.SmartRoutingKt (siehe docs/endpunkte/oeamtc-epower.md).
set -euo pipefail
: "${OEAMTC_BASIC:?OEAMTC_BASIC setzen}"
S=${S:-47.05}; N=${N:-47.09}; W=${W:-15.40}; E=${E:-15.47}; COUNT=${COUNT:-100}; START=${START:-0}
curl -sf -m 30 -H "Authorization: $OEAMTC_BASIC" -H "Content-Type: application/json" \
  -d "{\"geographic\":{\"boundingBox\":{\"longitudeMin\":$W,\"longitudeMax\":$E,\"latitudeMin\":$S,\"latitudeMax\":$N}}}" \
  "https://www.oeamtc.at/api/mob/oeamtc/gis-efuel/efuel/search?count=$COUNT&start=$START" |
python3 -I -c '
import json,sys
d=json.load(sys.stdin)
print(d["header"])
for r in d["results"][:5]:
    x=r["data"]; a=x["addresses"][0]
    print(x["header"].get("chargePointOperator"),a["street"],a["city"],[(c["id"],c["capacityKw"],c["cost"],c["availability"]) for c in x["chargingPoints"]][:2],x.get("prices"))
'
