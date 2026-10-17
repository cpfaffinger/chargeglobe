import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../auth/auth.service';
import { FavoritesService } from '../favorites/favorites.service';
import { LocationDetailPage } from '../pages/location-detail.page';
import { ME, fakeAuth, flush, locationSummary, text } from '../testing';
import { CardsStore } from '../preferences/cards.store';
import { LocationDetails } from './location-details';
import { EmpPrice, EvsePricesResponse, PriceView, comparison, duration, money, priceParts, sessionText } from './prices';
import {
  EvseDetail,
  EvsesService,
  LocationDetailResponse,
  Occupancy,
  OccupancyDay,
  OccupancyResponse,
  StatusHistoryResponse,
} from './evses.service';
import { combine, EVSE_STATUS, faultSource, heatColor, sourceName, statusOf, timeline } from './status';

const DAY = 24 * 60 * 60 * 1000;
const ATTRIBUTION = { source: 'econtrol', text: 'Datenquelle: E-Control', license: 'Terms' };

/** Seven days without readings, with the given cells set: [weekday, hour, share, observations]. */
function days(...cells: [number, number, number | null, number][]): OccupancyDay[] {
  const names = ['MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY', 'SUNDAY'];
  const result = names.map((day, index) => ({
    weekday: index + 1,
    day,
    chargingShare: Array<number | null>(24).fill(null),
    observations: Array<number>(24).fill(0),
  }));
  for (const [weekday, hour, share, count] of cells) {
    result[weekday - 1].chargingShare[hour] = share;
    result[weekday - 1].observations[hour] = count;
  }
  return result;
}

function occupancy(
  evseId: string,
  utilization: number | null,
  observations: number,
  matrix: OccupancyDay[],
): OccupancyResponse {
  const data: Occupancy = {
    id: evseId,
    evseId,
    weeks: 4,
    from: '2026-09-16T08:00:00Z',
    to: '2026-10-14T08:00:00Z',
    timeZone: 'Europe/Vienna',
    utilization,
    observations,
    days: matrix,
  };
  return { data, meta: { attribution: [ATTRIBUTION] } };
}

function evse(overrides: Partial<EvseDetail> = {}): EvseDetail {
  return {
    id: 'e-1',
    evseId: 'AT*VIE*E1*1',
    hasEvseId: true,
    status: 'CHARGING',
    statusSince: '2026-10-14T07:40:00Z',
    statusObservedAt: '2026-10-14T08:15:00Z',
    statusSource: 'econtrol',
    chargingStation: null,
    maxPowerKw: 22,
    powerType: 'AC_3_PHASE',
    connectors: [
      { standard: 'IEC_62196_T2', format: 'SOCKET', powerType: 'AC_3_PHASE', maxPowerKw: 22 },
    ],
    ...overrides,
  };
}

describe('EvsesService', () => {
  let http: HttpTestingController;
  let service: EvsesService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(EvsesService);
  });

  afterEach(() => http.verify());

  it('loads the public location detail', async () => {
    const result = service.location('loc-1');
    http
      .expectOne('/api/v1/locations/loc-1')
      .flush({ data: { park: [], location: locationSummary(), evses: [] }, meta: { attribution: [] } });

    expect((await result).data.location.id).toBe('loc-1');
  });

  it('asks for the status history of a range and the occupancy of some weeks', async () => {
    const history = service.history(
      'e/1',
      new Date('2026-10-07T08:00:00Z'),
      new Date('2026-10-14T08:00:00Z'),
    );
    http
      .expectOne(
        '/api/v1/evses/e%2F1/status/history?from=2026-10-07T08:00:00.000Z&to=2026-10-14T08:00:00.000Z',
      )
      .flush({ data: { intervals: [] } });
    const profile = service.occupancy('e-1', 4);
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancy('e-1', null, 0, days()));

    expect((await history).data.intervals).toEqual([]);
    expect((await profile).data.weeks).toBe(4);
  });

  it('asks for the combined occupancy of a whole charging park', async () => {
    const park = service.locationOccupancy('loc-1', 4, true);
    http.expectOne('/api/v1/locations/loc-1/occupancy?weeks=4&park=true').flush({ data: { id: 'loc-1', park: true, locations: 2 } });

    expect((await park).data.locations).toBe(2);
  });
});

describe('status helpers', () => {
  it('names every OCPI status and falls back to unknown', () => {
    expect(statusOf('CHARGING').label).toBe('lädt');
    expect(statusOf('SOMETHING_NEW')).toBe(EVSE_STATUS['UNKNOWN']);
    expect(statusOf('CHARGING')).toEqual({ color: 'var(--cg-status-occupied)', icon: 'bolt', label: 'lädt' });
    expect(statusOf('INOPERATIVE').icon).toBe('close');
  });

  it('names the source of a fault and of a status', () => {
    expect(faultSource('OUTOFORDER', 'econtrol')).toBe('laut E-Control ');
    expect(faultSource('INOPERATIVE', 'ocm')).toBe('laut Open Charge Map ');
    expect(faultSource('CHARGING', 'econtrol')).toBe('');
    expect(faultSource('OUTOFORDER', null)).toBe('');
    expect(sourceName('econtrol')).toBe('E-Control');
    expect(sourceName('other')).toBe('other');
    expect(sourceName(null)).toBe('');
  });

  it('clips the intervals to the range and drops those outside', () => {
    const from = new Date('2026-10-07T00:00:00Z');
    const to = new Date('2026-10-17T00:00:00Z');

    const segments = timeline(
      [
        {
          status: 'OUTOFORDER',
          from: '2026-09-01T00:00:00Z',
          to: '2026-09-02T00:00:00Z',
          source: 'econtrol',
          confidence: 1,
        },
        {
          status: 'AVAILABLE',
          from: '2026-10-01T00:00:00Z',
          to: '2026-10-09T00:00:00Z',
          source: 'econtrol',
          confidence: 1,
        },
        {
          status: 'CHARGING',
          from: '2026-10-09T00:00:00Z',
          to: null,
          source: 'econtrol',
          confidence: 1,
        },
      ],
      from,
      to,
    );

    expect(segments.map((segment) => [segment.status, segment.left, segment.width])).toEqual([
      ['AVAILABLE', 0, 20],
      ['CHARGING', 20, 80],
    ]);
    expect(segments[0].from).toEqual(from);
    expect(segments[1].to).toEqual(to);
  });

  it('sums the readings of several EVSEs per weekday and hour', () => {
    const combined = combine([days([1, 8, 0.5, 4]), days([1, 8, 1, 2], [2, 9, 0, 3])]);

    expect(combined[0].observations[8]).toBe(6);
    expect(combined[0].chargingShare[8]).toBeCloseTo(4 / 6);
    expect(combined[1].chargingShare[9]).toBe(0);
    expect(combined[1].chargingShare[10]).toBeNull();
    expect(combined.map((day) => day.weekday)).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });

  it('colours cells from light to dark orange and empty cells grey', () => {
    expect(heatColor(null)).toBe('var(--mat-sys-surface-container-highest)');
    expect(heatColor(0)).toBe('color-mix(in srgb, var(--mat-sys-primary) 8%, transparent)');
    expect(heatColor(1)).toBe('color-mix(in srgb, var(--mat-sys-primary) 100%, transparent)');
  });
});

describe('LocationDetailPage', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  const page = () => harness.routeNativeElement!;
  const charger = evse();
  const surrogate = evse({
    id: 'e-2',
    evseId: 'XX*CG*E0123456789ABCDEF',
    hasEvseId: false,
    status: 'SOMETHING_NEW',
    statusSince: null,
    maxPowerKw: null,
    connectors: [
      { standard: 'DOMESTIC_F', format: null, powerType: null, maxPowerKw: null },
      { standard: 'IEC_62196_T2', format: null, powerType: null, maxPowerKw: null },
    ],
  });

  function detail(evses: EvseDetail[]): LocationDetailResponse {
    return {
      data: { park: [], location: locationSummary({ evsesTotal: evses.length, evsesAvailable: 0 }), evses },
      meta: { attribution: [ATTRIBUTION, ATTRIBUTION] },
    };
  }

  async function settle(): Promise<void> {
    await flush();
    await harness.fixture.whenStable();
  }

  async function open(answer: LocationDetailResponse | number): Promise<void> {
    harness = await RouterTestingHarness.create('/locations/loc-1');
    const request = http.expectOne('/api/v1/locations/loc-1');
    if (typeof answer === 'number') {
      request.flush({ title: 'Error' }, { status: answer, statusText: 'Error' });
    } else {
      request.flush(answer);
    }
    await settle();
  }

  const historyRequest = (id: string): TestRequest =>
    http.expectOne((request) => request.url === `/api/v1/evses/${id}/status/history`);
  const occupancyRequest = (id: string): TestRequest =>
    http.expectOne(`/api/v1/evses/${id}/occupancy?weeks=4`);
  const fail = (request: TestRequest) =>
    request.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });

  function history(request: TestRequest, statuses: [string, number, number | null][]): void {
    const to = Date.parse(request.request.params.get('to')!);
    const response: StatusHistoryResponse = {
      data: {
        id: 'e',
        evseId: 'e',
        from: request.request.params.get('from')!,
        to: request.request.params.get('to')!,
        intervals: statuses.map(([status, daysBack, untilDaysBack]) => ({
          status,
          from: new Date(to - daysBack * DAY).toISOString(),
          to: untilDaysBack === null ? null : new Date(to - untilDaysBack * DAY).toISOString(),
          source: 'econtrol',
          confidence: 1,
        })),
      },
      meta: { attribution: [] },
    };
    request.flush(response);
  }

  async function select(value: string): Promise<void> {
    const toggles = Array.from(page().querySelectorAll<HTMLElement>('#heatmap-select mat-button-toggle'));
    const index = value === 'all' ? 0 : ['e-1', 'e-2'].indexOf(value) + 1;
    toggles[index].querySelector('button')!.click();
    await settle();
  }

  const heatmapCell = (weekday: number, hour: number) =>
    page()
      .querySelectorAll<HTMLTableRowElement>('#heatmap tbody tr')
      [weekday - 1].querySelectorAll('td')[hour];

  beforeEach(() => {
    const auth = fakeAuth(ME);
    auth.hasPermission.and.callFake((permission) => permission === 'evse:read');
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
        // The star of the page has its own spec.
        { provide: FavoritesService, useValue: jasmine.createSpyObj<FavoritesService>('FavoritesService', ['isFavorite', 'add', 'remove', 'list']) },
        provideRouter([{ path: 'locations/:id', component: LocationDetailPage }]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows the location, its EVSEs with timeline and the heatmap of all EVSEs', async () => {
    await open(detail([charger, surrogate]));

    expect(text(page(), '#location-name')).toBe('Parkgarage Stephansplatz');
    expect(text(page(), '.address')).toBe('Stephansplatz 1, 1010 Wien');
    expect(text(page(), '.operator')).toBe('Betreiber: Wien Energie GmbH');
    expect(text(page(), '.availability')).toBe('0 von 2 Ladepunkten frei');
    expect(text(page(), '#detail-attribution')).toBe('Datenquelle: E-Control');
    expect(text(page(), '.timeline-state')).toBe('Verlauf wird geladen …');
    expect(text(page(), '.heatmap-state')).toBe('Auslastung wird geladen …');

    const chargerHistory = historyRequest('e-1');
    expect(
      Date.parse(chargerHistory.request.params.get('to')!) -
        Date.parse(chargerHistory.request.params.get('from')!),
    ).toBe(7 * DAY);
    history(chargerHistory, [
      ['AVAILABLE', 10, 3.5],
      ['CHARGING', 3.5, null],
    ]);
    history(historyRequest('e-2'), []);
    occupancyRequest('e-1').flush(occupancy('e-1', 0.5, 4, days([1, 8, 0.75, 4])));
    occupancyRequest('e-2').flush(occupancy('e-2', 0, 4, days([1, 8, 0, 2], [7, 23, 0, 2])));
    await settle();

    const [first, second] = Array.from(page().querySelectorAll('.evse'));
    expect(text(first, 'h3')).toBe('AT*VIE*E1*1');
    expect(first.querySelector('h3 a')!.getAttribute('href')).toBe('/evses/AT*VIE*E1*1');
    expect(text(first, '.status')).toBe('lädt');
    expect(first.querySelector<HTMLElement>('.status .status-dot')!.style.background).toBe('var(--cg-status-occupied)');
    expect(text(first, '.meta')).toMatch(/^seit 14\.10\.2026 \d\d:40 · bis 22 kW$/);
    expect(text(first, '.connectors')).toBe('Typ 2 · Buchse · 22 kW');
    // Without known stations: no station count and no groups.
    expect(text(page(), '.capacity')).toBe('2 Ladepunkte (bis zu 2 Fahrzeuge gleichzeitig) · 3 Anschlüsse');
    expect(page().querySelector('.station-title')).toBeNull();
    const segments = Array.from(first.querySelectorAll<HTMLElement>('.segment'));
    expect(segments.map((segment) => [segment.style.left, segment.style.width])).toEqual([
      ['0%', '50%'],
      ['50%', '50%'],
    ]);
    expect(segments[1].title).toMatch(/^lädt: \d\d\.\d\d\. \d\d:\d\d – \d\d\.\d\d\. \d\d:\d\d$/);
    expect(text(second, 'h3')).toBe('XX*CG*E0123456789ABCDEF (ID von chargeglobe vergeben)');
    expect(text(second, '.status')).toBe('unbekannt');
    expect(Array.from(second.querySelectorAll('.connector')).map((connector) => connector.textContent!.trim())).toEqual(['Schuko', 'Typ 2']);
    // Several connectors share the status of their EVSE.
    expect(first.querySelector('.connector-note')).toBeNull();
    expect(text(second, '.connector-note')).toBe(
      'Ein Status für alle Anschlüsse: Die Quelle meldet die Belegung je Ladepunkt. Lädt an einem Anschluss ein Auto, gilt der ganze Ladepunkt als belegt.',
    );
    expect(text(second, '.timeline-state')).toBe('Keine Statusmeldungen in den letzten 7 Tagen.');

    expect(text(page(), '#utilization')).toBe(
      'In den letzten 4 Wochen lud hier ein Fahrzeug bei 25% der 8 Statusmeldungen.',
    );
    expect(page().querySelectorAll('#heatmap tbody tr').length).toBe(7);
    expect(text(page(), '#heatmap tbody tr th')).toBe('Mo');
    expect(heatmapCell(1, 8).title).toBe('Mo 8–9 Uhr: 50% lädt (6 Meldungen)');
    expect(heatmapCell(7, 23).title).toBe('So 23–24 Uhr: 0% lädt (2 Meldungen)');
    expect(heatmapCell(2, 0).title).toBe('Di 0–1 Uhr: keine Daten (0 Meldungen)');
    expect(heatmapCell(2, 0).style.background).toBe('var(--mat-sys-surface-container-highest)');

    await select('e-1');
    expect(heatmapCell(1, 8).title).toBe('Mo 8–9 Uhr: 75% lädt (4 Meldungen)');
    expect(text(page(), '#utilization')).toContain('bei 50% der 4 Statusmeldungen');

    await select('all');
    expect(heatmapCell(1, 8).title).toBe('Mo 8–9 Uhr: 50% lädt (6 Meldungen)');
  });

  it('shows failed timelines and heatmaps per EVSE and keeps the loaded ones', async () => {
    await open(detail([charger, surrogate]));

    fail(historyRequest('e-1'));
    history(historyRequest('e-2'), []);
    occupancyRequest('e-1').flush(occupancy('e-1', null, 0, days()));
    fail(occupancyRequest('e-2'));
    await settle();

    expect(text(page().querySelector('.evse')!, '.timeline-state')).toBe(
      'Der Statusverlauf konnte nicht geladen werden.',
    );
    expect(text(page(), '#utilization')).toBe('Keine Statusmeldungen in den letzten 4 Wochen.');

    await select('e-2');
    expect(text(page(), '.heatmap-state')).toBe('Die Auslastung konnte nicht geladen werden.');
  });

  it('shows an error when no heatmap could be loaded and no selection for a single EVSE', async () => {
    await open(detail([charger]));

    history(historyRequest('e-1'), []);
    fail(occupancyRequest('e-1'));
    await settle();

    expect(page().querySelector('#heatmap-select')).toBeNull();
    expect(text(page(), '.heatmap-state')).toBe('Die Auslastung konnte nicht geladen werden.');
  });

  it('says that a location has no EVSEs and omits the heatmap', async () => {
    const empty = detail([]);
    empty.data.location = locationSummary({
      name: null,
      operator: null,
      evsesTotal: 0,
      evsesAvailable: 0,
    });
    empty.meta.attribution = [];
    await open(empty);

    expect(text(page(), '#location-name')).toBe('Ladestandort');
    expect(page().querySelector('.operator')).toBeNull();
    expect(text(page(), '#no-evses')).toBe('Für diesen Standort sind keine Ladepunkte bekannt.');
    expect(page().querySelector('#heatmap')).toBeNull();
    expect(page().querySelector('#detail-attribution')).toBeNull();
  });

  it('explains an unknown location', async () => {
    await open(404);

    expect(text(page(), '#detail-notice')).toBe(
      'Diesen Ladestandort gibt es nicht oder nicht mehr.',
    );
    expect(page().querySelector('#location-name')).toBeNull();
  });

  it('explains a failed request', async () => {
    await open(502);

    expect(text(page(), '#detail-notice')).toContain('konnte nicht geladen werden');
  });

  it('opens another station of the charging park in place', async () => {
    const other = locationSummary({ id: 'loc-2', name: 'Andere Säule', evsesTotal: 1, parkId: 'loc-1', parkLocations: 2 });
    await open({ data: { park: [other], location: locationSummary({ parkId: 'loc-1', parkLocations: 2 }), evses: [] }, meta: { attribution: [] } });

    page().querySelector<HTMLButtonElement>('.park-member')!.click();
    await settle();
    http.expectOne('/api/v1/locations/loc-2').flush({ data: { park: [locationSummary()], location: other, evses: [] }, meta: { attribution: [] } });
    await settle();

    expect(TestBed.inject(Router).url).toBe('/locations/loc-2');
    expect(text(page(), '#location-name')).toBe('Andere Säule');
  });
});

/** An empty answer to a status history request. */
function history(request: TestRequest): void {
  request.flush({ data: { intervals: [] }, meta: { attribution: [] } });
}

describe('LocationDetails (info card)', () => {
  let http: HttpTestingController;
  let auth: ReturnType<typeof fakeAuth>;
  let fixture: ComponentFixture<LocationDetails>;

  const root = () => fixture.nativeElement as HTMLElement;
  const response = (id: string, evses: EvseDetail[]): LocationDetailResponse => ({
    data: { park: [], location: locationSummary({ id }), evses },
    meta: { attribution: [] },
  });

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function show(id: string): Promise<void> {
    fixture.componentRef.setInput('locationId', id);
    await settle();
  }

  beforeEach(() => {
    auth = fakeAuth();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: AuthService, useValue: auth }],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows anonymous visitors the EVSEs with their status and points to the login for more', async () => {
    fixture = TestBed.createComponent(LocationDetails);
    await show('loc-1');
    expect(text(root(), '#details-loading')).toBe('Ladepunkte werden geladen …');

    http.expectOne('/api/v1/locations/loc-1').flush(response('loc-1', [evse()]));
    await settle();

    expect(root().classList).toContain('compact');
    expect(root().querySelector('#location-name')).toBeNull();
    expect(text(root(), '.evse h3')).toBe('AT*VIE*E1*1');
    expect(text(root(), '#history-hint')).toBe('Statusverlauf und Auslastung siehst du nach der Anmeldung.');
    expect(root().querySelector('#history-hint a')!.getAttribute('href')).toBe('/login');
    expect(root().querySelector('.timeline, .timeline-state, #heatmap')).toBeNull();
  });

  it('shows a short fault of the source as a hint next to the status before it', async () => {
    fixture = TestBed.createComponent(LocationDetails);
    await show('loc-1');
    http
      .expectOne('/api/v1/locations/loc-1')
      .flush(
        response('loc-1', [
          evse({ status: 'AVAILABLE', reportedStatus: 'OUTOFORDER', reportedSince: '2026-10-14T08:12:00Z' }),
          evse({ id: 'e-2', evseId: 'AT*VIE*E1*2', reportedStatus: null, reportedSince: null }),
        ]),
      );
    await settle();

    const evses = Array.from(root().querySelectorAll('.evse'));
    expect(text(evses[0], '.status')).toBe('frei');
    expect(text(evses[0], '.brief-fault')).toMatch(/^kurz gestört laut E-Control seit \d{2}:12 – zählt erst nach längerer Dauer als Störung$/);
    expect(evses[1].querySelector('.brief-fault')).toBeNull();
  });

  it('starts over for another location and drops the answers for the one before', async () => {
    auth.hasPermission.and.returnValue(true);
    fixture = TestBed.createComponent(LocationDetails);

    // The location itself answers late, successfully or not.
    await show('loc-1');
    await show('loc-2');
    http.expectOne('/api/v1/locations/loc-1').flush(response('loc-1', [evse()]));
    await settle();
    expect(text(root(), '#details-loading')).toBe('Ladepunkte werden geladen …');
    await show('loc-3');
    http.expectOne('/api/v1/locations/loc-2').flush({ title: 'Error' }, { status: 502, statusText: 'Error' });
    await settle();
    expect(root().querySelector('#detail-notice')).toBeNull();

    // Timeline and heatmap of the location before arrive after the switch.
    http.expectOne('/api/v1/locations/loc-3').flush(response('loc-3', [evse()]));
    await settle();
    const history = http.expectOne((request) => request.url === '/api/v1/evses/e-1/status/history');
    const occupancy = http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4');
    const price = http.expectOne('/api/v1/evses/e-1/prices');
    await show('loc-4');
    history.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    occupancy.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    price.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();
    http.expectOne('/api/v1/locations/loc-4').flush(response('loc-4', []));
    await settle();

    expect(text(root(), '#no-evses')).toBe('Für diesen Standort sind keine Ladepunkte bekannt.');
    expect(root().querySelector('.timeline-state')).toBeNull();
  });

  it('shows the combined occupancy of the whole charging park on demand, loaded once', async () => {
    auth.hasPermission.and.callFake((permission) => permission === 'evse:read');
    fixture = TestBed.createComponent(LocationDetails);
    const own = locationSummary({ id: 'loc-1', parkId: 'loc-1', parkLocations: 2 });
    const twin = locationSummary({ id: 'loc-2', parkId: 'loc-1', parkLocations: 2 });
    const parkBody = {
      data: { id: 'loc-1', park: true, locations: 2, evses: 4, weeks: 4, from: '2026-09-18T15:00:00Z', to: '2026-10-16T15:00:00Z',
              timeZone: 'Europe/Vienna', utilization: 0.25, observations: 40, days: days([1, 8, 0.25, 40]) },
    };
    const toggle = (label: string) =>
      Array.from(root().querySelectorAll<HTMLElement>('#heatmap-select mat-button-toggle')).find((item) => item.textContent!.trim() === label)!;
    const choose = async (label: string) => {
      toggle(label).querySelector('button')!.click();
      await settle();
    };
    await show('loc-1');
    http.expectOne('/api/v1/locations/loc-1').flush({ data: { park: [twin], location: own, evses: [evse()] }, meta: { attribution: [] } });
    await settle();
    history(http.expectOne((request) => request.url === '/api/v1/evses/e-1/status/history'));
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancy('e-1', 0.5, 4, days([1, 8, 0.5, 4])));
    await settle();

    expect(Array.from(root().querySelectorAll('#heatmap-select mat-button-toggle')).map((item) => item.textContent!.trim())).toEqual([
      'Ganzer Ladepark',
      'Diese Station',
      'Ladepunkt 1',
    ]);
    expect(root().querySelector('#heatmap-scope')).toBeNull();

    await choose('Ganzer Ladepark');
    expect(text(root(), '.heatmap-state')).toBe('Auslastung wird geladen …');
    http.expectOne('/api/v1/locations/loc-1/occupancy?weeks=4&park=true').flush(parkBody);
    await settle();
    expect(text(root(), '#heatmap-scope')).toBe('Ganzer Ladepark: 2 Stationen, 4 Ladepunkte zusammen');
    expect(text(root(), '#utilization')).toBe('In den letzten 4 Wochen lud hier ein Fahrzeug bei 25% der 40 Statusmeldungen.');

    // Back to this station and to the park again: no second request.
    await choose('Diese Station');
    expect(root().querySelector('#heatmap-scope')).toBeNull();
    expect(text(root(), '#utilization')).toContain('50%');
    await choose('Ganzer Ladepark');
    expect(text(root(), '#heatmap-scope')).toContain('2 Stationen');

    // The park view switches to the whole park and back; another location starts over, a failure is told.
    root().querySelector<HTMLButtonElement>('#park-toggle')!.click();
    await settle();
    http.expectOne('/api/v1/locations/loc-2').flush({ data: { park: [own], location: twin, evses: [] }, meta: { attribution: [] } });
    await settle();
    expect(text(root(), '#heatmap-scope')).toContain('2 Stationen');
    root().querySelector<HTMLButtonElement>('#park-toggle')!.click();
    await settle();
    expect(root().querySelector('#heatmap-scope')).toBeNull();

    await show('loc-5');
    http.expectOne('/api/v1/locations/loc-5').flush({ data: { park: [twin], location: locationSummary({ id: 'loc-5' }), evses: [evse()] }, meta: { attribution: [] } });
    await settle();
    history(http.expectOne((request) => request.url === '/api/v1/evses/e-1/status/history'));
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancy('e-1', 0.5, 4, days([1, 8, 0.5, 4])));
    await settle();
    root().querySelector<HTMLButtonElement>('#park-toggle')!.click();
    await settle();
    http.expectOne('/api/v1/locations/loc-5/occupancy?weeks=4&park=true').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    http.expectOne('/api/v1/locations/loc-2').flush({ data: { park: [], location: twin, evses: [] }, meta: { attribution: [] } });
    await settle();
    expect(text(root(), '.heatmap-state.error')).toBe('Die Auslastung konnte nicht geladen werden.');
  });

  it('sorts a large park, lets it be searched and loads the other stations step by step', async () => {
    fixture = TestBed.createComponent(LocationDetails);
    const states = ['OUT_OF_ORDER', 'AVAILABLE', 'OCCUPIED', 'UNKNOWN'] as const;
    const others = Array.from({ length: 12 }, (_, index) =>
      locationSummary({ id: `m-${index}`, name: `Wallbox ${index}`, availability: states[index % 4], parkId: 'loc-1', parkLocations: 13 }),
    );
    const words = (element: Element) => element.textContent!.replace(/\s+/g, ' ').trim();
    const flushMembers = (ids: string[]) =>
      ids.forEach((id) =>
        http.expectOne(`/api/v1/locations/${id}`).flush({ data: { park: [], location: locationSummary({ id }), evses: [] }, meta: { attribution: [] } }),
      );
    await show('loc-1');
    http.expectOne('/api/v1/locations/loc-1').flush({ data: { park: others, location: locationSummary({ id: 'loc-1' }), evses: [] }, meta: { attribution: [] } });
    await settle();

    // Free first, then occupied, out of order and unknown; the list scrolls and can be searched.
    const names = () => Array.from(root().querySelectorAll('.park-member')).map((member) => words(member).split(' · ')[1]);
    expect(names().slice(0, 4)).toEqual(['Wallbox 1', 'Wallbox 5', 'Wallbox 9', 'Wallbox 2']);
    expect(names().at(-1)).toBe('Wallbox 11');
    expect(root().querySelector('.park-members')!.classList).toContain('scroll');
    const search = root().querySelector<HTMLInputElement>('#park-search')!;
    search.value = 'wallbox 1';
    search.dispatchEvent(new Event('input'));
    await settle();
    expect(names()).toEqual(['Wallbox 1', 'Wallbox 10', 'Wallbox 11']);
    search.value = 'gibt es nicht';
    search.dispatchEvent(new Event('input'));
    await settle();
    expect(text(root(), '.park-none')).toBe('Keine Station passt zur Suche.');
    search.value = '';
    search.dispatchEvent(new Event('input'));
    await settle();

    // The park view loads ten at once, the rest on demand.
    root().querySelector<HTMLButtonElement>('#park-toggle')!.click();
    await settle();
    flushMembers(['m-1', 'm-5', 'm-9', 'm-2', 'm-6', 'm-10', 'm-0', 'm-4', 'm-8', 'm-3']);
    await settle();
    expect(root().querySelectorAll('.park-station').length).toBe(10);
    const more = root().querySelector<HTMLButtonElement>('#park-more')!;
    expect(words(more)).toBe('Weitere 2 Stationen laden (2 offen)');
    more.click();
    await settle();
    flushMembers(['m-7', 'm-11']);
    await settle();
    expect(root().querySelectorAll('.park-station').length).toBe(12);
    expect(root().querySelector('#park-more')).toBeNull();
  });

  it('groups the EVSEs by their charging station and counts stations, EVSEs and connectors', async () => {
    fixture = TestBed.createComponent(LocationDetails);
    await show('loc-1');
    http.expectOne('/api/v1/locations/loc-1').flush(
      response('loc-1', [
        evse({ id: 'e-1', evseId: 'AT*MER*E006566*01', chargingStation: 'AT*MER*E006566' }),
        evse({ id: 'e-2', evseId: 'AT*MER*E006567*01', chargingStation: 'AT*MER*E006567' }),
        evse({ id: 'e-3', evseId: 'AT*MER*E006566*02', chargingStation: 'AT*MER*E006566' }),
      ]),
    );
    await settle();

    expect(text(root(), '.capacity')).toBe('2 Ladestationen · 3 Ladepunkte (bis zu 3 Fahrzeuge gleichzeitig) · 3 Anschlüsse');
    expect(Array.from(root().querySelectorAll('.station-title')).map((title) => title.textContent!.trim())).toEqual([
      'Ladestation E006566 · 2 Ladepunkte',
      'Ladestation E006567 · 1 Ladepunkt',
    ]);
    expect(Array.from(root().querySelectorAll('.evse h3')).map((title) => title.textContent!.trim())).toEqual([
      'AT*MER*E006566*01',
      'AT*MER*E006566*02',
      'AT*MER*E006567*01',
    ]);

    // One EVSE without a known station: the stations are not counted, its group comes last.
    await show('loc-2');
    http.expectOne('/api/v1/locations/loc-2').flush(
      response('loc-2', [evse({ id: 'e-4', chargingStation: null }), evse({ id: 'e-5', evseId: 'AT*MER*E1*1', chargingStation: 'AT*MER*E1' })]),
    );
    await settle();
    expect(text(root(), '.capacity')).toBe('2 Ladepunkte (bis zu 2 Fahrzeuge gleichzeitig) · 2 Anschlüsse');
    expect(Array.from(root().querySelectorAll('.station-title')).map((title) => title.textContent!.trim())).toEqual([
      'Ladestation E1 · 1 Ladepunkt',
      'Ohne Angabe der Ladestation · 1 Ladepunkt',
    ]);

    await show('loc-3');
    http.expectOne('/api/v1/locations/loc-3').flush(response('loc-3', [evse({ connectors: [] })]));
    await settle();
    expect(text(root(), '.capacity')).toBe('1 Ladepunkt (ein Fahrzeug zugleich) · 0 Anschlüsse');
  });

  it('shows the other stations of its charging park and the whole park on demand', async () => {
    fixture = TestBed.createComponent(LocationDetails);
    const opened: string[] = [];
    fixture.componentInstance.openLocation.subscribe((location) => opened.push(location.id));
    const own = locationSummary({ id: 'loc-1', evsesTotal: 2, evsesAvailable: 1, parkId: 'loc-1', parkLocations: 3 });
    const twin = locationSummary({ id: 'loc-2', evsesTotal: 2, evsesAvailable: 0, parkId: 'loc-1', parkLocations: 3 });
    const south = locationSummary({ id: 'loc-3', name: 'Parkplatz Süd', evsesTotal: 1, evsesAvailable: 1, parkId: 'loc-1', parkLocations: 3 });
    const words = (element: Element) => element.textContent!.replace(/\s+/g, ' ').trim();
    await show('loc-1');
    http.expectOne('/api/v1/locations/loc-1').flush({ data: { park: [twin, south], location: own, evses: [evse()] }, meta: { attribution: [] } });
    await settle();

    expect(text(root(), '.park-summary')).toBe('Ladepark: 3 Stationen an diesem Ort, zusammen 5 Ladepunkte für bis zu 5 Fahrzeuge (2 frei)');
    const members = Array.from(root().querySelectorAll<HTMLButtonElement>('.park-member'));
    expect(members.map(words)).toEqual(['Andere Station: 2 Ladepunkte, 0 frei', 'Andere Station: 1 Ladepunkt, 1 frei · Parkplatz Süd']);
    members[1].click();
    expect(opened).toEqual(['loc-3']);
    expect(root().querySelector('.park-station')).toBeNull();

    // The whole park: the other stations load their own details, without a park section of their own.
    const toggle = root().querySelector<HTMLButtonElement>('#park-toggle')!;
    expect(words(toggle)).toBe('Ganzen Ladepark ansehen');
    toggle.click();
    await settle();
    http.expectOne('/api/v1/locations/loc-2').flush({ data: { park: [own, south], location: twin, evses: [] }, meta: { attribution: [] } });
    http.expectOne('/api/v1/locations/loc-3').flush({ data: { park: [own, twin], location: south, evses: [] }, meta: { attribution: [] } });
    await settle();
    expect(Array.from(root().querySelectorAll('.park-station-title')).map(words)).toEqual(['Station 2 von 3', 'Station 3 von 3']);
    expect(root().querySelectorAll('#park').length).toBe(1);
    expect(text(root().querySelector('.park-station')!, 'app-location-details h2')).toBe('Ladepunkte dieser Station');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(words(toggle)).toBe('Nur diese Station zeigen');

    // Another location starts with its own view again.
    await show('loc-4');
    http.expectOne('/api/v1/locations/loc-4').flush(response('loc-4', []));
    await settle();
    expect(root().querySelector('#park')).toBeNull();
    expect(root().querySelector('.park-station')).toBeNull();
  });
});

describe('LocationDetails with prices', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<LocationDetails>;

  const root = () => fixture.nativeElement as HTMLElement;
  const priceView = (overrides: Partial<PriceView> = {}): PriceView => ({
    source: 'econtrol',
    currency: 'EUR',
    pricePerKwh: 0.59,
    pricePerMinute: null,
    sessionFee: 0,
    blockingFeePerMinute: 0.1,
    blockingAfterMinutes: 240,
    freeOfCharge: false,
    exampleCost: 17.7,
    ...overrides,
  });
  const prices = (current: EvsePricesResponse['data']['current'], emps: EmpPrice[] = []): EvsePricesResponse => ({
    data: { id: 'e', evseId: 'e', tariffKind: 'AD_HOC', from: '', to: '', example: { kwh: 30, minutes: 40, powerKw: null, basis: 'REQUEST' }, current, history: [], emps, unavailable: [], priceUnknown: [] },
    meta: { attribution: [] },
  });

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    const auth = fakeAuth(ME);
    auth.hasPermission.and.callFake((permission) => permission === 'price:read');
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: AuthService, useValue: auth }],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows the ad-hoc price of each EVSE with the cost of 30 kWh in 40 minutes, also without evse:read', async () => {
    fixture = TestBed.createComponent(LocationDetails);
    fixture.componentRef.setInput('locationId', 'loc-1');
    await settle();
    http
      .expectOne('/api/v1/locations/loc-1')
      .flush({ data: { park: [], location: locationSummary(), evses: [evse(), evse({ id: 'e-2' }), evse({ id: 'e-3' })] }, meta: { attribution: [] } });
    await settle();
    expect(Array.from(root().querySelectorAll('.price')).map((price) => price.textContent!.trim())).toEqual([
      'Preis wird geladen …',
      'Preis wird geladen …',
      'Preis wird geladen …',
    ]);

    http.expectOne('/api/v1/evses/e-1/prices').flush(prices({ price: priceView(), since: '2026-10-01T00:00:00Z', observedAt: '2026-10-14T00:00:00Z' }));
    const card: EmpPrice = {
      emp: 'smatrics',
      empName: 'SMATRICS',
      tariffId: 't-1',
      tariff: 'Basic',
      tariffKey: 'basic',
      zone: 'ROAMING',
      pricing: 'FIXED',
      sourceUrl: 'https://example.org/preise',
      sourceDate: null,
      confidence: 'HIGH',
      monthlyFee: null,
      price: priceView({ source: 'emp:smatrics', exampleCost: 13.5 }),
    };
    http.expectOne('/api/v1/evses/e-2/prices').flush(prices(null, [card]));
    http.expectOne('/api/v1/evses/e-3/prices').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();

    const lines = Array.from(root().querySelectorAll('.price')).map((price) => price.textContent!.replace(/\s+/g, ' ').trim());
    expect(lines).toEqual([
      'Ad-hoc: 0,59 €/kWh · Blockiergebühr 0,10 €/min ab Minute 240 · 30 kWh in 40 min: 17,70 €',
      'Kein Ad-hoc-Preis bekannt.',
      'günstigste Karte: SMATRICS (Basic) · 30 kWh in 40 min: 13,50 €',
      'Der Preis konnte nicht geladen werden.',
    ]);
    expect(root().querySelector('.timeline, #heatmap')).toBeNull();
    // Only the EVSE with a card gets the list of all cards.
    expect(root().querySelectorAll('.all-tariffs').length).toBe(1);
  });

  it('lists every card with zone, cost and monthly fee and names the tariffs that do not work here', async () => {
    TestBed.inject(CardsStore).set(['own']);
    const card = (emp: string, zone: EmpPrice['zone'], exampleCost: number, monthlyFee: number | null): EmpPrice => ({
      emp,
      empName: emp.toUpperCase(),
      tariffId: `t-${emp}`,
      tariffKey: 'basic',
      tariff: 'Basic',
      zone,
      pricing: 'FIXED',
      monthlyFee,
      price: priceView({ source: `emp:${emp}`, exampleCost }),
      sourceUrl: 'https://example.org/preise',
      sourceDate: '2026-07-01',
      confidence: 'HIGH',
    });
    fixture = TestBed.createComponent(LocationDetails);
    fixture.componentRef.setInput('locationId', 'loc-1');
    await settle();
    http.expectOne('/api/v1/locations/loc-1').flush({ data: { park: [], location: locationSummary(), evses: [evse()] }, meta: { attribution: [] } });
    await settle();
    const response = prices(null, [card('own', 'HOME', 9, 4.9), { ...card('roam', 'ROAMING', 13, null), pricing: 'OPERATOR_AD_HOC' }]);
    response.data.priceUnknown = [{ emp: 'plugsurfing', empName: 'Plugsurfing', tariffKey: 'pay-per-use', tariff: 'Plugsurfing', zone: 'ROAMING' }];
    response.data.unavailable = [
      { emp: 'tesla', empName: 'Tesla', tariffKey: 'supercharging', tariff: 'Supercharging' },
      { emp: 'evn', empName: 'EVN', tariffKey: 'drive-easy', tariff: '#Drive Easy' },
    ];
    http.expectOne('/api/v1/evses/e-1/prices').flush(response);
    await settle();

    expect(text(root(), '.all-tariffs summary')).toBe('Alle Ladekarten und Apps (2)');
    const rows = Array.from(root().querySelectorAll('.tariff-table tr'));
    expect(rows.map((row) => [text(row, 'th'), text(row, '.zone'), text(row, '.price-example'), text(row, '.monthly'), row.classList.contains('own')])).toEqual([
      ['OWN · Basic', 'Heimnetz', '9,00 €', '+ 4,90 €/Monat', true],
      ['ROAM · Basic', 'Roaming, Betreiberpreis', '13,00 €', '', false],
    ]);
    expect(rows[0].querySelector('.zone-home')).not.toBeNull();
    expect(text(root(), '.unavailable')).toBe('Hier nicht nutzbar: Tesla · Supercharging, EVN · #Drive Easy');
    expect(text(root(), '.price-unknown')).toBe('Nutzbar, Preis hier unbekannt: Plugsurfing · Plugsurfing');
  });

  it('names the cheapest own card and the cheapest card overall if it is another one', async () => {
    const cards = TestBed.inject(CardsStore);
    const empPrice = (emp: string, exampleCost: number): EmpPrice => ({
      emp,
      empName: emp.toUpperCase(),
      tariffId: `t-${emp}`,
      tariff: 'Basic',
      tariffKey: 'basic',
      zone: 'ROAMING',
      pricing: 'FIXED',
      sourceUrl: 'https://example.org/preise',
      sourceDate: null,
      confidence: 'HIGH',
      monthlyFee: null,
      price: priceView({ source: `emp:${emp}`, exampleCost }),
    });
    fixture = TestBed.createComponent(LocationDetails);
    fixture.componentRef.setInput('locationId', 'loc-1');
    await settle();
    http.expectOne('/api/v1/locations/loc-1').flush({ data: { park: [], location: locationSummary(), evses: [evse()] }, meta: { attribution: [] } });
    await settle();
    http.expectOne('/api/v1/evses/e-1/prices').flush(prices(null, [empPrice('cheap', 10), empPrice('mine', 12), empPrice('other', 15)]));
    await settle();
    const lines = () => Array.from(root().querySelectorAll('.own-card, .cheapest-card')).map((line) => line.textContent!.replace(/\s+/g, ' ').trim());

    cards.set(['mine', 'other']);
    await settle();
    expect(lines()).toEqual(['deine günstigste Karte: MINE (Basic) · 30 kWh in 40 min: 12,00 €', 'günstigste Karte: CHEAP (Basic) · 30 kWh in 40 min: 10,00 €']);

    // The own card is the cheapest one: one line.
    cards.set(['cheap']);
    await settle();
    expect(lines()).toEqual(['deine günstigste Karte: CHEAP (Basic) · 30 kWh in 40 min: 10,00 €']);
  });
});

describe('price texts', () => {
  /** Intl puts a non-breaking space before the currency sign. */
  const nbsp = (texts: string[]) => texts.map((value) => value.replace(/ €/g, '\u00a0€'));

  it('names every part of a price in German', () => {
    const base: PriceView = {
      source: 'econtrol',
      currency: 'EUR',
      pricePerKwh: 0.59,
      pricePerMinute: 0.02,
      sessionFee: 0.5,
      blockingFeePerMinute: 0.1,
      blockingAfterMinutes: null,
      freeOfCharge: false,
      exampleCost: 19,
    };
    expect(priceParts(base)).toEqual(nbsp(['0,59 €/kWh', '0,02 €/min', 'Startgebühr 0,50 €', 'Blockiergebühr 0,10 €/min']));
    expect(priceParts({ ...base, freeOfCharge: true })).toEqual(['kostenlos']);
    expect(priceParts({ ...base, pricePerKwh: null, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: null })).toEqual(['keine Angaben']);
    expect(money(0.1234, 'EUR')).toBe('0,1234\u00a0€');
    expect(money(19, 'EUR')).toBe('19,00\u00a0€');
    expect([duration(16), duration(120), duration(164)]).toEqual(['16 min', '2 h', '2 h 44 min']);
    expect(sessionText({ kwh: 22.5, minutes: 164, powerKw: 11, basis: 'VEHICLE' })).toBe('22,5 kWh in 2 h 44 min');
  });

  it('compares the ad-hoc price with the cards by the cost of the example and marks the cheapest', () => {
    const view = (exampleCost: number): PriceView => ({
      source: 'x',
      currency: 'EUR',
      pricePerKwh: null,
      pricePerMinute: null,
      sessionFee: null,
      blockingFeePerMinute: null,
      blockingAfterMinutes: null,
      freeOfCharge: false,
      exampleCost,
    });
    const card = (tariffId: string, exampleCost: number): EmpPrice => ({
      emp: 'e',
      empName: 'Karte',
      tariffId,
      tariff: tariffId,
      tariffKey: 'basic',
      zone: 'ROAMING',
      pricing: 'FIXED',
      sourceUrl: 'https://example.org/preise',
      sourceDate: null,
      confidence: 'HIGH',
      monthlyFee: 2,
      price: view(exampleCost),
    });
    const data = (current: EvsePricesResponse['data']['current'], emps: EmpPrice[]): EvsePricesResponse['data'] => ({
      id: 'e',
      evseId: 'e',
      tariffKind: 'AD_HOC',
      from: '',
      to: '',
      example: { kwh: 30, minutes: 40, powerKw: null, basis: 'REQUEST' },
      current,
      history: [],
      emps,
      unavailable: [],
      priceUnknown: [],
    });
    const adHoc = { price: view(13), since: '', observedAt: '' };

    expect(comparison(data(adHoc, []))).toEqual([]);
    const rows = comparison(data(adHoc, [card('a', 13), card('b', 20), card('c', 9)]));
    expect(rows.map((row) => [row.key, row.label, row.monthlyFee, row.cheapest])).toEqual([
      ['c', 'Karte · c (Roaming)', 2, true],
      ['ad-hoc', 'Ad-hoc (ohne Vertrag)', null, false],
      ['a', 'Karte · a (Roaming)', 2, false],
      ['b', 'Karte · b (Roaming)', 2, false],
    ]);
    expect(comparison(data(null, [card('a', 13), card('b', 13)])).map((row) => row.cheapest)).toEqual([true, true]);
    // Own cards are marked; the ad-hoc price is never one.
    expect(comparison(data(adHoc, [{ ...card('a', 13), emp: 'mine' }, card('b', 20)]), ['mine']).map((row) => [row.key, row.own])).toEqual([
      ['ad-hoc', false],
      ['a', true],
      ['b', false],
    ]);
  });
});
