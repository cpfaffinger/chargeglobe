import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { EvseResponse, EvsesService, Occupancy, StatusInterval } from '../evses/evses.service';
import {
  AD_HOC_METHODS,
  attributeLabel,
  authMethodLabel,
  connectorLabel,
  formatLabel,
  powerTypeLabel,
  yesNo,
} from '../evses/labels';
import { Segment, faultSource, sourceName, statusOf, timeline } from '../evses/status';
import { OccupancyHeatmap, StatusTimeline } from '../evses/status-charts';
import { EvsePricesResponse, implausibleText, comparison, money, plausible, priceParts, sessionText } from '../evses/prices';
import { MapLink, ShareResult } from '../map/map-link';
import { CardsStore } from '../preferences/cards.store';

/** Ranges of the status history in days; the API allows up to 90. */
export const HISTORY_DAYS = [1, 7, 30];
/** Ranges of the occupancy matrix in weeks; the API allows up to 12. */
export const OCCUPANCY_WEEKS = [4, 12];
/** Status periods listed below the timeline, newest first. */
export const LISTED_INTERVALS = 20;

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Page of one charge point (/evses/:evseId, evse:read): everything chargeglobe knows about it. Status with source
 * and age, technology (power, current, connectors), payment and access (with ad-hoc payment, roaming, opening
 * times, facilities), the status history of a selectable range with the list of periods, the occupancy of 4 or 12
 * weeks, and the origin of the data (records of the sources, last confirmation of each attribute).
 */
const SHARE_MESSAGES: Record<ShareResult, string | null> = {
  shared: null,
  cancelled: null,
  copied: 'Der Link zu diesem Ladepunkt ist in der Zwischenablage.',
  failed: 'Der Link konnte nicht kopiert werden. Kopiere die Adresse aus der Adresszeile.',
};

@Component({
  imports: [
    AppLoading,
    AppIcon,
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    OccupancyHeatmap,
    PercentPipe,
    RouterLink,
    StatusTimeline,
  ],
  selector: 'app-evse-detail',
  template: `
    <a matButton routerLink="/" id="back"><app-icon name="arrow_back" />Zur Karte</a>
    @if (notice() === 'missing') {
      <p id="evse-notice" class="error">Diesen Ladepunkt gibt es nicht.</p>
    } @else if (notice() === 'failed') {
      <p id="evse-notice" class="error">Der Ladepunkt konnte nicht geladen werden. Bitte versuche es später erneut.</p>
    }
    @if (evse(); as response) {
      @let evse = response.data;
      @let location = evse.location;
      <header class="evse-header">
        <div class="evse-title">
          <h1 id="evse-id">{{ evse.evseId }}</h1>
          <button matButton="tonal" type="button" id="share-evse" (click)="share(evse.evseId)"><app-icon name="share" />Teilen</button>
        </div>
        @if (shareMessage(); as message) {
          <p id="share-message" class="lead" role="status">{{ message }}</p>
        }
        @if (!evse.hasEvseId) {
          <p class="lead">Die Quelle meldet keine EVSE-ID; die Kennung hat chargeglobe vergeben.</p>
        }
        @if (evse.lifecycle !== 'ACTIVE') {
          <p class="lead" id="lifecycle">Dieser Ladepunkt ist nicht mehr aktiv ({{ evse.lifecycle }}).</p>
        }
        <mat-chip-set aria-label="Status">
          <mat-chip class="status" [disableRipple]="true">
            <span matChipAvatar class="status-dot" [style.background]="statusOf(evse.status.status).color"
              ><app-icon [name]="statusOf(evse.status.status).icon"
            /></span>
            {{ statusOf(evse.status.status).label }}
          </mat-chip>
        </mat-chip-set>
        <p class="lead" id="status-meta">
          @if (evse.status.since) {
            {{ faultSource(evse.status.status, evse.status.source) }}seit {{ evse.status.since | date: 'dd.MM.yyyy HH:mm' }} ·
          }
          @if (evse.status.observedAt) {
            gemeldet {{ evse.status.observedAt | date: 'dd.MM.yyyy HH:mm' }} von {{ sourceName(evse.status.source) }}
          } @else {
            noch keine Statusmeldung
          }
          @if (evse.status.stale) {
            <span class="stale">(nicht aktuell)</span>
          }
        </p>
        <p class="lead" id="evse-location">
          <a [routerLink]="['/locations', location.id]">{{ location.name ?? 'Ladestandort' }}</a>,
          {{ location.address }}, {{ location.postalCode }} {{ location.city }}
          · <a id="show-on-map" routerLink="/" [fragment]="mapFragment()">auf der Karte zeigen</a>
        </p>
      </header>

      <div class="evse-sections">
        <mat-card appearance="outlined">
          <mat-card-content>
            <h2>Technik</h2>
            <dl id="technology">
              <dt>Maximale Leistung</dt>
              <dd>{{ evse.maxPowerKw === null ? '–' : (evse.maxPowerKw | number: '1.0-2' : 'en-US') + ' kW' }}</dd>
              <dt>Stromart</dt>
              <dd>{{ powerTypeLabel(evse.powerType) }}</dd>
              <dt>Anschlüsse</dt>
              <dd>
                <ul class="plain">
                  @for (connector of evse.connectors; track $index) {
                    <li>
                      {{ connectorLabel(connector.standard) }}
                      @if (connector.format) {
                        · {{ formatLabel(connector.format) }}
                      }
                      @if (connector.maxPowerKw !== null) {
                        · {{ connector.maxPowerKw | number: '1.0-2' : 'en-US' }} kW
                      }
                    </li>
                  } @empty {
                    <li>keine Angabe</li>
                  }
                </ul>
              </dd>
              <dt>Betreiber</dt>
              <dd>{{ evse.operator ? evse.operator.name + (evse.operator.operatorId ? ' (' + evse.operator.operatorId + ')' : '') : '–' }}</dd>
              @if (evse.physicalReference) {
                <dt>Bezeichnung vor Ort</dt>
                <dd>{{ evse.physicalReference }}</dd>
              }
              <dt>Region</dt>
              <dd>{{ location.region ?? '–' }}</dd>
            </dl>
          </mat-card-content>
        </mat-card>

        <mat-card appearance="outlined">
          <mat-card-content>
            <h2>Bezahlen und Zugang</h2>
            <dl id="access">
              <dt>Zahlungsarten</dt>
              <dd>{{ evse.authMethods.length ? authMethods() : 'keine Angabe' }}</dd>
              <dt>Ad-hoc ohne Vertrag</dt>
              <dd id="ad-hoc">{{ evse.authMethods.length ? yesNo(adHoc()) : 'unbekannt' }}</dd>
              <dt>Roaming (Ladekarten anderer Anbieter)</dt>
              <dd>{{ yesNo(evse.roaming) }}</dd>
              <dt>Zugang</dt>
              <dd>{{ location.access === 'PUBLIC' ? 'öffentlich' : 'eingeschränkt' }}</dd>
              <dt>Öffnungszeiten</dt>
              <dd id="opening-times">
                {{ location.openingTimes === null ? 'keine Angabe' : location.openingTimes.twentyFourSeven ? 'rund um die Uhr' : location.openingTimes.description }}
              </dd>
              <dt>Ökostrom</dt>
              <dd>{{ yesNo(location.facilities.greenEnergy) }}</dd>
              <dt>Kostenloses Parken</dt>
              <dd>{{ yesNo(location.facilities.freeParking) }}</dd>
              <dt>Überdacht</dt>
              <dd>{{ yesNo(location.facilities.roofedParking) }}</dd>
              <dt>Barrierefreie Plätze</dt>
              <dd>{{ location.facilities.barrierFreeParkingSpaces ?? 'unbekannt' }}</dd>
              @if (location.facilities.priceUrl) {
                <dt>Preise</dt>
                <dd><a id="price-url" [href]="location.facilities.priceUrl" target="_blank" rel="noopener">Preisseite des Betreibers</a></dd>
              }
            </dl>
          </mat-card-content>
        </mat-card>
      </div>

      @if (prices) {
        <mat-card appearance="outlined">
          <mat-card-content>
            <section aria-labelledby="price-title">
              <h2 id="price-title">Preise</h2>
              @switch (priceState()) {
                @case ('loading') {
                  <app-loading class="price-state" text="Preis wird geladen …" />
                }
                @case ('error') {
                  <p class="price-state error">Der Preis konnte nicht geladen werden.</p>
                }
                @default {
                  @let pricing = priceData()!.data;
                  @if (pricing.current; as current) {
                    <p id="price-current">{{ parts(current.price).join(' · ') }}</p>
                    @if (isPlausible(current.price)) {
                      <p id="price-example">
                        {{ session(pricing.example) }}:
                        <strong>{{ money(current.price.exampleCost, current.price.currency) }}</strong>
                      </p>
                    } @else {
                      <p id="price-example" class="price-implausible">{{ implausible(current.price) }}</p>
                    }
                    <p class="lead" id="price-since">
                      gilt seit {{ current.since | date: 'dd.MM.yyyy HH:mm' }} · bestätigt {{ current.observedAt | date: 'dd.MM.yyyy HH:mm' }} ·
                      Quelle {{ current.price.source }}
                    </p>
                  } @else {
                    <p id="price-current">Für diesen Ladepunkt ist kein Ad-hoc-Preis bekannt.</p>
                  }
                  @if (pricing.priceNote; as note) {
                    <p id="price-note" class="price-note">
                      {{ note.text }}
                      @if (note.url) {
                        <a [href]="note.url" target="_blank" rel="noopener">Preise des Betreibers</a>
                      }
                    </p>
                  }
                  @let rows = compare(pricing, cards.cards());
                  @if (rows.length > 0) {
                    <h3 id="comparison-title">Vergleich mit Ladekarten</h3>
                    <table id="price-comparison" class="data-table" aria-labelledby="comparison-title">
                      <thead>
                        <tr>
                          <th scope="col">Tarif</th><th scope="col">Preis</th><th scope="col">Grundgebühr/Monat</th>
                          <th scope="col">{{ session(pricing.example) }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (row of rows; track row.key) {
                          <tr [class.cheapest]="row.cheapest" [class.own]="row.own">
                            <td>
                              {{ row.label }}
                              @if (row.cheapest) {
                                <span class="cheapest-badge">günstigste</span>
                              }
                              @if (row.own) {
                                <span class="own-badge">deine Karte</span>
                              }
                            </td>
                            <td>{{ parts(row.price).join(' · ') }}</td>
                            <td>{{ row.monthlyFee === null ? '–' : money(row.monthlyFee, row.price.currency) }}</td>
                            <td>{{ money(row.price.exampleCost, row.price.currency) }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  }
                  @if (pricing.history.length > 1) {
                    <h3>Verlauf des Ad-hoc-Preises</h3>
                    <table id="price-history" class="data-table">
                      <thead>
                        <tr><th scope="col">von</th><th scope="col">bis</th><th scope="col">Preis</th><th scope="col">{{ session(pricing.example) }}</th></tr>
                      </thead>
                      <tbody>
                        @for (period of pricing.history; track period.from + period.price.source) {
                          <tr>
                            <td>{{ period.from | date: 'dd.MM.yyyy' }}</td>
                            <td>{{ period.to === null ? 'heute' : (period.to | date: 'dd.MM.yyyy') }}</td>
                            <td>{{ parts(period.price).join(' · ') }}</td>
                            <td>{{ isPlausible(period.price) ? money(period.price.exampleCost, period.price.currency) : implausible(period.price) }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  }
                }
              }
            </section>
          </mat-card-content>
        </mat-card>
      }

      <mat-card appearance="outlined">
        <mat-card-content>
          <section aria-labelledby="history-title">
            <h2 id="history-title">Statusverlauf</h2>
            <mat-button-toggle-group id="history-range" aria-label="Zeitraum" hideSingleSelectionIndicator [value]="days()" (change)="showDays($event.value)">
              @for (option of historyDays; track option) {
                <mat-button-toggle [value]="option">{{ option === 1 ? '24 Stunden' : option + ' Tage' }}</mat-button-toggle>
              }
            </mat-button-toggle-group>
            @switch (history()) {
              @case ('loading') {
                <app-loading class="timeline-state" text="Verlauf wird geladen …" />
              }
              @case ('error') {
                <p class="timeline-state error">Der Statusverlauf konnte nicht geladen werden.</p>
              }
              @default {
                @let loaded = historyData()!;
                <app-status-timeline [segments]="loaded.segments" [from]="loaded.from" [label]="'Statusverlauf der letzten ' + days() + ' Tage'" />
                @if (loaded.intervals.length === 0) {
                  <p class="timeline-state">Keine Statusmeldungen in diesem Zeitraum.</p>
                } @else {
                  <table id="intervals" class="data-table">
                    <thead>
                      <tr><th scope="col">Status</th><th scope="col">von</th><th scope="col">bis</th><th scope="col">Quelle</th></tr>
                    </thead>
                    <tbody>
                      @for (interval of loaded.intervals; track interval.from) {
                        <tr>
                          <td>{{ statusOf(interval.status).label }}</td>
                          <td>{{ interval.from | date: 'dd.MM. HH:mm' }}</td>
                          <td>{{ interval.to === null ? 'andauernd' : (interval.to | date: 'dd.MM. HH:mm') }}</td>
                          <td>{{ interval.source }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                }
              }
            }
          </section>
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-content>
          <section aria-labelledby="occupancy-title">
            <h2 id="occupancy-title">Auslastung nach Wochentag und Stunde</h2>
            <mat-button-toggle-group id="occupancy-range" aria-label="Zeitraum" hideSingleSelectionIndicator [value]="weeks()" (change)="showWeeks($event.value)">
              @for (option of occupancyWeeks; track option) {
                <mat-button-toggle [value]="option">{{ option }} Wochen</mat-button-toggle>
              }
            </mat-button-toggle-group>
            @switch (occupancyState()) {
              @case ('loading') {
                <app-loading class="heatmap-state" text="Auslastung wird geladen …" />
              }
              @case ('error') {
                <p class="heatmap-state error">Die Auslastung konnte nicht geladen werden.</p>
              }
              @default {
                @let matrix = occupancy()!;
                <p id="utilization">
                  @if (matrix.utilization !== null) {
                    In den letzten {{ weeks() }} Wochen lud hier ein Fahrzeug bei {{ matrix.utilization | percent: '1.0-0' : 'en-US' }} der
                    {{ matrix.observations }} Statusmeldungen.
                  } @else {
                    Keine Statusmeldungen in den letzten {{ weeks() }} Wochen.
                  }
                </p>
                <app-occupancy-heatmap [days]="matrix.days" />
              }
            }
          </section>
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-content>
          <section aria-labelledby="origin-title">
            <h2 id="origin-title">Herkunft der Daten</h2>
            <p class="lead">
              Erstmals gesehen {{ evse.firstSeen | date: 'dd.MM.yyyy' }}, zuletzt {{ evse.lastSeen | date: 'dd.MM.yyyy HH:mm' }}
              @if (evse.lastConfirmedAt) {
                , Stammdaten bestätigt {{ evse.lastConfirmedAt | date: 'dd.MM.yyyy HH:mm' }}
              }
            </p>
            <table id="sources" class="data-table">
              <thead>
                <tr><th scope="col">Quelle</th><th scope="col">Datensatz</th><th scope="col">Kennung in der Quelle</th><th scope="col">zuletzt</th></tr>
              </thead>
              <tbody>
                @for (record of evse.sources; track record.entity + record.source + record.externalId) {
                  <tr>
                    <td>{{ record.source }}</td>
                    <td>{{ record.entity === 'EVSE' ? 'Ladepunkt' : 'Standort' }}</td>
                    <td><code>{{ record.externalId }}</code></td>
                    <td>{{ record.lastSeen | date: 'dd.MM.yyyy HH:mm' }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="4">Keine Quelle verknüpft.</td></tr>
                }
              </tbody>
            </table>
            @if (evse.provenance.length > 0) {
              <table id="provenance" class="data-table">
                <thead>
                  <tr><th scope="col">Angabe</th><th scope="col">Quelle</th><th scope="col">bestätigt</th></tr>
                </thead>
                <tbody>
                  @for (origin of evse.provenance; track origin.entity + origin.attribute + origin.source) {
                    <tr>
                      <td>{{ attributeLabel(origin.attribute) }}{{ origin.entity === 'LOCATION' ? ' (Standort)' : '' }}</td>
                      <td>{{ origin.source }}</td>
                      <td>{{ origin.observedAt | date: 'dd.MM.yyyy HH:mm' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </section>
        </mat-card-content>
      </mat-card>

      @if (attribution().length > 0) {
        <p id="evse-attribution">{{ attribution().join(' · ') }}</p>
      }
    }
  `,
})
export class EvseDetailPage {
  private readonly service = inject(EvsesService);
  private readonly link = inject(MapLink);
  /** What became of "Teilen"; null when the share sheet took it. */
  protected readonly shareMessage = signal<string | null>(null);
  private readonly evseId = inject(ActivatedRoute).snapshot.paramMap.get('evseId')!;
  /** Numbers of the newest history and occupancy loads; older answers are dropped. */
  private historyRequest = 0;
  private occupancyRequest = 0;

  protected readonly historyDays = HISTORY_DAYS;
  protected readonly occupancyWeeks = OCCUPANCY_WEEKS;
  protected readonly statusOf = statusOf;
  protected readonly faultSource = faultSource;
  protected readonly sourceName = sourceName;
  protected readonly connectorLabel = connectorLabel;
  protected readonly formatLabel = formatLabel;
  protected readonly powerTypeLabel = powerTypeLabel;
  protected readonly attributeLabel = attributeLabel;
  protected readonly yesNo = yesNo;
  protected readonly parts = priceParts;
  protected readonly money = money;
  protected readonly isPlausible = plausible;
  protected readonly implausible = implausibleText;
  protected readonly session = sessionText;
  protected readonly compare = comparison;
  protected readonly cards = inject(CardsStore);
  /** The ad-hoc price needs price:read. */
  protected readonly prices = inject(AuthService).hasPermission('price:read');
  protected readonly priceData = signal<EvsePricesResponse | null>(null);
  protected readonly priceState = signal<'loading' | 'error' | 'ready'>('loading');

  protected readonly notice = signal<'missing' | 'failed' | null>(null);
  protected readonly evse = signal<EvseResponse | null>(null);
  protected readonly days = signal(7);
  protected readonly weeks = signal(OCCUPANCY_WEEKS[0]);
  protected readonly historyData = signal<{ segments: Segment[]; intervals: StatusInterval[]; from: Date } | null>(null);
  protected readonly history = signal<'loading' | 'error' | 'ready'>('loading');
  protected readonly occupancy = signal<Occupancy | null>(null);
  protected readonly occupancyState = signal<'loading' | 'error' | 'ready'>('loading');

  protected readonly attribution = computed(() => [...new Set(this.evse()?.meta.attribution.map((source) => source.text) ?? [])]);
  protected readonly authMethods = computed(() => this.evse()!.data.authMethods.map(authMethodLabel).join(', '));
  protected readonly adHoc = computed(() => this.evse()!.data.authMethods.some((method) => AD_HOC_METHODS.includes(method)));
  /** Shares the address of this page: system share sheet (Web Share API) if there is one, else the clipboard. */
  protected async share(evseId: string): Promise<void> {
    this.shareMessage.set(SHARE_MESSAGES[await this.link.share(`Ladepunkt ${evseId} – chargeglobe`)]);
  }

  /** View of the map page on the location (see MapLink). */
  protected readonly mapFragment = computed(() => {
    const location = this.evse()!.data.location;
    return `map=17.00/${location.latitude.toFixed(5)}/${location.longitude.toFixed(5)}`;
  });

  constructor() {
    void this.load();
  }

  protected showDays(days: number): Promise<void> {
    this.days.set(days);
    return this.loadHistory();
  }

  protected showWeeks(weeks: number): Promise<void> {
    this.weeks.set(weeks);
    return this.loadOccupancy();
  }

  private async load(): Promise<void> {
    try {
      this.evse.set(await this.service.evse(this.evseId));
    } catch (error) {
      this.notice.set(error instanceof HttpErrorResponse && error.status === 404 ? 'missing' : 'failed');
      return;
    }
    await Promise.all([this.loadHistory(), this.loadOccupancy(), ...(this.prices ? [this.loadPrices()] : [])]);
  }

  private async loadPrices(): Promise<void> {
    try {
      this.priceData.set(await this.service.prices(this.evse()!.data.id));
      this.priceState.set('ready');
    } catch {
      this.priceState.set('error');
    }
  }

  private async loadHistory(): Promise<void> {
    const request = ++this.historyRequest;
    this.history.set('loading');
    const to = new Date();
    const from = new Date(to.getTime() - this.days() * DAY_MS);
    try {
      const response = await this.service.history(this.evse()!.data.id, from, to);
      if (request === this.historyRequest) {
        const intervals = response.data.intervals;
        this.historyData.set({ segments: timeline(intervals, from, to), intervals: [...intervals].reverse().slice(0, LISTED_INTERVALS), from });
        this.history.set('ready');
      }
    } catch {
      if (request === this.historyRequest) {
        this.history.set('error');
      }
    }
  }

  private async loadOccupancy(): Promise<void> {
    const request = ++this.occupancyRequest;
    this.occupancyState.set('loading');
    try {
      const response = await this.service.occupancy(this.evse()!.data.id, this.weeks());
      if (request === this.occupancyRequest) {
        this.occupancy.set(response.data);
        this.occupancyState.set('ready');
      }
    } catch {
      if (request === this.occupancyRequest) {
        this.occupancyState.set('error');
      }
    }
  }
}
