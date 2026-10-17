#!/usr/bin/env bash
# EVN Drive: chargeAtlas des e-mobil-Portals, anonym. zoom < ~14 liefert Cluster, ab ~15 Stationen mit Ladepunkten.
set -euo pipefail
S=${S:-47.05}; N=${N:-47.09}; W=${W:-15.40}; E=${E:-15.47}; ZOOM=${ZOOM:-15}
curl -sf -m 30 "https://www.emobilportal.at/api/v1/chargeStations/chargeAtlas?topLeftLat=$N&topLeftLong=$W&bottomRightLat=$S&bottomRightLong=$E&zoom=$ZOOM" |
python3 -I -c '
import json,sys,collections
d=json.load(sys.stdin)
st=[x for x in d if x.get("type")=="charge_station"]
print(len(d),"Einträge,",len(st),"Stationen,",sum(len(x["chargePoints"]) for x in st),"Ladepunkte")
print(collections.Counter(c["status"] for x in st for c in x["chargePoints"]))
for x in st[:5]: print(x["operatorToken"],x["stationNameOriginal"],[(c["evseId"],c["maxPower"],c["status"]) for c in x["chargePoints"]][:2])
'
