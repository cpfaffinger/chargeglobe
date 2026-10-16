import { Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';
import { AppIcon } from '../design/icon';

/** One term of the glossary: what chargeglobe calls it, what it means, an example. */
export interface Term {
  id: string;
  term: string;
  meaning: string;
  example: string;
}

/**
 * The terms of chargeglobe, after the EU regulation AFIR (Art. 2) and OCPI (docs/01, "Begriffe"); from the biggest to
 * the smallest unit.
 */
export const TERMS: Term[] = [
  {
    id: 'park',
    term: 'Ladepark',
    meaning:
      'Mehrere Ladestandorte desselben Betreibers direkt nebeneinander (höchstens 25 m). chargeglobe fasst sie für die Anzeige ' +
      'zusammen, weil die Quellen eine Anlage oft in mehrere Einträge teilen.',
    example: 'Zwei Schnellladesäulen vor einem Bürogebäude, die die E-Control als zwei Stationen meldet.',
  },
  {
    id: 'location',
    term: 'Ladestandort',
    meaning:
      'Ein Ort mit Adresse und Koordinate, so wie ihn eine Quelle meldet; er hat eine oder mehrere Ladestationen. Bei der E-Control ' +
      'heißt er „Station“ und ist mal eine einzelne Säule, mal eine ganze Garage.',
    example: 'Parkgarage Stephansplatz, Wien.',
  },
  {
    id: 'station',
    term: 'Ladestation',
    meaning:
      'Das Gerät, das auf der Straße oder in der Garage steht: eine Säule oder Wallbox mit einem oder mehreren Ladepunkten. ' +
      'chargeglobe kennt sie, wenn die Quelle sie benennt, etwa über die EVSE-ID.',
    example: 'Säule E006566 mit den Ladepunkten AT*MER*E006566*01 und *02.',
  },
  {
    id: 'evse',
    term: 'Ladepunkt',
    meaning:
      'Die Stelle, an der zur selben Zeit genau ein Fahrzeug lädt (EVSE). Er hat die EVSE-ID und den Status (frei, belegt, außer ' +
      'Betrieb). So viele Ladepunkte, so viele Fahrzeuge können gleichzeitig laden.',
    example: 'AT*EVN*E2454*1, 100 kW Gleichstrom.',
  },
  {
    id: 'connector',
    term: 'Anschluss',
    meaning:
      'Ein Stecker oder fest angeschlagenes Kabel eines Ladepunkts, mit Typ und Leistung. Mehrere Anschlüsse an einem Ladepunkt ' +
      'sind Alternativen: Lädt an einem ein Fahrzeug, ist der ganze Ladepunkt belegt.',
    example: 'Typ 2 Buchse 22 kW, CCS Kabel 150 kW, Schuko.',
  },
];

/** The glossary (/help/begriffe), linked from the legend of the map; public. */
@Component({
  imports: [AppIcon, MatButtonModule, RouterLink],
  selector: 'app-help-terms',
  template: `
    <div class="page-actions">
      <a matButton routerLink="/" id="back"><app-icon name="arrow_back" />Zur Karte</a>
    </div>
    <h1>Begriffe</h1>
    <p class="lead">
      So nennt chargeglobe die Teile der Ladeinfrastruktur, vom größten zum kleinsten – nach der EU-Verordnung AFIR und dem
      Standard OCPI.
    </p>
    <dl id="terms">
      @for (term of terms; track term.id) {
        <div class="term" [id]="'term-' + term.id">
          <dt>{{ term.term }}</dt>
          <dd>
            {{ term.meaning }}
            <span class="term-example">Beispiel: {{ term.example }}</span>
          </dd>
        </div>
      }
    </dl>
  `,
})
export class HelpTermsPage {
  protected readonly terms = TERMS;
}
