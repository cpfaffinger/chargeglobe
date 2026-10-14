import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
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
import { Bbox, LocationsResponse, LocationsService } from './locations.service';
import { HOME_VIEW_KEY, LAST_VIEW_KEY, MapView, MapViewStore } from './map-view.store';
import { FILTERS_STORAGE_KEY, MapFilterStore, NO_FILTERS } from './map-filters';
import { MapLink } from './map-link';

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

      expect(text(root(), '#truncated')).toContain('nur die nächsten 2000 Standorte');
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
      expect(text(root(), '#location-card .availability')).toBe('0 von 4 Ladepunkten frei');
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
