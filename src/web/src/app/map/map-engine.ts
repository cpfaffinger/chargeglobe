import { Injectable } from '@angular/core';
// Type-only imports are erased by the compiler; the library itself is still loaded on demand.
import type { FeatureCollection, Point } from 'geojson';
import type { GeoJSONSource, Map as MapLibreMap } from 'maplibre-gl';
import { ICONS } from '../design/icon';
import { AVAILABILITIES, AVAILABILITY } from './availability';
import { Availability, Bbox, LocationSummary } from './locations.service';
import { MapView } from './map-view.store';

/** Base map style and marker colours of the current colour scheme, resolved from the theme tokens. */
export interface MapTheme {
  styleUrl: string;
  status: Record<Availability, string>;
  /** Symbol on a status marker. */
  onStatus: string;
  /** Ring around markers, the surface colour. */
  ring: string;
  cluster: string;
  onCluster: string;
}

/** What the engine needs to show the base map (docs/adr/0007-karte-standort-routing.md). */
export interface MapViewOptions {
  container: HTMLElement;
  theme: MapTheme;
  /** Always visible, in addition to the attribution the style's sources carry. */
  attribution: string;
  center: { latitude: number; longitude: number };
  zoom: number;
  /** Results of the browser geolocation; the position itself never leaves the engine. */
  onGeolocate: (event: GeolocateEvent) => void;
  /** The visible area with center and zoom after loading and after every move or zoom. */
  onViewChange: (bbox: Bbox, view: MapView) => void;
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
  /** Switches base map style and marker colours, e.g. after a change between light and dark mode. */
  setTheme(theme: MapTheme): void;
  /** Moves the view to a point (e.g. a search hit); the view change loads the locations there. */
  flyTo(latitude: number, longitude: number, zoom: number): void;
  destroy(): void;
}

/**
 * Narrow abstraction over the map renderer. The page and its specs only know this class; the specs
 * replace it with a stub because WebGL is not reliably available in the headless test browser.
 */
@Injectable({ providedIn: 'root', useFactory: () => new MapLibreEngine() })
export abstract class MapEngine {
  /** Starts loading the renderer, e.g. while the configuration is still on its way. */
  abstract prepare(): void;
  abstract create(options: MapViewOptions): Promise<MapHandle>;
}

/**
 * The only file that imports maplibre-gl. The library is loaded on demand (own lazy chunk), so the
 * initial bundle stays small. Excluded from the coverage (codeCoverageExclude in angular.json): it
 * needs a WebGL context to run, so it must not contain logic beyond translating the calls.
 */
export class MapLibreEngine extends MapEngine {
  private library: Promise<typeof import('maplibre-gl')> | null = null;

  prepare(): void {
    this.library ??= import('maplibre-gl');
  }

  async create(options: MapViewOptions): Promise<MapHandle> {
    this.prepare();
    const maplibre = await this.library!;
    // The worker ships as an asset (angular.json), next to the app instead of next to the chunk.
    maplibre.setWorkerUrl(new URL(`maplibre/maplibre-gl-worker.mjs?v=${maplibre.getVersion()}`, document.baseURI).href);

    let theme = options.theme;
    const map = new maplibre.Map({
      container: options.container,
      style: theme.styleUrl,
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
      const center = map.getCenter();
      options.onViewChange(
        { west: bounds.getWest(), south: bounds.getSouth(), east: bounds.getEast(), north: bounds.getNorth() },
        { latitude: center.lat, longitude: center.lng, zoom: map.getZoom() },
      );
    };
    // Every style load (the first one and each theme change) starts without the app's source, images and layers.
    map.on('style.load', () => {
      addMarkerImages(map, theme);
      map.addSource(source, { type: 'geojson', data: toGeoJson(pending), cluster: true, clusterRadius: 50, clusterMaxZoom: 13 });
      map.addLayer({
        id: 'location-clusters',
        type: 'circle',
        source,
        filter: ['has', 'point_count'],
        paint: {
          'circle-color': theme.cluster,
          'circle-radius': ['step', ['get', 'point_count'], 16, 50, 20, 200, 26],
          'circle-stroke-width': 3,
          'circle-stroke-color': theme.ring,
        },
      });
      map.addLayer({
        id: 'location-cluster-count',
        type: 'symbol',
        source,
        filter: ['has', 'point_count'],
        layout: { 'text-field': ['get', 'point_count_abbreviated'], 'text-font': ['Noto Sans Bold'], 'text-size': 13 },
        paint: { 'text-color': theme.onCluster },
      });
      map.addLayer({
        id: 'location-points',
        type: 'symbol',
        source,
        filter: ['!', ['has', 'point_count']],
        layout: {
          'icon-image': ['concat', 'status-', ['get', 'availability']],
          'icon-allow-overlap': true,
          'icon-ignore-placement': true,
        },
      });
      if (!loaded) {
        loaded = true;
        emitView();
      }
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
      setTheme: (next) => {
        theme = next;
        // setStyle reloads the style; the style.load handler adds markers in the new colours.
        map.setStyle(next.styleUrl);
      },
      flyTo: (latitude, longitude, zoom) => map.flyTo({ center: [longitude, latitude], zoom }),
      destroy: () => map.remove(),
    };
  }
}

/** One marker image per availability: a disc in the status colour with a ring and the status symbol. */
function addMarkerImages(map: MapLibreMap, theme: MapTheme): void {
  const ratio = 2;
  const size = 28 * ratio;
  for (const key of AVAILABILITIES) {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = size;
    const context = canvas.getContext('2d')!;
    context.beginPath();
    context.arc(size / 2, size / 2, size / 2 - 2 * ratio, 0, 2 * Math.PI);
    context.fillStyle = theme.status[key];
    context.fill();
    context.lineWidth = 3 * ratio;
    context.strokeStyle = theme.ring;
    context.stroke();
    const scale = (16 * ratio) / 24;
    context.translate((size - 24 * scale) / 2, (size - 24 * scale) / 2);
    context.scale(scale, scale);
    context.fillStyle = theme.onStatus;
    context.fill(new Path2D(ICONS[AVAILABILITY[key].icon]));
    map.addImage(`status-${key}`, context.getImageData(0, 0, size, size), { pixelRatio: ratio });
  }
}
