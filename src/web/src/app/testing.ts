import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { Viewport } from './design/theme.service';
import { AuthService, Me } from './auth/auth.service';
import { BrowserCapabilities } from './map/browser-capabilities';
import { MapConfig } from './map/map-config.service';
import { MapEngine, MapHandle } from './map/map-engine';
import { LocationSummary } from './map/locations.service';

/** Shared helpers for the specs. */

export const ME: Me = {
  id: '01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4d',
  email: 'anna@example.org',
  roles: ['User'],
  permissions: ['apikey:manage', 'evse:read', 'price:read', 'route:read'],
  authenticatedVia: 'bearer',
};

/** Lets pending promise chains and timers of the code under test run. */
export const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve));

/** AuthService double: spies for the methods, writable signal for the signed-in user. */
export function fakeAuth(me: Me | null = null) {
  const current = signal<Me | null>(me);
  const spies = jasmine.createSpyObj<AuthService>('AuthService', [
    'login',
    'createInitialAdmin',
    'logout',
    'register',
    'confirmEmail',
    'resendConfirmation',
    'requestPasswordReset',
    'resetPassword',
    'changePassword',
    'refresh',
    'accessToken',
    'hasPermission',
  ]);
  return Object.assign(spies, { me: current, isAuthenticated: computed(() => current() !== null) });
}

/** Response of GET /api/v1/map/config with the defaults of the settings catalog. */
export const MAP_CONFIG: MapConfig = {
  styleUrl: 'https://tiles.openfreemap.org/styles/liberty',
  styleUrlDark: 'https://tiles.openfreemap.org/styles/dark',
  attribution: '© OpenStreetMap contributors, OpenMapTiles',
  center: { latitude: 48.2, longitude: 15.6 },
  zoom: 7,
};

/** MapEngine double: WebGL is not reliably available in the headless test browser. */
export function fakeMapEngine() {
  const handle = jasmine.createSpyObj<MapHandle>('MapHandle', ['locate', 'destroy', 'showLocations', 'setTheme', 'setCluster', 'flyTo', 'showRoute', 'highlightLocations', 'showReach']);
  handle.locate.and.returnValue(true);
  const engine = jasmine.createSpyObj<MapEngine>('MapEngine', ['create', 'prepare']);
  engine.create.and.resolveTo(handle);
  return { engine, handle };
}

/** A location of GET /api/v1/locations. */
export function locationSummary(overrides: Partial<LocationSummary> = {}): LocationSummary {
  return {
    id: 'loc-1',
    name: 'Parkgarage Stephansplatz',
    address: 'Stephansplatz 1',
    postalCode: '1010',
    city: 'Wien',
    country: 'AT',
    latitude: 48.2085,
    longitude: 16.3731,
    operator: 'Wien Energie GmbH',
    evsesTotal: 4,
    evsesAvailable: 2,
    maxPowerKw: 22,
    statusObservedAt: '2026-10-14T08:15:00Z',
    availability: 'AVAILABLE',
    maxAvailablePowerKw: 22,
    ...overrides,
  };
}

/** BrowserCapabilities double; by default the browser offers everything the map needs. */
export function fakeBrowser() {
  const browser = jasmine.createSpyObj<BrowserCapabilities>('BrowserCapabilities', [
    'webGl',
    'secureContext',
    'geolocation',
    'geolocationPermission',
    'currentPosition',
  ]);
  browser.webGl.and.returnValue(true);
  browser.secureContext.and.returnValue(true);
  browser.geolocation.and.returnValue(true);
  browser.geolocationPermission.and.resolveTo('prompt');
  browser.currentPosition.and.resolveTo({ latitude: 48.21, longitude: 16.37 });
  return browser;
}

/** A 422 problem response with one field error. */
export const validationError = (message: string): HttpErrorResponse =>
  new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { field: [message] } } });

export function type(root: Element, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

export function submit(root: Element): void {
  root.querySelector('form')!.dispatchEvent(new Event('submit'));
}

export const text = (root: Element, selector: string): string | undefined =>
  root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();

/** Viewport double: desktop by default; set mobile (phone layout) and compactNav (menu button) to switch the layout. */
export function fakeViewport(mobile = false, compactNav = mobile) {
  return { mobile: signal(mobile), compactNav: signal(compactNav) } as unknown as Viewport & {
    mobile: ReturnType<typeof signal<boolean>>;
    compactNav: ReturnType<typeof signal<boolean>>;
  };
}
