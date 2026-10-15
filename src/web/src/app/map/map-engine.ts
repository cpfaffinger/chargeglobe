import { Injectable } from '@angular/core';
// Type-only imports are erased by the compiler; the library itself is still loaded on demand.
import type { FeatureCollection, Point } from 'geojson';
import type { GeoJSONSource, Map as MapLibreMap, Marker } from 'maplibre-gl';
import { ICONS } from '../design/icon';
import { MARKERS, MARKER_KEYS, MarkerKey, markerOf } from './availability';
import { CLUSTER_PROPERTIES, clusterCounts, clusterPie } from './cluster-pie';
import { Bbox, LocationSummary } from './locations.service';
import { MapOptions } from './map-options';
import { MapView } from './map-view.store';
import type { ReachAreas } from './routes.service';

/** Base map style and marker colours of the current colour scheme, resolved from the theme tokens. */
export interface MapTheme {
  styleUrl: string;
  /** Per marker: free locations by power class, the others by availability. */
  status: Record<MarkerKey, string>;
  /** Symbol on a status marker. */
  onStatus: string;
  /** Ring around markers, the surface colour. */
  ring: string;
  cluster: string;
  onCluster: string;
  /** Line of a calculated route. */
  route: string;
}

/** What the engine needs to show the base map (docs/adr/0007-karte-standort-routing.md). */
export interface MapViewOptions {
  container: HTMLElement;
  theme: MapTheme;
  /** Always visible, in addition to the attribution the style's sources carry. */
  attribution: string;
  center: { latitude: number; longitude: number };
  zoom: number;
  /** How nearby locations are combined (clusterRadius 0 = never). */
  cluster: MapOptions;
  /** Results of the browser geolocation; the position itself never leaves the engine. */
  onGeolocate: (event: GeolocateEvent) => void;
  /** The visible area with center and zoom after loading and after every move or zoom. */
  onViewChange: (bbox: Bbox, view: MapView) => void;
  /** A location marker was clicked. */
  onSelect: (locationId: string) => void;
  /** The map itself was clicked, not a marker or cluster (e.g. to pick a start or destination). */
  onClick: (latitude: number, longitude: number) => void;
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
  /** Changes how nearby locations are combined, without reloading the map. */
  setCluster(options: MapOptions): void;
  /** Moves the view to a point (e.g. a search hit); the view change loads the locations there. */
  flyTo(latitude: number, longitude: number, zoom: number): void;
  /** Draws a route ([longitude, latitude] positions) below the markers and fits the view to it; null removes it. */
  showRoute(coordinates: [number, number][] | null): void;
  /** Rings in the colour of the route below these locations (the stops along a route); an empty list removes them. */
  highlightLocations(locations: LocationSummary[]): void;
  /**
   * Draws areas reachable by car (largest first) as translucent layers below route and markers and fits the view to the
   * largest; null removes them.
   */
  showReach(areas: ReachAreas | null): void;
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
    let cluster = options.cluster;
    const clusterOptions = (value: MapOptions) => ({
      cluster: value.clusterRadius > 0,
      clusterRadius: value.clusterRadius,
      clusterMaxZoom: value.clusterMaxZoom,
    });
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
        properties: { id: location.id, marker: markerOf(location) },
      })),
    });
    let pending: LocationSummary[] = [];
    let route: [number, number][] = [];
    let stops: LocationSummary[] = [];
    const stopsSource = 'route-stops';
    const stopsData = (): FeatureCollection => ({
      type: 'FeatureCollection',
      features: stops.map((location) => ({
        type: 'Feature',
        geometry: { type: 'Point', coordinates: [location.longitude, location.latitude] },
        properties: { id: location.id },
      })),
    });
    const routeSource = 'route';
    const routeData = (): FeatureCollection => ({
      type: 'FeatureCollection',
      features: route.length > 1 ? [{ type: 'Feature', geometry: { type: 'LineString', coordinates: route }, properties: {} }] : [],
    });
    const reachSource = 'reach';
    let reach: ReachAreas = { type: 'FeatureCollection', features: [] };
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
      // The smaller areas lie on the larger ones, so the shade grows towards the start.
      map.addSource(reachSource, { type: 'geojson', data: reach });
      map.addLayer({ id: 'reach-fill', type: 'fill', source: reachSource, paint: { 'fill-color': theme.route, 'fill-opacity': 0.12 } });
      map.addLayer({ id: 'reach-line', type: 'line', source: reachSource, paint: { 'line-color': theme.route, 'line-width': 1.5, 'line-opacity': 0.8 } });
      map.addSource(routeSource, { type: 'geojson', data: routeData() });
      map.addLayer({
        id: 'route-line',
        type: 'line',
        source: routeSource,
        layout: { 'line-join': 'round', 'line-cap': 'round' },
        paint: { 'line-color': theme.route, 'line-width': 5, 'line-opacity': 0.85 },
      });
      map.addSource(stopsSource, { type: 'geojson', data: stopsData() });
      map.addLayer({
        id: 'route-stops',
        type: 'circle',
        source: stopsSource,
        paint: { 'circle-radius': 20, 'circle-color': 'transparent', 'circle-stroke-width': 4, 'circle-stroke-color': theme.route },
      });
      // Clusters sum up their locations per marker; they are drawn as HTML donut charts (updateClusters), not as a layer.
      map.addSource(source, {
        type: 'geojson',
        data: toGeoJson(pending),
        ...clusterOptions(cluster),
        clusterProperties: CLUSTER_PROPERTIES as Record<string, never>,
      });
      clearClusters();
      map.addLayer({
        id: 'location-points',
        type: 'symbol',
        source,
        filter: ['!', ['has', 'point_count']],
        layout: {
          'icon-image': ['concat', 'status-', ['get', 'marker']],
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
    map.on('click', 'location-points', (event) => options.onSelect(event.features![0].properties['id']));
    map.on('click', (event) => {
      if (map.queryRenderedFeatures(event.point, { layers: ['location-points'] }).length === 0) {
        options.onClick(event.lngLat.lat, event.lngLat.lng);
      }
    });
    map.on('mouseenter', 'location-points', () => (map.getCanvas().style.cursor = 'pointer'));
    map.on('mouseleave', 'location-points', () => (map.getCanvas().style.cursor = ''));

    // Donut charts of the clusters as HTML markers: built once per cluster id and kept while the data stay the same,
    // added and removed as clusters come into and leave the rendered tiles.
    const labels = Object.fromEntries(MARKER_KEYS.map((key) => [key, MARKERS[key].label])) as Record<MarkerKey, string>;
    let clusterCache = new Map<number, Marker>();
    let shownClusters = new Map<number, Marker>();
    function clearClusters(): void {
      shownClusters.forEach((marker) => marker.remove());
      shownClusters = new Map();
      clusterCache = new Map();
    }
    const updateClusters = () => {
      if (!loaded || !map.getSource(source) || !map.isSourceLoaded(source)) {
        return;
      }
      const next = new Map<number, Marker>();
      for (const feature of map.querySourceFeatures(source)) {
        const properties = feature.properties;
        const id = properties['cluster_id'] as number | undefined;
        if (!properties['cluster'] || id === undefined || next.has(id)) {
          continue;
        }
        let marker = clusterCache.get(id);
        if (!marker) {
          const coordinates = (feature.geometry as Point).coordinates as [number, number];
          const element = document.createElement('button');
          element.type = 'button';
          element.className = 'cluster-pie';
          element.innerHTML = clusterPie(
            clusterCounts(properties),
            { status: theme.status, center: theme.cluster, onCenter: theme.onCluster, ring: theme.ring },
            labels,
          );
          element.setAttribute('aria-label', element.querySelector('title')?.textContent ?? '');
          element.addEventListener('click', async (event) => {
            event.stopPropagation();
            const zoom = await (map.getSource(source) as GeoJSONSource).getClusterExpansionZoom(id);
            map.easeTo({ center: coordinates, zoom });
          });
          marker = new maplibre.Marker({ element }).setLngLat(coordinates);
          clusterCache.set(id, marker);
        }
        next.set(id, marker);
        if (!shownClusters.has(id)) {
          marker.addTo(map);
        }
      }
      shownClusters.forEach((marker, id) => !next.has(id) && marker.remove());
      shownClusters = next;
    };
    map.on('render', updateClusters);

    return {
      locate: () => geolocate.trigger(),
      showLocations: (locations) => {
        pending = locations;
        if (loaded) {
          // New data: cluster ids are reused with other contents, so the donuts are built anew.
          clearClusters();
          (map.getSource(source) as GeoJSONSource).setData(toGeoJson(locations));
        }
      },
      setCluster: (next) => {
        cluster = next;
        if (loaded) {
          clearClusters();
          void (map.getSource(source) as GeoJSONSource).setClusterOptions(clusterOptions(next));
        }
      },
      setTheme: (next) => {
        theme = next;
        // setStyle reloads the style; the style.load handler adds markers in the new colours.
        map.setStyle(next.styleUrl);
      },
      flyTo: (latitude, longitude, zoom) => map.flyTo({ center: [longitude, latitude], zoom }),
      showRoute: (coordinates) => {
        route = coordinates ?? [];
        if (loaded) {
          (map.getSource(routeSource) as GeoJSONSource).setData(routeData());
        }
        if (route.length > 1) {
          const bounds = new maplibre.LngLatBounds(route[0], route[0]);
          route.forEach((position) => bounds.extend(position));
          map.fitBounds(bounds, { padding: 60, maxZoom: 15 });
        }
      },
      highlightLocations: (locations) => {
        stops = locations;
        if (loaded) {
          (map.getSource(stopsSource) as GeoJSONSource).setData(stopsData());
        }
      },
      showReach: (areas) => {
        reach = areas ?? { type: 'FeatureCollection', features: [] };
        if (loaded) {
          (map.getSource(reachSource) as GeoJSONSource).setData(reach);
        }
        const outer = reach.features[0]?.geometry.coordinates.flatMap((polygon) => polygon[0]) ?? [];
        if (outer.length > 0) {
          const bounds = new maplibre.LngLatBounds(outer[0], outer[0]);
          outer.forEach((position) => bounds.extend(position));
          map.fitBounds(bounds, { padding: 40 });
        }
      },
      destroy: () => map.remove(),
    };
  }
}

/** One marker image per availability: a disc in the status colour with a ring and the status symbol. */
function addMarkerImages(map: MapLibreMap, theme: MapTheme): void {
  const ratio = 2;
  const size = 28 * ratio;
  for (const key of MARKER_KEYS) {
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
    context.fill(new Path2D(ICONS[MARKERS[key].icon]));
    map.addImage(`status-${key}`, context.getImageData(0, 0, size, size), { pixelRatio: ratio });
  }
}
