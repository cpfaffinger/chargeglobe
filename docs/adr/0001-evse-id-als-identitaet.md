# ADR-0001: EVSE-ID als fachliche Identität eines Ladepunkts

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Ladepunkte werden aus vielen Quellen zusammengeführt. Wir brauchen einen Schlüssel, der in möglichst vielen Quellen vorkommt, stabil ist und einzelne Ladepunkte (nicht nur Säulen) unterscheidet. Die Anforderung nennt dafür die „Netzpunkt-ID“, die über mehrere Apps hinweg auffindbar sein soll.

## Entscheidung
- Fachlicher Schlüssel ist die **EVSE-ID** (ISO 15118 / DIN SPEC 91286; Format `<CC>*<OperatorID>*E<OutletID>`), normalisiert ohne Separatoren in `evse.evse_id_norm` (UNIQUE), Anzeigeform in `evse_id_display`.
- Technischer Schlüssel bleibt eine UUID, damit Umbenennungen/Korrekturen möglich sind.
- Ladepunkte ohne bekannte EVSE-ID erhalten eine Surrogat-ID (`identity_kind=SURROGATE`) und werden per Heuristik nachträglich mit einer echten EVSE-ID verschmolzen.
- „Netzpunkt-/Zählpunkt-ID“ (Netzbetreiber-Kennung des Anschlusses) wird nur als optionales Attribut geführt, falls eine Quelle sie liefert.

## Konsequenzen
- Quellen mit EVSE-IDs (amtliche Verzeichnisse, OCPI/OICP-Feeds, die meisten EMP-Apps) matchen trivial und zuverlässig.
- Quellen ohne EVSE-IDs (Tesla-App, OSM, Teile von OCM) brauchen Geo/Operator-Heuristiken und eine Review-Queue.
- Die Normalisierung ohne Separatoren kann theoretisch kollidieren; Kollisionen werden erkannt und als Konflikt behandelt, nicht gemerged.
