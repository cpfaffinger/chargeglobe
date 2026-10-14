import { Injectable, computed, signal } from '@angular/core';

/** How the map combines nearby locations into clusters. */
export interface MapOptions {
  /** Distance in screen pixels within which locations are combined; 0 = never combine. */
  clusterRadius: number;
  /** Highest zoom level at which locations are combined; above it every location shows on its own. */
  clusterMaxZoom: number;
}

export const DEFAULT_MAP_OPTIONS: MapOptions = { clusterRadius: 50, clusterMaxZoom: 13 };

/** Ranges of the option sliders. */
export const CLUSTER_RADIUS = { min: 0, max: 120, step: 10 };
export const CLUSTER_MAX_ZOOM = { min: 5, max: 18, step: 1 };

export const OPTIONS_STORAGE_KEY = 'chargeglobe.map.options';

const within = (value: unknown, range: { min: number; max: number }, fallback: number): number =>
  typeof value === 'number' && Number.isFinite(value) && value >= range.min && value <= range.max ? value : fallback;

/**
 * Display options of the map chosen by the viewer, kept in the browser (localStorage). Unknown or out-of-range values
 * fall back to the defaults; "Standard" removes the stored options.
 */
@Injectable({ providedIn: 'root' })
export class MapOptionsStore {
  readonly options = signal<MapOptions>(read());
  readonly isDefault = computed(
    () =>
      this.options().clusterRadius === DEFAULT_MAP_OPTIONS.clusterRadius &&
      this.options().clusterMaxZoom === DEFAULT_MAP_OPTIONS.clusterMaxZoom,
  );

  set(change: Partial<MapOptions>): void {
    const options = { ...this.options(), ...change };
    this.options.set(options);
    try {
      localStorage.setItem(OPTIONS_STORAGE_KEY, JSON.stringify(options));
    } catch {
      // Storage blocked: the options hold until the page is closed.
    }
  }

  reset(): void {
    this.options.set(DEFAULT_MAP_OPTIONS);
    try {
      localStorage.removeItem(OPTIONS_STORAGE_KEY);
    } catch {
      // Storage blocked: nothing stored.
    }
  }
}

function read(): MapOptions {
  try {
    const stored = JSON.parse(localStorage.getItem(OPTIONS_STORAGE_KEY) ?? '{}') ?? {};
    return {
      clusterRadius: within(stored.clusterRadius, CLUSTER_RADIUS, DEFAULT_MAP_OPTIONS.clusterRadius),
      clusterMaxZoom: within(stored.clusterMaxZoom, CLUSTER_MAX_ZOOM, DEFAULT_MAP_OPTIONS.clusterMaxZoom),
    };
  } catch {
    return DEFAULT_MAP_OPTIONS;
  }
}
