import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HomePage } from '../pages/home.page';
import { MAP_CONFIG, fakeBrowser, fakeMapEngine, flush, text } from '../testing';
import { BrowserCapabilities } from './browser-capabilities';
import { MapConfigService } from './map-config.service';
import { GeolocateEvent, MapEngine, MapViewOptions } from './map-engine';

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
