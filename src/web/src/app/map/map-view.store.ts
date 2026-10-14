import { Injectable, signal } from '@angular/core';

/** A view of the map: its center and zoom. */
export interface MapView {
  latitude: number;
  longitude: number;
  zoom: number;
}

/** The view when the map was last moved; the map opens there again. */
export const LAST_VIEW_KEY = 'chargeglobe.map.last-view';
/** The start position chosen by the viewer; "Startposition" returns to it. */
export const HOME_VIEW_KEY = 'chargeglobe.map.home';

/**
 * Views of the map that stay in the browser (localStorage): the last one and the viewer's start position. Storage
 * that is blocked or holds something unreadable counts as empty; the map then uses the defaults of map.* from the
 * config service.
 */
@Injectable({ providedIn: 'root' })
export class MapViewStore {
  readonly home = signal<MapView | null>(read(HOME_VIEW_KEY));

  last(): MapView | null {
    return read(LAST_VIEW_KEY);
  }

  saveLast(view: MapView): void {
    write(LAST_VIEW_KEY, view);
  }

  setHome(view: MapView): void {
    this.home.set(view);
    write(HOME_VIEW_KEY, view);
  }

  resetHome(): void {
    this.home.set(null);
    write(HOME_VIEW_KEY, null);
  }
}

function read(key: string): MapView | null {
  try {
    const view = JSON.parse(localStorage.getItem(key) ?? 'null') as Partial<MapView> | null;
    return view &&
      isIn(view.latitude, -90, 90) &&
      isIn(view.longitude, -180, 180) &&
      isIn(view.zoom, 0, 24)
      ? { latitude: view.latitude!, longitude: view.longitude!, zoom: view.zoom! }
      : null;
  } catch {
    return null;
  }
}

function write(key: string, view: MapView | null): void {
  try {
    if (view) {
      localStorage.setItem(key, JSON.stringify({ latitude: view.latitude, longitude: view.longitude, zoom: view.zoom }));
    } else {
      localStorage.removeItem(key);
    }
  } catch {
    // Storage blocked (private mode, policy): the view holds until the page is closed.
  }
}

const isIn = (value: unknown, min: number, max: number): boolean =>
  typeof value === 'number' && Number.isFinite(value) && value >= min && value <= max;
