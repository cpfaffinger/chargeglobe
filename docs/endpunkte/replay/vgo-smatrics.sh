#!/usr/bin/env bash
# Verbund V-Go (SMATRICS poi-service): Standorte mit Steckern und Status im Ausschnitt.
# Key: BuildConfig.POI_API_KEY in at.eprovider (siehe docs/endpunkte/vgo-smatrics.md); wird als Bearer gesendet.
set -euo pipefail
: "${VGO_POI_KEY:?VGO_POI_KEY setzen}"
S=${S:-47.05}; N=${N:-47.09}; W=${W:-15.40}; E=${E:-15.47}
curl -sf -m 30 --compressed -H "Authorization: Bearer $VGO_POI_KEY" -H "Content-Type: application/json" \
  -d "{\"latitude_north\":$N,\"latitude_south\":$S,\"longitude_east\":$E,\"longitude_west\":$W}" \
  https://poi-service.smatrics-api.com/b2c/locations/reduced/bounding-box |
python3 -I -c '
import json,sys,collections
d=json.load(sys.stdin)
print(len(d),"Standorte,",sum(len(l["evses"]) for l in d),"EVSEs")
print(collections.Counter(e["status"] for l in d for e in l["evses"]))
for l in d[:5]: print(l["id"],l["operator"]["name"],[(e["evse_id"],e["connectors"][0]["standard"],e["connectors"][0]["power_kw"],e["status"]) for e in l["evses"]][:2])
'
# Gesamtbestand (~2,6 MB gzip, ~29.500 Standorte, ~91.600 EVSEs, Europa): GET https://poi-service.smatrics-api.com/b2c/locations/reduced
