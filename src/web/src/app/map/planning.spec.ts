import { TestBed } from '@angular/core/testing';
import { locationSummary } from '../testing';
import { MARKERS, MARKER_KEYS, MarkerKey } from './availability';
import {
  DEFAULT_VEHICLE,
  VEHICLE_STORAGE_KEY,
  Vehicle,
  VehicleStore,
  planCharging,
  validVehicle,
} from './charging-plan';
import { CLUSTER_PROPERTIES, clusterCounts, clusterPie, clusterRadius } from './cluster-pie';
import { RouteLocation, locationPlace, ownPlace, pointPlace } from './routes.service';
import { LEGEND_STORAGE_KEY, MapHints, TRUNCATED_SNOOZE_KEY, TRUNCATED_SNOOZE_MS } from './map-hints';

const at = (id: string, km: number, overrides: Partial<RouteLocation> = {}): RouteLocation => ({
  ...locationSummary({ id, maxPowerKw: 22, evsesAvailable: 1 }),
  distanceFromRouteMeters: 100,
  positionOnRouteMeters: km * 1000,
  ...overrides,
});

/** 20 kWh/100 km with 10 kWh: 2 % per km, 45 km from 100 % down to the 10 % buffer. */
const SMALL: Vehicle = { consumptionKwhPer100Km: 20, batteryKwh: 10, startSoc: null, arrivalSoc: null };

describe('planCharging', () => {
  it('needs no stop when the battery suffices', () => {
    const plan = planCharging(30_000, [at('a', 10)], SMALL);

    expect(plan.feasible).toBeTrue();
    expect(plan.stops).toEqual([]);
    expect(plan.energyKwh).toBeCloseTo(6);
    expect(plan.arrivalSoc).toBeCloseTo(40);
  });

  it('stops at the strongest location late in the range and charges only as much as needed', () => {
    const candidates = [
      at('slow-late', 44),
      at('hpc', 40, { maxPowerKw: 150 }),
      at('early-fast', 10, { maxPowerKw: 50 }),
      at('later', 70, { maxPowerKw: 50 }),
      at('ac', 72, { maxPowerKw: 43 }),
    ];
    const plan = planCharging(100_000, candidates, SMALL);

    expect(plan.feasible).toBeTrue();
    expect(plan.stops.map((stop) => stop.location.id)).toEqual(['hpc', 'later']);
    // 100 − 40 × 2; charged to 80 (the rest needs more), then 80 − 30 × 2 and only to 10 + 30 × 2.
    expect(plan.stops.map((stop) => [Math.round(stop.arrivalSoc), Math.round(stop.departureSoc)])).toEqual([
      [20, 80],
      [20, 70],
    ]);
    expect(plan.arrivalSoc).toBeCloseTo(10);
    expect(plan.energyKwh).toBeCloseTo(20);
  });

  it('prefers free locations and then the later one among equal power', () => {
    const busy = at('busy', 40, { maxPowerKw: 150, evsesAvailable: 0 });
    const free = at('free', 35, { maxPowerKw: 150 });
    const twin = at('twin', 35, { maxPowerKw: 150 });

    expect(planCharging(60_000, [busy, free], SMALL).stops[0].location.id).toBe('free');
    expect(planCharging(60_000, [free, twin], SMALL).stops[0].location.id).toBe('free');
    expect(planCharging(60_000, [at('early', 30, { maxPowerKw: 150 }), at('late', 31, { maxPowerKw: 150 })], SMALL).stops[0].location.id).toBe('late');
  });

  it('falls back to a fast charger before the window, then to any reachable location', () => {
    expect(planCharging(60_000, [at('slow', 5), at('fast', 10, { maxPowerKw: 50 })], SMALL).stops[0].location.id).toBe('fast');
    expect(planCharging(60_000, [at('slow', 5), at('slower', 8)], SMALL).stops[0].location.id).toBe('slower');
  });

  it('uses the given states of charge', () => {
    const plan = planCharging(30_000, [], { ...SMALL, startSoc: 50, arrivalSoc: 20 });

    expect(plan.feasible).toBeFalse();
    expect(plan.arrivalSoc).toBeCloseTo(-10);
    expect(planCharging(10_000, [], { ...SMALL, startSoc: 50, arrivalSoc: 20 }).arrivalSoc).toBeCloseTo(30);
  });

  it('is not feasible without a reachable location or when a stop cannot charge further', () => {
    const none = planCharging(100_000, [at('far', 80)], SMALL);
    expect(none.feasible).toBeFalse();
    expect(none.stops).toEqual([]);

    // Only a location right at the start: arriving with 98 % the stop adds nothing.
    const stuck = planCharging(100_000, [at('start', 1)], SMALL);
    expect(stuck.feasible).toBeFalse();
    expect(stuck.stops.map((stop) => [stop.location.id, Math.round(stop.departureSoc)])).toEqual([['start', 98]]);
  });

  it('gives up after twelve stops', () => {
    // A tiny battery: every stop brings 4 km.
    const car: Vehicle = { consumptionKwhPer100Km: 60, batteryKwh: 10, startSoc: 20, arrivalSoc: 10 };
    const candidates = Array.from({ length: 40 }, (_, index) => at(`c${index}`, 1 + index * 5));
    const plan = planCharging(200_000, candidates, car);

    expect(plan.feasible).toBeFalse();
    expect(plan.stops.length).toBe(12);
  });
});

describe('VehicleStore', () => {
  afterEach(() => localStorage.removeItem(VEHICLE_STORAGE_KEY));

  it('keeps valid values in the browser and falls back to the defaults', () => {
    localStorage.setItem(VEHICLE_STORAGE_KEY, JSON.stringify({ consumptionKwhPer100Km: 21.5, batteryKwh: 77, startSoc: 60, arrivalSoc: 500 }));
    const store = TestBed.inject(VehicleStore);
    expect(store.vehicle()).toEqual({ consumptionKwhPer100Km: 21.5, batteryKwh: 77, startSoc: 60, arrivalSoc: null });

    store.set({ batteryKwh: 5, arrivalSoc: 15 });
    expect(store.vehicle()).toEqual({ consumptionKwhPer100Km: 21.5, batteryKwh: 60, startSoc: 60, arrivalSoc: 15 });
    expect(JSON.parse(localStorage.getItem(VEHICLE_STORAGE_KEY)!)).toEqual(store.vehicle());
  });

  it('reads defaults from unreadable storage and works without storage', () => {
    localStorage.setItem(VEHICLE_STORAGE_KEY, '{nope');
    expect(TestBed.inject(VehicleStore).vehicle()).toEqual(DEFAULT_VEHICLE);
    localStorage.setItem(VEHICLE_STORAGE_KEY, 'null');
    expect(validVehicle({})).toEqual(DEFAULT_VEHICLE);

    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
    TestBed.inject(VehicleStore).set({ consumptionKwhPer100Km: 30 });
    expect(TestBed.inject(VehicleStore).vehicle().consumptionKwhPer100Km).toBe(30);
  });

  it('starts with the defaults when nothing is stored', () => {
    localStorage.setItem(VEHICLE_STORAGE_KEY, 'null');
    expect(TestBed.inject(VehicleStore).vehicle()).toEqual(DEFAULT_VEHICLE);
  });
});

describe('cluster pies', () => {
  const colors = {
    status: Object.fromEntries(MARKER_KEYS.map((key, index) => [key, `#00000${index}`])) as Record<MarkerKey, string>,
    center: '#111111',
    onCenter: '#ffffff',
    ring: '#eeeeee',
  };
  const labels = Object.fromEntries(MARKER_KEYS.map((key) => [key, MARKERS[key].label])) as Record<MarkerKey, string>;
  const counts = (values: Partial<Record<MarkerKey, number>>) => clusterCounts(values);

  it('sums the markers of a cluster and reads them back', () => {
    expect(CLUSTER_PROPERTIES.OCCUPIED).toEqual(['+', ['case', ['==', ['get', 'marker'], 'OCCUPIED'], 1, 0]]);
    expect(clusterCounts({ AVAILABLE: 3, UNKNOWN: '2' })).toEqual({
      AVAILABLE: 3,
      AVAILABLE_FAST: 0,
      AVAILABLE_HPC: 0,
      OCCUPIED: 0,
      OUT_OF_ORDER: 0,
      UNKNOWN: 2,
    });
    expect([clusterRadius(10), clusterRadius(50), clusterRadius(200)]).toEqual([18, 22, 28]);
  });

  it('draws one segment per marker with the count in the middle', () => {
    const svg = new DOMParser().parseFromString(clusterPie(counts({ AVAILABLE: 30, OCCUPIED: 10, UNKNOWN: 10 }), colors, labels), 'image/svg+xml');

    const paths = Array.from(svg.querySelectorAll('path'));
    expect(paths.map((path) => path.getAttribute('fill'))).toEqual([colors.status.AVAILABLE, colors.status.OCCUPIED, colors.status.UNKNOWN]);
    // The free share is above half: the large arc.
    expect(paths[0].getAttribute('d')).toContain(' 0 1 1 ');
    expect(paths[1].getAttribute('d')).toContain(' 0 0 1 ');
    expect(svg.querySelector('text')!.textContent).toBe('50');
    expect(svg.querySelector('text')!.getAttribute('font-size')).toBe('12');
    expect(svg.querySelector('title')!.textContent).toBe('50 Standorte: 30 frei bis 22 kW, 10 belegt, 10 Status unbekannt');
  });

  it('draws a full circle for a cluster of one kind', () => {
    const svg = new DOMParser().parseFromString(clusterPie(counts({ OUT_OF_ORDER: 4 }), colors, labels), 'image/svg+xml');

    expect(svg.querySelectorAll('path').length).toBe(0);
    expect(Array.from(svg.querySelectorAll('circle')).map((circle) => circle.getAttribute('fill'))).toEqual([
      colors.ring,
      colors.status.OUT_OF_ORDER,
      colors.center,
    ]);
    expect(svg.querySelector('text')!.getAttribute('font-size')).toBe('11');
  });
});

describe('places of a route', () => {
  it('names the own position, a location and a point', () => {
    expect(ownPlace({ latitude: 48.2, longitude: 16.3 })).toEqual({ latitude: 48.2, longitude: 16.3, name: 'Mein Standort', kind: 'own', locationId: null });
    expect(locationPlace(locationSummary())).toEqual({
      latitude: 48.2085,
      longitude: 16.3731,
      name: 'Parkgarage Stephansplatz',
      kind: 'location',
      locationId: 'loc-1',
    });
    expect(locationPlace(locationSummary({ name: null })).name).toBe('Ladestandort');
    expect(pointPlace(48.21, 16.37)).toEqual({ latitude: 48.21, longitude: 16.37, name: 'Punkt 48,2100, 16,3700', kind: 'point', locationId: null });
  });
});

describe('MapHints', () => {
  afterEach(() => {
    localStorage.removeItem(LEGEND_STORAGE_KEY);
    localStorage.removeItem(TRUNCATED_SNOOZE_KEY);
  });

  it('snoozes the truncated notice for a day', () => {
    const hints = TestBed.inject(MapHints);
    expect(hints.truncatedSnoozed(1000)).toBeFalse();
    hints.snoozeTruncated(1000);
    expect(localStorage.getItem(TRUNCATED_SNOOZE_KEY)).toBe(String(1000 + TRUNCATED_SNOOZE_MS));
    expect(hints.truncatedSnoozed(1000 + TRUNCATED_SNOOZE_MS - 1)).toBeTrue();
    expect(hints.truncatedSnoozed(1000 + TRUNCATED_SNOOZE_MS)).toBeFalse();
  });

  it('works without storage', () => {
    spyOn(Storage.prototype, 'getItem').and.throwError('blocked');
    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
    const hints = TestBed.inject(MapHints);

    expect(hints.legendVisible()).toBeTrue();
    expect(hints.truncatedSnoozed()).toBeFalse();
    hints.setLegend(false);
    expect(hints.legendVisible()).toBeFalse();
    expect(() => hints.snoozeTruncated()).not.toThrow();
  });
});
