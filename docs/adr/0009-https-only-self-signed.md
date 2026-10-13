# ADR-0009: HTTPS-only am exponierten Port mit self-signed Zertifikat

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Browser im HTTPS-only-Modus (z. B. Firefox) lassen sich gegen einen reinen HTTP-Port nicht sinnvoll zum Debuggen verwenden, und Browser-Funktionen wie die Geolocation-API ([ADR-0007](0007-karte-standort-routing.md)) setzen einen sicheren Kontext voraus. Das öffentlich gültige Zertifikat stellt der vorgelagerte Loadbalancer (nginx, außerhalb dieses Repos) bereit.

## Entscheidung
1. **Der exponierte Port (`CHARGEGLOBE_PORT`, Default `5005`) spricht ausschließlich HTTPS.** Ein HTTP-Request auf diesen Port wird per `301` auf `https://` umgeleitet; einen separaten HTTP-Port gibt es nicht.
2. **Das Zertifikat ist self-signed und bleibt es dauerhaft.** Der `web`-Container erzeugt es beim ersten Start selbst (RSA 2048, 10 Jahre gültig, SAN: `localhost`, `127.0.0.1` und der Hostname aus `PUBLIC_BASE_URL`) und legt es im Volume `tls` ab, damit eine im Browser gespeicherte Ausnahme Rebuilds übersteht. Wird das Volume gelöscht, entsteht ein neues Zertifikat.
3. **TLS endet im `web`-Container.** Innerhalb des Compose-Netzes spricht nginx weiter HTTP mit `api:8080`; `api`, `crawler`, `worker` und `db` veröffentlichen keine Ports.
4. **Öffentlich gültige Zertifikate, Let's Encrypt und Caddy bleiben außerhalb der App.** Der Loadbalancer terminiert TLS für Endnutzer und spricht den App-Port per HTTPS an, ohne das self-signed Zertifikat zu validieren.

## Alternativen
- HTTP am App-Port und TLS nur im Loadbalancer: Direktzugriff auf den Dev-Port wäre im HTTPS-only-Modus nicht debugbar und kein sicherer Kontext.
- Zertifikatsverwaltung in der App (ACME): doppelt sich mit dem Loadbalancer und braucht Port 80/443 sowie DNS-Kontrolle.
- Eigene CA für die Entwicklung (mkcert): müsste auf jedem Entwicklerrechner installiert werden; die Browser-Ausnahme genügt.

## Konsequenzen
- Direktzugriff zeigt einmalig eine Zertifikatswarnung; `curl` braucht `-k`. Healthcheck: `curl -k https://<host>:5005/api/v1/health`.
- `PUBLIC_BASE_URL` beginnt mit `https://`; ändert sich der Hostname, muss das Zertifikat neu erzeugt werden (Volume `tls` löschen).
- Cookies (Refresh-Token) werden immer mit `Secure` gesetzt.
