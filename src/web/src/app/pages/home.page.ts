import { DatePipe, DecimalPipe, NgTemplateOutlet } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatMenuModule } from '@angular/material/menu';
import { AppIcon } from '../design/icon';
import { ThemeService, Viewport } from '../design/theme.service';
import { LocationDetails } from '../evses/location-details';
import { FavoriteButton } from '../favorites/favorite-button';
import { MARKERS, MARKER_KEYS, MarkerKey, markerOf, resolveColors, tokenColor } from '../map/availability';
import { BrowserCapabilities } from '../map/browser-capabilities';
import { MapConfig, MapConfigService } from '../map/map-config.service';
import { GeolocateEvent, MapEngine, MapHandle, MapTheme } from '../map/map-engine';
import { MapFilterPanel } from '../map/map-filter-panel';
import { MapOptionsPanel } from '../map/map-options-panel';
import { MapOptionsStore } from '../map/map-options';
import { MapFilterStore, filterParams } from '../map/map-filters';
import { MapLink, ShareResult } from '../map/map-link';
import { MapView, MapViewStore } from '../map/map-view.store';
import { Bbox, LocationSummary, LocationsService } from '../map/locations.service';
import { MapFocus } from '../search/search.service';

/** Zoom of the map on a search hit: streets and single locations visible. */
export const FOCUS_ZOOM = 16;

/**
 * Age up to which loaded locations count as current: zooming into an area that was loaded completely within this
 * time needs no new request.
 */
export const LOCATIONS_FRESH_MS = 60_000;

/** Reasons the map or the own position cannot be shown. */
export type MapNotice =
  | 'config'
  | 'webgl'
  | 'map'
  | 'locations'
  | 'insecure'
  | 'unsupported'
  | 'denied'
  | 'unavailable'
  | 'not-ready';

const NOTICES: Record<MapNotice, string> = {
  config: 'Die Kartenkonfiguration konnte nicht geladen werden. Bitte lade die Seite später neu.',
  webgl: 'Dein Browser unterstützt kein WebGL, das die Karte zum Zeichnen braucht. Aktiviere die Hardwarebeschleunigung oder nutze einen aktuellen Browser.',
  map: 'Die Karte konnte nicht angezeigt werden. Bitte lade die Seite neu.',
  locations: 'Die Ladepunkte konnten nicht geladen werden. Verschiebe die Karte, um es erneut zu versuchen.',
  insecure: 'Der Standort ist nur über eine sichere Verbindung (HTTPS) verfügbar.',
  unsupported: 'Dein Browser bietet keine Standortbestimmung an.',
  denied: 'Die Standortfreigabe wurde abgelehnt. Du kannst sie in den Website-Einstellungen deines Browsers erlauben.',
  unavailable: 'Dein Standort konnte nicht ermittelt werden. Bitte versuche es erneut.',
  'not-ready': 'Die Standortfunktion ist noch nicht bereit. Bitte versuche es gleich noch einmal.',
};

const SHARE_MESSAGES: Record<ShareResult, string | null> = {
  shared: null,
  cancelled: null,
  copied: 'Der Link zu dieser Ansicht ist in der Zwischenablage.',
  failed: 'Der Link konnte nicht kopiert werden. Kopiere die Adresse aus der Adresszeile.',
};

/** Whether the box `inner` lies within `outer`. */
const covers = (outer: Bbox, inner: Bbox): boolean =>
  inner.west >= outer.west && inner.east <= outer.east && inner.south >= outer.south && inner.north <= outer.north;

/**
 * Start page, usable without an account: the base map (docs/adr/0007-karte-standort-routing.md) with the charging
 * locations of the visible area and the own position. A click on a location opens its details in the info card
 * next to the map (a bottom sheet on small screens), so the map keeps its position. The map opens at the last
 * view; "Startposition" returns to the view the user chose (both stay in the browser), else to the default of the
 * configuration. The position is processed in the browser only and never sent to the API.
 */
@Component({
  imports: [AppIcon, DatePipe, DecimalPipe, FavoriteButton, LocationDetails, MapFilterPanel, MapOptionsPanel, MatButtonModule, MatCardModule, MatMenuModule, NgTemplateOutlet],
  selector: 'app-home',
  host: { '[class.has-card]': 'selected() !== null', '[class.has-filters]': 'filtersOpen() || optionsOpen()' },
  template: `
    <ng-template #card>
      @if (selected(); as location) {
        <mat-card id="location-card" appearance="outlined" role="region" aria-label="Ladestandort">
          <mat-card-content>
            <div class="card-head">
              <div>
                <h2>{{ location.name ?? 'Ladestandort' }}</h2>
                <p class="address">{{ location.address }}, {{ location.postalCode }} {{ location.city }}</p>
              </div>
              <app-favorite-button [locationId]="location.id" />
              <button matIconButton type="button" id="close-card" aria-label="Schließen" (click)="selected.set(null)">
                <app-icon name="close" />
              </button>
            </div>
            @if (location.operator) {
              <p class="operator">{{ location.operator }}</p>
            }
            <p class="availability">
              <span class="status-dot" [style.background]="color(marker(location))"><app-icon [name]="markers[marker(location)].icon" /></span>
              {{ location.evsesAvailable }} von {{ location.evsesTotal }} Ladepunkten frei
              @if (location.maxPowerKw !== null) {
                · bis {{ location.maxPowerKw | number: '1.0-1' : 'en-US' }} kW
              }
            </p>
            <p class="status-age">
              @if (location.statusObservedAt) {
                Status von {{ location.statusObservedAt | date: 'dd.MM.yyyy HH:mm' }}
              } @else {
                Kein aktueller Status
              }
            </p>
            <!-- The details are a chunk of their own and load after the card is visible. -->
            @defer (on immediate) {
              <app-location-details [locationId]="location.id" />
            } @placeholder {
              <p class="details-state">Ladepunkte werden geladen …</p>
            }
          </mat-card-content>
        </mat-card>
      }
    </ng-template>
    <div #map id="map" role="region" aria-label="Karte"></div>
    @if (loading()) {
      <div id="map-loading" role="progressbar" aria-label="Ladepunkte werden geladen"></div>
    }
    <div id="map-overlay">
      @if (notice(); as key) {
        <p id="map-notice" class="map-message" role="status">{{ notices[key] }}</p>
      }
      @if (homeSaved()) {
        <p id="home-saved" class="map-message" role="status">Dieser Ausschnitt ist jetzt deine Startposition.</p>
      }
      @if (shareMessage(); as message) {
        <p id="share-message" class="map-message" role="status">{{ message }}</p>
      }
      @if (truncated()) {
        <p id="truncated" class="map-message" role="status">Es werden nur die nächsten {{ limit }} Standorte gezeigt – zoome hinein, um alle zu sehen.</p>
      }
      @if (!viewport.mobile()) {
        <ng-container [ngTemplateOutlet]="card" />
      }
    </div>
    @if (viewport.mobile()) {
      <ng-container [ngTemplateOutlet]="card" />
    }
    @if (filtersOpen()) {
      <!-- The filter controls are a chunk of their own, loaded when opened for the first time. -->
      @defer (on immediate) {
        <app-map-filter-panel (closed)="filtersOpen.set(false)" />
      }
    }
    @if (optionsOpen()) {
      @defer (on immediate) {
        <app-map-options-panel (closed)="optionsOpen.set(false)" />
      }
    }
    <div id="map-actions">
      <div class="home-actions">
        <button matMiniFab type="button" id="home" [disabled]="!handle()" title="Zur Startposition" aria-label="Zur Startposition" (click)="goHome()">
          <app-icon name="home" />
        </button>
        <button matMiniFab type="button" id="options-toggle" title="Kartenoptionen" aria-label="Kartenoptionen" [attr.aria-expanded]="optionsOpen()" (click)="toggleOptions()">
          <app-icon name="layers" />
        </button>
        <button matMiniFab type="button" id="share" [disabled]="!view()" title="Ansicht teilen" aria-label="Ansicht teilen" (click)="share()">
          <app-icon name="share" />
        </button>
        <button matMiniFab type="button" id="home-menu" title="Startposition festlegen" aria-label="Startposition festlegen" [matMenuTriggerFor]="homeMenu">
          <app-icon name="more_vert" />
        </button>
        <mat-menu #homeMenu="matMenu">
          <button mat-menu-item type="button" id="set-home" [disabled]="!view()" (click)="setHome()">Aktuellen Ausschnitt als Startposition</button>
          <button mat-menu-item type="button" id="reset-home" [disabled]="!views.home()" (click)="views.resetHome()">Startposition zurücksetzen</button>
        </mat-menu>
      </div>
      <button matFab extended type="button" id="locate" [disabled]="!handle()" (click)="locate()">
        <app-icon name="my_location" />{{ locating() ? 'Standort wird gesucht …' : 'Mein Standort' }}
      </button>
    </div>
    <div id="map-legend-bar">
      <ul id="legend" aria-label="Legende">
        @for (key of markerKeys; track key) {
          <li>
            <span class="status-dot" [style.background]="color(key)"><app-icon [name]="markers[key].icon" /></span>{{ markers[key].label }}
          </li>
        }
      </ul>
      <button matButton="tonal" type="button" id="filter-toggle" [attr.aria-expanded]="filtersOpen()" (click)="toggleFilters()">
        <app-icon name="filter_list" />Filter
        @if (filterStore.count() > 0) {
          <span class="filter-count" [attr.aria-label]="filterStore.count() + ' aktiv'">{{ filterStore.count() }}</span>
        }
      </button>
    </div>
    @if (attribution().length > 0) {
      <p id="data-attribution">{{ attribution().join(' · ') }}</p>
    }
  `,
})
export class HomePage {
  private readonly configService = inject(MapConfigService);
  private readonly engine = inject(MapEngine);
  private readonly browser = inject(BrowserCapabilities);
  private readonly locationsService = inject(LocationsService);
  private readonly theme = inject(ThemeService);
  private readonly focus = inject(MapFocus);
  protected readonly views = inject(MapViewStore);
  protected readonly filterStore = inject(MapFilterStore);
  private readonly link = inject(MapLink);
  /** The newest visible area, loaded again when the filters change. */
  private box: Bbox | null = null;
  /** Query parameters of the filters of the shown locations. */
  private filterKey: string | null = null;
  /** Location of a search hit whose info card opens once the map has loaded it. */
  private pending: string | null = null;
  protected readonly viewport = inject(Viewport);
  private config: MapConfig | null = null;
  /** The newest visible area that still has to be loaded; at most one request runs at a time. */
  private queued: Bbox | null = null;
  private busy = false;
  /** Area, completeness and time of the shown locations. */
  private shown: { box: Bbox; complete: boolean; at: number } | null = null;
  private readonly container = viewChild.required<ElementRef<HTMLElement>>('map');
  private destroyed = false;

  protected readonly notices = NOTICES;
  protected readonly notice = signal<MapNotice | null>(null);
  protected readonly locating = signal(false);
  protected readonly loading = signal(false);
  protected readonly handle = signal<MapHandle | null>(null);
  /** The current view (center and zoom), known after the map has loaded. */
  protected readonly view = signal<MapView | null>(null);
  protected readonly homeSaved = signal(false);
  protected readonly shareMessage = signal<string | null>(null);
  protected readonly filtersOpen = signal(false);
  protected readonly optionsOpen = signal(false);
  private readonly mapOptions = inject(MapOptionsStore);
  protected readonly locations = signal<LocationSummary[]>([]);
  protected readonly attribution = signal<string[]>([]);
  protected readonly truncated = signal(false);
  protected readonly selected = signal<LocationSummary | null>(null);
  protected readonly markers = MARKERS;
  protected readonly markerKeys = MARKER_KEYS;
  protected readonly marker = markerOf;
  protected readonly limit = LocationsService.limit;
  protected readonly color = (key: MarkerKey) => tokenColor(MARKERS[key].token);

  constructor() {
    // The renderer is the largest download; it loads while the configuration is on its way.
    this.engine.prepare();
    afterNextRender(() => void this.show());
    // Light/dark switch: the map takes the matching style and marker colours. A map created later reads them itself.
    effect(() => {
      const dark = this.theme.dark();
      untracked(() => this.handle()?.setTheme(this.mapTheme(dark)));
    });
    // A search hit: center the map on it; its card opens when the locations there have arrived.
    effect(() => {
      const target = this.focus.target();
      const handle = this.handle();
      if (target && handle) {
        untracked(() => {
          this.focus.target.set(null);
          this.pending = target.locationId;
          handle.flyTo(target.latitude, target.longitude, FOCUS_ZOOM);
          this.openPending();
        });
      }
    });
    // Other cluster options: the map combines the locations anew.
    effect(() => {
      const options = this.mapOptions.options();
      untracked(() => this.handle()?.setCluster(options));
    });
    // Other filters: the shown locations no longer fit; the visible area loads again.
    effect(() => {
      const key = JSON.stringify(filterParams(this.filterStore.filters()));
      untracked(() => {
        if (this.filterKey !== null && key !== this.filterKey && this.box) {
          this.shown = null;
          this.queue(this.box);
        }
        this.filterKey = key;
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.handle()?.destroy();
    });
  }

  protected async locate(): Promise<void> {
    if (!this.browser.secureContext()) {
      this.notice.set('insecure');
      return;
    }
    if (!this.browser.geolocation()) {
      this.notice.set('unsupported');
      return;
    }
    // After an earlier refusal the browser does not ask again, and MapLibre disables its control.
    if ((await this.browser.geolocationPermission()) === 'denied') {
      this.notice.set('denied');
      return;
    }
    this.notice.set(null);
    if (this.handle()!.locate()) {
      this.locating.set(true);
    } else {
      this.notice.set('not-ready');
    }
  }

  /** Back to the start position of the user, else to the default view of the configuration. */
  protected goHome(): void {
    const home = this.views.home() ?? this.defaultView();
    this.handle()!.flyTo(home.latitude, home.longitude, home.zoom);
  }

  protected toggleFilters(): void {
    this.filtersOpen.update((open) => !open);
    this.optionsOpen.set(false);
    this.selected.set(null);
  }

  protected toggleOptions(): void {
    this.optionsOpen.update((open) => !open);
    this.filtersOpen.set(false);
    this.selected.set(null);
  }

  /** Shares the address with view and filters (share sheet or clipboard). */
  protected async share(): Promise<void> {
    this.shareMessage.set(SHARE_MESSAGES[await this.link.share()]);
  }

  protected setHome(): void {
    this.views.setHome(this.view()!);
    this.homeSaved.set(true);
  }

  private defaultView(): MapView {
    return { latitude: this.config!.center.latitude, longitude: this.config!.center.longitude, zoom: this.config!.zoom };
  }

  private async show(): Promise<void> {
    let config;
    try {
      config = this.config = await this.configService.load();
    } catch {
      this.notice.set('config');
      return;
    }
    if (this.destroyed) {
      return;
    }
    if (!this.browser.webGl()) {
      this.notice.set('webgl');
      return;
    }
    // A shared link wins over the last view of this browser.
    const start = this.link.read() ?? this.views.last() ?? this.views.home() ?? this.defaultView();
    try {
      const handle = await this.engine.create({
        container: this.container().nativeElement,
        theme: this.mapTheme(this.theme.dark()),
        attribution: config.attribution,
        center: { latitude: start.latitude, longitude: start.longitude },
        zoom: start.zoom,
        cluster: this.mapOptions.options(),
        onGeolocate: (event) => this.geolocated(event),
        onViewChange: (bbox, view) => this.viewChanged(bbox, view),
        onSelect: (id) => this.select(id),
      });
      if (this.destroyed) {
        handle.destroy();
        return;
      }
      this.handle.set(handle);
      // Locations may have arrived before the engine handed out its handle.
      handle.showLocations(this.locations());
    } catch {
      this.notice.set('map');
    }
  }

  private select(id: string): void {
    this.selected.set(this.locations().find((location) => location.id === id) ?? null);
    if (this.selected()) {
      this.filtersOpen.set(false);
      this.optionsOpen.set(false);
    }
  }

  /**
   * Remembers the view (browser storage and address) and loads the locations of the visible area; the box is clamped
   * to valid coordinates.
   */
  private viewChanged(bbox: Bbox, view: MapView): void {
    this.view.set(view);
    this.views.saveLast(view);
    this.link.write(view);
    this.homeSaved.set(false);
    this.shareMessage.set(null);
    const box: Bbox = {
      west: Math.max(-180, bbox.west),
      south: Math.max(-90, bbox.south),
      east: Math.min(180, bbox.east),
      north: Math.min(90, bbox.north),
    };
    // Zoomed into an area whose locations are all shown and current: nothing new to load.
    this.box = box;
    const shown = this.shown;
    if (!this.busy && shown?.complete && Date.now() - shown.at < LOCATIONS_FRESH_MS && covers(shown.box, box)) {
      return;
    }
    this.queue(box);
  }

  private queue(box: Bbox): void {
    this.queued = box;
    if (!this.busy) {
      void this.loadLocations();
    }
  }

  /**
   * Loads the newest queued area until none is left. Areas passed over while a request runs are skipped, and an
   * answer is only shown if no newer area is waiting, so fast panning causes one request at a time.
   */
  private async loadLocations(): Promise<void> {
    this.busy = true;
    this.loading.set(true);
    while (this.queued && !this.destroyed) {
      const box = this.queued;
      this.queued = null;
      try {
        const response = await this.locationsService.inBox(box, this.filterStore.filters());
        if (this.queued || this.destroyed) {
          continue;
        }
        this.locations.set(response.data);
        this.attribution.set([...new Set(response.meta.attribution.map((source) => source.text))]);
        this.truncated.set(response.meta.truncated);
        this.shown = { box, complete: !response.meta.truncated, at: Date.now() };
        this.handle()?.showLocations(response.data);
        this.openPending();
        if (this.notice() === 'locations') {
          this.notice.set(null);
        }
      } catch {
        if (!this.queued) {
          this.notice.set('locations');
        }
      }
    }
    this.busy = false;
    this.loading.set(false);
  }

  /** Opens the card of the searched location as soon as it is among the loaded ones. */
  private openPending(): void {
    const location = this.locations().find((item) => item.id === this.pending);
    if (location) {
      this.selected.set(location);
      this.pending = null;
    }
  }

  /** Style and marker colours for the map renderer, resolved from the theme tokens of the current mode. */
  private mapTheme(dark: boolean): MapTheme {
    const tokens = MARKER_KEYS.map((key) => MARKERS[key].token);
    const colors = resolveColors([...tokens, '--cg-on-status', '--mat-sys-surface', '--mat-sys-primary', '--mat-sys-on-primary']);
    return {
      styleUrl: dark ? this.config!.styleUrlDark : this.config!.styleUrl,
      status: Object.fromEntries(MARKER_KEYS.map((key) => [key, colors[MARKERS[key].token]])) as MapTheme['status'],
      onStatus: colors['--cg-on-status'],
      ring: colors['--mat-sys-surface'],
      cluster: colors['--mat-sys-primary'],
      onCluster: colors['--mat-sys-on-primary'],
    };
  }

  private geolocated(event: GeolocateEvent): void {
    this.locating.set(false);
    if (event.type === 'error') {
      this.notice.set(event.code === 1 ? 'denied' : 'unavailable');
    } else if (event.type === 'found') {
      this.notice.set(null);
    }
  }
}
