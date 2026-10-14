import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { BrowserCapabilities } from '../map/browser-capabilities';
import { MapConfigService } from '../map/map-config.service';
import { GeolocateEvent, MapEngine, MapHandle } from '../map/map-engine';

/** Reasons the map or the own position cannot be shown. */
export type MapNotice = 'config' | 'webgl' | 'map' | 'insecure' | 'unsupported' | 'denied' | 'unavailable' | 'not-ready';

const NOTICES: Record<MapNotice, string> = {
  config: 'Die Kartenkonfiguration konnte nicht geladen werden. Bitte lade die Seite später neu.',
  webgl: 'Dein Browser unterstützt kein WebGL, das die Karte zum Zeichnen braucht. Aktiviere die Hardwarebeschleunigung oder nutze einen aktuellen Browser.',
  map: 'Die Karte konnte nicht angezeigt werden. Bitte lade die Seite neu.',
  insecure: 'Der Standort ist nur über eine sichere Verbindung (HTTPS) verfügbar.',
  unsupported: 'Dein Browser bietet keine Standortbestimmung an.',
  denied: 'Die Standortfreigabe wurde abgelehnt. Du kannst sie in den Website-Einstellungen deines Browsers erlauben.',
  unavailable: 'Dein Standort konnte nicht ermittelt werden. Bitte versuche es erneut.',
  'not-ready': 'Die Standortfunktion ist noch nicht bereit. Bitte versuche es gleich noch einmal.',
};

/**
 * Start page, usable without an account: the base map (docs/adr/0007-karte-standort-routing.md) with
 * the own position. The position is processed in the browser only and never sent to the API.
 */
@Component({
  selector: 'app-home',
  template: `
    <div #map id="map" role="region" aria-label="Karte"></div>
    <div id="map-overlay">
      <button type="button" id="locate" [disabled]="!handle()" (click)="locate()">
        {{ locating() ? 'Standort wird gesucht …' : 'Mein Standort' }}
      </button>
      @if (notice(); as key) {
        <p id="map-notice" role="status">{{ notices[key] }}</p>
      }
    </div>
  `,
})
export class HomePage {
  private readonly configService = inject(MapConfigService);
  private readonly engine = inject(MapEngine);
  private readonly browser = inject(BrowserCapabilities);
  private readonly container = viewChild.required<ElementRef<HTMLElement>>('map');
  private destroyed = false;

  protected readonly notices = NOTICES;
  protected readonly notice = signal<MapNotice | null>(null);
  protected readonly locating = signal(false);
  protected readonly handle = signal<MapHandle | null>(null);

  constructor() {
    afterNextRender(() => void this.show());
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
      config = await this.configService.load();
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
        styleUrl: config.styleUrl,
        attribution: config.attribution,
        center: config.center,
        zoom: config.zoom,
        onGeolocate: (event) => this.geolocated(event),
      });
      if (this.destroyed) {
        handle.destroy();
        return;
      }
      this.handle.set(handle);
    } catch {
      this.notice.set('map');
    }
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
