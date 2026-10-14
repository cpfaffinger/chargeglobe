# ADR-0012: Design-System und Theming (Angular Material 3, Light/Dark)

**Status:** akzeptiert · **Datum:** 2026-10-14

## Kontext
Die Web-App hatte kein Design: ein Stylesheet mit Systemschrift, nur hell, eigene Formular- und Tabellenstile. Das Design soll auf Angular aufbauen und dessen Möglichkeiten nutzen statt eigener Bausteine, Light und Dark Mode unterstützen, barrierearm sein und das Initial-Bundle der Karte nicht unnötig vergrößern. Der klickbare Entwurf (Richtungen Volt, Donau, Graphit; Schriften Roboto Flex, Atkinson Hyperlegible, Figtree) wurde vom Betreiber freigegeben: **Volt mit Roboto Flex**.

## Entscheidung
1. **Angular Material 3 und Angular CDK** (`@angular/material`, `@angular/cdk`, Version passend zu Angular 22) sind das Design-System. Eigene Komponenten nur, wo Material nichts bietet (Karte, Status-Timeline, Heatmap); sie verwenden dieselben Tokens.
2. **Theme** in `src/web/src/styles.scss` über `mat.theme`:
   - Palette „Volt“: Primär `#006A60`, Tertiär `#7C5800`, erzeugt mit `ng generate @angular/material:theme-color` nach `src/web/src/_theme-colors.scss` (bei einem Farbwechsel neu erzeugen, nicht von Hand ändern).
   - Schrift Roboto Flex (variabel), selbst gehostet über das npm-Paket `@fontsource-variable/roboto-flex` (OFL-1.1); der Build legt die `woff2`-Dateien unter `media/` ab, es gibt keine Anfragen an Font-CDNs.
   - `theme-type: color-scheme`: Material erzeugt jede Farbrolle als `light-dark()`-Paar. Eigene Stile verwenden ausschließlich `--mat-sys-*`-Tokens und die eigenen `--cg-*`-Tokens, keine fest verdrahteten Farben.
   - Eigene Tokens für den Ladestatus (`--cg-status-available`, `--cg-status-occupied`, `--cg-status-out-of-order`, `--cg-status-unknown`, `--cg-on-status`), ebenfalls als `light-dark()`-Paare (hell `#1B7F3A` / `#B45309` / `#B3261E` / `#6F7976` mit weißem Symbol, dunkel `#6DD58C` / `#FFB870` / `#FFB4AB` / `#9AA39F` mit Symbol `#0F1416`). Die Heatmap mischt die Primärfarbe per `color-mix()` mit 8 % bis 100 % Deckkraft; ohne Daten `--mat-sys-surface-container-highest`.
3. **Light und Dark Mode**: `html { color-scheme: light dark }` folgt dem System. Der `ThemeService` (Signal `system | light | dark`) setzt `color-scheme` am Dokument oder entfernt es und merkt sich die Wahl im `localStorage` (Zugriff in `try/catch`; ohne Speicher gilt die Wahl bis zum Schließen der Seite). Umschalter: Menü in der Toolbar, auf kleinen Bildschirmen zusätzlich ein Button-Toggle im Seitenmenü. Später wird die Wahl ein Customizing im Benutzerprofil (`/me/preferences`).
4. **Karte im selben Theme**: Die Einstellung `map.style_url_dark` (Default `https://tiles.openfreemap.org/styles/dark`, leer = `map.style_url`) liefert über `GET /map/config` den dunklen Kartenstil. Weil der WebGL-Renderer kein CSS liest, löst `resolveColors()` (`map/availability.ts`) die Tokens über ein Probe-Element in echte Farben des aktuellen Modus auf; beim Wechsel ruft die Kartenseite `MapHandle.setTheme()` auf, die Engine lädt den Stil neu und zeichnet die Marker (Kreis in Statusfarbe mit Symbol) neu.
5. **Icons**: die benötigten Material Symbols (Apache-2.0) als eingebettete SVG-Pfade (`design/icon.ts`, Komponente `<app-icon>`), keine Icon-Schrift. Das spart mehrere Megabyte, braucht keine Anfrage und zeigt nie Ligatur-Text vor dem Laden der Schrift; Stepper-Icons werden über `matStepperIcon` ersetzt.
6. **Layout**: `mat-toolbar` mit Navigation, unter 840 px (Material-Fensterklasse „compact/medium“, `Viewport.mobile` über den CDK-`BreakpointObserver`) ein Menü-Button mit `mat-sidenav`. Formulare mit `mat-form-field` (Outline, Default über `MAT_FORM_FIELD_DEFAULT_OPTIONS`), Client-Validierung mit `mat-error`, Serverfehler als Hinweis mit `role="alert"`. Infokarte der Karte als `mat-card`, auf kleinen Bildschirmen als Bottom-Sheet am unteren Rand. Setup-Assistent als linearer `mat-stepper` (horizontal ab 840 px, sonst vertikal), Admin-Einstellungen als `mat-expansion-panel` je Schlüsselgruppe mit Suche, `mat-slide-toggle` für Bool-Werte, `MatSnackBar` für „gespeichert“, Postausgang als `mat-table` mit `mat-paginator` (deutsche Texte über `GermanPaginatorIntl`).
7. **Barrierefreiheit**: Textkontraste nach WCAG AA in beiden Modi (die Material-Rollen sind darauf ausgelegt, die Statusfarben sind dagegen geprüft), sichtbarer Fokus (`mat.strong-focus-indicators`, `:focus-visible`), Touch-Ziele mindestens 44 px, ein Status nie nur über die Farbe (Symbol und Text).
8. **Bundle**: Nur die Karte ist Teil des Initial-Bundles; alle anderen Seiten laden per `loadComponent`, damit Stepper, Tabelle, Akkordeon und Chips in eigenen Chunks liegen. Das Initial-Budget in `angular.json` liegt bei 900 kB Warnung / 1,2 MB Fehler (gemessen 806 kB roh, rund 173 kB übertragen, davon Toolbar, Sidenav, Menü, Karte und Card); `maplibre-gl` bleibt ein eigener Lazy-Chunk.

## Alternativen
- Eigenes Design-System aus CSS-Variablen ohne Material: kleiner, aber jede Komponente (Stepper, Tabelle, Menüs, Fokus- und Tastaturverhalten) wäre selbst zu bauen und barrierefrei zu halten.
- Andere Bibliotheken (PrimeNG, Taiga UI, Tailwind): nicht aus dem Angular-Team, eigenes Theming-Modell neben Angular.
- Material Symbols als variable Schrift: mehrere Megabyte, Ligatur-Text während des Ladens.

## Konsequenzen
- Neue Oberflächen verwenden Material-Komponenten und Tokens; fest kodierte Farben sind ein Review-Fehler.
- Ein Wechsel der Farbrichtung bedeutet: Palette neu erzeugen, Statusfarben gegen beide Modi prüfen, Screenshots aktualisieren.
- Tests der Seiten simulieren die Fensterbreite über einen `Viewport`-Double; `styles.scss` ist auch im Karma-Lauf eingebunden, damit Farbauflösung und Modus geprüft werden können.
