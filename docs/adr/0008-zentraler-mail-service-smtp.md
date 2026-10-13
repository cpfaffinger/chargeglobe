# ADR-0008: Zentraler Mail-Service mit konfigurierbarem SMTP-Versand

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Benutzerkonten mit Selbstregistrierung ([ADR-0005](0005-identity-rbac-openapi.md)) brauchen E-Mails (Adresse bestätigen, Passwort zurücksetzen); später kommen Benachrichtigungen dazu. Der Versand soll von jeder Stelle der App aus auf dieselbe Weise aufrufbar sein, und der Betreiber muss den SMTP-Server frei wählen und ohne Neustart ändern können.

## Entscheidung
1. **Ein Port für alle Aufrufer:** `IEmailSender.QueueAsync(EmailMessage)` in `ChargeGlobe.Application`. Api, Crawler und Worker rufen ausschließlich diesen Port auf; niemand spricht SMTP direkt.
2. **Outbox statt Direktversand:** `QueueAsync` schreibt in die Tabelle `email_outbox` (Empfänger, Betreff, Text- und HTML-Körper, `template_key`, Status `QUEUED | SENT | FAILED`, `attempts`, `last_error`, `created_at`, `sent_at`) in derselben Transaktion wie der auslösende Use Case. Die Rolle `worker` versendet per `FOR UPDATE SKIP LOCKED`, mit Backoff und begrenzter Zahl an Versuchen. Ein nicht erreichbarer SMTP-Server blockiert damit keinen Request und verliert keine Mail.
3. **SMTP-Versand mit MailKit** (MIT) in `ChargeGlobe.Infrastructure`.
4. **Konfiguration im Config-Service** ([ADR-0006](0006-konfiguration-env-minimal-config-service.md)), nicht in `.env`, änderbar über `PUT /settings/{key}` und die Admin-Seite `/admin/settings`, wirksam ohne Neustart. Die Keys stehen im Katalog (`SettingsCatalog`):

   | Key | Typ | Default | Bedeutung |
   |---|---|---|---|
   | `mail.enabled` | bool | `false` | Versand aktiv; solange `false`, bleiben Mails in der Outbox |
   | `mail.smtp.host` | string | – | SMTP-Server |
   | `mail.smtp.port` | int | `587` | Port |
   | `mail.smtp.security` | enum | `starttls` | `plain` (unverschlüsselt), `starttls`, `ssl` (implizites TLS) |
   | `mail.smtp.username` | string | – | Benutzer; leer = ohne Authentifizierung |
   | `mail.smtp.password` | secret | – | verschlüsselt gespeichert (Data-Protection), in der API maskiert |
   | `mail.from.address` | string | – | Absenderadresse |
   | `mail.from.name` | string | `chargeglobe` | Anzeigename des Absenders |

5. **Testversand:** `POST /mail/test` (`settings:manage`) sendet eine Testmail an eine angegebene Adresse und liefert das SMTP-Ergebnis als Problem Details bzw. Erfolg zurück.
6. **Vorlagen im Code** (Text + HTML, de/en), adressiert über `template_key`; Links entstehen aus `PUBLIC_BASE_URL`.

## Alternativen
- SMTP-Zugang in `.env`: widerspricht ADR-0006, Passwort im Klartext auf der Platte, Neustart bei jeder Änderung.
- Transaktionsmail-Dienst per HTTP-API (SendGrid, Postmark, SES): Anbieterbindung; solche Dienste bieten ohnehin SMTP-Zugänge und bleiben damit nutzbar.
- Synchroner Versand im Request: einfacher, aber Registrierung und Passwort-Reset hingen an der Erreichbarkeit des Mailservers.

## Konsequenzen
- Bis SMTP konfiguriert und `mail.enabled` gesetzt ist, werden Mails nur gesammelt (Status `QUEUED`). Für diese Zeit bestätigt `AUTH_REQUIRE_CONFIRMED_EMAIL=false` ([ADR-0005](0005-identity-rbac-openapi.md)) neue Konten sofort; ein Passwort-Reset per Mail ist dann nicht möglich.
- Mail-Körper enthalten Tokens und werden nie geloggt.
- `email_outbox` enthält personenbezogene Daten; versendete Einträge werden nach 30 Tagen gelöscht (Retention-Job des Workers).
- Zustellstatus und Fehler sind für Admins einsehbar (`GET /mail/outbox`, `settings:manage`).
