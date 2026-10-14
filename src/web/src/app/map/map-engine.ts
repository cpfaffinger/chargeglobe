import { Injectable } from '@angular/core';

/** What the engine needs to show the base map (docs/adr/0007-karte-standort-routing.md). */
export interface MapViewOptions {
  container: HTMLElement;
  styleUrl: string;
  /** Always visible, in addition to the attribution the style's sources carry. */
  attribution: string;
  center: { latitude: number; longitude: number };
  zoom: number;
  /** Results of the browser geolocation; the position itself never leaves the engine. */
  onGeolocate: (event: GeolocateEvent) => void;
}

export type GeolocateEvent =
  | { type: 'found' }
  /** code of the GeolocationPositionError: 1 denied, 2 unavailable, 3 timeout. */
  | { type: 'error'; code: number }
  /** Tracking switched off or the user moved the map away from the position. */
  | { type: 'ended' };

export interface MapHandle {
  /** Starts (or toggles) tracking the user's position; false if the control is not ready yet. */
  locate(): boolean;
  destroy(): void;
}

/**
 * Narrow abstraction over the map renderer. The page and its specs only know this class; the specs
 * replace it with a stub because WebGL is not reliably available in the headless test browser.
 */
@Injectable({ providedIn: 'root', useFactory: () => new MapLibreEngine() })
export abstract class MapEngine {
  abstract create(options: MapViewOptions): Promise<MapHandle>;
}

/**
 * The only file that imports maplibre-gl. The library is loaded on demand (own lazy chunk), so the
 * initial bundle stays small. Excluded from the coverage (codeCoverageExclude in angular.json): it
 * needs a WebGL context to run, so it must not contain logic beyond translating the calls.
 */
export class MapLibreEngine extends MapEngine {
  async create(options: MapViewOptions): Promise<MapHandle> {
    const maplibre = await import('maplibre-gl');
    // The worker ships as an asset (angular.json), next to the app instead of next to the chunk.
    maplibre.setWorkerUrl(new URL(`maplibre/maplibre-gl-worker.mjs?v=${maplibre.getVersion()}`, document.baseURI).href);

    const map = new maplibre.Map({
      container: options.container,
      style: options.styleUrl,
      center: [options.center.longitude, options.center.latitude],
      zoom: options.zoom,
      attributionControl: false,
    });
    map.addControl(new maplibre.AttributionControl({ compact: false, customAttribution: options.attribution }));
    map.addControl(new maplibre.NavigationControl(), 'top-right');
    const geolocate = new maplibre.GeolocateControl({
      positionOptions: { enableHighAccuracy: true },
      trackUserLocation: true,
    });
    map.addControl(geolocate, 'top-right');
    geolocate.on('geolocate', () => options.onGeolocate({ type: 'found' }));
    geolocate.on('error', (event) => options.onGeolocate({ type: 'error', code: event.code }));
    geolocate.on('trackuserlocationend', () => options.onGeolocate({ type: 'ended' }));

    return {
      locate: () => geolocate.trigger(),
      destroy: () => map.remove(),
    };
  }
}
