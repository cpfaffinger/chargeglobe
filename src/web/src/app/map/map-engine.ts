import { Injectable } from '@angular/core';
// Type-only imports are erased by the compiler; the library itself is still loaded on demand.
import type { FeatureCollection, Point } from 'geojson';
import type { ExpressionSpecification, GeoJSONSource } from 'maplibre-gl';
import { AVAILABILITIES, AVAILABILITY } from './availability';
import { Bbox, LocationSummary } from './locations.service';

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
  /** The visible area after loading and after every move or zoom. */
  onViewChange: (bbox: Bbox) => void;
  /** A location marker was clicked. */
  onSelect: (locationId: string) => void;
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
  /** Replaces the location markers (clustered, coloured by availability). */
  showLocations(locations: LocationSummary[]): void;
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

    const source = 'locations';
    const toGeoJson = (locations: LocationSummary[]): FeatureCollection => ({
      type: 'FeatureCollection',
      features: locations.map((location) => ({
        type: 'Feature',
        geometry: { type: 'Point', coordinates: [location.longitude, location.latitude] },
        properties: { id: location.id, availability: location.availability },
      })),
    });
    let pending: LocationSummary[] = [];
    let loaded = false;
    const emitView = () => {
      const bounds = map.getBounds();
      options.onViewChange({ west: bounds.getWest(), south: bounds.getSouth(), east: bounds.getEast(), north: bounds.getNorth() });
    };
    map.on('load', () => {
      map.addSource(source, { type: 'geojson', data: toGeoJson(pending), cluster: true, clusterRadius: 50, clusterMaxZoom: 13 });
      map.addLayer({
        id: 'location-clusters',
        type: 'circle',
        source,
        filter: ['has', 'point_count'],
        paint: {
          'circle-color': '#1565c0',
          'circle-radius': ['step', ['get', 'point_count'], 14, 50, 18, 200, 24],
          'circle-stroke-width': 2,
          'circle-stroke-color': '#ffffff',
        },
      });
      map.addLayer({
        id: 'location-cluster-count',
        type: 'symbol',
        source,
        filter: ['has', 'point_count'],
        layout: { 'text-field': ['get', 'point_count_abbreviated'], 'text-font': ['Noto Sans Bold'], 'text-size': 12 },
        paint: { 'text-color': '#ffffff' },
      });
      map.addLayer({
        id: 'location-points',
        type: 'circle',
        source,
        filter: ['!', ['has', 'point_count']],
        paint: {
          'circle-color': [
            'match',
            ['get', 'availability'],
            ...AVAILABILITIES.flatMap((key) => [key, AVAILABILITY[key].color]),
            AVAILABILITY.UNKNOWN.color,
          ] as unknown as ExpressionSpecification,
          'circle-radius': 7,
          'circle-stroke-width': 2,
          'circle-stroke-color': '#ffffff',
        },
      });
      loaded = true;
      emitView();
    });
    map.on('moveend', () => loaded && emitView());
    map.on('click', 'location-clusters', async (event) => {
      const feature = event.features![0];
      const zoom = await (map.getSource(source) as GeoJSONSource).getClusterExpansionZoom(feature.properties['cluster_id']);
      map.easeTo({ center: (feature.geometry as Point).coordinates as [number, number], zoom });
    });
    map.on('click', 'location-points', (event) => options.onSelect(event.features![0].properties['id']));
    for (const layer of ['location-clusters', 'location-points']) {
      map.on('mouseenter', layer, () => (map.getCanvas().style.cursor = 'pointer'));
      map.on('mouseleave', layer, () => (map.getCanvas().style.cursor = ''));
    }

    return {
      locate: () => geolocate.trigger(),
      showLocations: (locations) => {
        pending = locations;
        if (loaded) {
          (map.getSource(source) as GeoJSONSource).setData(toGeoJson(locations));
        }
      },
      destroy: () => map.remove(),
    };
  }
}
