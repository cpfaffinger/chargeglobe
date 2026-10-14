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
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import { ThemeService, Viewport } from '../design/theme.service';
import { AVAILABILITIES, AVAILABILITY, resolveColors, tokenColor } from '../map/availability';
import { BrowserCapabilities } from '../map/browser-capabilities';
import { MapConfig, MapConfigService } from '../map/map-config.service';
import { GeolocateEvent, MapEngine, MapHandle, MapTheme } from '../map/map-engine';
import { Bbox, LocationSummary, LocationsService } from '../map/locations.service';
import { MapFocus } from '../search/search.service';

/** Zoom of the map on a search hit: streets and single locations visible. */
export const FOCUS_ZOOM = 16;

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

/**
 * Start page, usable without an account: the base map (docs/adr/0007-karte-standort-routing.md) with
 * the charging locations of the visible area and the own position. The position is processed in the
 * browser only and never sent to the API.
 */
@Component({
  imports: [AppIcon, DatePipe, DecimalPipe, MatButtonModule, MatCardModule, NgTemplateOutlet, RouterLink],
  selector: 'app-home',
  host: { '[class.has-card]': 'selected() !== null' },
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
              <button matIconButton type="button" id="close-card" aria-label="Schließen" (click)="selected.set(null)">
                <app-icon name="close" />
              </button>
            </div>
            @if (location.operator) {
              <p class="operator">{{ location.operator }}</p>
            }
            <p class="availability">
              <span class="status-dot" [style.background]="color(location.availability)"><app-icon [name]="availability[location.availability].icon" /></span>
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
            @if (!auth.isAuthenticated()) {
              <p class="details">
                <a matButton="filled" id="details" [routerLink]="['/locations', location.id]">Details</a>
                <span class="hint">Verlauf und Auslastung nach Anmeldung</span>
              </p>
            } @else if (auth.hasPermission('evse:read')) {
              <p class="details"><a matButton="filled" id="details" [routerLink]="['/locations', location.id]">Details</a></p>
            }
          </mat-card-content>
        </mat-card>
      }
    </ng-template>
    <div #map id="map" role="region" aria-label="Karte"></div>
    <div id="map-overlay">
      @if (notice(); as key) {
        <p id="map-notice" class="map-message" role="status">{{ notices[key] }}</p>
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
    <button matFab extended type="button" id="locate" [disabled]="!handle()" (click)="locate()">
      <app-icon name="my_location" />{{ locating() ? 'Standort wird gesucht …' : 'Mein Standort' }}
    </button>
    <ul id="legend" aria-label="Legende">
      @for (key of availabilities; track key) {
        <li>
          <span class="status-dot" [style.background]="color(key)"><app-icon [name]="availability[key].icon" /></span>{{ availability[key].label }}
        </li>
      }
    </ul>
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
  /** Location of a search hit whose info card opens once the map has loaded it. */
  private pending: string | null = null;
  protected readonly auth = inject(AuthService);
  protected readonly viewport = inject(Viewport);
  private config: MapConfig | null = null;
  /** Number of the newest location request; older answers are dropped. */
  private request = 0;
  private readonly container = viewChild.required<ElementRef<HTMLElement>>('map');
  private destroyed = false;

  protected readonly notices = NOTICES;
  protected readonly notice = signal<MapNotice | null>(null);
  protected readonly locating = signal(false);
  protected readonly handle = signal<MapHandle | null>(null);
  protected readonly locations = signal<LocationSummary[]>([]);
  protected readonly attribution = signal<string[]>([]);
  protected readonly truncated = signal(false);
  protected readonly selected = signal<LocationSummary | null>(null);
  protected readonly availability = AVAILABILITY;
  protected readonly availabilities = AVAILABILITIES;
  protected readonly limit = LocationsService.limit;
  protected readonly color = (key: keyof typeof AVAILABILITY) => tokenColor(AVAILABILITY[key].token);

  constructor() {
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
    try {
      const handle = await this.engine.create({
        container: this.container().nativeElement,
        theme: this.mapTheme(this.theme.dark()),
        attribution: config.attribution,
        center: config.center,
        zoom: config.zoom,
        onGeolocate: (event) => this.geolocated(event),
        onViewChange: (bbox) => void this.loadLocations(bbox),
        onSelect: (id) => this.selected.set(this.locations().find((location) => location.id === id) ?? null),
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

  /** Loads the locations of the visible area; the box is clamped to valid coordinates (world wrap). */
  private async loadLocations(bbox: Bbox): Promise<void> {
    const request = ++this.request;
    const box: Bbox = {
      west: Math.max(-180, bbox.west),
      south: Math.max(-90, bbox.south),
      east: Math.min(180, bbox.east),
      north: Math.min(90, bbox.north),
    };
    try {
      const response = await this.locationsService.inBox(box);
      if (request !== this.request || this.destroyed) {
        return;
      }
      this.locations.set(response.data);
      this.attribution.set([...new Set(response.meta.attribution.map((source) => source.text))]);
      this.truncated.set(response.meta.truncated);
      this.handle()?.showLocations(response.data);
      this.openPending();
      if (this.notice() === 'locations') {
        this.notice.set(null);
      }
    } catch {
      if (request === this.request) {
        this.notice.set('locations');
      }
    }
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
    const tokens = AVAILABILITIES.map((key) => AVAILABILITY[key].token);
    const colors = resolveColors([...tokens, '--cg-on-status', '--mat-sys-surface', '--mat-sys-primary', '--mat-sys-on-primary']);
    return {
      styleUrl: dark ? this.config!.styleUrlDark : this.config!.styleUrl,
      status: Object.fromEntries(AVAILABILITIES.map((key) => [key, colors[AVAILABILITY[key].token]])) as MapTheme['status'],
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
