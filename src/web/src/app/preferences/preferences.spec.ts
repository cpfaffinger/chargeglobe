import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { FILTERS_STORAGE_KEY, MapFilterStore, NO_FILTERS } from '../map/map-filters';
import { DEFAULT_MAP_OPTIONS, MapOptionsStore, OPTIONS_STORAGE_KEY } from '../map/map-options';
import { HOME_VIEW_KEY, MapViewStore } from '../map/map-view.store';
import { ME, fakeAuth, flush } from '../testing';
import { CardsStore } from './cards.store';
import { DEFAULT_VEHICLE, VEHICLE_STORAGE_KEY, VehicleStore } from '../map/charging-plan';
import { PREFERENCES_DEBOUNCE_MS, PreferencesDocument, PreferencesService } from './preferences.service';

const URL = '/api/v1/me/preferences';
const HOME = { latitude: 48.3, longitude: 14.29, zoom: 12 };

describe('PreferencesService', () => {
  let http: HttpTestingController;
  let auth: ReturnType<typeof fakeAuth>;

  function setUp(query: Record<string, string> = {}): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: query } } },
      ],
    });
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(PreferencesService);
    TestBed.tick();
  }

  async function signIn(): Promise<void> {
    auth.me.set(ME);
    TestBed.tick();
    await flush();
  }

  /** Lets the pause before writing pass. */
  async function waitForWrite(): Promise<void> {
    TestBed.tick();
    await new Promise((resolve) => setTimeout(resolve, PREFERENCES_DEBOUNCE_MS + 20));
  }

  const respond = (data: PreferencesDocument | null, updatedAt: string | null = '2026-10-14T08:00:00Z') =>
    http.expectOne((request) => request.method === 'GET' && request.url === URL).flush({ data: data ?? { home: null, filters: null, mapOptions: null }, meta: { updatedAt } });

  beforeEach(() => {
    for (const key of [HOME_VIEW_KEY, FILTERS_STORAGE_KEY, OPTIONS_STORAGE_KEY, VEHICLE_STORAGE_KEY]) {
      localStorage.removeItem(key);
    }
    auth = fakeAuth();
  });

  afterEach(() => {
    http.verify();
    for (const key of [HOME_VIEW_KEY, FILTERS_STORAGE_KEY, OPTIONS_STORAGE_KEY, VEHICLE_STORAGE_KEY]) {
      localStorage.removeItem(key);
    }
  });

  it('leaves anonymous visitors in the browser', async () => {
    setUp();
    TestBed.inject(MapViewStore).setHome(HOME);
    await waitForWrite();
    http.expectNone(URL);
  });

  it('uploads the values of the browser at the first login of the account', async () => {
    localStorage.setItem(HOME_VIEW_KEY, JSON.stringify(HOME));
    localStorage.setItem(OPTIONS_STORAGE_KEY, JSON.stringify({ clusterRadius: 80, clusterMaxZoom: 15 }));
    localStorage.setItem(VEHICLE_STORAGE_KEY, JSON.stringify({ consumptionKwhPer100Km: 21, batteryKwh: 64 }));
    setUp();
    await signIn();
    respond(null, null);
    await flush();

    const put = http.expectOne((request) => request.method === 'PUT' && request.url === URL);
    expect(put.request.body).toEqual({
      home: HOME,
      filters: NO_FILTERS,
      mapOptions: { clusterRadius: 80, clusterMaxZoom: 15 },
      cards: [],
      vehicle: { consumptionKwhPer100Km: 21, batteryKwh: 64, startSoc: null, arrivalSoc: null },
    });
    put.flush({ data: put.request.body, meta: { updatedAt: '2026-10-14T08:00:00Z' } });
    await flush();

    // Nothing changed since: nothing to write.
    await waitForWrite();
  });

  it('takes the values of the account, writes later changes after a pause and stops after the logout', async () => {
    setUp();
    await signIn();
    respond({ home: HOME, filters: { connector: ['CHADEMO'], minPowerKw: 50, maxPowerKw: null, availableOnly: true }, mapOptions: { clusterRadius: 0, clusterMaxZoom: 18 } });
    await flush();

    expect(TestBed.inject(MapViewStore).home()).toEqual(HOME);
    expect(TestBed.inject(MapFilterStore).filters()).toEqual({ ...NO_FILTERS, connector: ['CHADEMO'], minPowerKw: 50, availableOnly: true });
    expect(TestBed.inject(MapOptionsStore).options()).toEqual({ clusterRadius: 0, clusterMaxZoom: 18 });
    // Without a car in the account the defaults apply.
    expect(TestBed.inject(VehicleStore).vehicle()).toEqual(DEFAULT_VEHICLE);
    // Taking them over is no change of the user.
    await waitForWrite();

    TestBed.inject(MapViewStore).resetHome();
    TestBed.inject(MapOptionsStore).reset();
    await waitForWrite();
    const put = http.expectOne((request) => request.method === 'PUT');
    expect(put.request.body).toEqual(jasmine.objectContaining({ home: null, mapOptions: DEFAULT_MAP_OPTIONS }));
    put.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await flush();

    auth.me.set(null);
    TestBed.tick();
    TestBed.inject(MapViewStore).setHome(HOME);
    await waitForWrite();
  });

  it('takes the car of the account and writes a changed one', async () => {
    setUp();
    await signIn();
    respond({ home: null, filters: null, mapOptions: null, vehicle: { consumptionKwhPer100Km: 16, batteryKwh: 82, startSoc: 90, arrivalSoc: 20 } });
    await flush();
    const vehicles = TestBed.inject(VehicleStore);
    expect(vehicles.vehicle()).toEqual({ consumptionKwhPer100Km: 16, batteryKwh: 82, startSoc: 90, arrivalSoc: 20 });

    vehicles.set({ arrivalSoc: null });
    await waitForWrite();
    const put = http.expectOne((request) => request.method === 'PUT');
    expect(put.request.body.vehicle).toEqual({ consumptionKwhPer100Km: 16, batteryKwh: 82, startSoc: 90, arrivalSoc: null });
    put.flush({ data: put.request.body, meta: { updatedAt: '2026-10-16T08:00:00Z' } });
    await flush();
  });

  it('takes the own cards of the account, writes a changed choice and forgets them after the logout', async () => {
    setUp();
    await signIn();
    respond({ home: null, filters: null, mapOptions: null, cards: ['smatrics'] });
    await flush();
    const cards = TestBed.inject(CardsStore);
    expect(cards.cards()).toEqual(['smatrics']);

    cards.toggle('oeamtc', true);
    cards.toggle('smatrics', false);
    await waitForWrite();
    const put = http.expectOne((request) => request.method === 'PUT');
    expect(put.request.body.cards).toEqual(['oeamtc']);
    put.flush({ data: put.request.body, meta: { updatedAt: '2026-10-15T08:00:00Z' } });
    await flush();

    auth.me.set(null);
    TestBed.tick();
    expect(cards.cards()).toEqual([]);
  });

  it('applies an account without values as defaults and keeps the filters of a shared link', async () => {
    localStorage.setItem(HOME_VIEW_KEY, JSON.stringify(HOME));
    setUp({ roaming: 'true' });
    await signIn();
    respond({ home: null, filters: { greenEnergy: true }, mapOptions: null });
    await flush();

    expect(TestBed.inject(MapViewStore).home()).toBeNull();
    expect(TestBed.inject(MapFilterStore).filters()).toEqual({ ...NO_FILTERS, roaming: true });
    expect(TestBed.inject(MapOptionsStore).options()).toEqual(DEFAULT_MAP_OPTIONS);

    // The link's filters hold for this visit; they are not written to the account.
    await waitForWrite();
  });

  it('drops the answer for a login that has ended and keeps the browser values when loading fails', async () => {
    setUp();
    await signIn();
    auth.me.set(null);
    TestBed.tick();
    respond({ home: HOME, filters: null, mapOptions: null });
    await flush();
    expect(TestBed.inject(MapViewStore).home()).toBeNull();

    await signIn();
    http.expectOne(URL).flush({ title: 'Error' }, { status: 502, statusText: 'Error' });
    await flush();
    TestBed.inject(MapViewStore).setHome(HOME);
    await waitForWrite();
    expect(TestBed.inject(MapViewStore).home()).toEqual(HOME);
  });
});
