import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { LocationSummary, LocationsResponse } from './locations.service';
import { MapFilters } from './map-filters';

export interface Position {
  latitude: number;
  longitude: number;
}

/** Response of POST /api/v1/routes. */
export interface RouteResponse {
  data: {
    distanceMeters: number;
    durationSeconds: number;
    /** GeoJSON LineString, positions as [longitude, latitude]. */
    geometry: { type: 'LineString'; coordinates: [number, number][] };
  };
  meta: { attribution: { source: string; text: string; license: string }[] };
}

/** A location along a route (POST /api/v1/routes/locations). */
export interface RouteLocation extends LocationSummary {
  distanceFromRouteMeters: number;
  /** Distance from the start along the route. */
  positionOnRouteMeters: number;
}

export interface RouteLocationsResponse {
  data: RouteLocation[];
  meta: LocationsResponse['meta'];
}

/** Largest distance of a stop from the route. */
export const CORRIDOR_METERS = 1000;

/** Stops asked for; a long route through a city has hundreds of locations near it. */
export const ROUTE_STOPS_LIMIT = 500;

/** Charging stops one route may have (waypoints of POST /routes: start, stops, destination). */
export const MAX_CHARGING_STOPS = 3;

/** A planned trip: from the own position via charging stops (in the order of the route) to a location. */
export interface Trip {
  start: Position;
  destination: LocationSummary;
  stops: RouteLocation[];
}

/**
 * The stops with <paramref name="added"/> in the order of the current route: by their position in <paramref name="along"/>
 * (the list of the current route), a stop missing there keeps the position it had when it was chosen.
 */
export function withStop(stops: RouteLocation[], added: RouteLocation, along: RouteLocation[]): RouteLocation[] {
  const position = (stop: RouteLocation) => along.find((item) => item.id === stop.id)?.positionOnRouteMeters ?? stop.positionOnRouteMeters;
  return [...stops, added].sort((a, b) => position(a) - position(b));
}

/** "· 1 Ladestopp" / "· 2 Ladestopps" after the summary of a route; empty without stops. */
export function stopsSummary(count: number): string {
  return count === 0 ? '' : ` · ${count} ${count === 1 ? 'Ladestopp' : 'Ladestopps'}`;
}

/** Cheapest prices of a session at a location (POST /api/v1/locations/prices, default 30 kWh in 40 minutes). */
export interface LocationPrices {
  locationId: string;
  adHoc: { cost: number; currency: string; evseId: string } | null;
  cheapestCard: { emp: string; empName: string; tariffId: string; tariff: string; cost: number; currency: string; evseId: string } | null;
}

export interface LocationPricesResponse {
  data: LocationPrices[];
  meta: { session: { kwh: number; minutes: number }; attribution: { source: string; text: string; license: string }[] };
}

/** Energy of the session the prices along a route are for. */
export const PRICE_SESSION_KWH = 30;

/** The lowest price of a stop and how to pay it: a card's name or "ohne Vertrag". */
export interface StopPrice {
  cost: number;
  currency: string;
  label: string;
}

/** The lower of the ad-hoc price and the cheapest own card; ad hoc when equal; null without either. */
export function cheapest(prices: LocationPrices): StopPrice | null {
  const { adHoc, cheapestCard: card } = prices;
  if (card && (!adHoc || card.cost < adHoc.cost)) {
    return { cost: card.cost, currency: card.currency, label: card.empName };
  }
  return adHoc ? { cost: adHoc.cost, currency: adHoc.currency, label: 'ohne Vertrag' } : null;
}

/** The stops by their lowest price, those without a price last; equal ones keep the order of the route. */
export function byPrice(stops: RouteLocation[], prices: ReadonlyMap<string, StopPrice>): RouteLocation[] {
  const cost = (stop: RouteLocation) => prices.get(stop.id)?.cost ?? Number.POSITIVE_INFINITY;
  return [...stops].sort((a, b) => (cost(a) === cost(b) ? 0 : cost(a) - cost(b)));
}

/** Routes by car (route:read); the positions go to the own API only, which neither stores nor logs them. */
@Injectable({ providedIn: 'root' })
export class RoutesService {
  private readonly http = inject(HttpClient);

  route(waypoints: Position[]): Promise<RouteResponse> {
    return firstValueFrom(this.http.post<RouteResponse>('/api/v1/routes', { waypoints }));
  }

  /** The locations along the route that match the filters of the map, start first. */
  along(geometry: RouteResponse['data']['geometry'], filters: MapFilters): Promise<RouteLocationsResponse> {
    return firstValueFrom(
      this.http.post<RouteLocationsResponse>('/api/v1/routes/locations', { geometry, corridorMeters: CORRIDOR_METERS, limit: ROUTE_STOPS_LIMIT, ...filters }),
    );
  }

  /** The cheapest ad-hoc price and own card (EMP keys) of a session at each location (price:read). */
  prices(locationIds: string[], cards: readonly string[]): Promise<LocationPricesResponse> {
    return firstValueFrom(this.http.post<LocationPricesResponse>('/api/v1/locations/prices', { locationIds, cards }));
  }
}

/** "12,5 km · 16 min" or "1 h 05 min" for longer trips. */
export function routeSummary(distanceMeters: number, durationSeconds: number): string {
  const km = new Intl.NumberFormat('de-DE', { maximumFractionDigits: distanceMeters < 10_000 ? 1 : 0 }).format(distanceMeters / 1000);
  const minutes = Math.max(1, Math.round(durationSeconds / 60));
  const time = minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`;
  return `${km} km · ${time}`;
}

/** "12,3 km" from the start of the route. */
export function kilometres(meters: number): string {
  return `${new Intl.NumberFormat('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(meters / 1000)} km`;
}
