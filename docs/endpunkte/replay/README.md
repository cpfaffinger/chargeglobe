# Replay-Skripte der App-Analysen

Je Skript ein Abruf, wie ihn die jeweilige App ausführt, für einen Kartenausschnitt in Graz (Standard) oder frei wählbar. Alle Skripte brauchen nur `curl` und `python3`.

| Skript | App | Zugang | Aufruf |
|---|---|---|---|
| `evn-drive.sh` | EVN Drive | anonym | `./evn-drive.sh` |
| `ionity.sh` | IONITY | anonym, zwei Pflicht-Header | `./ionity.sh` |
| `vgo-smatrics.sh` | Verbund V-Go (SMATRICS) | statischer App-Key | `VGO_POI_KEY=… ./vgo-smatrics.sh` |
| `oeamtc-epower.sh` | ÖAMTC ePower | statischer Basic-Client der App | `OEAMTC_BASIC="Basic …" ./oeamtc-epower.sh` |

Die Schlüssel der Apps stehen **nie** im Repo (CLAUDE.md Regel 6). Sie stehen in der dekompilierten App (Fundstellen in der jeweiligen Endpunkt-Notiz) und werden per Umgebungsvariable übergeben.

Ausschnitt ändern: `S`, `N`, `W`, `E` (Süd/Nord/West/Ost in Grad) vor dem Aufruf setzen, z. B. `S=48.10 N=48.30 W=15.50 E=15.75 ./evn-drive.sh` (Raum St. Pölten).

Electroverse hat kein Skript: Der Kartenabruf setzt ein Benutzerkonto voraus ([electroverse.md](../electroverse.md)).
