import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { ImplausiblePrices, OperatorImplausiblePrices, PriceQualityService, PriceSample, mailDraft, sampleText } from '../prices/price-quality.service';

/**
 * Errors in the ad-hoc prices of the register (jobs:read): per operator the active EVSEs whose newest ad-hoc price lies
 * outside price.min_plausible_kwh–price.max_plausible_kwh or charges nothing without being free of charge, with the
 * most frequent of these prices. The prices themselves come from the sources; the page only shows them.
 */
@Component({
  imports: [AppIcon, AppLoading, DatePipe, MatButtonModule],
  selector: 'app-admin-prices',
  template: `
    <h1>Ad-hoc-Preise</h1>
    <p class="muted">
      Ladepunkte, deren Ad-hoc-Preis laut Register vermutlich falsch ist: unter oder über der Spanne der Einstellungen
      <code>price.min_plausible_kwh</code> und <code>price.max_plausible_kwh</code> oder ganz ohne Preis, obwohl nicht kostenlos.
      Diese Preise zeigt die Karte als „vermutlich falsch“ bzw. „kein Preis gemeldet“ und lässt sie im Preisvergleich weg.
      Korrigieren kann sie nur die Quelle (bei der E-Control der Betreiber); die CSV-Datei listet jeden Ladepunkt für die Meldung, der
      Mail-Entwurf je Betreiber lässt sich kopieren – die App verschickt nichts.
    </p>
    <div class="tariff-tools">
      <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
      @if (canExport) {
        <button matButton type="button" id="export" [disabled]="loading()" (click)="download()"><app-icon name="download" />Als CSV für die Meldung</button>
      }
    </div>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loading()) {
      <app-loading id="prices-loading" text="Preise werden geprüft …" />
    }
    @if (result(); as data) {
      <p id="prices-summary">
        Spanne {{ amount(data.minKwh) }}–{{ amount(data.maxKwh) }} €/kWh:
        <strong>{{ data.evses }}</strong> {{ data.evses === 1 ? 'Ladepunkt' : 'Ladepunkte' }} bei {{ data.operators.length }}
        {{ data.operators.length === 1 ? 'Betreiber' : 'Betreibern' }} · Stand {{ data.at | date: 'dd.MM.yyyy HH:mm' }}
      </p>
      <p id="prices-reasons" class="muted">
        kein Preis gemeldet: {{ data.noAmount }} · unter der Spanne: {{ data.belowMin }} · über der Spanne: {{ data.aboveMax }}
      </p>
      @if (data.operators.length > 0) {
        <div class="table-box">
          <table class="data-table" id="implausible">
            <thead>
              <tr>
                <th scope="col">Betreiber</th><th scope="col">Ladepunkte</th><th scope="col">Preise laut Register</th><th scope="col">zuletzt gemeldet</th>
                <th scope="col"><span class="cdk-visually-hidden">Meldung</span></th>
              </tr>
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
                  <td>
                    <button matButton type="button" class="draft-toggle" (click)="toggleDraft(operator)">
                      {{ draftOf() === operator.operatorId ? 'Entwurf schließen' : 'Mail-Entwurf' }}
                    </button>
                  </td>
                </tr>
                @if (draftOf() === operator.operatorId) {
                  <tr class="draft-row">
                    <td colspan="5">
                      <textarea class="mail-draft" readonly rows="14" [value]="draft(operator, data.at)"></textarea>
                      <button matButton type="button" class="copy-draft" (click)="copy(operator, data.at)">Kopieren</button>
                      @if (copied()) {
                        <span class="muted copied">kopiert</span>
                      }
                    </td>
                  </tr>
                }
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

  /** The CSV report needs export:read besides jobs:read. */
  protected readonly canExport = inject(AuthService).hasPermission('export:read');

  protected readonly result = signal<ImplausiblePrices | null>(null);
  /** The operator whose mail draft is open; the draft is copied, never sent. */
  protected readonly draftOf = signal<string | null | undefined>(undefined);
  protected readonly copied = signal(false);
  protected readonly loading = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void this.load();
  }

  protected load(): Promise<void> {
    return attempt(this.loading, this.errors, async () => this.result.set(await this.service.implausible()));
  }

  /** Saves the CSV report as implausible-prices.csv. */
  protected download(): Promise<void> {
    return attempt(this.loading, this.errors, async () => {
      const url = URL.createObjectURL(await this.service.export());
      const link = document.createElement('a');
      link.href = url;
      link.download = 'implausible-prices.csv';
      link.click();
      URL.revokeObjectURL(url);
    });
  }

  protected toggleDraft(operator: OperatorImplausiblePrices): void {
    this.copied.set(false);
    this.draftOf.update((open) => (open === operator.operatorId ? undefined : operator.operatorId));
  }

  protected draft(operator: OperatorImplausiblePrices, at: string): string {
    return mailDraft(operator, at);
  }

  protected async copy(operator: OperatorImplausiblePrices, at: string): Promise<void> {
    await navigator.clipboard.writeText(mailDraft(operator, at));
    this.copied.set(true);
  }

  protected amount(value: number): string {
    return new Intl.NumberFormat('de-DE', { minimumFractionDigits: 2, maximumFractionDigits: 3 }).format(value);
  }

  protected describe(sample: PriceSample): string {
    return sampleText(sample);
  }
}
