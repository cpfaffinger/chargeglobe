import { Injectable, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { MapLink } from './map-link';

/** Filters of the map; the names are the query parameters of GET /api/v1/locations and of the page URL. */
export interface MapFilters {
  /** OCPI connector standards; one of them is enough. */
  connector: string[];
  minPowerKw: number | null;
  operator: string;
  availableOnly: boolean;
  adHoc: boolean;
  roaming: boolean;
  greenEnergy: boolean;
  barrierFree: boolean;
}

export type FilterFlag = 'availableOnly' | 'adHoc' | 'roaming' | 'greenEnergy' | 'barrierFree';

export const NO_FILTERS: MapFilters = {
  connector: [],
  minPowerKw: null,
  operator: '',
  availableOnly: false,
  adHoc: false,
  roaming: false,
  greenEnergy: false,
  barrierFree: false,
};

export const FILTER_FLAGS: { key: FilterFlag; label: string }[] = [
  { key: 'availableOnly', label: 'Nur freie' },
  { key: 'adHoc', label: 'Ad-hoc-Zahlung' },
  { key: 'roaming', label: 'Roaming' },
  { key: 'greenEnergy', label: 'Ökostrom' },
  { key: 'barrierFree', label: 'Barrierefrei' },
];

/** The connectors offered in the filter (OCPI standard and common name). */
export const CONNECTORS: { standard: string; label: string }[] = [
  { standard: 'IEC_62196_T2', label: 'Typ 2' },
  { standard: 'IEC_62196_T2_COMBO', label: 'CCS' },
  { standard: 'CHADEMO', label: 'CHAdeMO' },
  { standard: 'DOMESTIC_F', label: 'Schuko' },
];

export const POWER_STEPS = [11, 22, 50, 150];

export const FILTERS_STORAGE_KEY = 'chargeglobe.map.filters';

const FILTER_KEYS = ['connector', 'minPowerKw', 'operator', ...FILTER_FLAGS.map((flag) => flag.key)];

const MAX_OPERATOR = 100;

/** Filters read from URL parameters or storage; unknown values are dropped. */
export function parseFilters(params: Params): MapFilters {
  const connectors = [params['connector'] ?? []].flat().flatMap((value: unknown) => String(value).split(','));
  const power = Number(params['minPowerKw']);
  const flag = (key: FilterFlag) => params[key] === true || params[key] === 'true';
  return {
    connector: CONNECTORS.map((item) => item.standard).filter((standard) => connectors.includes(standard)),
    minPowerKw: power > 0 && power <= 1000 ? power : null,
    operator: String(params['operator'] ?? '').trim().slice(0, MAX_OPERATOR),
    availableOnly: flag('availableOnly'),
    adHoc: flag('adHoc'),
    roaming: flag('roaming'),
    greenEnergy: flag('greenEnergy'),
    barrierFree: flag('barrierFree'),
  };
}

/** Query parameters of the active filters only (empty for no filters). */
export function filterParams(filters: MapFilters): Record<string, string | string[]> {
  const params: Record<string, string | string[]> = {};
  if (filters.connector.length > 0) {
    params['connector'] = filters.connector;
  }
  if (filters.minPowerKw !== null) {
    params['minPowerKw'] = String(filters.minPowerKw);
  }
  if (filters.operator) {
    params['operator'] = filters.operator;
  }
  for (const { key } of FILTER_FLAGS) {
    if (filters[key]) {
      params[key] = 'true';
    }
  }
  return params;
}

/** Number of active filters, for the badge of the filter button. */
export const countFilters = (filters: MapFilters): number => Object.keys(filterParams(filters)).length;

/**
 * Current filters of the map. A link with filter parameters wins; otherwise the filters of the last visit come from
 * localStorage. Every change goes to both, so a reload or a shared link shows the same markers.
 */
@Injectable({ providedIn: 'root' })
export class MapFilterStore {
  private readonly router = inject(Router);
  private readonly link = inject(MapLink);

  readonly filters = signal<MapFilters>(this.initial(inject(ActivatedRoute).snapshot.queryParams));
  readonly count = computed(() => countFilters(this.filters()));

  set(filters: MapFilters): void {
    this.filters.set(filters);
    const params = filterParams(filters);
    try {
      localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify(params));
    } catch {
      // Storage blocked: the filters hold until the page is closed.
    }
    // Parameters of filters that are off are removed from the URL, other parameters stay.
    const cleared = Object.fromEntries(FILTER_KEYS.map((key) => [key, null]));
    void this.router.navigate([], { queryParams: { ...cleared, ...params }, queryParamsHandling: 'merge', fragment: this.link.fragment(), replaceUrl: true });
  }

  private initial(query: Params): MapFilters {
    if (countFilters(parseFilters(query)) > 0) {
      return parseFilters(query);
    }
    try {
      return parseFilters(JSON.parse(localStorage.getItem(FILTERS_STORAGE_KEY) ?? '{}') ?? {});
    } catch {
      return NO_FILTERS;
    }
  }
}
