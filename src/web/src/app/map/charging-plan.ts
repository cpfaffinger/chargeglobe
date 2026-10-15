import { Injectable, signal } from '@angular/core';
import type { RouteLocation } from './routes.service';

/** What the car needs and carries: energy use, battery and the state of charge at departure and on arrival. */
export interface Vehicle {
  /** Energy use in kWh per 100 km. */
  consumptionKwhPer100Km: number;
  /** Usable battery capacity in kWh. */
  batteryKwh: number;
  /** State of charge at departure in %; null: full. */
  startSoc: number | null;
  /** State of charge wanted on arrival in %; null: {@link DEFAULT_ARRIVAL_SOC}. */
  arrivalSoc: number | null;
}

export const DEFAULT_VEHICLE: Vehicle = { consumptionKwhPer100Km: 18, batteryKwh: 60, startSoc: null, arrivalSoc: null };

/** Ranges of the inputs. */
export const CONSUMPTION = { min: 5, max: 60 };
export const BATTERY = { min: 10, max: 250 };
export const SOC = { min: 0, max: 100 };

/** State of charge assumed at departure when none is given. */
export const DEFAULT_START_SOC = 100;
/** State of charge wanted on arrival when none is given. */
export const DEFAULT_ARRIVAL_SOC = 10;
/** Never planned below this on the way to a stop: a buffer for detours, cold and traffic. */
export const MIN_SOC = 10;
/** Fast chargers charge quickly up to about here; a planned stop charges at most to this. */
export const CHARGE_TO_SOC = 80;
/** A stop is looked for in the last part of the remaining range: from this share of it on. */
export const STOP_WINDOW_FROM = 0.6;
/** More planned stops than this mean the route is not feasible with the given car. */
const MAX_PLANNED_STOPS = 12;

export const VEHICLE_STORAGE_KEY = 'chargeglobe.vehicle';

/** A recommended charging stop: arrival with and departure after charging to these states of charge (%). */
export interface PlannedStop {
  location: RouteLocation;
  arrivalSoc: number;
  departureSoc: number;
}

export interface ChargingPlan {
  /** Energy of the whole route in kWh. */
  energyKwh: number;
  stops: PlannedStop[];
  /** Expected state of charge on arrival in %. */
  arrivalSoc: number;
  /** False if the remaining range ends before the destination and no reachable location is left. */
  feasible: boolean;
}

/** Power tier of a location: high-power, fast, AC above 22 kW, slow. */
const tier = (location: RouteLocation): number => {
  const power = location.maxPowerKw ?? 0;
  return power >= 150 ? 3 : power >= 50 ? 2 : power > 22 ? 1 : 0;
};

/**
 * The better stop: more power, then free now, then further along the route (more of the range used).
 */
function better(a: RouteLocation, b: RouteLocation): RouteLocation {
  const score = (location: RouteLocation): [number, number, number] => [tier(location), location.evsesAvailable > 0 ? 1 : 0, location.positionOnRouteMeters];
  const [x, y] = [score(a), score(b)];
  for (let index = 0; index < x.length; index++) {
    if (x[index] !== y[index]) {
      return x[index] > y[index] ? a : b;
    }
  }
  return a;
}

/**
 * The charging stops along a route of <paramref name="routeMeters"/> that keep the car above {@link MIN_SOC} and bring it
 * to the destination with the wanted state of charge. Greedy: from each point it drives as far as the range allows and
 * stops at the best location (power, free, far) in the last part of the reachable stretch ({@link STOP_WINDOW_FROM});
 * without one there at the furthest reachable fast charger, else at the furthest reachable location. A stop charges to
 * {@link CHARGE_TO_SOC}, or only as much as the rest of the route needs.
 */
export function planCharging(routeMeters: number, candidates: RouteLocation[], vehicle: Vehicle): ChargingPlan {
  const socPerMeter = vehicle.consumptionKwhPer100Km / 100_000 / vehicle.batteryKwh * 100;
  const arrivalTarget = vehicle.arrivalSoc ?? DEFAULT_ARRIVAL_SOC;
  const energyKwh = (routeMeters * vehicle.consumptionKwhPer100Km) / 100_000;
  const stops: PlannedStop[] = [];
  let position = 0;
  let soc = vehicle.startSoc ?? DEFAULT_START_SOC;

  while (soc - (routeMeters - position) * socPerMeter < arrivalTarget) {
    const reach = position + Math.max(0, soc - MIN_SOC) / socPerMeter;
    const reachable = candidates.filter((location) => location.positionOnRouteMeters > position && location.positionOnRouteMeters <= Math.min(reach, routeMeters));
    const window = reachable.filter((location) => location.positionOnRouteMeters >= position + (reach - position) * STOP_WINDOW_FROM);
    const fast = reachable.filter((location) => tier(location) >= 2);
    const pool = window.length > 0 ? window : fast.length > 0 ? fast : reachable;
    if (pool.length === 0 || stops.length === MAX_PLANNED_STOPS) {
      return { energyKwh, stops, arrivalSoc: soc - (routeMeters - position) * socPerMeter, feasible: false };
    }
    const stop = pool.reduce(better);
    const arrivalSoc = soc - (stop.positionOnRouteMeters - position) * socPerMeter;
    const needed = arrivalTarget + (routeMeters - stop.positionOnRouteMeters) * socPerMeter;
    const departureSoc = Math.max(arrivalSoc, Math.min(CHARGE_TO_SOC, needed));
    stops.push({ location: stop, arrivalSoc, departureSoc });
    position = stop.positionOnRouteMeters;
    soc = departureSoc;
    if (departureSoc === arrivalSoc) {
      // Charging to 80 % is not more than it already has: no progress possible from here.
      return { energyKwh, stops, arrivalSoc: soc - (routeMeters - position) * socPerMeter, feasible: false };
    }
  }
  return { energyKwh, stops, arrivalSoc: soc - (routeMeters - position) * socPerMeter, feasible: true };
}

const inRange = (value: unknown, range: { min: number; max: number }): value is number =>
  typeof value === 'number' && Number.isFinite(value) && value >= range.min && value <= range.max;

/** The values of a vehicle that are in range; the others fall back to the defaults. */
export function validVehicle(value: Partial<Record<keyof Vehicle, unknown>>): Vehicle {
  return {
    consumptionKwhPer100Km: inRange(value.consumptionKwhPer100Km, CONSUMPTION) ? value.consumptionKwhPer100Km : DEFAULT_VEHICLE.consumptionKwhPer100Km,
    batteryKwh: inRange(value.batteryKwh, BATTERY) ? value.batteryKwh : DEFAULT_VEHICLE.batteryKwh,
    startSoc: inRange(value.startSoc, SOC) ? value.startSoc : null,
    arrivalSoc: inRange(value.arrivalSoc, SOC) ? value.arrivalSoc : null,
  };
}

/** The car of the viewer, kept in the browser (localStorage); invalid values fall back to the defaults. */
@Injectable({ providedIn: 'root' })
export class VehicleStore {
  readonly vehicle = signal<Vehicle>(read());

  set(change: Partial<Vehicle>): void {
    const vehicle = validVehicle({ ...this.vehicle(), ...change });
    this.vehicle.set(vehicle);
    try {
      localStorage.setItem(VEHICLE_STORAGE_KEY, JSON.stringify(vehicle));
    } catch {
      // Storage blocked: the values hold until the page is closed.
    }
  }
}

function read(): Vehicle {
  try {
    return validVehicle(JSON.parse(localStorage.getItem(VEHICLE_STORAGE_KEY) ?? '{}') ?? {});
  } catch {
    return DEFAULT_VEHICLE;
  }
}
