import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../auth/auth.service';
import { LocationDetailPage } from '../pages/location-detail.page';
import { ME, fakeAuth, flush, locationSummary, text } from '../testing';
import { LocationDetails } from './location-details';
import {
  EvseDetail,
  EvsesService,
  LocationDetailResponse,
  Occupancy,
  OccupancyDay,
  OccupancyResponse,
  StatusHistoryResponse,
} from './evses.service';
import { EVSE_STATUS, combine, heatColor, statusOf, timeline } from './status';

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
      .flush({ data: { location: locationSummary(), evses: [] }, meta: { attribution: [] } });

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
});

describe('status helpers', () => {
  it('names every OCPI status and falls back to unknown', () => {
    expect(statusOf('CHARGING').label).toBe('lädt');
    expect(statusOf('SOMETHING_NEW')).toBe(EVSE_STATUS['UNKNOWN']);
    expect(statusOf('CHARGING')).toEqual({ color: 'var(--cg-status-occupied)', icon: 'bolt', label: 'lädt' });
    expect(statusOf('INOPERATIVE').icon).toBe('close');
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
    connectors: [{ standard: 'DOMESTIC_F', format: null, powerType: null, maxPowerKw: null }],
  });

  function detail(evses: EvseDetail[]): LocationDetailResponse {
    return {
      data: { location: locationSummary({ evsesTotal: evses.length, evsesAvailable: 0 }), evses },
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
    expect(text(first, '.connectors')).toBe('IEC_62196_T2 · SOCKET · 22 kW');
    const segments = Array.from(first.querySelectorAll<HTMLElement>('.segment'));
    expect(segments.map((segment) => [segment.style.left, segment.style.width])).toEqual([
      ['0%', '50%'],
      ['50%', '50%'],
    ]);
    expect(segments[1].title).toMatch(/^lädt: \d\d\.\d\d\. \d\d:\d\d – \d\d\.\d\d\. \d\d:\d\d$/);
    expect(text(second, 'h3')).toBe('XX*CG*E0123456789ABCDEF (ID von chargeglobe vergeben)');
    expect(text(second, '.status')).toBe('unbekannt');
    expect(text(second, '.connectors')).toBe('DOMESTIC_F');
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
});

describe('LocationDetails (info card)', () => {
  let http: HttpTestingController;
  let auth: ReturnType<typeof fakeAuth>;
  let fixture: ComponentFixture<LocationDetails>;

  const root = () => fixture.nativeElement as HTMLElement;
  const response = (id: string, evses: EvseDetail[]): LocationDetailResponse => ({
    data: { location: locationSummary({ id }), evses },
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
    await show('loc-4');
    history.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    occupancy.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();
    http.expectOne('/api/v1/locations/loc-4').flush(response('loc-4', []));
    await settle();

    expect(text(root(), '#no-evses')).toBe('Für diesen Standort sind keine Ladepunkte bekannt.');
    expect(root().querySelector('.timeline-state')).toBeNull();
  });
});
