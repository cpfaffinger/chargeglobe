# ADR-0004: OCPI 2.2.1 als Vokabular des kanonischen Modells

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Quellen liefern Status, Steckertypen und Tarife in unterschiedlichen Vokabularen. Wir brauchen ein internes Modell, das verlustfrei genug ist und zu dem bestehende Standards leicht gemappt werden können.

## Entscheidung
Das kanonische Modell übernimmt aus OCPI 2.2.1: die Hierarchie Location → EVSE → Connector, die Enums `Status`, `ConnectorType`, `ConnectorFormat`, `PowerType`, sowie das Tarifmodell (`Tariff` → `TariffElement` → `PriceComponent` + `TariffRestrictions`). Erweiterungen (Provenienz, Surrogat-IDs, Beobachtungszeitreihen, `ChargingStation`-Ebene) sind additiv.

## Konsequenzen
- OCPI-, OICP- und DATEX-II-Quellen lassen sich mit dokumentierten Mappings abbilden (OICP `EvseStatus` → OCPI `Status`, DATEX II `EnergyInfrastructureStatus` → OCPI `Status`).
- Die öffentliche API kann perspektivisch einen OCPI-kompatiblen Read-Only-Export anbieten.
- Das Tarifmodell ist komplex; für Quellen, die nur Einzelpreise zeigen, gibt es die flache `price_observation` als Ergänzung, nicht als Ersatz.
