# Endpunkt-Notiz: <Anbieter> – <Quelle (Web-Karte / App / Export)>

| | |
|---|---|
| Analysiert am | YYYY-MM-DD |
| Analysiert von | |
| Methode | DevTools (Web-Karte) / Traffic-Mitschnitt (eigenes Gerät) / Export-Doku |
| Tier | 0 / 1 / 2 / 3 |
| Zugang | anonym / statischer App-Key / Nutzer-Login / signiert |
| ToS geprüft | Link, Datum, relevante Klauseln |
| Lizenz / Attribution | |
| Rate-Limit beobachtet | z. B. 429 ab ~120 req/min |
| Capabilities | StaticInventory · EvseIds · LiveStatusPerEvse · LiveStatusPerLocation · AdHocPrice · EmpTariff · BulkByBoundingBox · BulkAll · ByEvseId · DeltaSince |

## Endpunkte

### `GET https://…/…`
- Parameter:
- Header:
- Antwort (gekürzt, sanitisiert; vollständiges Beispiel unter `fixtures/<key>/…json`):
```json
{}
```
- Mapping auf Ingest-Modell:
  - `…` → `ProviderEvseSnapshot.EvseId`
  - `…` → `Status` (Tabelle unten)

## Status-Mapping

| Rohwert | OCPI-Status |
|---|---|
| | |

## Stecker-Mapping

| Rohwert | ConnectorStandard |
|---|---|
| | |

## Preisdarstellung
Wie werden Preise geliefert (je Ladepunkt / netzweit, kWh/min/Session/Blockiergebühr, Ad-hoc vs. App-Tarif)?

## Offene Punkte / Risiken
