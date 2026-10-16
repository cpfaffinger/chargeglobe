import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import {
  EvseDetail,
  EvsesService,
  LocationDetailResponse,
  LocationOccupancy,
  Occupancy,
} from './evses.service';
import { Segment, combine, faultSource, statusOf, timeline } from './status';
import { OccupancyHeatmap, StatusTimeline } from './status-charts';
import { CardsStore } from '../preferences/cards.store';
import { EmpPrice, EvsePricesResponse, money, priceParts } from './prices';
import { AVAILABILITY, busyText, tokenColor } from '../map/availability';
import { connectorLabel, formatLabel } from './labels';
import { LocationSummary } from '../map/locations.service';

/** Other stations of a park whose details load at once in the park view, and per "load more". */
const PARK_PAGE = 10;
/** From this many other stations (the ring of the map is full) the park list gets a search. */
const PARK_SEARCH_FROM = 8;
/** Order of the park list: free stations first. */
const AVAILABILITY_ORDER = ['AVAILABLE', 'OCCUPIED', 'OUT_OF_ORDER', 'UNKNOWN'];

/** "1 Ladepunkt", "4 Ladepunkte". */
const count = (value: number, one: string, many: string): string => `${value} ${value === 1 ? one : many}`;

/** The name of a station label without country and operator: AT*MER*E006566 → E006566. */
export const stationName = (label: string): string => label.split('*').at(-1)!;

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
    AppLoading,
    AppIcon,
    DatePipe,
    DecimalPipe,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
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
      <app-loading id="details-loading" class="details-state" text="Ladepunkte werden geladen …" />
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
            {{ location.evsesAvailable }} von {{ location.evsesTotal }} Ladepunkten frei{{ busy(location) }}
          </mat-chip>
          @if (location.maxPowerKw !== null) {
            <mat-chip [disableRipple]="true">bis {{ location.maxPowerKw | number: '1.0-1' : 'en-US' }} kW</mat-chip>
          }
        </mat-chip-set>
      </div>
      }

      @if (detail.data.park.length > 0 && !nested()) {
        @let park = detail.data.park;
        <section id="park" aria-label="Ladepark">
          <p class="park-summary">
            <app-icon name="ev_station" />
            Ladepark: {{ park.length + 1 }} Stationen an diesem Ort, zusammen {{ parkTotal() }} Ladepunkte für bis zu
            {{ parkTotal() }} Fahrzeuge ({{ parkFree() }} frei)
          </p>
          @if (park.length >= parkSearchFrom) {
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="park-search">
              <mat-label>Station suchen</mat-label>
              <input matInput type="search" id="park-search" [value]="parkQuery()" (input)="searchPark($event)" />
            </mat-form-field>
          }
          <ul class="park-members" [class.scroll]="park.length >= parkSearchFrom">
            @for (member of parkMembers(); track member.id) {
              <li>
                <span class="status-dot" [style.background]="availabilityColor(member.availability)"
                  ><app-icon [name]="availabilityIcon(member.availability)"
                /></span>
                <button matButton type="button" class="park-member" (click)="openLocation.emit(member)">
                  Andere Station: {{ member.evsesTotal }} {{ member.evsesTotal === 1 ? 'Ladepunkt' : 'Ladepunkte' }},
                  {{ member.evsesAvailable }} frei{{ member.name !== location.name && member.name ? ' · ' + member.name : '' }}
                </button>
              </li>
            } @empty {
              <li class="park-none">Keine Station passt zur Suche.</li>
            }
          </ul>
          <button matButton="tonal" type="button" id="park-toggle" [attr.aria-expanded]="parkOpen()" (click)="togglePark()">
            {{ parkOpen() ? 'Nur diese Station zeigen' : 'Ganzen Ladepark ansehen' }}
          </button>
        </section>
      }

      <section aria-labelledby="evses-title">
        <h2 id="evses-title">{{ nested() ? 'Ladepunkte dieser Station' : 'Ladepunkte' }}</h2>
        @if (detail.data.evses.length > 0) {
          <p class="capacity">{{ capacity() }}</p>
        }
        @if (!history) {
          <p id="history-hint" class="details-state">
            Statusverlauf und Auslastung siehst du nach der <a routerLink="/login">Anmeldung</a>.
          </p>
        }
        @for (group of evseGroups(); track group.label) {
          @if (group.title) {
            <h3 class="station-title">{{ group.title }}</h3>
          }
          @for (evse of group.evses; track evse.id) {
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
                  {{ faultSource(evse.status, evse.statusSource) }}seit {{ evse.statusSince | date: 'dd.MM.yyyy HH:mm' }}
                }
                @if (evse.maxPowerKw !== null) {
                  · bis {{ evse.maxPowerKw | number: '1.0-1' : 'en-US' }} kW
                }
              </p>
              <ul class="connectors" aria-label="Anschlüsse">
                @for (connector of evse.connectors; track $index) {
                  <li class="connector"><app-icon name="ev_station" />{{ connectorText(connector) }}</li>
                }
              </ul>
              @if (evse.connectors.length > 1) {
                <p class="connector-note">
                  Ein Status für alle Anschlüsse: Die Quelle meldet die Belegung je Ladepunkt. Lädt an einem Anschluss ein
                  Auto, gilt der ganze Ladepunkt als belegt.
                </p>
              }
              @if (prices) {
                @switch (priceState(evse.id)) {
                  @case ('loading') {
                    <app-loading class="price price-state" text="Preis wird geladen …" />
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
                    @let own = ownCheapest(pricing);
                    @if (own) {
                      <p class="price own-card">
                        deine günstigste Karte: {{ own.empName }} ({{ own.tariff }})
                        <span class="price-example">· {{ example }}: {{ money(own.price.exampleCost, own.price.currency) }}</span>
                      </p>
                    }
                    @if (pricing.emps[0]; as card) {
                      @if (card !== own) {
                        <p class="price cheapest-card">
                          günstigste Karte: {{ card.empName }} ({{ card.tariff }})
                          <span class="price-example">· {{ example }}: {{ money(card.price.exampleCost, card.price.currency) }}</span>
                        </p>
                      }
                    }
                  }
                }
              }
              @if (history) {
              @switch (timelineState(evse.id)) {
                @case ('loading') {
                  <app-loading class="timeline-state" text="Verlauf wird geladen …" />
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
          }
        } @empty {
          <p id="no-evses">Für diesen Standort sind keine Ladepunkte bekannt.</p>
        }
      </section>

      @if (history && detail.data.evses.length > 0) {
        <mat-card appearance="outlined">
          <mat-card-content>
            <section aria-labelledby="heatmap-title">
              <h2 id="heatmap-title">Auslastung nach Wochentag und Stunde</h2>
              @if (detail.data.evses.length > 1 || inPark()) {
                <mat-button-toggle-group
                  id="heatmap-select"
                  aria-label="Ladepunkt"
                  hideSingleSelectionIndicator
                  [value]="heatmapOf()"
                  (change)="showHeatmap($event.value)"
                >
                  @if (inPark()) {
                    <mat-button-toggle value="park">Ganzer Ladepark</mat-button-toggle>
                  }
                  <mat-button-toggle value="all">{{ inPark() ? 'Diese Station' : 'Alle Ladepunkte' }}</mat-button-toggle>
                  @for (evse of detail.data.evses; track evse.id; let index = $index) {
                    <mat-button-toggle [value]="evse.id" [title]="evse.evseId">Ladepunkt {{ index + 1 }}</mat-button-toggle>
                  }
                </mat-button-toggle-group>
              }
              @switch (heatmapState()) {
                @case ('loading') {
                  <app-loading class="heatmap-state" text="Auslastung wird geladen …" />
                }
                @case ('error') {
                  <p class="heatmap-state error">Die Auslastung konnte nicht geladen werden.</p>
                }
                @default {
                  @let heatmap = heatmapData();
                  @if (heatmap.scope; as scope) {
                    <p id="heatmap-scope">Ganzer Ladepark: {{ scope.locations }} Stationen, {{ scope.evses }} Ladepunkte zusammen</p>
                  }
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
      @if (parkOpen() && !nested()) {
        <!-- Large parks load the details of the other stations step by step, in the order of the list above. -->
        @for (member of parkMembers().slice(0, parkShown()); track member.id) {
          <section class="park-station" [attr.aria-label]="'Station ' + ($index + 2) + ' des Ladeparks'">
            <h2 class="park-station-title">Station {{ $index + 2 }} von {{ detail.data.park.length + 1 }}</h2>
            <app-location-details [locationId]="member.id" [nested]="true" />
          </section>
        }
        @if (parkMembers().length > parkShown()) {
          <button matButton="tonal" type="button" id="park-more" (click)="parkShown.set(parkShown() + parkPage)">
            Weitere {{ nextPage() }} Stationen laden ({{ parkMembers().length - parkShown() }} offen)
          </button>
        }
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
  /** Shown inside the park view of another location: without the park section. */
  readonly nested = input(false);
  /** Another location of the park was chosen. */
  readonly openLocation = output<LocationSummary>();
  /**
   * The EVSEs by their charging station (in the order of their first EVSE); those without one last. Without any known
   * station one group without title.
   */
  protected readonly evseGroups = computed(() => {
    const evses = this.detail()?.data.evses ?? [];
    if (evses.every((evse) => evse.chargingStation === null)) {
      return [{ label: '', title: null, evses }];
    }
    const groups = new Map<string, EvseDetail[]>();
    for (const evse of evses) {
      const label = evse.chargingStation ?? '';
      groups.set(label, [...(groups.get(label) ?? []), evse]);
    }
    return [...groups.entries()]
      .sort(([a], [b]) => (a === '' ? 1 : 0) - (b === '' ? 1 : 0))
      .map(([label, members]) => ({
        label,
        title: `${label === '' ? 'Ohne Angabe der Ladestation' : `Ladestation ${stationName(label)}`} · ${count(members.length, 'Ladepunkt', 'Ladepunkte')}`,
        evses: members,
      }));
  });

  /**
   * "2 Ladestationen · 4 Ladepunkte (bis zu 4 Fahrzeuge gleichzeitig) · 4 Anschlüsse"; the stations only when all EVSEs
   * have a known one.
   */
  protected readonly capacity = computed(() => {
    const evses = this.detail()?.data.evses ?? [];
    const stations = new Set(evses.map((evse) => evse.chargingStation));
    const connectors = evses.reduce((sum, evse) => sum + evse.connectors.length, 0);
    return [
      ...(stations.has(null) ? [] : [count(stations.size, 'Ladestation', 'Ladestationen')]),
      evses.length === 1 ? '1 Ladepunkt (ein Fahrzeug zugleich)' : `${evses.length} Ladepunkte (bis zu ${evses.length} Fahrzeuge gleichzeitig)`,
      count(connectors, 'Anschluss', 'Anschlüsse'),
    ].join(' · ');
  });

  /** The other stations of the park are shown below. */
  protected readonly parkOpen = signal(false);
  /** From this many other stations the park list can be searched and scrolls. */
  protected readonly parkSearchFrom = PARK_SEARCH_FROM;
  protected readonly parkPage = PARK_PAGE;
  /** Other stations whose details are shown in the park view. */
  protected readonly parkShown = signal(PARK_PAGE);
  protected readonly parkQuery = signal('');
  /** The other stations of the park: free first, then occupied, out of order and unknown; filtered by the search. */
  protected readonly parkMembers = computed(() => {
    const query = this.parkQuery().trim().toLowerCase();
    return [...(this.detail()?.data.park ?? [])]
      .filter((member) => query === '' || `${member.name ?? ''} ${member.address ?? ''}`.toLowerCase().includes(query))
      .sort((a, b) => AVAILABILITY_ORDER.indexOf(a.availability) - AVAILABILITY_ORDER.indexOf(b.availability));
  });
  protected readonly nextPage = computed(() => Math.min(PARK_PAGE, this.parkMembers().length - this.parkShown()));
  /** EVSEs of the whole park, this location included. */
  protected readonly parkTotal = computed(() => this.parkSum((location) => location.evsesTotal));
  protected readonly parkFree = computed(() => this.parkSum((location) => location.evsesAvailable));

  private readonly auth = inject(AuthService);
  /** Timeline and heatmap need evse:read; everybody sees the EVSEs with their current status. */
  protected readonly history = this.auth.hasPermission('evse:read');
  /** The ad-hoc price per EVSE needs price:read. */
  protected readonly prices = this.auth.hasPermission('price:read');
  private readonly cards = inject(CardsStore);
  protected readonly example = '30 kWh in 40 min';
  protected readonly parts = priceParts;
  protected readonly money = money;

  protected readonly timelineDays = TIMELINE_DAYS;
  protected readonly occupancyWeeks = OCCUPANCY_WEEKS;
  protected readonly statusOf = statusOf;
  protected readonly faultSource = faultSource;
  protected readonly busy = busyText;
  protected readonly availabilityColor = (key: keyof typeof AVAILABILITY) => tokenColor(AVAILABILITY[key].token);
  protected readonly availabilityIcon = (key: keyof typeof AVAILABILITY) => AVAILABILITY[key].icon;

  protected readonly notice = signal<'missing' | 'failed' | null>(null);
  protected readonly detail = signal<LocationDetailResponse | null>(null);
  protected readonly range = signal({ from: new Date(), to: new Date() });
  private readonly timelines = signal<Record<string, Segment[] | 'error'>>({});
  private readonly occupancies = signal<Record<string, Occupancy | 'error'>>({});
  private readonly priceResults = signal<Record<string, EvsePricesResponse['data'] | 'error'>>({});
  /** "all", "park" (the whole charging park) or the id of the EVSE whose heatmap is shown. */
  protected readonly heatmapOf = signal('all');
  /** The combined occupancy of the whole park, loaded when it is chosen first. */
  private readonly parkOccupancy = signal<LocationOccupancy | 'error' | null>(null);
  /** The location belongs to a charging park and is not shown inside the park view of another one. */
  protected readonly inPark = computed(() => (this.detail()?.data.park.length ?? 0) > 0 && !this.nested());

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
    if (this.heatmapOf() === 'park') {
      const park = this.parkOccupancy();
      return park === null ? 'loading' : park === 'error' ? 'error' : 'ready';
    }
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
    const park = this.parkOccupancy();
    if (this.heatmapOf() === 'park' && park !== null && park !== 'error') {
      return { days: park.days, observations: park.observations, utilization: park.utilization, scope: park };
    }
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
      scope: null,
    };
  });

  constructor() {
    // Another location in the same card: start over.
    effect(() => {
      const id = this.locationId();
      untracked(() => {
        this.parkOpen.set(false);
        this.parkShown.set(PARK_PAGE);
        this.parkQuery.set('');
        void this.load(id);
      });
    });
  }

  /** Sum over this location and the other locations of its park. */
  private parkSum(count: (location: LocationSummary) => number): number {
    const detail = this.detail();
    return detail === null ? 0 : [detail.data.location, ...detail.data.park].reduce((sum, location) => sum + count(location), 0);
  }

  protected timelineState(evseId: string): LoadState {
    const segments = this.timelines()[evseId];
    return segments === undefined ? 'loading' : segments === 'error' ? 'error' : 'ready';
  }

  protected segments(evseId: string): Segment[] {
    return this.timelines()[evseId] as Segment[];
  }

  /** "Typ 2 · Buchse · 22 kW": the type, socket or cable, the power. */
  protected connectorText(connector: EvseDetail['connectors'][number]): string {
    return [
      connectorLabel(connector.standard),
      connector.format === null ? null : formatLabel(connector.format),
      connector.maxPowerKw === null ? null : `${connector.maxPowerKw} kW`,
    ]
      .filter((part) => part !== null)
      .join(' · ');
  }


  private async load(id: string): Promise<void> {
    const request = ++this.request;
    this.detail.set(null);
    this.notice.set(null);
    this.timelines.set({});
    this.occupancies.set({});
    this.priceResults.set({});
    this.heatmapOf.set('all');
    this.parkOccupancy.set(null);
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

  /** The cheapest of the user's own cards at the EVSE (the list is sorted by cost); undefined without one. */
  protected ownCheapest(pricing: EvsePricesResponse['data']): EmpPrice | undefined {
    const cards = this.cards.cards();
    return pricing.emps.find((emp) => cards.includes(emp.emp));
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

  protected searchPark(event: Event): void {
    this.parkQuery.set((event.target as HTMLInputElement).value);
    this.parkShown.set(PARK_PAGE);
  }

  /** The park view shows the combined occupancy of the whole park; closing it goes back to this station. */
  protected togglePark(): void {
    const open = !this.parkOpen();
    this.parkOpen.set(open);
    if (this.history) {
      this.showHeatmap(open ? 'park' : 'all');
    }
  }

  /** Shows the heatmap of the choice; the whole park is loaded with one request the first time. */
  protected showHeatmap(choice: string): void {
    this.heatmapOf.set(choice);
    if (choice === 'park' && this.parkOccupancy() === null) {
      void this.loadParkOccupancy(this.request);
    }
  }

  private async loadParkOccupancy(request: number): Promise<void> {
    let result: LocationOccupancy | 'error';
    try {
      result = (await this.service.locationOccupancy(this.locationId(), OCCUPANCY_WEEKS, true)).data;
    } catch {
      result = 'error';
    }
    if (request === this.request) {
      this.parkOccupancy.set(result);
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
