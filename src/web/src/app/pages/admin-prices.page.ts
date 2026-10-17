import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { ImplausiblePrices, PriceQualityService, PriceSample, sampleText } from '../prices/price-quality.service';

/**
 * Errors in the ad-hoc prices of the register (jobs:read): per operator the active EVSEs whose newest ad-hoc price lies
 * outside price.min_plausible_kwh–price.max_plausible_kwh or charges nothing without being free of charge, with the
 * most frequent of these prices. The prices themselves come from the sources; the page only shows them.
 */
@Component({
  imports: [AppIcon, AppLoading, DatePipe, DecimalPipe, MatButtonModule],
  selector: 'app-admin-prices',
  template: `
    <h1>Ad-hoc-Preise</h1>
    <p class="muted">
      Ladepunkte, deren Ad-hoc-Preis laut Register vermutlich falsch ist: unter oder über der Spanne der Einstellungen
      <code>price.min_plausible_kwh</code> und <code>price.max_plausible_kwh</code> oder ganz ohne Preis, obwohl nicht kostenlos.
      Diese Preise zeigt die Karte als „vermutlich falsch“ und lässt sie im Preisvergleich weg. Korrigieren kann sie nur die
      Quelle (bei der E-Control der Betreiber).
    </p>
    <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loading()) {
      <app-loading id="prices-loading" text="Preise werden geprüft …" />
    }
    @if (result(); as data) {
      <p id="prices-summary">
        Spanne {{ data.minKwh | number: '1.2-3' : 'de-DE' }}–{{ data.maxKwh | number: '1.2-2' : 'de-DE' }} €/kWh:
        <strong>{{ data.evses }}</strong> {{ data.evses === 1 ? 'Ladepunkt' : 'Ladepunkte' }} bei {{ data.operators.length }}
        {{ data.operators.length === 1 ? 'Betreiber' : 'Betreibern' }} · Stand {{ data.at | date: 'dd.MM.yyyy HH:mm' }}
      </p>
      @if (data.operators.length > 0) {
        <div class="table-box">
          <table class="data-table" id="implausible">
            <thead>
              <tr><th scope="col">Betreiber</th><th scope="col">Ladepunkte</th><th scope="col">Preise laut Register</th><th scope="col">zuletzt gemeldet</th></tr>
            </thead>
            <tbody>
              @for (operator of data.operators; track operator.operatorId) {
                <tr [attr.data-operator]="operator.operatorId">
                  <td>
                    {{ operator.operatorId ?? 'unbekannt' }}
                    @if (operator.name) {
                      · {{ operator.name }}
                    }
                  </td>
                  <td>{{ operator.evses }}</td>
                  <td>
                    @for (sample of operator.examples; track $index) {
                      <div>{{ describe(sample) }} ({{ sample.evses }})</div>
                    }
                  </td>
                  <td>{{ operator.lastObservedAt | date: 'dd.MM.yyyy HH:mm' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <p id="no-implausible">Keine unplausiblen Ad-hoc-Preise.</p>
      }
    }
  `,
})
export class AdminPricesPage {
  private readonly service = inject(PriceQualityService);

  protected readonly result = signal<ImplausiblePrices | null>(null);
  protected readonly loading = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void this.load();
  }

  protected load(): Promise<void> {
    return attempt(this.loading, this.errors, async () => this.result.set(await this.service.implausible()));
  }

  protected describe(sample: PriceSample): string {
    return sampleText(sample);
  }
}
