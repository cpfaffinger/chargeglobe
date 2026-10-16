import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { EvsesService, LocationDetailResponse } from '../evses/evses.service';
import { FOCUS_ZOOM, HomePage, LOCATIONS_FRESH_MS } from '../pages/home.page';
import { MapFocus } from '../search/search.service';
import { ThemeService, Viewport } from '../design/theme.service';
import { MAP_CONFIG, fakeAuth, fakeBrowser, fakeMapEngine, fakeViewport, flush, locationSummary, text } from '../testing';
import { BrowserCapabilities } from './browser-capabilities';
import { MapConfigService } from './map-config.service';
import { GeolocateEvent, MapEngine, MapViewOptions } from './map-engine';
import { MARKERS, markerOf } from './availability';
import { Bbox, LocationSummary, LocationsResponse, LocationsService } from './locations.service';
import { HOME_VIEW_KEY, LAST_VIEW_KEY, MapView, MapViewStore } from './map-view.store';
import { FILTERS_STORAGE_KEY, MapFilterStore, NO_FILTERS } from './map-filters';
import { MapLink } from './map-link';
import { DEFAULT_MAP_OPTIONS, MapOptionsStore, OPTIONS_STORAGE_KEY } from './map-options';
import { VEHICLE_STORAGE_KEY, VehicleStore } from './charging-plan';
import { LEGEND_STORAGE_KEY, TRUNCATED_NOTICE_MS, TRUNCATED_SNOOZE_KEY } from './map-hints';
import { CardsStore } from '../preferences/cards.store';
import {
  LocationPrices,
  Place,
  ReachAreas,
  RouteLocation,
  RoutesService,
  byPrice,
  cheapest,
  inArea,
  kilometres,
  routeSummary,
  smallestArea,
  stopsSummary,
  withStop,
} from './routes.service';

describe('MapConfigService', () => {
  it('loads the map configuration', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const config = TestBed.inject(MapConfigService).load();
    http.expectOne('/api/v1/map/config').flush(MAP_CONFIG);

    expect(await config).toEqual(MAP_CONFIG);
    http.verify();
  });
});

describe('LocationsService', () => {
  it('asks for the locations of the box with the maximum limit', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const response: LocationsResponse = { data: [], meta: { returned: 0, limit: 2000, truncated: false, attribution: [] } };

    const result = TestBed.inject(LocationsService).inBox({ west: 16.2, south: 48.1, east: 16.6, north: 48.3 });
    http.expectOne('/api/v1/locations?bbox=16.20000,48.10000,16.60000,48.30000&limit=2000').flush(response);

    expect(await result).toEqual(response);

    const filtered = TestBed.inject(LocationsService).inBox(
      { west: 16.2, south: 48.1, east: 16.6, north: 48.3 },
      { ...NO_FILTERS, connector: ['CHADEMO', 'IEC_62196_T2_COMBO'], minPowerKw: 150, adHoc: true },
    );
    http
      .expectOne(
        '/api/v1/locations?bbox=16.20000,48.10000,16.60000,48.30000&limit=2000&connector=CHADEMO&connector=IEC_62196_T2_COMBO&minPowerKw=150&adHoc=true',
      )
      .flush(response);
    expect(await filtered).toEqual(response);
    http.verify();
  });
});

describe('BrowserCapabilities', () => {
  let browser: BrowserCapabilities;

  beforeEach(() => (browser = TestBed.inject(BrowserCapabilities)));

  /** Lets document.createElement('canvas') return a canvas whose contexts are given. */
  function canvasWith(contexts: Record<string, object>): void {
    const create = document.createElement.bind(document);
    spyOn(document, 'createElement').and.callFake((tag: string) =>
      tag === 'canvas'
        ? ({ getContext: (id: string) => contexts[id] ?? null } as unknown as HTMLCanvasElement)
        : create(tag),
    );
  }

  it('detects WebGL 2', () => {
    canvasWith({ webgl2: {} });
    expect(browser.webGl()).toBeTrue();
  });

  it('falls back to WebGL 1', () => {
    canvasWith({ webgl: {} });
    expect(browser.webGl()).toBeTrue();
  });

  it('reports a browser without WebGL', () => {
    canvasWith({});
    expect(browser.webGl()).toBeFalse();
  });

  it('reads secure context and geolocation from the window', () => {
    expect(browser.secureContext()).toBe(window.isSecureContext);
    expect(browser.geolocation()).toBe('geolocation' in navigator);
  });

  it('reads the state of the geolocation permission', async () => {
    spyOn(navigator.permissions, 'query').and.resolveTo({ state: 'denied' } as PermissionStatus);

    expect(await browser.geolocationPermission()).toBe('denied');
    expect(navigator.permissions.query).toHaveBeenCalledOnceWith({ name: 'geolocation' });
  });

  it('asks for the current position once', async () => {
    const ask = spyOn(navigator.geolocation, 'getCurrentPosition').and.callFake((found) =>
      found({ coords: { latitude: 48.2, longitude: 16.4 } } as GeolocationPosition),
    );

    expect(await browser.currentPosition()).toEqual({ latitude: 48.2, longitude: 16.4 });
    expect(ask.calls.mostRecent().args[2]).toEqual({ enableHighAccuracy: true, timeout: 15_000, maximumAge: 60_000 });

    ask.and.callFake((_, failed) => failed!({ code: 1 } as GeolocationPositionError));
    await expectAsync(browser.currentPosition()).toBeRejectedWith({ code: 1 });
  });

  it('reports an unknown permission state when the Permissions API fails', async () => {
    spyOn(navigator.permissions, 'query').and.rejectWith(new TypeError('not supported'));

    expect(await browser.geolocationPermission()).toBeNull();
  });
});

describe('HomePage (map)', () => {
  let fixture: ComponentFixture<HomePage>;
  let http: HttpTestingController;
  let map: ReturnType<typeof fakeMapEngine>;
  let browser: ReturnType<typeof fakeBrowser>;
  let auth: ReturnType<typeof fakeAuth>;
  let viewport: ReturnType<typeof fakeViewport>;
  let evses: jasmine.SpyObj<EvsesService>;
  let link: jasmine.SpyObj<MapLink>;
  /** How long the notice about a truncated map shows. */
  let noticeMs = 6000;

  const root = () => fixture.nativeElement as HTMLElement;
  /** View reported with every move in the specs. */
  const view: MapView = { latitude: 48.2, longitude: 16.4, zoom: 12 };
  const locateButton = () => root().querySelector<HTMLButtonElement>('#locate')!;
  const options = (): MapViewOptions => map.engine.create.calls.mostRecent().args[0];

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  /** Renders the page and answers the configuration request (or fails it). */
  async function render(config: 'ok' | 'fail' = 'ok'): Promise<void> {
    fixture = TestBed.createComponent(HomePage);
    await fixture.whenStable();
    const request = http.expectOne('/api/v1/map/config');
    if (config === 'ok') {
      request.flush(MAP_CONFIG);
    } else {
      request.flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
    }
    await settle();
  }

  async function geolocated(event: GeolocateEvent): Promise<void> {
    options().onGeolocate(event);
    await settle();
  }

  async function clickLocate(): Promise<void> {
    locateButton().click();
    await settle();
  }

  beforeEach(() => {
    map = fakeMapEngine();
    browser = fakeBrowser();
    auth = fakeAuth();
    viewport = fakeViewport();
    // The details in the card load through their own service; by default they stay pending.
    evses = jasmine.createSpyObj<EvsesService>('EvsesService', ['location', 'history', 'occupancy']);
    evses.location.and.returnValue(new Promise(() => undefined));
    localStorage.removeItem(LAST_VIEW_KEY);
    localStorage.removeItem(HOME_VIEW_KEY);
    localStorage.removeItem(FILTERS_STORAGE_KEY);
    localStorage.removeItem(OPTIONS_STORAGE_KEY);
    localStorage.removeItem(VEHICLE_STORAGE_KEY);
    localStorage.removeItem(LEGEND_STORAGE_KEY);
    localStorage.removeItem(TRUNCATED_SNOOZE_KEY);
    noticeMs = 6000;
    link = jasmine.createSpyObj<MapLink>('MapLink', ['read', 'write', 'fragment', 'share']);
    link.read.and.returnValue(null);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: EvsesService, useValue: evses },
        { provide: MapLink, useValue: link },
        { provide: Viewport, useValue: viewport },
        { provide: MapEngine, useValue: map.engine },
        { provide: BrowserCapabilities, useValue: browser },
        // Read when the page first injects it, so a spec can shorten it before rendering.
        { provide: TRUNCATED_NOTICE_MS, useFactory: () => noticeMs },
      ],
    });
    // Deterministic colours: the system of the test browser may prefer dark mode.
    TestBed.inject(ThemeService).set('light');
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    TestBed.inject(ThemeService).set('system');
    localStorage.removeItem(LAST_VIEW_KEY);
    localStorage.removeItem(HOME_VIEW_KEY);
    localStorage.removeItem(FILTERS_STORAGE_KEY);
    localStorage.removeItem(OPTIONS_STORAGE_KEY);
    localStorage.removeItem(VEHICLE_STORAGE_KEY);
    localStorage.removeItem(LEGEND_STORAGE_KEY);
    localStorage.removeItem(TRUNCATED_SNOOZE_KEY);
  });

  it('creates the map in its container with style, attribution, center and zoom of the configuration', async () => {
    await render();

    expect(map.engine.create).toHaveBeenCalledTimes(1);
    expect(options()).toEqual(
      jasmine.objectContaining({
        container: root().querySelector<HTMLElement>('#map')!,
        theme: jasmine.objectContaining({ styleUrl: 'https://tiles.openfreemap.org/styles/liberty' }),
        attribution: '© OpenStreetMap contributors, OpenMapTiles',
        center: { latitude: 48.2, longitude: 15.6 },
        zoom: 7,
      }),
    );
    expect(locateButton().disabled).toBeFalse();
    expect(text(root(), '#locate')).toBe('Mein Standort');
    expect(root().querySelector('#map-notice')).toBeNull();
    expect(map.engine.prepare).toHaveBeenCalledTimes(1);
  });

  describe('views', () => {
    const homeButton = () => root().querySelector<HTMLButtonElement>('#home')!;
    const menuItem = (id: string) => document.querySelector<HTMLButtonElement>(`#${id}`)!;

    async function openMenu(): Promise<void> {
      root().querySelector<HTMLButtonElement>('#home-menu')!.click();
      await settle();
    }

    it('opens at the view of a shared link before everything else and writes every view into the address', async () => {
      localStorage.setItem(LAST_VIEW_KEY, JSON.stringify({ latitude: 48.3, longitude: 14.3, zoom: 13.5 }));
      link.read.and.returnValue({ latitude: 47.07, longitude: 15.44, zoom: 15 });
      await render();
      expect(options()).toEqual(jasmine.objectContaining({ center: { latitude: 47.07, longitude: 15.44 }, zoom: 15 }));

      options().onViewChange({ west: 16.3, south: 48.15, east: 16.45, north: 48.25 }, view);
      http.expectOne('/api/v1/locations?bbox=16.30000,48.15000,16.45000,48.25000&limit=2000').flush({
        data: [],
        meta: { returned: 0, limit: 2000, truncated: false, attribution: [] },
      });
      expect(link.write).toHaveBeenCalledOnceWith(view);
    });

    it('shares the view and tells when the link was copied or could not be', async () => {
      await render();
      const share = () => root().querySelector<HTMLButtonElement>('#share')!;
      expect(share().disabled).toBeTrue();
      options().onViewChange({ west: 16.3, south: 48.15, east: 16.45, north: 48.25 }, view);
      http.expectOne('/api/v1/locations?bbox=16.30000,48.15000,16.45000,48.25000&limit=2000').flush({
        data: [],
        meta: { returned: 0, limit: 2000, truncated: false, attribution: [] },
      });
      await settle();

      link.share.and.resolveTo('copied');
      share().click();
      await settle();
      expect(text(root(), '#share-message')).toBe('Der Link zu dieser Ansicht ist in der Zwischenablage.');

      link.share.and.resolveTo('failed');
      share().click();
      await settle();
      expect(text(root(), '#share-message')).toContain('konnte nicht kopiert werden');

      link.share.and.resolveTo('shared');
      share().click();
      await settle();
      expect(root().querySelector('#share-message')).toBeNull();
    });

    it('opens at the last view, else at the start position, else at the configuration', async () => {
      localStorage.setItem(HOME_VIEW_KEY, JSON.stringify({ latitude: 47.8, longitude: 13.0, zoom: 10 }));
      await render();
      expect(options()).toEqual(jasmine.objectContaining({ center: { latitude: 47.8, longitude: 13 }, zoom: 10 }));
      fixture.destroy();

      localStorage.setItem(LAST_VIEW_KEY, JSON.stringify({ latitude: 48.3, longitude: 14.3, zoom: 13.5 }));
      await render();
      expect(options()).toEqual(jasmine.objectContaining({ center: { latitude: 48.3, longitude: 14.3 }, zoom: 13.5 }));
    });

    it('remembers every view and sets, uses and resets the start position', async () => {
      await render();
      expect(homeButton().disabled).toBeFalse();
      await openMenu();
      expect(menuItem('set-home').disabled).toBeTrue();
      expect(menuItem('reset-home').disabled).toBeTrue();
      document.querySelector<HTMLElement>('.cdk-overlay-backdrop')!.click();
      await settle();

      // Without a start position "Startposition" returns to the default of the configuration.
      homeButton().click();
      expect(map.handle.flyTo).toHaveBeenCalledWith(48.2, 15.6, 7);

      options().onViewChange({ west: 16.3, south: 48.15, east: 16.45, north: 48.25 }, view);
      http.expectOne('/api/v1/locations?bbox=16.30000,48.15000,16.45000,48.25000&limit=2000').flush({
        data: [],
        meta: { returned: 0, limit: 2000, truncated: false, attribution: [] },
      });
      await settle();
      expect(JSON.parse(localStorage.getItem(LAST_VIEW_KEY)!)).toEqual(view);

      await openMenu();
      menuItem('set-home').click();
      await settle();
      expect(TestBed.inject(MapViewStore).home()).toEqual(view);
      expect(JSON.parse(localStorage.getItem(HOME_VIEW_KEY)!)).toEqual(view);
      expect(text(root(), '#home-saved')).toBe('Dieser Ausschnitt ist jetzt deine Startposition.');

      homeButton().click();
      expect(map.handle.flyTo).toHaveBeenCalledWith(48.2, 16.4, 12);

      // The message goes with the next move; the area is loaded already.
      options().onViewChange({ west: 16.35, south: 48.17, east: 16.4, north: 48.2 }, { ...view, zoom: 14 });
      await settle();
      expect(root().querySelector('#home-saved')).toBeNull();

      await openMenu();
      menuItem('reset-home').click();
      await settle();
      expect(localStorage.getItem(HOME_VIEW_KEY)).toBeNull();
      homeButton().click();
      expect(map.handle.flyTo.calls.mostRecent().args).toEqual([48.2, 15.6, 7]);
    });
  });

  it('shows a notice when the configuration cannot be loaded', async () => {
    await render('fail');

    expect(text(root(), '#map-notice')).toContain('Kartenkonfiguration konnte nicht geladen werden');
    expect(map.engine.create).not.toHaveBeenCalled();
    expect(locateButton().disabled).toBeTrue();
  });

  it('shows a notice when the browser has no WebGL', async () => {
    browser.webGl.and.returnValue(false);

    await render();

    expect(text(root(), '#map-notice')).toContain('kein WebGL');
    expect(map.engine.create).not.toHaveBeenCalled();
  });

  it('shows a notice when the engine fails to create the map', async () => {
    map.engine.create.and.rejectWith(new Error('Failed to initialize WebGL'));

    await render();

    expect(text(root(), '#map-notice')).toContain('Karte konnte nicht angezeigt werden');
    expect(locateButton().disabled).toBeTrue();
  });

  it('starts the geolocation on "Mein Standort" and clears the state when the position is found', async () => {
    await render();

    await clickLocate();

    expect(map.handle.locate).toHaveBeenCalledTimes(1);
    expect(text(root(), '#locate')).toBe('Standort wird gesucht …');

    await geolocated({ type: 'found' });

    expect(text(root(), '#locate')).toBe('Mein Standort');
    expect(root().querySelector('#map-notice')).toBeNull();
  });

  it('explains a denied permission and keeps the notice when tracking ends', async () => {
    await render();
    await clickLocate();

    await geolocated({ type: 'error', code: 1 });
    await geolocated({ type: 'ended' });

    expect(text(root(), '#map-notice')).toContain('Standortfreigabe wurde abgelehnt');
    expect(text(root(), '#locate')).toBe('Mein Standort');
  });

  it('explains an unavailable position', async () => {
    await render();
    await clickLocate();

    await geolocated({ type: 'error', code: 3 });

    expect(text(root(), '#map-notice')).toContain('Standort konnte nicht ermittelt werden');
  });

  it('does not ask for the position outside a secure context', async () => {
    browser.secureContext.and.returnValue(false);
    await render();

    await clickLocate();

    expect(text(root(), '#map-notice')).toContain('sichere Verbindung (HTTPS)');
    expect(map.handle.locate).not.toHaveBeenCalled();
  });

  it('explains an earlier refusal without asking again', async () => {
    browser.geolocationPermission.and.resolveTo('denied');
    await render();

    await clickLocate();

    expect(text(root(), '#map-notice')).toContain('Standortfreigabe wurde abgelehnt');
    expect(map.handle.locate).not.toHaveBeenCalled();
  });

  it('explains a browser without geolocation', async () => {
    browser.geolocation.and.returnValue(false);
    await render();

    await clickLocate();

    expect(text(root(), '#map-notice')).toContain('keine Standortbestimmung');
    expect(map.handle.locate).not.toHaveBeenCalled();
  });

  it('asks to retry while the geolocation control is not ready', async () => {
    map.handle.locate.and.returnValue(false);
    await render();

    await clickLocate();

    expect(text(root(), '#map-notice')).toContain('noch nicht bereit');
    expect(text(root(), '#locate')).toBe('Mein Standort');
  });

  describe('locations', () => {
    const vienna: Bbox = { west: 16.2, south: 48.1, east: 16.6, north: 48.3 };
    const url = '/api/v1/locations?bbox=16.20000,48.10000,16.60000,48.30000&limit=2000';
    const zoomedOut = '/api/v1/locations?bbox=15.00000,47.00000,17.00000,49.00000&limit=2000';
    const ring = locationSummary({
      id: 'loc-2',
      name: null,
      operator: null,
      evsesAvailable: 0,
      maxPowerKw: null,
      statusObservedAt: null,
      availability: 'OUT_OF_ORDER',
      evsesFaulted: 3,
    });
    const attributionText = 'Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)';

    function response(overrides: Partial<LocationsResponse['meta']> = {}): LocationsResponse {
      return {
        data: [locationSummary(), ring],
        meta: {
          returned: 2,
          limit: 2000,
          truncated: false,
          attribution: [
            { source: 'econtrol', text: attributionText, license: 'Terms' },
            { source: 'other', text: attributionText, license: 'Terms' },
          ],
          ...overrides,
        },
      };
    }

    async function moveTo(box: Bbox, answer: LocationsResponse | 'fail', expected = url): Promise<void> {
      options().onViewChange(box, view);
      const request = http.expectOne(expected);
      if (answer === 'fail') {
        request.flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
      } else {
        request.flush(answer);
      }
      await settle();
    }

    it('loads the locations of the visible area and shows markers, legend and attribution', async () => {
      await render();
      expect(root().querySelector('#data-attribution')).toBeNull();
      expect(Array.from(root().querySelectorAll('#legend li')).map((item) => item.textContent?.trim())).toEqual([
        'frei bis 22 kW',
        'frei 23–149 kW',
        'frei ab 150 kW',
        'belegt',
        'außer Betrieb',
        'Status unbekannt',
      ]);

      await moveTo(vienna, response());

      expect(map.handle.showLocations).toHaveBeenCalledWith([locationSummary(), ring]);
      expect(text(root(), '#data-attribution')).toBe(attributionText);
      expect(root().querySelector('#truncated')).toBeNull();
    });

    it('clamps the box at the edges of the world and hints at truncated results', async () => {
      await render();

      await moveTo(
        { west: -200, south: -95, east: 200, north: 95 },
        response({ truncated: true }),
        '/api/v1/locations?bbox=-180.00000,-90.00000,180.00000,90.00000&limit=2000',
      );

      expect(text(root(), '#truncated')).toBe('Nur die nächsten 2000 Standorte – zoome hinein.');
    });

    it('hides the notice about a truncated map after a few seconds', async () => {
      noticeMs = 20;
      await render();
      await moveTo(vienna, response({ truncated: true }));
      expect(root().querySelector('#truncated')).not.toBeNull();

      await new Promise((resolve) => setTimeout(resolve, 60));
      await settle();
      expect(root().querySelector('#truncated')).toBeNull();
    });

    it('snoozes the notice about a truncated map for a day once it is closed', async () => {
      // A box apart from Vienna: loading it does not cover Vienna, so Vienna loads again afterwards.
      const elsewhere: Bbox = { west: 10, south: 47, east: 11, north: 48 };
      const elsewhereUrl = '/api/v1/locations?bbox=10.00000,47.00000,11.00000,48.00000&limit=2000';
      await render();
      await moveTo(vienna, response({ truncated: true }));

      root().querySelector<HTMLButtonElement>('#truncated-close')!.click();
      await settle();
      expect(root().querySelector('#truncated')).toBeNull();
      expect(Number(localStorage.getItem(TRUNCATED_SNOOZE_KEY))).toBeGreaterThan(Date.now() + 23 * 60 * 60 * 1000);

      // Truncated again while snoozed: no notice.
      await moveTo(elsewhere, response(), elsewhereUrl);
      await moveTo(vienna, response({ truncated: true }));
      expect(root().querySelector('#truncated')).toBeNull();

      // After the snooze it comes back.
      localStorage.setItem(TRUNCATED_SNOOZE_KEY, '1');
      await moveTo(elsewhere, response(), elsewhereUrl);
      await moveTo(vienna, response({ truncated: true }));
      expect(root().querySelector('#truncated')).not.toBeNull();
    });

    it('folds the legend into a button and remembers it', async () => {
      await render();
      expect(root().querySelectorAll('#legend li').length).toBe(6);

      root().querySelector<HTMLButtonElement>('#legend-hide')!.click();
      await settle();
      expect(root().querySelector('#legend')).toBeNull();
      // Folded into a small symbol next to the filter button.
      expect(root().querySelector('#map-legend-bar > #legend-show')!.getAttribute('aria-label')).toBe('Legende einblenden');
      expect(text(root(), '#legend-show')).toBe('');
      expect(localStorage.getItem(LEGEND_STORAGE_KEY)).toBe('0');

      root().querySelector<HTMLButtonElement>('#legend-show')!.click();
      await settle();
      expect(root().querySelector('#legend')).not.toBeNull();
      expect(localStorage.getItem(LEGEND_STORAGE_KEY)).toBe('1');
    });

    it('starts with the legend folded when it was folded before', async () => {
      localStorage.setItem(LEGEND_STORAGE_KEY, '0');
      await render();
      expect(root().querySelector('#legend')).toBeNull();
      expect(root().querySelector('#legend-show')).not.toBeNull();
    });

    it('shows a notice when loading fails and clears it with the next successful load', async () => {
      await render();

      await moveTo(vienna, 'fail');
      expect(text(root(), '#map-notice')).toContain('Ladepunkte konnten nicht geladen werden');

      await moveTo(vienna, response());
      expect(root().querySelector('#map-notice')).toBeNull();
    });

    it('runs one request at a time and shows only the newest area', async () => {
      await render();
      const east = '/api/v1/locations?bbox=16.00000,48.00000,17.00000,49.00000&limit=2000';

      options().onViewChange({ west: 15, south: 47, east: 17, north: 49 }, view);
      options().onViewChange({ west: 10, south: 46, east: 12, north: 47 }, view);
      options().onViewChange(vienna, view);
      const older = http.expectOne(zoomedOut);
      await fixture.whenStable();
      expect(root().querySelector('#map-loading')).not.toBeNull();
      older.flush({ ...response(), data: [] });
      await settle();
      // The area passed over while the first request ran is never asked for.
      http.expectOne(url).flush(response());
      await settle();

      expect(map.handle.showLocations).toHaveBeenCalledTimes(2);
      expect(map.handle.showLocations.calls.mostRecent().args[0].length).toBe(2);
      expect(root().querySelector('#map-loading')).toBeNull();

      // A failure of a request that is overtaken shows no notice.
      options().onViewChange({ west: 15, south: 47, east: 17, north: 49 }, view);
      options().onViewChange({ west: 16, south: 48, east: 17, north: 49 }, view);
      http.expectOne(zoomedOut).flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
      await settle();
      http.expectOne(east).flush(response());
      await settle();
      expect(root().querySelector('#map-notice')).toBeNull();
    });

    it('loads the visible area again with the filters and shows their number', async () => {
      await render();
      const filters = TestBed.inject(MapFilterStore);
      expect(root().querySelector('.filter-count')).toBeNull();

      // Before the map reported its area, a change only takes effect with the first load.
      filters.set({ ...NO_FILTERS, greenEnergy: true });
      await settle();
      await moveTo(vienna, response(), `${url}&greenEnergy=true`);
      expect(text(root(), '#filter-toggle .filter-count')).toBe('1');

      filters.set({ ...NO_FILTERS, greenEnergy: true, minPowerKw: 50 });
      await settle();
      http.expectOne(`${url}&minPowerKw=50&greenEnergy=true`).flush(response());
      await settle();
      expect(text(root(), '#filter-toggle .filter-count')).toBe('2');

      // The same filters again: nothing to load.
      filters.set({ ...NO_FILTERS, greenEnergy: true, minPowerKw: 50 });
      await settle();
      expect(http.match(() => true)).toEqual([]);
    });

    it('opens the map options instead of filters and card and applies changed cluster options at once', async () => {
      await render();
      expect(options().cluster).toEqual(DEFAULT_MAP_OPTIONS);
      await moveTo(vienna, response());
      options().onSelect('loc-1');
      await settle();

      root().querySelector<HTMLButtonElement>('#options-toggle')!.click();
      await settle();
      await settle();
      expect(root().querySelector('#location-card')).toBeNull();
      expect(root().querySelector('#map-options')).not.toBeNull();
      expect(root().querySelector('#options-toggle')!.getAttribute('aria-expanded')).toBe('true');

      TestBed.inject(MapOptionsStore).set({ clusterRadius: 80 });
      await settle();
      expect(map.handle.setCluster).toHaveBeenCalledWith({ clusterRadius: 80, clusterMaxZoom: 13 });

      root().querySelector<HTMLButtonElement>('#filter-toggle')!.click();
      await settle();
      expect(root().querySelector('#map-options')).toBeNull();
      root().querySelector<HTMLButtonElement>('#options-toggle')!.click();
      await settle();
      expect(root().querySelector('app-map-filter-panel')).toBeNull();

      root().querySelector<HTMLButtonElement>('#close-options')!.click();
      await settle();
      expect(root().querySelector('#map-options')).toBeNull();
      root().querySelector<HTMLButtonElement>('#options-toggle')!.click();
      await settle();
      options().onSelect('loc-1');
      await settle();
      expect(root().querySelector('#map-options')).toBeNull();
      expect(root().querySelector('#location-card')).not.toBeNull();
    });

    it('opens the filters instead of the card and closes them when a location is chosen', async () => {
      await render();
      await moveTo(vienna, response());
      options().onSelect('loc-1');
      await settle();

      root().querySelector<HTMLButtonElement>('#filter-toggle')!.click();
      await settle();
      await settle();
      expect(root().querySelector('#location-card')).toBeNull();
      expect(root().querySelector('#filter-panel')).not.toBeNull();
      expect(root().classList).toContain('has-filters');
      expect(root().querySelector('#filter-toggle')!.getAttribute('aria-expanded')).toBe('true');

      root().querySelector<HTMLButtonElement>('#close-filters')!.click();
      await settle();
      expect(root().querySelector('#filter-panel')).toBeNull();

      root().querySelector<HTMLButtonElement>('#filter-toggle')!.click();
      await settle();
      options().onSelect('unknown');
      await settle();
      expect(root().querySelector('#filter-panel')).not.toBeNull();
      options().onSelect('loc-1');
      await settle();
      expect(root().querySelector('#filter-panel')).toBeNull();
      expect(root().querySelector('#location-card')).not.toBeNull();
    });

    it('needs no request when zooming into an area that is loaded completely and recently', async () => {
      await render();
      await moveTo(vienna, response());
      const inner: Bbox = { west: 16.3, south: 48.15, east: 16.5, north: 48.25 };
      const innerUrl = '/api/v1/locations?bbox=16.30000,48.15000,16.50000,48.25000&limit=2000';

      options().onViewChange(inner, view);
      expect(http.match(innerUrl)).toEqual([]);

      // Older data is loaded again.
      const now = Date.now();
      spyOn(Date, 'now').and.returnValue(now + LOCATIONS_FRESH_MS);
      await moveTo(inner, response({ truncated: true }), innerUrl);

      // Incomplete data, too.
      await moveTo({ west: 16.35, south: 48.17, east: 16.45, north: 48.2 }, response(), '/api/v1/locations?bbox=16.35000,48.17000,16.45000,48.20000&limit=2000');
    });

    describe('routes', () => {
      const routeButton = () => root().querySelector<HTMLButtonElement>('#route-here');
      const stop = (id: string, positionOnRouteMeters: number, overrides: Partial<LocationSummary> = {}): RouteLocation => ({
        ...locationSummary({ id, ...overrides }),
        distanceFromRouteMeters: 120,
        positionOnRouteMeters,
      });
      const geometry: [number, number][] = [
        [16.37, 48.21],
        [16.3731, 48.2085],
      ];

      async function openCard(): Promise<void> {
        auth.hasPermission.and.callFake((permission) => permission === 'route:read');
        await render();
        await moveTo(vienna, response());
        options().onSelect('loc-1');
        await settle();
      }

      async function clickRoute(): Promise<void> {
        routeButton()!.click();
        await settle();
      }

      it('needs route:read', async () => {
        await render();
        await moveTo(vienna, response());
        options().onSelect('loc-1');
        await settle();

        expect(root().querySelector('#location-card')).not.toBeNull();
        expect(routeButton()).toBeNull();
      });

      it('calculates the route from the own position, draws it and removes it again', async () => {
        await openCard();
        await clickRoute();

        const request = http.expectOne('/api/v1/routes');
        expect(request.request.method).toBe('POST');
        expect(request.request.body).toEqual({
          waypoints: [
            { latitude: 48.21, longitude: 16.37 },
            { latitude: 48.2085, longitude: 16.3731 },
          ],
        });
        expect(text(root(), '#route-here')).toBe('Route wird berechnet …');
        expect(routeButton()!.disabled).toBeTrue();
        request.flush({ data: { distanceMeters: 12480, durationSeconds: 960, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
        await settle();

        expect(map.handle.showRoute).toHaveBeenCalledWith(geometry);
        expect(text(root(), '#route-summary')).toBe('Route: 12 km · 16 min Route entfernen');
        // The card of the destination gives way to the stops along the route.
        expect(root().querySelector('#location-card')).toBeNull();
        expect(text(root(), '#route-stops app-loading')).toBe('Ladepunkte entlang der Route werden gesucht …');
        const along = http.expectOne('/api/v1/routes/locations');
        expect(along.request.body).toEqual({ geometry: { type: 'LineString', coordinates: geometry }, corridorMeters: 1000, limit: 500, ...NO_FILTERS });
        along.flush({
          data: [stop('loc-1', 1840), stop('loc-2', 12400, { name: null, evsesAvailable: 0, maxAvailablePowerKw: null })],
          meta: { returned: 2, limit: 200, truncated: true, attribution: [] },
        });
        await settle();

        // 12 km need no charging: no location gets a ring.
        expect(map.handle.highlightLocations).not.toHaveBeenCalledWith([jasmine.objectContaining({ id: 'loc-1' }), jasmine.objectContaining({ id: 'loc-2' })]);
        expect(map.handle.highlightLocations).toHaveBeenCalledWith([]);
        expect(text(root(), '#charging-plan')).toBe('Ohne Ladestopp erreichbar: Ankunft mit etwa 96 % (Energiebedarf 2 kWh).');
        expect(text(root(), '#working')).toBeUndefined();
        expect(text(root(), '#stops-hint')).toBe('Die ersten 2 Standorte bis 1 km neben der Route, mit den Filtern der Karte.');
        const stops = Array.from(root().querySelectorAll<HTMLButtonElement>('#route-stops li button.open-stop'));
        expect(stops.map((button) => text(button, '.stop-name'))).toEqual(['1,8 km · Parkgarage Stephansplatz', '12,4 km · Ladestandort']);
        expect(stops.map((button) => text(button, '.stop-state'))).toEqual(['2 von 4 frei · bis 22 kW frei', '0 von 4 frei']);

        // A stop opens its card; closing it returns to the list.
        stops[1].click();
        await settle();
        expect(map.handle.flyTo).toHaveBeenCalledWith(48.2085, 16.3731, FOCUS_ZOOM);
        expect(text(root(), '#location-card h2')).toBe('Ladestandort');
        root().querySelector<HTMLButtonElement>('#close-card')!.click();
        await settle();
        expect(root().querySelector('#route-stops')).not.toBeNull();

        root().querySelector<HTMLButtonElement>('#route-clear')!.click();
        await settle();
        expect(map.handle.showRoute).toHaveBeenCalledWith(null);
        expect(map.handle.highlightLocations).toHaveBeenCalledWith([]);
        expect(root().querySelector('#route-summary')).toBeNull();
        expect(root().querySelector('#route-stops')).toBeNull();
      });

      describe('charging stops', () => {
        const START = { latitude: 48.21, longitude: 16.37 };
        const DESTINATION = { latitude: 48.2085, longitude: 16.3731 };
        const along = [
          stop('loc-3', 3000, { name: 'Stopp A', latitude: 48.1, longitude: 16.1 }),
          stop('loc-4', 5000, { name: 'Stopp B', latitude: 48.2, longitude: 15.6 }),
          stop('loc-5', 7000, { name: null, latitude: 48.3, longitude: 15.1 }),
          stop('loc-6', 9000, { name: 'Stopp D', latitude: 48.3, longitude: 14.5 }),
          stop('loc-1', 12000),
        ];
        const point = (location: RouteLocation) => ({ latitude: location.latitude, longitude: location.longitude });
        const addButton = (id: string) =>
          root().querySelector<HTMLButtonElement>(`#route-stops li:has([data-stop="${id}"]) .add-stop`);
        const removeButton = (id: string) => root().querySelector<HTMLButtonElement>(`#route-summary .remove-stop[data-stop="${id}"]`);

        /** Answers the pending route and then the list along it. */
        async function answerRoute(distanceMeters: number, durationSeconds: number, request = http.expectOne('/api/v1/routes')): Promise<void> {
          request.flush({ data: { distanceMeters, durationSeconds, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          http.expectOne('/api/v1/routes/locations').flush({ data: along, meta: { returned: along.length, limit: 500, truncated: false, attribution: [] } });
          await settle();
        }

        async function showRoute(): Promise<void> {
          await openCard();
          await clickRoute();
          await answerRoute(12480, 960);
        }

        /** The pending route request, after checking its waypoints. */
        function expectWaypoints(...waypoints: unknown[]): TestRequest {
          const request = http.expectOne('/api/v1/routes');
          expect(request.request.body).toEqual({ waypoints });
          return request;
        }

        it('plans up to three stops in the order of the route and removes them again', async () => {
          await showRoute();
          // Every location of the list but the destination can become a stop.
          expect(Array.from(root().querySelectorAll('#route-stops .add-stop')).length).toBe(4);
          expect(addButton('loc-1')).toBeNull();

          addButton('loc-4')!.click();
          await settle();
          const request = expectWaypoints(START, point(along[1]), DESTINATION);
          expect(addButton('loc-3')!.disabled).toBeTrue();
          request.flush({ data: { distanceMeters: 182_000, durationSeconds: 8_040, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          http.expectOne('/api/v1/routes/locations').flush({ data: along, meta: { returned: 5, limit: 500, truncated: false, attribution: [] } });
          await settle();
          expect(text(root(), '#route-summary')).toBe('Route: 182 km · 2 h 14 min · 1 Ladestopp Route entfernen ✕ Stopp B');
          expect(text(root(), '#route-stops li.planned .stop-name')).toBe('5,0 km · Stopp B Ladestopp');
          expect(addButton('loc-4')).toBeNull();
          expect(map.handle.showRoute).toHaveBeenCalledTimes(2);

          // A stop earlier on the route goes before the first one, a later one after it.
          addButton('loc-3')!.click();
          await settle();
          await answerRoute(190_000, 8_400, expectWaypoints(START, point(along[0]), point(along[1]), DESTINATION));
          addButton('loc-6')!.click();
          await settle();
          await answerRoute(200_000, 9_000, expectWaypoints(START, point(along[0]), point(along[1]), point(along[3]), DESTINATION));
          expect(text(root(), '#route-summary')).toBe('Route: 200 km · 2 h 30 min · 3 Ladestopps Route entfernen ✕ Stopp A ✕ Stopp B ✕ Stopp D');
          // Three stops: no room for another.
          expect(root().querySelector('#route-stops .add-stop')).toBeNull();
          expect(Array.from(root().querySelectorAll('#route-stops li.planned')).length).toBe(3);

          removeButton('loc-4')!.click();
          await settle();
          expect(removeButton('loc-3')!.disabled).toBeTrue();
          await answerRoute(185_000, 8_100, expectWaypoints(START, point(along[0]), point(along[3]), DESTINATION));
          expect(text(root(), '#route-summary')).toBe('Route: 185 km · 2 h 15 min · 2 Ladestopps Route entfernen ✕ Stopp A ✕ Stopp D');
          expect(addButton('loc-4')).not.toBeNull();
        });

        it('keeps the shown route when the new one fails', async () => {
          await showRoute();
          addButton('loc-5')!.click();
          await settle();
          http.expectOne('/api/v1/routes').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
          await settle();

          expect(text(root(), '#map-notice')).toBe('Die Route konnte nicht berechnet werden. Bitte versuche es erneut.');
          expect(text(root(), '#route-summary')).toBe('Route: 12 km · 16 min Route entfernen');
          expect(addButton('loc-5')!.disabled).toBeFalse();
          expect(root().querySelector('#route-stops li.planned')).toBeNull();

          // A stop without a name is named in its button.
          addButton('loc-5')!.click();
          await settle();
          await answerRoute(13_000, 1_000);
          expect(removeButton('loc-5')!.getAttribute('aria-label')).toBe('Stopp entfernen: Ladestandort');
          expect(text(root(), '#route-summary .trip-stops')).toBe('✕ Ladestandort');
        });

        it('starts every route without stops and forgets them with the route', async () => {
          await showRoute();
          addButton('loc-3')!.click();
          await settle();
          await answerRoute(13_000, 1_000);

          root().querySelector<HTMLButtonElement>('#route-clear')!.click();
          await settle();
          options().onSelect('loc-1');
          await settle();
          await clickRoute();
          await answerRoute(12_480, 960, expectWaypoints(START, DESTINATION));
          expect(root().querySelector('#route-summary .trip-stops')).toBeNull();
        });

        it('loads the stops of the shown route again when the filters change, without a new route', async () => {
          await showRoute();
          const filters = TestBed.inject(MapFilterStore);
          const flushLocations = async () => {
            for (const request of http.match((r) => r.url.startsWith('/api/v1/locations'))) {
              request.flush(response());
            }
            await settle();
          };

          filters.set({ ...NO_FILTERS, minPowerKw: 150 });
          await settle();
          const reload = http.expectOne('/api/v1/routes/locations');
          expect(reload.request.body).toEqual({ geometry: { type: 'LineString', coordinates: geometry }, corridorMeters: 1000, limit: 500, ...NO_FILTERS, minPowerKw: 150 });
          expect(text(root(), '#route-stops app-loading')).toBe('Ladepunkte entlang der Route werden gesucht …');
          reload.flush({ data: [along[1]], meta: { returned: 1, limit: 500, truncated: false, attribution: [] } });
          await settle();
          await flushLocations();
          expect(Array.from(root().querySelectorAll('#route-stops li')).length).toBe(1);
          http.expectNone('/api/v1/routes');

          // Two changes in a row: only the answer for the newest filters counts.
          filters.set({ ...NO_FILTERS, minPowerKw: 50 });
          await settle();
          filters.set({ ...NO_FILTERS, minPowerKw: 300 });
          await settle();
          const [older, newer] = http.match('/api/v1/routes/locations');
          expect(newer.request.body.minPowerKw).toBe(300);
          newer.flush({ data: [], meta: { returned: 0, limit: 500, truncated: false, attribution: [] } });
          await settle();
          older.flush({ data: along, meta: { returned: 5, limit: 500, truncated: false, attribution: [] } });
          await settle();
          await flushLocations();
          await flushLocations();
          expect(text(root(), '#route-stops li')).toBe('Keine Ladepunkte entlang der Route.');

          // Without a route the filters only change the map.
          root().querySelector<HTMLButtonElement>('#route-clear')!.click();
          await settle();
          filters.set({ ...NO_FILTERS });
          await settle();
          await flushLocations();
          await flushLocations();
          http.expectNone('/api/v1/routes/locations');
        });
      });

      describe('reach', () => {
        const reachButton = () => root().querySelector<HTMLButtonElement>('#reach');
        const square = (west: number, south: number, east: number, north: number): [number, number][] => [
          [west, south],
          [east, south],
          [east, north],
          [west, north],
          [west, south],
        ];
        const area = (minutes: number, ...rings: [number, number][][]) => ({
          type: 'Feature' as const,
          properties: { minutes },
          geometry: { type: 'MultiPolygon' as const, coordinates: [rings] },
        });
        // Nested areas; the smallest has a hole.
        const areas: ReachAreas = {
          type: 'FeatureCollection',
          features: [
            area(60, square(16.0, 48.0, 16.8, 48.6)),
            area(30, square(16.2, 48.1, 16.5, 48.3)),
            area(15, square(16.3, 48.15, 16.4, 48.25), square(16.33, 48.19, 16.34, 48.2)),
          ],
        };
        const inReach = [
          locationSummary({ id: 'r-15', name: 'Nah', maxAvailablePowerKw: 22 }),
          locationSummary({ id: 'r-hole', name: 'Im Loch', latitude: 48.195, longitude: 16.335, maxAvailablePowerKw: 150 }),
          locationSummary({ id: 'r-60', name: null, latitude: 48.4, longitude: 16.6, maxAvailablePowerKw: 50 }),
          locationSummary({ id: 'r-out', name: 'Draußen', latitude: 49, longitude: 17, maxAvailablePowerKw: 300 }),
          locationSummary({ id: 'r-busy', name: 'Belegt', evsesAvailable: 0, maxAvailablePowerKw: null }),
        ];
        const locationsOf = (data: LocationSummary[], total = data.length) => ({
          data,
          meta: { returned: data.length, total, limit: 500, truncated: total > data.length, attribution: [] },
        });

        /** Answers the list of the largest area and the counts of the smaller ones (largest first). */
        async function answerWithin(data: LocationSummary[], totals: number[]): Promise<void> {
          const requests = http.match('/api/v1/locations/within');
          expect(requests.map((request) => request.request.body.limit)).toEqual([500, 0, 0]);
          requests.forEach((request, index) => request.flush(locationsOf(index === 0 ? data : [], totals[index])));
          await settle();
        }

        async function showMap(): Promise<void> {
          auth.hasPermission.and.callFake((permission) => permission === 'route:read');
          await render();
          await moveTo(vienna, response());
        }

        async function clickReach(): Promise<void> {
          reachButton()!.click();
          await settle();
        }

        it('needs route:read', async () => {
          await render();
          expect(reachButton()).toBeNull();
        });

        it('shows the areas reachable from the own position, the locations in them and removes them', async () => {
          await showMap();
          expect(text(root(), '#reach')).toBe('Reichweite');

          await clickReach();
          const request = http.expectOne('/api/v1/routes/reach');
          expect(request.request.method).toBe('POST');
          expect(request.request.body).toEqual({ position: { latitude: 48.21, longitude: 16.37 }, minutes: [15, 30, 60] });
          expect(text(root(), '#reach')).toBe('Reichweite wird berechnet …');
          expect(text(root(), '#working')).toBe('Reichweite wird berechnet …');
          expect(reachButton()!.disabled).toBeTrue();
          request.flush({ data: areas, meta: { attribution: [] } });
          await settle();

          expect(map.handle.showReach).toHaveBeenCalledWith(areas);
          expect(text(root(), '#reach-summary')).toBe('Reichweite mit dem Auto: 15 · 30 · 60 min Reichweite entfernen');
          expect(reachButton()!.disabled).toBeFalse();

          // The locations inside the largest area and the counts of the smaller ones, from the own position, with the
          // filters of the map.
          const within = http.match('/api/v1/locations/within');
          expect(within.map((request) => request.request.body)).toEqual(
            [0, 1, 2].map((index) => ({
              geometry: areas.features[index].geometry,
              center: { latitude: 48.21, longitude: 16.37 },
              limit: index === 0 ? 500 : 0,
              ...NO_FILTERS,
            })),
          );
          expect(text(root(), '#reach-locations app-loading')).toBe('Ladepunkte in Reichweite werden gesucht …');
          within.forEach((request, index) => request.flush(locationsOf(index === 0 ? inReach : [], [9, 4, 2][index])));
          await settle();

          expect(text(root(), '#reach-hint')).toBe('9 Standorte in Reichweite, mit den Filtern der Karte. Die Liste nutzt die nächsten 5.');
          const bands = Array.from(root().querySelectorAll<HTMLElement>('#reach-counts li'));
          expect(bands.map((band) => [text(band, '.band-label'), text(band, '.band-count')])).toEqual([
            ['bis 15 min', '2'],
            ['15–30 min', '2'],
            ['30–60 min', '5'],
          ]);
          const fastest = Array.from(root().querySelectorAll<HTMLButtonElement>('#reach-locations li button.open-stop'));
          expect(fastest.map((button) => text(button, '.stop-name'))).toEqual(['Draußen', 'Im Loch', 'Ladestandort', 'Nah']);
          expect(fastest.map((button) => text(button, '.stop-state'))).toEqual([
            'bis 300 kW frei · 2 von 4 frei',
            'bis 150 kW frei · 2 von 4 frei · 15–30 min',
            'bis 50 kW frei · 2 von 4 frei · 30–60 min',
            'bis 22 kW frei · 2 von 4 frei · bis 15 min',
          ]);

          // The list closes and opens again from the summary.
          root().querySelector<HTMLButtonElement>('#close-reach-list')!.click();
          await settle();
          expect(root().querySelector('#reach-locations')).toBeNull();
          root().querySelector<HTMLButtonElement>('#show-reach-list')!.click();
          await settle();

          // A location opens its card; closing it returns to the list.
          root().querySelectorAll<HTMLButtonElement>('#reach-locations li button.open-stop')[1].click();
          await settle();
          expect(map.handle.flyTo).toHaveBeenCalledWith(48.195, 16.335, FOCUS_ZOOM);
          expect(text(root(), '#location-card h2')).toBe('Im Loch');
          root().querySelector<HTMLButtonElement>('#close-card')!.click();
          await settle();
          expect(root().querySelector('#reach-locations')).not.toBeNull();

          root().querySelector<HTMLButtonElement>('#reach-clear')!.click();
          await settle();
          expect(map.handle.showReach).toHaveBeenCalledWith(null);
          expect(root().querySelector('#reach-summary')).toBeNull();
          expect(root().querySelector('#reach-locations')).toBeNull();
        });

        it('loads the locations again when the filters change, without a new reach, and drops older answers', async () => {
          await showMap();
          await clickReach();
          http.expectOne('/api/v1/routes/reach').flush({ data: areas, meta: { attribution: [] } });
          await settle();
          http.match('/api/v1/locations/within').forEach((request) => request.flush({ title: 'Error' }, { status: 500, statusText: 'Error' }));
          await settle();
          expect(text(root(), '#reach-locations .error')).toBe('Die Ladepunkte in Reichweite konnten nicht geladen werden.');

          const filters = TestBed.inject(MapFilterStore);
          const flushLocations = async () => {
            for (const request of http.match((r) => r.urlWithParams.startsWith('/api/v1/locations?'))) {
              request.flush(response());
            }
            await settle();
          };
          filters.set({ ...NO_FILTERS, minPowerKw: 50 });
          await settle();
          filters.set({ ...NO_FILTERS, minPowerKw: 150 });
          await settle();
          await flushLocations();
          await flushLocations();
          const pending = http.match('/api/v1/locations/within');
          const [older, newer] = [pending.slice(0, 3), pending.slice(3)];
          expect(newer.map((request) => request.request.body.minPowerKw)).toEqual([150, 150, 150]);
          newer.forEach((request, index) => request.flush(locationsOf(index === 0 ? [inReach[1]] : [], [1, 1, 0][index])));
          await settle();
          older.forEach((request, index) => request.flush(locationsOf(index === 0 ? inReach : [], [9, 4, 2][index])));
          await settle();
          // The answer for the older filters is dropped; nested counts never go below 0.
          expect(text(root(), '#reach-hint')).toBe('1 Standorte in Reichweite, mit den Filtern der Karte.');
          expect(Array.from(root().querySelectorAll<HTMLElement>('#reach-counts .band-count')).map((count) => count.textContent)).toEqual(['0', '1', '0']);
          http.expectNone('/api/v1/routes/reach');

          // Without free locations.
          filters.set({ ...NO_FILTERS, minPowerKw: 300 });
          await settle();
          await flushLocations();
          await answerWithin([inReach[4]], [1, 1, 1]);
          expect(text(root(), '#reach-locations ol li')).toBe('Gerade kein freier Ladepunkt in Reichweite.');

          // Removed while loading: the answer is dropped.
          filters.set({ ...NO_FILTERS });
          await settle();
          await flushLocations();
          const late = http.match('/api/v1/locations/within');
          root().querySelector<HTMLButtonElement>('#reach-clear')!.click();
          await settle();
          late.forEach((request) => request.flush({ title: 'Error' }, { status: 500, statusText: 'Error' }));
          await settle();
          expect(root().querySelector('#reach-locations')).toBeNull();
        });

        it('drops the answer for a reach removed meanwhile', async () => {
          await showMap();
          await clickReach();
          http.expectOne('/api/v1/routes/reach').flush({ data: areas, meta: { attribution: [] } });
          await settle();
          await answerWithin([], [0, 0, 0]);

          // Asked again, then removed while the new one is calculated.
          await clickReach();
          const late = http.expectOne('/api/v1/routes/reach');
          root().querySelector<HTMLButtonElement>('#reach-clear')!.click();
          await settle();
          late.flush({ data: areas, meta: { attribution: [] } });
          await settle();

          expect(map.handle.showReach.calls.allArgs()).toEqual([[areas], [null]]);
          expect(root().querySelector('#reach-summary')).toBeNull();
        });

        for (const [status, notice] of [
          [503, 'Die Routenberechnung ist gerade nicht verfügbar. Bitte versuche es später.'],
          [422, 'In der Nähe deines Standorts wurde keine Straße gefunden.'],
          [500, 'Die Reichweite konnte nicht berechnet werden. Bitte versuche es erneut.'],
        ] as const) {
          it(`explains an answer ${status} of the API`, async () => {
            await showMap();
            await clickReach();

            http.expectOne('/api/v1/routes/reach').flush({ title: 'Error' }, { status, statusText: 'Error' });
            await settle();

            expect(text(root(), '#map-notice')).toBe(notice);
            expect(map.handle.showReach).not.toHaveBeenCalled();
            expect(reachButton()!.disabled).toBeFalse();
          });
        }

        it('explains a refused or failed position and sends nothing', async () => {
          await showMap();

          browser.currentPosition.and.rejectWith({ code: 1 });
          await clickReach();
          expect(text(root(), '#map-notice')).toBe('Die Standortfreigabe wurde abgelehnt. Du kannst sie in den Website-Einstellungen deines Browsers erlauben.');

          browser.currentPosition.and.rejectWith({ code: 2 });
          await clickReach();
          expect(text(root(), '#map-notice')).toBe('Dein Standort konnte nicht ermittelt werden. Bitte versuche es erneut.');

          browser.secureContext.and.returnValue(false);
          browser.currentPosition.calls.reset();
          await clickReach();
          expect(text(root(), '#map-notice')).toBe('Der Standort ist nur über eine sichere Verbindung (HTTPS) verfügbar.');
          expect(browser.currentPosition).not.toHaveBeenCalled();
          http.expectNone('/api/v1/routes/reach');
        });
      });

      describe('prices', () => {
        const along = [
          stop('loc-3', 3000, { name: 'Stopp A' }),
          stop('loc-4', 5000, { name: 'Stopp B' }),
          stop('loc-5', 7000, { name: 'Stopp C' }),
          stop('loc-1', 12000),
        ];
        const price = (
          locationId: string,
          adHoc: number | null,
          card: number | null = null,
        ): LocationPrices => ({
          locationId,
          adHoc: adHoc === null ? null : { cost: adHoc, currency: 'EUR', evseId: 'AT*TST*E1*1' },
          cheapestCard:
            card === null
              ? null
              : { emp: 'smatrics', empName: 'SMATRICS', tariffId: 't-1', tariff: 'Basic', cost: card, currency: 'EUR', evseId: 'AT*TST*E1*1' },
        });
        const pricesOf = (data: LocationPrices[]) => ({ data, meta: { session: { kwh: 30, minutes: 40 }, attribution: [] } });
        const names = () => Array.from(root().querySelectorAll<HTMLElement>('#route-stops li .open-stop')).map((button) => text(button, '.stop-name'));
        const prices = () =>
          Array.from(root().querySelectorAll<HTMLElement>('#route-stops li .open-stop')).map((button) => text(button, '.stop-price') ?? null);
        const order = (value: 'route' | 'price') =>
          root().querySelector<HTMLElement>(`#stops-order mat-button-toggle[value="${value}"] button`)!.click();

        /** A route to loc-1 with the given permissions, answered up to the list along it. */
        async function showRoute(permissions: string[], stops = along): Promise<void> {
          auth.hasPermission.and.callFake((permission) => permissions.includes(permission));
          await render();
          await moveTo(vienna, response());
          options().onSelect('loc-1');
          await settle();
          await clickRoute();
          http.expectOne('/api/v1/routes').flush({ data: { distanceMeters: 1000, durationSeconds: 60, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          http.expectOne('/api/v1/routes/locations').flush({ data: stops, meta: { returned: stops.length, limit: 500, truncated: false, attribution: [] } });
          await settle();
        }

        it('shows the lowest price of every stop with the own cards and sorts by it', async () => {
          TestBed.inject(CardsStore).set(['smatrics']);
          await showRoute(['route:read', 'price:read']);

          const request = http.expectOne('/api/v1/locations/prices');
          expect(request.request.method).toBe('POST');
          expect(request.request.body).toEqual({ locationIds: ['loc-3', 'loc-4', 'loc-5', 'loc-1'], cards: ['smatrics'] });
          expect(prices()).toEqual([null, null, null, null]);
          request.flush(pricesOf([price('loc-3', 17.7, 13.5), price('loc-4', 9), price('loc-5', null), price('loc-1', 12, 12)]));
          await settle();

          expect(prices()).toEqual([
            'ab 13,50 € (30 kWh · SMATRICS)',
            'ab 9,00 € (30 kWh · ohne Vertrag)',
            null,
            'ab 12,00 € (30 kWh · ohne Vertrag)',
          ]);
          expect(text(root(), '#stops-order-label')).toBe('Reihenfolge:');
          expect(Array.from(root().querySelectorAll<HTMLElement>('#stops-order mat-button-toggle')).map((toggle) => text(toggle, 'button'))).toEqual([
            'Route',
            'günstigste zuerst',
          ]);

          order('price');
          await settle();
          expect(names()).toEqual(['5,0 km · Stopp B', '12,0 km · Parkgarage Stephansplatz', '3,0 km · Stopp A', '7,0 km · Stopp C']);
          order('route');
          await settle();
          expect(names()).toEqual(['3,0 km · Stopp A', '5,0 km · Stopp B', '7,0 km · Stopp C', '12,0 km · Parkgarage Stephansplatz']);
        });

        it('drops prices for an older list and stays without prices when they fail', async () => {
          await showRoute(['route:read', 'price:read']);
          const older = http.expectOne('/api/v1/locations/prices');
          expect(older.request.body.cards).toEqual([]);

          // New filters: a new list, whose prices replace the pending ones.
          TestBed.inject(MapFilterStore).set({ ...NO_FILTERS, minPowerKw: 150 });
          await settle();
          for (const request of http.match((r) => r.urlWithParams.startsWith('/api/v1/locations?'))) {
            request.flush(response());
          }
          http.expectOne('/api/v1/routes/locations').flush({ data: along.slice(0, 2), meta: { returned: 2, limit: 500, truncated: false, attribution: [] } });
          await settle();
          const newer = http.expectOne('/api/v1/locations/prices');
          expect(newer.request.body.locationIds).toEqual(['loc-3', 'loc-4']);
          older.flush(pricesOf([price('loc-3', 1)]));
          await settle();
          expect(prices()).toEqual([null, null]);

          newer.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
          await settle();
          expect(prices()).toEqual([null, null]);
          expect(names().length).toBe(2);
        });

        it('needs price:read and a list with stops', async () => {
          await showRoute(['route:read']);
          expect(root().querySelector('#stops-order')).toBeNull();
          http.expectNone('/api/v1/locations/prices');

          await showRoute(['route:read', 'price:read'], []);
          expect(root().querySelector('#stops-order')).toBeNull();
          http.expectNone('/api/v1/locations/prices');
        });
      });

      describe('charging plan', () => {
        // 20 kWh/100 km with 10 kWh: 2 % per km, 45 km from 100 % down to the 10 % buffer.
        const along = [
          stop('slow', 30_000, { name: 'Langsam', maxPowerKw: 22 }),
          stop('hpc', 40_000, { name: 'Schnell', maxPowerKw: 150 }),
          stop('later', 70_000, { name: 'Später', maxPowerKw: 50 }),
          stop('loc-1', 100_000),
        ];

        async function showLongRoute(stops = along, distanceMeters = 100_000): Promise<void> {
          TestBed.inject(VehicleStore).set({ consumptionKwhPer100Km: 20, batteryKwh: 10 });
          await openCard();
          await clickRoute();
          http.expectOne('/api/v1/routes').flush({ data: { distanceMeters, durationSeconds: 3600, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          http.expectOne('/api/v1/routes/locations').flush({ data: stops, meta: { returned: stops.length, limit: 500, truncated: false, attribution: [] } });
          await settle();
        }

        it('recommends the stops for the car, rings only them and takes them over as route', async () => {
          await showLongRoute();

          expect(text(root(), '#vehicle summary')).toBe('Fahrzeug: 20 kWh/100 km · Akku 10 kWh · Start 100 % · Ankunft 10 %');
          expect(text(root(), '#charging-plan')).toBe('Empfohlen: 2 Ladestopps, Ankunft mit etwa 10 % (Energiebedarf 20 kWh). Empfehlung übernehmen');
          expect(map.handle.highlightLocations).toHaveBeenCalledWith([jasmine.objectContaining({ id: 'hpc' }), jasmine.objectContaining({ id: 'later' })]);
          const recommended = Array.from(root().querySelectorAll<HTMLElement>('#route-stops li.recommended'));
          expect(recommended.map((item) => text(item, '.stop-recommended'))).toEqual([
            'Empfohlen: an mit 20 %, laden auf 80 %',
            'Empfohlen: an mit 20 %, laden auf 70 %',
          ]);

          root().querySelector<HTMLButtonElement>('#apply-plan')!.click();
          await settle();
          const request = http.expectOne('/api/v1/routes');
          expect(request.request.body.waypoints.length).toBe(4);
          expect(text(root(), '#working')).toBe('Route wird berechnet …');
          request.flush({ data: { distanceMeters: 100_000, durationSeconds: 3700, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          http.expectOne('/api/v1/routes/locations').flush({ data: along, meta: { returned: 4, limit: 500, truncated: false, attribution: [] } });
          await settle();
          // The route goes via the recommendation now: nothing left to take over.
          expect(root().querySelector('#apply-plan')).toBeNull();
          expect(text(root(), '#route-summary')).toContain('2 Ladestopps');
        });

        it('takes the car from the form and tells when the route cannot be done', async () => {
          await showLongRoute([stop('far', 80_000), stop('loc-1', 100_000)]);
          expect(text(root(), '#charging-plan')).toBe(
            'Mit diesen Werten reichen die Ladepunkte entlang der Route nicht bis zum Ziel. Prüfe Verbrauch, Akku oder die Filter der Karte.',
          );

          const input = (id: string, value: string) => {
            const field = root().querySelector<HTMLInputElement>(id)!;
            field.value = value;
            field.dispatchEvent(new Event('change'));
          };
          input('#vehicle-battery', '60');
          input('#vehicle-consumption', '18');
          input('#vehicle-start', '80');
          input('#vehicle-arrival', '20');
          await settle();
          expect(TestBed.inject(VehicleStore).vehicle()).toEqual({ consumptionKwhPer100Km: 18, batteryKwh: 60, startSoc: 80, arrivalSoc: 20 });
          expect(text(root(), '#charging-plan')).toBe('Ohne Ladestopp erreichbar: Ankunft mit etwa 50 % (Energiebedarf 18 kWh).');
          expect(text(root(), '#vehicle summary')).toBe('Fahrzeug: 18 kWh/100 km · Akku 60 kWh · Start 80 % · Ankunft 20 %');

          // An empty field means the default.
          input('#vehicle-start', '');
          await settle();
          expect(TestBed.inject(VehicleStore).vehicle().startSoc).toBeNull();
        });

        it('offers no takeover for more stops than a route can have', async () => {
          TestBed.inject(VehicleStore).set({ consumptionKwhPer100Km: 20, batteryKwh: 10 });
          const many = Array.from({ length: 20 }, (_, index) => stop(`s${index}`, (index + 1) * 10_000, { maxPowerKw: 150 }));
          await openCard();
          await clickRoute();
          http.expectOne('/api/v1/routes').flush({ data: { distanceMeters: 200_000, durationSeconds: 7200, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          http.expectOne('/api/v1/routes/locations').flush({ data: many, meta: { returned: 20, limit: 500, truncated: false, attribution: [] } });
          await settle();

          expect(text(root(), '#charging-plan')).toContain('Empfohlen: 6 Ladestopps');
          expect(root().querySelector('#apply-plan')).toBeNull();
        });

        it('recommends a single stop in the singular', async () => {
          await showLongRoute([stop('hpc', 40_000, { maxPowerKw: 150 })], 60_000);
          expect(text(root(), '#charging-plan')).toContain('Empfohlen: 1 Ladestopp,');
        });
      });

      describe('lists and planner', () => {
        const routeAnswer = (distanceMeters = 12_480) => ({
          data: { distanceMeters, durationSeconds: 960, geometry: { type: 'LineString', coordinates: geometry } },
          meta: { attribution: [] },
        });
        const listAnswer = { data: [stop('loc-2', 3000)], meta: { returned: 1, limit: 500, truncated: false, attribution: [] } };

        it('closes the list along the route and shows it again', async () => {
          await openCard();
          await clickRoute();
          http.expectOne('/api/v1/routes').flush(routeAnswer());
          await settle();
          http.expectOne('/api/v1/routes/locations').flush(listAnswer);
          await settle();

          root().querySelector<HTMLButtonElement>('#close-stops')!.click();
          await settle();
          expect(root().querySelector('#route-stops')).toBeNull();
          expect(fixture.nativeElement.classList).not.toContain('has-card');
          root().querySelector<HTMLButtonElement>('#show-stops')!.click();
          await settle();
          expect(root().querySelector('#route-stops')).not.toBeNull();
          expect(root().querySelector('#show-stops')).toBeNull();
        });

        it('plans a route from a location to a point picked on the map and changes its start', async () => {
          await openCard();
          root().querySelector<HTMLButtonElement>('#route-from')!.click();
          await settle();

          expect(root().querySelector('#location-card')).toBeNull();
          expect(text(root(), '#planner-start')).toBe('Parkgarage Stephansplatz');
          expect(text(root(), '#planner-destination')).toBe('noch nicht gewählt');
          expect(text(root(), '#planner-hint')).toBe('Tippe auf die Karte oder einen Ladestandort, um das Ziel festzulegen.');
          expect(root().querySelector<HTMLButtonElement>('#planner-calculate')!.disabled).toBeTrue();

          options().onClick(48.3, 16.5);
          await settle();
          expect(text(root(), '#planner-destination')).toBe('Punkt 48,3000, 16,5000');
          expect(root().querySelector('#planner-hint')).toBeNull();
          // Without waiting for a place, clicks on the map do nothing.
          options().onClick(1, 2);
          await settle();
          expect(text(root(), '#planner-destination')).toBe('Punkt 48,3000, 16,5000');

          root().querySelector<HTMLButtonElement>('#planner-calculate')!.click();
          await settle();
          const request = http.expectOne('/api/v1/routes');
          expect(request.request.body).toEqual({ waypoints: [{ latitude: 48.2085, longitude: 16.3731 }, { latitude: 48.3, longitude: 16.5 }] });
          request.flush(routeAnswer());
          await settle();
          http.expectOne('/api/v1/routes/locations').flush(listAnswer);
          await settle();
          expect(root().querySelector('#route-planner')).toBeNull();
          // The destination is no location: every location of the list can become a stop.
          expect(root().querySelectorAll('#route-stops .add-stop').length).toBe(1);

          // Change the start: a location picked on the map, then back to the own position.
          root().querySelector<HTMLButtonElement>('#route-edit')!.click();
          await settle();
          expect(text(root(), '#planner-start')).toBe('Parkgarage Stephansplatz');
          root().querySelector<HTMLButtonElement>('#pick-start')!.click();
          await settle();
          expect(text(root(), '#planner-hint')).toBe('Tippe auf die Karte oder einen Ladestandort, um den Start festzulegen.');
          options().onSelect('unknown');
          await settle();
          expect(root().querySelector('#location-card')).toBeNull();
          options().onSelect('loc-2');
          await settle();
          expect(root().querySelector('#location-card')).toBeNull();
          expect(text(root(), '#planner-start')).toBe('Ladestandort');
          root().querySelector<HTMLButtonElement>('#planner-own')!.click();
          await settle();
          expect(text(root(), '#planner-start')).toBe('Mein Standort');
          expect(root().querySelector('#planner-own')).toBeNull();

          // The own position is asked for when calculating; a failed route keeps the planner open.
          root().querySelector<HTMLButtonElement>('#planner-calculate')!.click();
          await settle();
          http.expectOne('/api/v1/routes').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
          await settle();
          expect(root().querySelector('#route-planner')).not.toBeNull();
          root().querySelector<HTMLButtonElement>('#planner-cancel')!.click();
          await settle();
          expect(root().querySelector('#route-planner')).toBeNull();
        });

        it('takes start and destination from the search fields of the planner', async () => {
          await openCard();
          root().querySelector<HTMLButtonElement>('#route-from')!.click();
          await settle();
          const pickFrom = (id: string, place: Place) => {
            fixture.debugElement.query(By.css(id)).componentInstance.picked.emit(place);
          };

          pickFrom('#planner-destination-search', { latitude: 48.3, longitude: 14.29, name: 'Linz Hauptplatz', kind: 'location', locationId: 'loc-9' });
          await settle();
          expect(text(root(), '#planner-destination')).toBe('Linz Hauptplatz');
          expect(root().querySelector('#planner-hint')).toBeNull();
          pickFrom('#planner-start-search', { latitude: 48.1, longitude: 16.2, name: 'Mödling', kind: 'location', locationId: 'loc-8' });
          await settle();
          expect(text(root(), '#planner-start')).toBe('Mödling');

          root().querySelector<HTMLButtonElement>('#planner-cancel')!.click();
          await settle();
        });

        it('keeps the own position as start when changing a route from there', async () => {
          await openCard();
          await clickRoute();
          http.expectOne('/api/v1/routes').flush(routeAnswer());
          await settle();
          http.expectOne('/api/v1/routes/locations').flush(listAnswer);
          await settle();

          root().querySelector<HTMLButtonElement>('#route-edit')!.click();
          await settle();
          expect(text(root(), '#planner-start')).toBe('Mein Standort');
          expect(text(root(), '#planner-destination')).toBe('Parkgarage Stephansplatz');
          root().querySelector<HTMLButtonElement>('#pick-destination')!.click();
          await settle();
          expect(root().querySelector('#pick-destination')!.classList).toContain('picking');

          // A refused position stops the calculation.
          browser.currentPosition.and.rejectWith({ code: 1 });
          root().querySelector<HTMLButtonElement>('#planner-calculate')!.click();
          await settle();
          expect(text(root(), '#map-notice')).toBe('Die Standortfreigabe wurde abgelehnt. Du kannst sie in den Website-Einstellungen deines Browsers erlauben.');
          browser.secureContext.and.returnValue(false);
          root().querySelector<HTMLButtonElement>('#planner-calculate')!.click();
          await settle();
          expect(text(root(), '#map-notice')).toBe('Der Standort ist nur über eine sichere Verbindung (HTTPS) verfügbar.');
          http.expectNone('/api/v1/routes');
        });
      });

      it('explains stops that cannot be loaded, an empty corridor and drops the answer for a removed route', async () => {
        const routeOnce = async () => {
          if (!root().querySelector('#location-card')) {
            options().onSelect('loc-1');
            await settle();
          }
          await clickRoute();
          http.expectOne('/api/v1/routes').flush({ data: { distanceMeters: 1000, durationSeconds: 60, geometry: { type: 'LineString', coordinates: geometry } }, meta: { attribution: [] } });
          await settle();
          return http.expectOne('/api/v1/routes/locations');
        };
        await openCard();

        (await routeOnce()).flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
        await settle();
        expect(text(root(), '#route-stops .error')).toBe('Die Ladepunkte entlang der Route konnten nicht geladen werden.');

        (await routeOnce()).flush({ data: [], meta: { returned: 0, limit: 200, truncated: false, attribution: [] } });
        await settle();
        expect(text(root(), '#stops-hint')).toBe('0 Standorte bis 1 km neben der Route, mit den Filtern der Karte.');
        expect(text(root(), '#route-stops li')).toBe('Keine Ladepunkte entlang der Route.');

        // Removed while loading: neither the answer nor an error shows up.
        const late = await routeOnce();
        root().querySelector<HTMLButtonElement>('#route-clear')!.click();
        await settle();
        late.flush({ data: [stop('loc-1', 100)], meta: { returned: 1, limit: 200, truncated: false, attribution: [] } });
        await settle();
        const failed = await routeOnce();
        root().querySelector<HTMLButtonElement>('#route-clear')!.click();
        await settle();
        failed.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
        await settle();
        expect(root().querySelector('#route-stops')).toBeNull();
      });

      for (const [status, notice] of [
        [503, 'Die Routenberechnung ist gerade nicht verfügbar. Bitte versuche es später.'],
        [422, 'Zu diesem Ladestandort wurde keine Straßenverbindung gefunden.'],
        [500, 'Die Route konnte nicht berechnet werden. Bitte versuche es erneut.'],
      ] as const) {
        it(`explains an answer ${status} of the API`, async () => {
          await openCard();
          await clickRoute();

          http.expectOne('/api/v1/routes').flush({ title: 'Error' }, { status, statusText: 'Error' });
          await settle();

          expect(text(root(), '#map-notice')).toBe(notice);
          expect(map.handle.showRoute).not.toHaveBeenCalled();
          expect(routeButton()!.disabled).toBeFalse();
        });
      }

      it('explains a refused or failed position and sends nothing', async () => {
        await openCard();

        browser.currentPosition.and.rejectWith({ code: 1 });
        await clickRoute();
        expect(text(root(), '#map-notice')).toBe('Die Standortfreigabe wurde abgelehnt. Du kannst sie in den Website-Einstellungen deines Browsers erlauben.');

        browser.currentPosition.and.rejectWith({ code: 3 });
        await clickRoute();
        expect(text(root(), '#map-notice')).toBe('Dein Standort konnte nicht ermittelt werden. Bitte versuche es erneut.');

        browser.secureContext.and.returnValue(false);
        browser.currentPosition.calls.reset();
        await clickRoute();
        expect(text(root(), '#map-notice')).toBe('Der Standort ist nur über eine sichere Verbindung (HTTPS) verfügbar.');
        expect(browser.currentPosition).not.toHaveBeenCalled();
        http.expectNone('/api/v1/routes');
      });
    });

    it('shows the selected location and closes the card', async () => {
      await render();
      await moveTo(vienna, response());

      options().onSelect('loc-1');
      await settle();

      expect(text(root(), '#location-card h2')).toBe('Parkgarage Stephansplatz');
      expect(text(root(), '#location-card .address')).toBe('Stephansplatz 1, 1010 Wien');
      expect(text(root(), '#location-card .operator')).toBe('Wien Energie GmbH');
      expect(text(root(), '#location-card .availability')).toBe('2 von 4 Ladepunkten frei · bis 22 kW');
      expect(text(root(), '#location-card .status-age')).toMatch(/^Status von 14\.10\.2026 \d\d:15$/);

      options().onSelect('loc-2');
      await settle();
      expect(text(root(), '#location-card h2')).toBe('Ladestandort');
      expect(root().querySelector('#location-card .operator')).toBeNull();
      // Partly out of order: the card says so.
      expect(text(root(), '#location-card .availability')).toBe('0 von 4 Ladepunkten frei · 3 außer Betrieb');
      expect(text(root(), '#location-card .status-age')).toBe('Kein aktueller Status');

      root().querySelector<HTMLButtonElement>('#close-card')!.click();
      await settle();
      expect(root().querySelector('#location-card')).toBeNull();

      options().onSelect('unknown');
      await settle();
      expect(root().querySelector('#location-card')).toBeNull();
    });

    it('shows the details of the location in the card, without leaving the map', async () => {
      const detail: LocationDetailResponse = {
        data: {
          location: locationSummary(),
          evses: [
            {
              id: 'e-1',
              evseId: 'AT*TST*E1*1',
              hasEvseId: true,
              status: 'AVAILABLE',
              statusSince: null,
              statusObservedAt: null,
              statusSource: null,
              maxPowerKw: 22,
              powerType: 'AC_3_PHASE',
              connectors: [{ standard: 'IEC_62196_T2', format: 'SOCKET', powerType: 'AC_3_PHASE', maxPowerKw: 22 }],
            },
          ],
        },
        meta: { attribution: [] },
      };
      evses.location.and.resolveTo(detail);
      await render();
      await moveTo(vienna, response());

      options().onSelect('loc-1');
      await settle();
      await settle();

      expect(evses.location).toHaveBeenCalledOnceWith('loc-1');
      expect(text(root(), '#location-card app-location-details h3')).toBe('AT*TST*E1*1');
      expect(text(root(), '#location-card #history-hint')).toBe('Statusverlauf und Auslastung siehst du nach der Anmeldung.');
      expect(root().querySelector('#location-card app-location-details #location-name')).toBeNull();
      expect(map.handle.flyTo).not.toHaveBeenCalled();
      expect(map.handle.destroy).not.toHaveBeenCalled();
    });

    it('shows the card as bottom sheet on small screens', async () => {
      viewport.mobile.set(true);
      await render();
      await moveTo(vienna, response());
      options().onSelect('loc-1');
      await settle();

      expect(root().querySelector('#map-overlay #location-card')).toBeNull();
      expect(root().querySelector(':scope > #location-card')).not.toBeNull();
      expect(root().classList).toContain('has-card');
    });

    it('folds the map actions on small screens and shows reach and own position as symbols', async () => {
      viewport.mobile.set(true);
      auth.hasPermission.and.callFake((permission) => permission === 'route:read');
      await render();
      await moveTo(vienna, response());

      // The legend heads the overlay column, so messages stack below it.
      expect(root().querySelector('#map-overlay > .legend-box > #legend')).not.toBeNull();
      expect(root().querySelector('#map-legend-bar #legend')).toBeNull();
      expect(text(root(), '#locate')).toBe('');
      expect(root().querySelector('#locate')!.getAttribute('aria-label')).toBe('Mein Standort');
      expect(text(root(), '#reach')).toBe('');
      const actions = () => root().querySelector<HTMLElement>('#map-actions .home-actions')!;
      const toggle = () => root().querySelector<HTMLButtonElement>('#actions-toggle')!;
      expect(actions().hidden).toBeTrue();
      // The stylesheet must not show it anyway.
      expect(getComputedStyle(actions()).display).toBe('none');
      expect(toggle().getAttribute('aria-label')).toBe('Weitere Kartenfunktionen');

      toggle().click();
      await settle();
      expect(actions().hidden).toBeFalse();
      expect(toggle().getAttribute('aria-expanded')).toBe('true');
      expect(toggle().getAttribute('aria-label')).toBe('Kartenfunktionen schließen');
      toggle().click();
      await settle();
      expect(actions().hidden).toBeTrue();
    });

    it('marks every status of the legend and the card with a symbol', async () => {
      await render();
      await moveTo(vienna, response());
      options().onSelect('loc-1');
      await settle();

      expect(Array.from(root().querySelectorAll('#legend li')).map((item) => item.textContent!.trim())).toEqual([
        'frei bis 22 kW',
        'frei 23–149 kW',
        'frei ab 150 kW',
        'belegt',
        'außer Betrieb',
        'Status unbekannt',
      ]);
      expect(root().querySelectorAll('#legend app-icon').length).toBe(6);
      expect(root().querySelector<HTMLElement>('#location-card .status-dot')!.style.background).toBe('var(--cg-status-available)');
    });

    it('flies to a search hit and opens its card once the location is loaded', async () => {
      await render();
      const focus = TestBed.inject(MapFocus);

      focus.focus({ locationId: 'loc-2', latitude: 48.2, longitude: 16.4 });
      await settle();
      expect(map.handle.flyTo).toHaveBeenCalledOnceWith(48.2, 16.4, FOCUS_ZOOM);
      expect(focus.target()).toBeNull();
      expect(root().querySelector('#location-card')).toBeNull();

      await moveTo(vienna, response());
      expect(text(root(), '#location-card h2')).toBe('Ladestandort');

      // A hit that is already loaded opens at once; later loads keep the selection.
      focus.focus({ locationId: 'loc-1', latitude: 48.2085, longitude: 16.3731 });
      await settle();
      expect(text(root(), '#location-card h2')).toBe('Parkgarage Stephansplatz');
      root().querySelector<HTMLButtonElement>('#close-card')!.click();
      await moveTo({ west: 15, south: 47, east: 17, north: 49 }, response(), zoomedOut);
      expect(root().querySelector('#location-card')).toBeNull();
    });

    it('hands locations that arrived before the map to the engine once it exists', async () => {
      let resolve!: (handle: typeof map.handle) => void;
      map.engine.create.and.returnValue(new Promise((r) => (resolve = r)));
      await render();

      await moveTo(vienna, response());
      expect(map.handle.showLocations).not.toHaveBeenCalled();

      resolve(map.handle);
      await settle();
      expect(map.handle.showLocations).toHaveBeenCalledOnceWith([locationSummary(), ring]);
    });

    it('ignores answers that arrive after the page was destroyed', async () => {
      await render();
      options().onViewChange(vienna, view);
      const request = http.expectOne(url);

      fixture.destroy();
      request.flush(response());
      await flush();

      // Only the empty list handed over right after the map was created.
      expect(map.handle.showLocations).toHaveBeenCalledOnceWith([]);
    });
  });

  it('removes the map when the page is destroyed', async () => {
    await render();

    fixture.destroy();

    expect(map.handle.destroy).toHaveBeenCalledTimes(1);
  });

  it('does not create the map when the page is gone before the configuration arrives', async () => {
    fixture = TestBed.createComponent(HomePage);
    await fixture.whenStable();
    const request = http.expectOne('/api/v1/map/config');

    fixture.destroy();
    request.flush(MAP_CONFIG);
    await flush();

    expect(map.engine.create).not.toHaveBeenCalled();
  });

  it('removes a map that was still being created when the page was destroyed', async () => {
    let resolve!: (handle: typeof map.handle) => void;
    map.engine.create.and.returnValue(new Promise((r) => (resolve = r)));
    await render();

    fixture.destroy();
    resolve(map.handle);
    await flush();

    expect(map.handle.destroy).toHaveBeenCalledTimes(1);
  });
});

describe('HomePage (map) in light and dark mode', () => {
  let map: ReturnType<typeof fakeMapEngine>;
  let theme: ThemeService;

  beforeEach(() => {
    map = fakeMapEngine();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: fakeAuth() },
        { provide: Viewport, useValue: fakeViewport() },
        { provide: MapEngine, useValue: map.engine },
        { provide: BrowserCapabilities, useValue: fakeBrowser() },
      ],
    });
    theme = TestBed.inject(ThemeService);
    theme.set('light');
  });

  afterEach(() => theme.set('system'));

  it('creates the map in the current mode and switches style and marker colours with the theme', async () => {
    const fixture = TestBed.createComponent(HomePage);
    await fixture.whenStable();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/map/config').flush(MAP_CONFIG);
    await flush();
    await fixture.whenStable();
    const created = map.engine.create.calls.mostRecent().args[0].theme;
    expect(created.styleUrl).toBe('https://tiles.openfreemap.org/styles/liberty');
    expect(created.status.AVAILABLE).toBe('rgb(27, 127, 58)');
    expect(created.onStatus).toBe('rgb(255, 255, 255)');

    theme.set('dark');
    await fixture.whenStable();

    const switched = map.handle.setTheme.calls.mostRecent().args[0];
    expect(switched.styleUrl).toBe('https://tiles.openfreemap.org/styles/dark');
    expect(switched.status.AVAILABLE).toBe('rgb(109, 213, 140)');
    expect(switched.status.OUT_OF_ORDER).toBe('rgb(255, 180, 171)');
    expect(switched.onStatus).toBe('rgb(15, 20, 22)');
    fixture.destroy();
  });
});

describe('RoutesService', () => {
  it('posts the waypoints', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const route = TestBed.inject(RoutesService).route([{ latitude: 1, longitude: 2 }]);
    const request = http.expectOne('/api/v1/routes');
    expect([request.request.method, request.request.body]).toEqual(['POST', { waypoints: [{ latitude: 1, longitude: 2 }] }]);
    request.flush({ data: { distanceMeters: 1, durationSeconds: 1, geometry: { type: 'LineString', coordinates: [] } }, meta: { attribution: [] } });
    expect((await route).data.distanceMeters).toBe(1);
    http.verify();
  });

  it('asks for the locations along a route with the filters of the map', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const geometry = { type: 'LineString' as const, coordinates: [[16.3, 48.2], [16.4, 48.2]] as [number, number][] };

    const along = TestBed.inject(RoutesService).along(geometry, { ...NO_FILTERS, minPowerKw: 50, availableOnly: true });
    const request = http.expectOne('/api/v1/routes/locations');
    expect(request.request.body).toEqual({ geometry, corridorMeters: 1000, limit: 500, ...NO_FILTERS, minPowerKw: 50, availableOnly: true });
    request.flush({ data: [], meta: { returned: 0, limit: 200, truncated: false, attribution: [] } });
    expect((await along).meta.returned).toBe(0);
    http.verify();
  });

  it('puts a new stop in the order of the current route', () => {
    const at = (id: string, positionOnRouteMeters: number): RouteLocation => ({ ...locationSummary({ id }), distanceFromRouteMeters: 0, positionOnRouteMeters });
    const along = [at('a', 1000), at('c', 3000), at('d', 4000)];

    expect(withStop([at('c', 3000)], at('a', 1000), along).map((s) => s.id)).toEqual(['a', 'c']);
    expect(withStop([at('a', 1000)], at('d', 4000), along).map((s) => s.id)).toEqual(['a', 'd']);
    // A planned stop the filters hide keeps the position it was chosen at.
    expect(withStop([at('b', 2000)], at('c', 3000), along).map((s) => s.id)).toEqual(['b', 'c']);
    expect(withStop([at('b', 5000)], at('c', 3000), along).map((s) => s.id)).toEqual(['c', 'b']);
  });

  it('asks for the locations inside an area with the filters of the map', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const geometry = { type: 'MultiPolygon' as const, coordinates: [] as [number, number][][][] };

    const within = TestBed.inject(RoutesService).within(geometry, { latitude: 48.2, longitude: 16.3 }, { ...NO_FILTERS, minPowerKw: 50 });
    const request = http.expectOne('/api/v1/locations/within');
    expect(request.request.body).toEqual({ geometry, center: { latitude: 48.2, longitude: 16.3 }, limit: 500, ...NO_FILTERS, minPowerKw: 50 });
    request.flush({ data: [], meta: { returned: 0, total: 3, limit: 500, truncated: false, attribution: [] } });
    expect((await within).meta.total).toBe(3);

    // Limit 0 only counts.
    const count = TestBed.inject(RoutesService).within(geometry, { latitude: 48.2, longitude: 16.3 }, NO_FILTERS, 0);
    const counting = http.expectOne('/api/v1/locations/within');
    expect(counting.request.body.limit).toBe(0);
    counting.flush({ data: [], meta: { returned: 0, total: 7, limit: 0, truncated: true, attribution: [] } });
    expect((await count).meta.total).toBe(7);
    http.verify();
  });

  it('finds the smallest area around a location, minding holes', () => {
    const ring = (west: number, south: number, east: number, north: number): [number, number][] => [
      [west, south],
      [east, south],
      [east, north],
      [west, north],
      [west, south],
    ];
    const areas: ReachAreas = {
      type: 'FeatureCollection',
      features: [
        { type: 'Feature', properties: { kilometres: 50 }, geometry: { type: 'MultiPolygon', coordinates: [[ring(0, 0, 10, 10)], [ring(20, 20, 30, 30)]] } },
        { type: 'Feature', properties: { kilometres: 20 }, geometry: { type: 'MultiPolygon', coordinates: [[ring(2, 2, 8, 8), ring(4, 4, 6, 6)]] } },
      ],
    };
    expect(smallestArea({ latitude: 3, longitude: 3 }, areas)).toBe(20);
    expect(smallestArea({ latitude: 5, longitude: 5 }, areas)).toBe(50);
    expect(smallestArea({ latitude: 25, longitude: 25 }, areas)).toBe(50);
    expect(smallestArea({ latitude: 15, longitude: 15 }, areas)).toBeNull();
    expect(inArea({ latitude: 5, longitude: 5 }, areas.features[1])).toBeFalse();
  });

  it('asks for the areas reachable within the driving times', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const reach = TestBed.inject(RoutesService).reach({ latitude: 48.2, longitude: 16.3 }, [15, 30]);
    const request = http.expectOne('/api/v1/routes/reach');
    expect([request.request.method, request.request.body]).toEqual(['POST', { position: { latitude: 48.2, longitude: 16.3 }, minutes: [15, 30] }]);
    request.flush({ data: { type: 'FeatureCollection', features: [] }, meta: { attribution: [] } });
    expect((await reach).data.features).toEqual([]);
    http.verify();
  });

  it('asks for the prices of locations with the own cards', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const prices = TestBed.inject(RoutesService).prices(['loc-1'], ['smatrics']);
    const request = http.expectOne('/api/v1/locations/prices');
    expect([request.request.method, request.request.body]).toEqual(['POST', { locationIds: ['loc-1'], cards: ['smatrics'] }]);
    request.flush({ data: [], meta: { session: { kwh: 30, minutes: 40 }, attribution: [] } });
    expect((await prices).meta.session.kwh).toBe(30);
    http.verify();
  });

  it('takes the lower of ad-hoc price and card and sorts stops by it', () => {
    const card = { emp: 'e', empName: 'Karte', tariffId: 't', tariff: 'T', currency: 'EUR', evseId: 'E' };
    const adHoc = { currency: 'EUR', evseId: 'E' };
    expect(cheapest({ locationId: 'a', adHoc: { ...adHoc, cost: 10 }, cheapestCard: { ...card, cost: 8 } })).toEqual({ cost: 8, currency: 'EUR', label: 'Karte' });
    expect(cheapest({ locationId: 'a', adHoc: { ...adHoc, cost: 8 }, cheapestCard: { ...card, cost: 8 } })).toEqual({ cost: 8, currency: 'EUR', label: 'ohne Vertrag' });
    expect(cheapest({ locationId: 'a', adHoc: null, cheapestCard: { ...card, cost: 5 } })!.label).toBe('Karte');
    expect(cheapest({ locationId: 'a', adHoc: null, cheapestCard: null })).toBeNull();

    const at = (id: string): RouteLocation => ({ ...locationSummary({ id }), distanceFromRouteMeters: 0, positionOnRouteMeters: 0 });
    const costs = new Map([
      ['b', { cost: 3, currency: 'EUR', label: 'x' }],
      ['c', { cost: 3, currency: 'EUR', label: 'x' }],
      ['d', { cost: 1, currency: 'EUR', label: 'x' }],
    ]);
    expect(byPrice([at('a'), at('b'), at('c'), at('d'), at('e')], costs).map((s) => s.id)).toEqual(['d', 'b', 'c', 'a', 'e']);
  });

  it('counts the charging stops in German', () => {
    expect(stopsSummary(0)).toBe('');
    expect(stopsSummary(1)).toBe(' · 1 Ladestopp');
    expect(stopsSummary(3)).toBe(' · 3 Ladestopps');
  });

  it('tells the kilometre of a stop in German', () => {
    expect(kilometres(1840)).toBe('1,8 km');
    expect(kilometres(152_000)).toBe('152,0 km');
  });

  it('summarizes length and driving time in German', () => {
    expect(routeSummary(12_480, 960)).toBe('12 km · 16 min');
    expect(routeSummary(8_450, 10)).toBe('8,5 km · 1 min');
    expect(routeSummary(152_000, 5_460)).toBe('152 km · 1 h 31 min');
    expect(routeSummary(98_000, 3_720)).toBe('98 km · 1 h 02 min');
  });
});

describe('MapViewStore', () => {
  const view: MapView = { latitude: 48.2, longitude: 16.4, zoom: 12 };

  beforeEach(() => {
    localStorage.removeItem(LAST_VIEW_KEY);
    localStorage.removeItem(HOME_VIEW_KEY);
  });

  afterEach(() => {
    localStorage.removeItem(LAST_VIEW_KEY);
    localStorage.removeItem(HOME_VIEW_KEY);
  });

  it('keeps the last view and the start position in the browser', () => {
    const store = TestBed.inject(MapViewStore);
    expect(store.last()).toBeNull();
    expect(store.home()).toBeNull();

    store.saveLast(view);
    store.setHome({ ...view, zoom: 9 });
    expect(store.last()).toEqual(view);
    expect(JSON.parse(localStorage.getItem(HOME_VIEW_KEY)!)).toEqual({ ...view, zoom: 9 });

    store.resetHome();
    expect(store.home()).toBeNull();
    expect(localStorage.getItem(HOME_VIEW_KEY)).toBeNull();
  });

  it('ignores unreadable or impossible views', () => {
    const store = TestBed.inject(MapViewStore);
    for (const stored of ['{', '42', '{"latitude":91,"longitude":16,"zoom":5}', '{"latitude":48,"longitude":"16","zoom":5}', '{"latitude":48,"longitude":16,"zoom":30}']) {
      localStorage.setItem(LAST_VIEW_KEY, stored);
      expect(store.last()).withContext(stored).toBeNull();
    }
  });

  it('works without storage', () => {
    spyOn(Storage.prototype, 'getItem').and.throwError('blocked');
    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
    const store = TestBed.inject(MapViewStore);

    expect(store.home()).toBeNull();
    store.setHome(view);
    store.saveLast(view);
    expect(store.home()).toEqual(view);
    expect(store.last()).toBeNull();
  });
});

describe('markerOf', () => {
  it('colours free locations by the power that is free and the others by availability', () => {
    expect(markerOf({ availability: 'AVAILABLE', maxAvailablePowerKw: null })).toBe('AVAILABLE');
    expect(markerOf({ availability: 'AVAILABLE', maxAvailablePowerKw: 22 })).toBe('AVAILABLE');
    expect(markerOf({ availability: 'AVAILABLE', maxAvailablePowerKw: 22.1 })).toBe('AVAILABLE_FAST');
    expect(markerOf({ availability: 'AVAILABLE', maxAvailablePowerKw: 149 })).toBe('AVAILABLE_FAST');
    expect(markerOf({ availability: 'AVAILABLE', maxAvailablePowerKw: 150 })).toBe('AVAILABLE_HPC');
    expect(markerOf({ availability: 'OCCUPIED', maxAvailablePowerKw: null })).toBe('OCCUPIED');
    expect(MARKERS.AVAILABLE_HPC.token).toBe('--cg-power-hpc');
  });
});
