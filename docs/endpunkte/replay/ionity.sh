#!/usr/bin/env bash
# IONITY: statische Standortliste (ganz Europa, anonym) und Details mit Ad-hoc-Preis je Stecker.
# Ohne die zwei x-adhoc-Header antwortet der BFF mit 400 "Platform or App Feature Version missing".
set -euo pipefail
S=${S:-46.90}; N=${N:-47.30}; W=${W:-15.20}; E=${E:-15.70}
H=(-H "x-adhoc-platform: ANDROID_V2" -H "x-adhoc-app-feature-version: v2.500.1")
B=https://adhoc-bff.ionity.cloud/api
curl -sf -m 60 --compressed "${H[@]}" "$B/v1/location/static" | S=$S N=$N W=$W E=$E python3 -I -c '
import json,sys,os
S,N,W,E=(float(os.environ[k]) for k in "SNWE")
d=json.load(sys.stdin)["locations"]
hit=[l for l in d if S<=float(l["latitude"])<=N and W<=float(l["longitude"])<=E]
print(len(d),"Standorte gesamt,",len(hit),"im Ausschnitt")
for l in hit: print(l["uuid"],l["name"])
' | tee /dev/stderr | awk 'NR==2{print $1}' | {
  read -r UUID || exit 0
  [ -n "$UUID" ] && curl -sf -m 30 --compressed "${H[@]}" "$B/v3/location/$UUID" | python3 -I -c '
import json,sys
d=json.load(sys.stdin)
print(d["name"],d["address"],d["city"])
for c in d["connectors"]: print(c["physicalReference"],c["type"],c["maxPower"]//1000,"kW",c["status"],c["adhocPrice"]["amount"],c["adhocPrice"]["unit"])
'
}
