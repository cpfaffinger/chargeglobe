import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import {
  EvseDetail,
  EvsesService,
  LocationDetailResponse,
  Occupancy,
} from './evses.service';
import { Segment, combine, statusOf, timeline } from './status';
import { OccupancyHeatmap, StatusTimeline } from './status-charts';
import { EvsePricesResponse, money, priceParts } from './prices';
import { AVAILABILITY, tokenColor } from '../map/availability';

/** Days of the status timeline and weeks of the occupancy matrix. */
const TIMELINE_DAYS = 7;
const OCCUPANCY_WEEKS = 4;

type LoadState = 'loading' | 'error' | 'ready';

/**
 * Details of a charging location, shown in the info card of the map and on the page /locations/:id: the EVSEs
 * with status and connectors (public, GET /locations/{id}); with evse:read also the status timeline of the last
 * 7 days per EVSE and a weekday × hour heatmap of the share of readings with status CHARGING over the last 4
 * weeks, loaded in parallel after the EVSEs. Colours come from the theme tokens, so both light and dark mode
 * work; statuses also carry a symbol and a label. With `header`, the component shows name, address and
 * availability itself (the page); the info card shows them from the map data before the details arrive.
 */
@Component({
  imports: [
    AppIcon,
    DatePipe,
    DecimalPipe,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    MatProgressBarModule,
    OccupancyHeatmap,
    PercentPipe,
    RouterLink,
    StatusTimeline,
  ],
  selector: 'app-location-details',
  host: { '[class.compact]': '!header()' },
  template: `
    @if (notice() === 'missing') {
      <p id="detail-notice" class="error">Diesen Ladestandort gibt es nicht oder nicht mehr.</p>
    } @else if (notice() === 'failed') {
      <p id="detail-notice" class="error">
        Der Ladestandort konnte nicht geladen werden. Bitte versuche es später erneut.
      </p>
    }
    @if (!detail() && !notice()) {
      <p id="details-loading" class="details-state">Ladepunkte werden geladen …</p>
    }
    @if (detail(); as detail) {
      @let location = detail.data.location;
      @if (header()) {
      <div>
        <h1 id="location-name">{{ location.name ?? 'Ladestandort' }}</h1>
        <p class="lead address">{{ location.address }}, {{ location.postalCode }} {{ location.city }}</p>
        @if (location.operator) {
          <p class="lead operator">Betreiber: {{ location.operator }}</p>
        }
        <mat-chip-set class="chips" aria-label="Verfügbarkeit">
          <mat-chip class="availability" [disableRipple]="true">
            <span matChipAvatar class="status-dot" [style.background]="availabilityColor(location.availability)"
              ><app-icon [name]="availabilityIcon(location.availability)"
            /></span>
            {{ location.evsesAvailable }} von {{ location.evsesTotal }} Ladepunkten frei
          </mat-chip>
          @if (location.maxPowerKw !== null) {
            <mat-chip [disableRipple]="true">bis {{ location.maxPowerKw | number: '1.0-1' : 'en-US' }} kW</mat-chip>
          }
        </mat-chip-set>
      </div>
      }

      <section aria-labelledby="evses-title">
        <h2 id="evses-title">Ladepunkte</h2>
        @if (!history) {
          <p id="history-hint" class="details-state">
            Statusverlauf und Auslastung siehst du nach der <a routerLink="/login">Anmeldung</a>.
          </p>
        }
        @for (evse of detail.data.evses; track evse.id) {
          <mat-card appearance="outlined" class="evse" [attr.data-evse]="evse.id">
            <mat-card-content>
              <div class="evse-head">
                <h3>
                  @if (history) {
                    <a class="evse-link" [routerLink]="['/evses', evse.evseId]">{{ evse.evseId }}</a>
                  } @else {
                    {{ evse.evseId }}
                  }
                  @if (!evse.hasEvseId) {
                    <small>&nbsp;(ID von chargeglobe vergeben)</small>
                  }
                </h3>
                <mat-chip-set aria-label="Status">
                  <mat-chip class="status" [disableRipple]="true">
                    <span matChipAvatar class="status-dot" [style.background]="statusOf(evse.status).color"
                      ><app-icon [name]="statusOf(evse.status).icon"
                    /></span>
                    {{ statusOf(evse.status).label }}
                  </mat-chip>
                </mat-chip-set>
              </div>
              <p class="meta">
                @if (evse.statusSince) {
                  seit {{ evse.statusSince | date: 'dd.MM.yyyy HH:mm' }}
                }
                @if (evse.maxPowerKw !== null) {
                  · bis {{ evse.maxPowerKw | number: '1.0-1' : 'en-US' }} kW
                }
              </p>
              <p class="connectors">{{ connectors(evse) }}</p>
              @if (prices) {
                @switch (priceState(evse.id)) {
                  @case ('loading') {
                    <p class="price price-state">Preis wird geladen …</p>
                  }
                  @case ('error') {
                    <p class="price price-state error">Der Preis konnte nicht geladen werden.</p>
                  }
                  @default {
                    @let pricing = priceOf(evse.id);
                    @if (pricing.current; as current) {
                      <p class="price">
                        Ad-hoc: {{ parts(current.price).join(' · ') }}
                        <span class="price-example">· {{ example }}: {{ money(current.price.exampleCost, current.price.currency) }}</span>
                      </p>
                    } @else {
                      <p class="price price-state">Kein Ad-hoc-Preis bekannt.</p>
                    }
                    @if (pricing.emps[0]; as card) {
                      <p class="price cheapest-card">
                        günstigste Karte: {{ card.empName }} ({{ card.tariff }})
                        <span class="price-example">· {{ example }}: {{ money(card.price.exampleCost, card.price.currency) }}</span>
                      </p>
                    }
                  }
                }
              }
              @if (history) {
              @switch (timelineState(evse.id)) {
                @case ('loading') {
                  <p class="timeline-state">Verlauf wird geladen …</p>
                  <mat-progress-bar mode="indeterminate" aria-label="Verlauf wird geladen" />
                }
                @case ('error') {
                  <p class="timeline-state error">Der Statusverlauf konnte nicht geladen werden.</p>
                }
                @default {
                  <app-status-timeline
                    [segments]="segments(evse.id)"
                    [from]="range().from"
                    [label]="'Statusverlauf der letzten ' + timelineDays + ' Tage'"
                  />
                  @if (segments(evse.id).length === 0) {
                    <p class="timeline-state">Keine Statusmeldungen in den letzten {{ timelineDays }} Tagen.</p>
                  }
                }
              }
              }
            </mat-card-content>
          </mat-card>
        } @empty {
          <p id="no-evses">Für diesen Standort sind keine Ladepunkte bekannt.</p>
        }
      </section>

      @if (history && detail.data.evses.length > 0) {
        <mat-card appearance="outlined">
          <mat-card-content>
            <section aria-labelledby="heatmap-title">
              <h2 id="heatmap-title">Auslastung nach Wochentag und Stunde</h2>
              @if (detail.data.evses.length > 1) {
                <mat-button-toggle-group
                  id="heatmap-select"
                  aria-label="Ladepunkt"
                  hideSingleSelectionIndicator
                  [value]="heatmapOf()"
                  (change)="heatmapOf.set($event.value)"
                >
                  <mat-button-toggle value="all">Alle Ladepunkte</mat-button-toggle>
                  @for (evse of detail.data.evses; track evse.id; let index = $index) {
                    <mat-button-toggle [value]="evse.id" [title]="evse.evseId">Ladepunkt {{ index + 1 }}</mat-button-toggle>
                  }
                </mat-button-toggle-group>
              }
              @switch (heatmapState()) {
                @case ('loading') {
                  <p class="heatmap-state">Auslastung wird geladen …</p>
                }
                @case ('error') {
                  <p class="heatmap-state error">Die Auslastung konnte nicht geladen werden.</p>
                }
                @default {
                  @let heatmap = heatmapData();
                  <p id="utilization">
                    @if (heatmap.utilization !== null) {
                      In den letzten {{ occupancyWeeks }} Wochen lud hier ein Fahrzeug bei
                      {{ heatmap.utilization | percent: '1.0-0' : 'en-US' }} der {{ heatmap.observations }}
                      Statusmeldungen.
                    } @else {
                      Keine Statusmeldungen in den letzten {{ occupancyWeeks }} Wochen.
                    }
                  </p>
                  <app-occupancy-heatmap [days]="heatmap.days" />
                }
              }
            </section>
          </mat-card-content>
        </mat-card>
      }

      @if (attribution().length > 0) {
        <p id="detail-attribution">{{ attribution().join(' · ') }}</p>
      }
    }
  `,
})
export class LocationDetails {
  private readonly service = inject(EvsesService);
  /** Number of the newest load; answers for a location shown before are dropped. */
  private request = 0;

  readonly locationId = input.required<string>();
  /** Show name, address and availability (the page); the info card shows them itself. */
  readonly header = input(false);

  private readonly auth = inject(AuthService);
  /** Timeline and heatmap need evse:read; everybody sees the EVSEs with their current status. */
  protected readonly history = this.auth.hasPermission('evse:read');
  /** The ad-hoc price per EVSE needs price:read. */
  protected readonly prices = this.auth.hasPermission('price:read');
  protected readonly example = '30 kWh in 40 min';
  protected readonly parts = priceParts;
  protected readonly money = money;

  protected readonly timelineDays = TIMELINE_DAYS;
  protected readonly occupancyWeeks = OCCUPANCY_WEEKS;
  protected readonly statusOf = statusOf;
  protected readonly availabilityColor = (key: keyof typeof AVAILABILITY) => tokenColor(AVAILABILITY[key].token);
  protected readonly availabilityIcon = (key: keyof typeof AVAILABILITY) => AVAILABILITY[key].icon;

  protected readonly notice = signal<'missing' | 'failed' | null>(null);
  protected readonly detail = signal<LocationDetailResponse | null>(null);
  protected readonly range = signal({ from: new Date(), to: new Date() });
  private readonly timelines = signal<Record<string, Segment[] | 'error'>>({});
  private readonly occupancies = signal<Record<string, Occupancy | 'error'>>({});
  private readonly priceResults = signal<Record<string, EvsePricesResponse['data'] | 'error'>>({});
  /** "all" or the id of the EVSE whose heatmap is shown. */
  protected readonly heatmapOf = signal('all');

  protected readonly attribution = computed(() => [
    ...new Set(this.detail()?.meta.attribution.map((source) => source.text) ?? []),
  ]);

  /** The loaded matrices of the selected EVSE(s). */
  private readonly selectedOccupancies = computed(() => {
    const selected = this.heatmapOf();
    return Object.entries(this.occupancies())
      .filter(([id]) => selected === 'all' || id === selected)
      .map(([, occupancy]) => occupancy);
  });

  protected readonly heatmapState = computed<LoadState>(() => {
    const evses = this.detail()!.data.evses;
    const wanted = this.heatmapOf() === 'all' ? evses.length : 1;
    const results = this.selectedOccupancies();
    if (results.some((result) => result !== 'error')) {
      return 'ready';
    }
    return results.length < wanted ? 'loading' : 'error';
  });

  /** Matrix and totals of the selection; several EVSEs are summed reading by reading. */
  protected readonly heatmapData = computed(() => {
    const loaded = this.selectedOccupancies().filter(
      (result): result is Occupancy => result !== 'error',
    );
    const observations = loaded.reduce((sum, occupancy) => sum + occupancy.observations, 0);
    const charging = loaded.reduce(
      (sum, occupancy) => sum + (occupancy.utilization ?? 0) * occupancy.observations,
      0,
    );
    return {
      days: combine(loaded.map((occupancy) => occupancy.days)),
      observations,
      utilization: observations > 0 ? charging / observations : null,
    };
  });

  constructor() {
    // Another location in the same card: start over.
    effect(() => {
      const id = this.locationId();
      untracked(() => void this.load(id));
    });
  }

  protected timelineState(evseId: string): LoadState {
    const segments = this.timelines()[evseId];
    return segments === undefined ? 'loading' : segments === 'error' ? 'error' : 'ready';
  }

  protected segments(evseId: string): Segment[] {
    return this.timelines()[evseId] as Segment[];
  }

  protected connectors(evse: EvseDetail): string {
    return evse.connectors
      .map((connector) =>
        [
          connector.standard,
          connector.format,
          connector.maxPowerKw === null ? null : `${connector.maxPowerKw} kW`,
        ]
          .filter((part) => part !== null)
          .join(' · '),
      )
      .join(', ');
  }

  private async load(id: string): Promise<void> {
    const request = ++this.request;
    this.detail.set(null);
    this.notice.set(null);
    this.timelines.set({});
    this.occupancies.set({});
    this.priceResults.set({});
    this.heatmapOf.set('all');
    let detail: LocationDetailResponse;
    try {
      detail = await this.service.location(id);
    } catch (error) {
      if (request === this.request) {
        this.notice.set(error instanceof HttpErrorResponse && error.status === 404 ? 'missing' : 'failed');
      }
      return;
    }
    if (request !== this.request) {
      return;
    }
    const to = new Date();
    const from = new Date(to.getTime() - TIMELINE_DAYS * 24 * 60 * 60 * 1000);
    this.range.set({ from, to });
    this.detail.set(detail);
    await Promise.all(
      detail.data.evses.flatMap((evse) => [
        ...(this.history ? [this.loadTimeline(request, evse.id, from, to), this.loadOccupancy(request, evse.id)] : []),
        ...(this.prices ? [this.loadPrice(request, evse.id)] : []),
      ]),
    );
  }

  protected priceState(evseId: string): LoadState {
    const result = this.priceResults()[evseId];
    return result === undefined ? 'loading' : result === 'error' ? 'error' : 'ready';
  }

  protected priceOf(evseId: string): EvsePricesResponse['data'] {
    return this.priceResults()[evseId] as EvsePricesResponse['data'];
  }

  private async loadPrice(request: number, evseId: string): Promise<void> {
    let result: EvsePricesResponse['data'] | 'error';
    try {
      result = (await this.service.prices(evseId)).data;
    } catch {
      result = 'error';
    }
    if (request === this.request) {
      this.priceResults.update((all) => ({ ...all, [evseId]: result }));
    }
  }

  private async loadTimeline(request: number, evseId: string, from: Date, to: Date): Promise<void> {
    let result: Segment[] | 'error';
    try {
      result = timeline((await this.service.history(evseId, from, to)).data.intervals, from, to);
    } catch {
      result = 'error';
    }
    if (request === this.request) {
      this.timelines.update((all) => ({ ...all, [evseId]: result }));
    }
  }

  private async loadOccupancy(request: number, evseId: string): Promise<void> {
    let result: Occupancy | 'error';
    try {
      result = (await this.service.occupancy(evseId, OCCUPANCY_WEEKS)).data;
    } catch {
      result = 'error';
    }
    if (request === this.request) {
      this.occupancies.update((all) => ({ ...all, [evseId]: result }));
    }
  }
}
