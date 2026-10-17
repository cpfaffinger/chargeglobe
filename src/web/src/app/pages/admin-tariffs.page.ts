import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { money } from '../evses/prices';
import { Emp, Network, PriceElement, Tariff, TariffsService, ZONE_LABELS, Zone } from '../tariffs/tariffs.service';

const CONFIDENCE: Record<Tariff['confidence'], string> = { HIGH: 'aus dem Preisblatt', MEDIUM: 'abgeleitet', LOW: 'unsicher' };

/**
 * The tariff catalog (tariff:write): per provider its current tariffs with monthly fee, source and "Stand", and per zone
 * which networks it covers and what AC and DC (by power stage) cost. The data comes from the tariff sources
 * (docs/adr/0013); the page shows it, changes go through the catalog files or importTariffCatalog.
 */
@Component({
  imports: [AppIcon, AppLoading, DatePipe, MatButtonModule, MatExpansionModule],
  selector: 'app-admin-tariffs',
  template: `
    <h1>Tarife</h1>
    <p class="muted">
      Ladekarten und Apps mit ihren Tarifen. Jeder Ladepunkt fällt je Tarif in eine Zone – Sonderpreis seines Betreibers,
      Heimnetz, Partnernetz oder Roaming – nach dem Betreiberteil seiner EVSE-ID. Gepflegt wird der Katalog als Datei je Anbieter
      im Repository (<code>src/ChargeGlobe.Providers.TariffCatalog/catalog</code>) oder über
      <code>PUT /api/v1/tariff-sources/&#123;source&#125;/catalog</code>.
    </p>
    <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loading()) {
      <app-loading id="tariffs-loading" text="Tarife werden geladen …" />
    }
    <mat-accordion multi>
      @for (emp of emps(); track emp.key) {
        <mat-expansion-panel class="emp" [attr.data-emp]="emp.key">
          <mat-expansion-panel-header>
            <mat-panel-title>{{ emp.name }}</mat-panel-title>
            <mat-panel-description>{{ emp.tariffs === 1 ? '1 Tarif' : emp.tariffs + ' Tarife' }} · {{ emp.country }}</mat-panel-description>
          </mat-expansion-panel-header>
          @for (tariff of tariffsOf(emp); track tariff.id) {
            <section class="tariff" [attr.data-tariff]="tariff.key">
              <h2>{{ tariff.name }}</h2>
              <p class="tariff-meta">
                {{ tariff.monthlyFee ? 'Grundgebühr ' + amount(tariff.monthlyFee, tariff.currency) + '/Monat' : 'ohne Grundgebühr' }}
                @if (tariff.prerequisites) {
                  · {{ tariff.prerequisites }}
                }
                · Stand {{ tariff.sourceDate ? (tariff.sourceDate | date: 'dd.MM.yyyy') : 'unbekannt' }} ({{ confidence(tariff) }}) ·
                <a [href]="tariff.sourceUrl" target="_blank" rel="noopener">Quelle</a>
              </p>
              <div class="table-box">
                <table class="data-table zones">
                  <thead>
                    <tr><th scope="col">Zone</th><th scope="col">gilt für</th><th scope="col">AC</th><th scope="col">DC</th></tr>
                  </thead>
                  <tbody>
                    @for (zone of tariff.zones; track $index) {
                      <tr>
                        <td>{{ zoneLabel(zone) }}</td>
                        <td class="covers">{{ covers(zone) }}</td>
                        <td>{{ pricesOf(zone, 'AC', tariff.currency) }}</td>
                        <td>{{ pricesOf(zone, 'DC', tariff.currency) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (tariff.notes) {
                <p class="muted">{{ tariff.notes }}</p>
              }
            </section>
          }
        </mat-expansion-panel>
      } @empty {
        @if (!loading()) {
          <p id="no-emps">Noch keine Tarife im Katalog.</p>
        }
      }
    </mat-accordion>
  `,
})
export class AdminTariffsPage {
  private readonly service = inject(TariffsService);

  protected readonly emps = signal<Emp[]>([]);
  protected readonly tariffs = signal<Tariff[]>([]);
  protected readonly networks = signal<Network[]>([]);
  protected readonly loading = signal(false);
  protected readonly errors = signal<string[]>([]);
  private readonly networkNames = computed(() => new Map(this.networks().map((network) => [network.key, network.name])));

  constructor() {
    void this.load();
  }

  protected load(): Promise<void> {
    return attempt(this.loading, this.errors, async () => {
      const [emps, tariffs, networks] = await Promise.all([this.service.emps(), this.service.tariffs(), this.service.networks()]);
      this.emps.set(emps);
      this.tariffs.set(tariffs);
      this.networks.set(networks);
    });
  }

  protected tariffsOf(emp: Emp): Tariff[] {
    return this.tariffs().filter((tariff) => tariff.emp === emp.key);
  }

  protected amount(value: number, currency: string): string {
    return money(value, currency);
  }

  protected confidence(tariff: Tariff): string {
    return CONFIDENCE[tariff.confidence];
  }

  protected zoneLabel(zone: Zone): string {
    return ZONE_LABELS[zone.kind];
  }

  /** The networks and operators of the zone; for roaming without them "alle mit Roaming" and what is left out. */
  protected covers(zone: Zone): string {
    const names = [...zone.networks.map((key) => this.networkNames().get(key) ?? key), ...zone.operators];
    const covered = names.length > 0 ? names.join(', ') : 'alle Betreiber mit Fremdkarten-Annahme';
    const excluded = [...zone.excludedNetworks.map((key) => this.networkNames().get(key) ?? key), ...zone.excludedOperators];
    return excluded.length > 0 ? `${covered} (außer ${excluded.join(', ')})` : covered;
  }

  /** The elements of one current, e.g. "0,59 €/kWh · ab 150 kW 0,79 €/kWh"; "–" without. */
  protected pricesOf(zone: Zone, current: PriceElement['current'], currency: string): string {
    const parts = zone.prices
      .filter((price) => price.current === current)
      .map((price) => {
        const pieces = [
          price.pricePerKwh !== null ? `${money(price.pricePerKwh, currency)}/kWh` : null,
          price.pricePerMinute ? `${money(price.pricePerMinute, currency)}/min` : null,
          price.sessionFee ? `Start ${money(price.sessionFee, currency)}` : null,
          price.blockingFeePerMinute
            ? `Blockieren ${money(price.blockingFeePerMinute, currency)}/min${price.blockingAfterMinutes !== null ? ` ab Min. ${price.blockingAfterMinutes}` : ''}`
            : null,
        ].filter((piece) => piece !== null);
        return `${price.minKw ? `ab ${price.minKw} kW ` : ''}${pieces.join(', ')}`;
      });
    return parts.length > 0 ? parts.join(' · ') : '–';
  }
}
