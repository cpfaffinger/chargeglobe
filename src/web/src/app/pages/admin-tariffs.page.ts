import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { money } from '../evses/prices';
import { Emp, Network, PriceElement, Tariff, TariffsService, ZONE_LABELS, Zone } from '../tariffs/tariffs.service';

const WEEKDAYS = ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So'];

const CONFIDENCE: Record<Tariff['confidence'], string> = { HIGH: 'aus dem Preisblatt', MEDIUM: 'abgeleitet', LOW: 'unsicher' };

/** Order of providers and tariffs: by name, or the longest unchecked first (by the newer of "Stand" and last check). */
type TariffOrder = 'name' | 'checked';

/** The day the values were last confirmed: the newer of "Stand" and last check ("" if neither, so it sorts first). */
function lastSeen(tariff: Tariff): string {
  return [tariff.sourceDate ?? '', tariff.checkedOn ?? ''].sort().at(-1)!;
}

/**
 * The tariff catalog (tariff:write): per provider its current tariffs with monthly fee, source and "Stand", and per zone
 * which networks it covers and what AC and DC (by power stage) cost. The data comes from the tariff sources
 * (docs/adr/0013); the page shows it, changes go through the catalog files or importTariffCatalog.
 */
@Component({
  imports: [AppIcon, AppLoading, DatePipe, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatExpansionModule],
  selector: 'app-admin-tariffs',
  template: `
    <h1>Tarife</h1>
    <p class="muted">
      Ladekarten und Apps mit ihren Tarifen. Jeder Ladepunkt fällt je Tarif in eine Zone – Sonderpreis seines Betreibers,
      Heimnetz, Partnernetz oder Roaming – nach dem Betreiberteil seiner EVSE-ID. Gepflegt wird der Katalog als Datei je Anbieter
      im Repository (<code>src/ChargeGlobe.Providers.TariffCatalog/catalog</code>) oder über
      <code>PUT /api/v1/tariff-sources/&#123;source&#125;/catalog</code>.
    </p>
    @if (staleCount() > 0) {
      <p id="stale-summary" class="stale-summary">
        {{ staleCount() === 1 ? '1 Tarif' : staleCount() + ' Tarife' }} mit altem oder unbekanntem Stand – bitte mit dem Preisblatt des
        Anbieters vergleichen.
      </p>
    }
    <div class="tariff-tools">
      <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
      <mat-checkbox id="only-stale" [checked]="onlyStale()" (change)="onlyStale.set($event.checked)">Nur „Stand prüfen“</mat-checkbox>
      <mat-button-toggle-group id="order" aria-label="Sortierung" hideSingleSelectionIndicator [value]="order()" (change)="order.set($event.value)">
        <mat-button-toggle value="name">nach Name</mat-button-toggle>
        <mat-button-toggle value="checked">am längsten ungeprüft zuerst</mat-button-toggle>
      </mat-button-toggle-group>
    </div>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loading()) {
      <app-loading id="tariffs-loading" text="Tarife werden geladen …" />
    }
    <mat-accordion multi>
      @for (emp of shownEmps(); track emp.key) {
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
                · Stand {{ tariff.sourceDate ? (tariff.sourceDate | date: 'dd.MM.yyyy') : 'unbekannt' }} ({{ confidence(tariff) }})
                @if (tariff.checkedOn) {
                  · geprüft {{ tariff.checkedOn | date: 'dd.MM.yyyy' }}
                }
                @if (tariff.stale) {
                  <span class="stale">Stand prüfen</span>
                }
                ·
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
          <p id="no-emps">{{ onlyStale() && emps().length > 0 ? 'Kein Tarif muss geprüft werden.' : 'Noch keine Tarife im Katalog.' }}</p>
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
  protected readonly staleCount = computed(() => this.tariffs().filter((tariff) => tariff.stale).length);
  protected readonly onlyStale = signal(false);
  protected readonly order = signal<TariffOrder>('name');

  /** The tariffs shown: all or only the stale ones, in the chosen order. */
  private readonly shownTariffs = computed(() => {
    const tariffs = this.tariffs().filter((tariff) => !this.onlyStale() || tariff.stale);
    return this.order() === 'checked' ? [...tariffs].sort((a, b) => lastSeen(a).localeCompare(lastSeen(b))) : tariffs;
  });

  /** The providers with a shown tariff; by name, or by their longest unchecked tariff. */
  protected readonly shownEmps = computed(() => {
    const tariffs = this.shownTariffs();
    const emps = this.emps().filter((emp) => tariffs.some((tariff) => tariff.emp === emp.key));
    if (this.order() === 'name') {
      return emps;
    }
    const first = (emp: Emp) => tariffs.findIndex((tariff) => tariff.emp === emp.key);
    return [...emps].sort((a, b) => first(a) - first(b));
  });
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
    return this.shownTariffs().filter((tariff) => tariff.emp === emp.key);
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
    const all = names.length > 0 ? names.join(', ') : 'alle Betreiber mit Fremdkarten-Annahme';
    const limits = [zone.currents?.length ? `nur ${zone.currents.join('/')}` : null, zone.countries?.length ? `nur in ${zone.countries.join(', ')}` : null];
    const shown = limits.filter((limit) => limit !== null);
    const covered = shown.length > 0 ? `${all} – ${shown.join(', ')}` : all;
    const excluded = [...zone.excludedNetworks.map((key) => this.networkNames().get(key) ?? key), ...zone.excludedOperators];
    return excluded.length > 0 ? `${covered} (außer ${excluded.join(', ')})` : covered;
  }

  /** The elements of one current, e.g. "0,59 €/kWh · ab 150 kW 0,79 €/kWh"; "–" without. */
  protected pricesOf(zone: Zone, current: PriceElement['current'], currency: string): string {
    if (zone.pricing === 'UNKNOWN') {
      return 'Preis je Ladepunkt, nicht veröffentlicht';
    }
    if (zone.pricing === 'OPERATOR_AD_HOC') {
      return [
        'Preis des Betreibers',
        zone.markupPercent ? `${zone.markupPercent > 0 ? '+' : '−'}${Math.abs(zone.markupPercent).toLocaleString('de-DE')} %` : null,
        zone.markupSessionFee ? `+ ${money(zone.markupSessionFee, currency)} je Ladevorgang` : null,
      ]
        .filter((part) => part !== null)
        .join(' ');
    }
    const parts = zone.prices
      .filter((price) => price.current === current)
      .map((price) => {
        const pieces = [
          price.pricePerKwh !== null ? `${money(price.pricePerKwh, currency)}/kWh` : null,
          price.pricePerMinute ? `${money(price.pricePerMinute, currency)}/min` : null,
          price.sessionFee ? `Start ${money(price.sessionFee, currency)}` : null,
          price.blockingFeePerMinute
            ? `Blockieren ${money(price.blockingFeePerMinute, currency)}/min` +
              (price.blockingAfterMinutes !== null ? ` ab Min. ${price.blockingAfterMinutes}` : '') +
              (price.blockingFrom === 'CHARGING_END' ? ' nach Ladeende' : '') +
              (price.blockingStartTime ? ` ${price.blockingStartTime}–${price.blockingEndTime} Uhr` : '') +
              (price.maxBlockingMinutes ? ` (max. ${price.maxBlockingMinutes} min)` : '') +
              (price.maxBlockingFee ? ` (max. ${money(price.maxBlockingFee, currency)})` : '')
            : null,
        ].filter((piece) => piece !== null);
        const window = price.startTime ? `${price.startTime}–${price.endTime} Uhr ` : '';
        const days = price.days?.length ? `${price.days.map((day) => WEEKDAYS[day - 1]).join(', ')} ` : '';
        return `${days}${window}${price.minKw ? `ab ${price.minKw} kW ` : ''}${pieces.join(', ')}`;
      });
    return parts.length > 0 ? parts.join(' · ') : '–';
  }
}
