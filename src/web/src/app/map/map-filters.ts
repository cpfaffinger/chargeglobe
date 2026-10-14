import { Injectable, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { MapLink } from './map-link';

/** Filters of the map; the names are the query parameters of GET /api/v1/locations and of the page URL. */
export interface MapFilters {
  /** OCPI connector standards; one of them is enough. */
  connector: string[];
  /** Power range of an EVSE in kW; null = open. */
  minPowerKw: number | null;
  maxPowerKw: number | null;
  /** Operators (ids of GET /api/v1/operators); one of them is enough. */
  operatorId: string[];
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
  maxPowerKw: null,
  operatorId: [],
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

/** Stops of the power slider in kW; the first means "no minimum", the last "no maximum". */
export const POWER_STOPS = [0, 3.7, 11, 22, 50, 100, 150, 250, 400];

export const FILTERS_STORAGE_KEY = 'chargeglobe.map.filters';

const FILTER_KEYS = ['connector', 'minPowerKw', 'maxPowerKw', 'operatorId', ...FILTER_FLAGS.map((flag) => flag.key)];

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const list = (value: unknown): string[] => [value ?? []].flat().flatMap((item: unknown) => String(item).split(','));

const power = (value: unknown): number | null => {
  const number = Number(value);
  return number > 0 && number <= 1000 ? number : null;
};

/** Filters read from URL parameters or storage; unknown values are dropped. */
export function parseFilters(params: Params): MapFilters {
  const connectors = list(params['connector']);
  const flag = (key: FilterFlag) => params[key] === true || params[key] === 'true';
  const min = power(params['minPowerKw']);
  const max = power(params['maxPowerKw']);
  return {
    connector: CONNECTORS.map((item) => item.standard).filter((standard) => connectors.includes(standard)),
    minPowerKw: min,
    maxPowerKw: max !== null && min !== null && max < min ? null : max,
    operatorId: [...new Set(list(params['operatorId']).filter((id) => UUID.test(id)))],
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
  if (filters.maxPowerKw !== null) {
    params['maxPowerKw'] = String(filters.maxPowerKw);
  }
  if (filters.operatorId.length > 0) {
    params['operatorId'] = filters.operatorId;
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
