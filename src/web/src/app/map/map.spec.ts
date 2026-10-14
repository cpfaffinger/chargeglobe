import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HomePage } from '../pages/home.page';
import { MAP_CONFIG, fakeBrowser, fakeMapEngine, flush, locationSummary, text } from '../testing';
import { BrowserCapabilities } from './browser-capabilities';
import { MapConfigService } from './map-config.service';
import { GeolocateEvent, MapEngine, MapViewOptions } from './map-engine';
import { Bbox, LocationsResponse, LocationsService } from './locations.service';

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

  const root = () => fixture.nativeElement as HTMLElement;
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
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MapEngine, useValue: map.engine },
        { provide: BrowserCapabilities, useValue: browser },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('creates the map in its container with style, attribution, center and zoom of the configuration', async () => {
    await render();

    expect(map.engine.create).toHaveBeenCalledTimes(1);
    expect(options()).toEqual(
      jasmine.objectContaining({
        container: root().querySelector<HTMLElement>('#map')!,
        styleUrl: 'https://tiles.openfreemap.org/styles/liberty',
        attribution: '© OpenStreetMap contributors, OpenMapTiles',
        center: { latitude: 48.2, longitude: 15.6 },
        zoom: 7,
      }),
    );
    expect(locateButton().disabled).toBeFalse();
    expect(text(root(), '#locate')).toBe('Mein Standort');
    expect(root().querySelector('#map-notice')).toBeNull();
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
      options().onViewChange(box);
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
        'frei',
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

    it('drops answers and failures of older requests', async () => {
      await render();

      options().onViewChange({ west: 15, south: 47, east: 17, north: 49 });
      options().onViewChange(vienna);
      const older = http.expectOne(zoomedOut);
      http.expectOne(url).flush(response());
      await settle();
      older.flush({ ...response(), data: [] });
      await settle();

      expect(map.handle.showLocations).toHaveBeenCalledTimes(2);
      expect(map.handle.showLocations.calls.mostRecent().args[0].length).toBe(2);

      options().onViewChange({ west: 15, south: 47, east: 17, north: 49 });
      options().onViewChange(vienna);
      http.expectOne(zoomedOut).flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
      http.expectOne(url).flush(response());
      await settle();
      expect(root().querySelector('#map-notice')).toBeNull();
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
      options().onViewChange(vienna);
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
