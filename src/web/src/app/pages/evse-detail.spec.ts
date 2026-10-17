import { MapLink } from '../map/map-link';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { EvseResponse, EvseView, EvsesService, OccupancyResponse, StatusHistoryResponse } from '../evses/evses.service';
import { attributeLabel, authMethodLabel, connectorLabel, formatLabel, powerTypeLabel, yesNo } from '../evses/labels';
import { AuthService } from '../auth/auth.service';
import { EmpPrice, EvsePricesResponse, PriceView } from '../evses/prices';
import { CardsStore } from '../preferences/cards.store';
import { ME, fakeAuth, flush, text } from '../testing';
import { EvseDetailPage, LISTED_INTERVALS } from './evse-detail.page';

const DAY = 24 * 60 * 60 * 1000;

function view(overrides: Partial<EvseView> = {}): EvseView {
  return {
    id: 'e-1',
    evseId: 'AT*VIE*E1234*1',
    hasEvseId: true,
    lifecycle: 'ACTIVE',
    location: {
      id: 'loc-1',
      name: 'Parkgarage Stephansplatz',
      address: 'Stephansplatz 1',
      postalCode: '1010',
      city: 'Wien',
      country: 'AT',
      latitude: 48.2085,
      longitude: 16.3731,
      region: 'AT-9',
      access: 'PUBLIC',
      openingTimes: { twentyFourSeven: true, description: null },
      facilities: { greenEnergy: true, freeParking: false, roofedParking: null, barrierFreeParkingSpaces: 2, priceUrl: 'https://example.org/preise' },
    },
    operator: { id: 'op-1', name: 'Wien Energie GmbH', operatorId: 'VIE' },
    physicalReference: 'Säule 1',
    maxPowerKw: 22,
    powerType: 'AC_3_PHASE',
    connectors: [
      { standard: 'IEC_62196_T2', format: 'SOCKET', powerType: 'AC_3_PHASE', maxPowerKw: 22 },
      { standard: 'DOMESTIC_F', format: null, powerType: null, maxPowerKw: null },
    ],
    authMethods: ['RFID_READER', 'CREDIT_CARD_PAYABLE'],
    roaming: true,
    status: { status: 'CHARGING', since: '2026-10-14T07:40:00Z', observedAt: '2026-10-14T08:15:00Z', source: 'econtrol', stale: false },
    firstSeen: '2026-03-01T06:00:00Z',
    lastSeen: '2026-10-14T08:15:00Z',
    lastConfirmedAt: '2026-10-14T06:00:00Z',
    sources: [
      { source: 'econtrol', entity: 'LOCATION', externalId: 'AT*VIE*E1234', firstSeen: '2026-03-01T06:00:00Z', lastSeen: '2026-10-14T06:00:00Z' },
      { source: 'econtrol', entity: 'EVSE', externalId: 'AT*VIE*E1234*1', firstSeen: '2026-03-01T06:00:00Z', lastSeen: '2026-10-14T06:00:00Z' },
    ],
    provenance: [
      { entity: 'LOCATION', attribute: 'address', source: 'econtrol', observedAt: '2026-10-14T06:00:00Z' },
      { entity: 'EVSE', attribute: 'max_power_kw', source: 'econtrol', observedAt: '2026-10-14T06:00:00Z' },
    ],
    ...overrides,
  };
}

const response = (data: EvseView): EvseResponse => ({
  data,
  meta: { attribution: [{ source: 'econtrol', text: 'Datenquelle: E-Control', license: 'Terms' }] },
});

function occupancyOf(weeks: number, utilization: number | null): OccupancyResponse {
  return {
    data: {
      id: 'e-1',
      evseId: 'AT*VIE*E1234*1',
      weeks,
      from: '2026-09-16T08:00:00Z',
      to: '2026-10-14T08:00:00Z',
      timeZone: 'Europe/Vienna',
      utilization,
      observations: utilization === null ? 0 : 10,
      days: ['MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY', 'SUNDAY'].map((day, index) => ({
        weekday: index + 1,
        day,
        chargingShare: Array<number | null>(24).fill(utilization),
        observations: Array<number>(24).fill(utilization === null ? 0 : 1),
      })),
    },
    meta: { attribution: [] },
  };
}

describe('EvseDetailPage', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  const page = () => harness.routeNativeElement!;
  /** The value of a term in a definition list. */
  const value = (list: string, term: string) =>
    Array.from(page().querySelectorAll(`#${list} dt`))
      .find((dt) => dt.textContent!.trim() === term)!
      .nextElementSibling!.textContent!.replace(/\s+/g, ' ')
      .trim();
  const historyRequest = (): TestRequest => http.expectOne((request) => request.url === '/api/v1/evses/e-1/status/history');

  function history(request: TestRequest, count: number): void {
    const to = Date.parse(request.request.params.get('to')!);
    const body: StatusHistoryResponse = {
      data: {
        id: 'e-1',
        evseId: 'AT*VIE*E1234*1',
        from: request.request.params.get('from')!,
        to: request.request.params.get('to')!,
        intervals: Array.from({ length: count }, (_, index) => ({
          status: index % 2 === 0 ? 'AVAILABLE' : 'CHARGING',
          from: new Date(to - (count - index) * 3600_000).toISOString(),
          to: index === count - 1 ? null : new Date(to - (count - index - 1) * 3600_000).toISOString(),
          source: 'econtrol',
          confidence: 1,
        })),
      },
      meta: { attribution: [] },
    };
    request.flush(body);
  }

  async function settle(): Promise<void> {
    await flush();
    await harness.fixture.whenStable();
  }

  async function open(answer: EvseResponse | number): Promise<void> {
    harness = await RouterTestingHarness.create('/evses/AT*VIE*E1234*1');
    const request = http.expectOne('/api/v1/evses/AT*VIE*E1234*1');
    if (typeof answer === 'number') {
      request.flush({ title: 'Error' }, { status: answer, statusText: 'Error' });
    } else {
      request.flush(answer);
    }
    await settle();
  }

  async function toggle(group: string, index: number): Promise<void> {
    page().querySelectorAll<HTMLElement>(`#${group} mat-button-toggle`)[index].querySelector('button')!.click();
    await settle();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'evses/:evseId', component: EvseDetailPage }])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shares the charge point with the share sheet of the system or the clipboard', async () => {
    const share = spyOn(TestBed.inject(MapLink), 'share').and.resolveTo('copied');
    await open(response(view()));
    history(historyRequest(), 1);
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancyOf(4, 0.25));
    await settle();
    const button = () => page().querySelector<HTMLButtonElement>('#share-evse')!;

    button().click();
    await settle();
    expect(share).toHaveBeenCalledWith('Ladepunkt AT*VIE*E1234*1 – chargeglobe');
    expect(text(page(), '#share-message')).toBe('Der Link zu diesem Ladepunkt ist in der Zwischenablage.');

    share.and.resolveTo('failed');
    button().click();
    await settle();
    expect(text(page(), '#share-message')).toBe('Der Link konnte nicht kopiert werden. Kopiere die Adresse aus der Adresszeile.');

    share.and.resolveTo('shared');
    button().click();
    await settle();
    expect(page().querySelector('#share-message')).toBeNull();
  });

  it('shows status, technology, payment, access and the origin of the data', async () => {
    await open(response(view()));

    expect(text(page(), '#evse-id')).toBe('AT*VIE*E1234*1');
    expect(text(page(), '.status')).toBe('lädt');
    expect(text(page(), '#status-meta')).toMatch(/^seit 14\.10\.2026 \d\d:40 · gemeldet 14\.10\.2026 \d\d:15 von E-Control$/);
    expect(page().querySelector('#lifecycle')).toBeNull();
    expect(text(page(), '#evse-location')).toContain('Parkgarage Stephansplatz, Stephansplatz 1, 1010 Wien');
    expect(page().querySelector('#evse-location a')!.getAttribute('href')).toBe('/locations/loc-1');
    expect(page().querySelector('#show-on-map')!.getAttribute('href')).toBe('/#map=17.00/48.20850/16.37310');

    expect(value('technology', 'Maximale Leistung')).toBe('22 kW');
    expect(value('technology', 'Stromart')).toBe('Wechselstrom, 3-phasig');
    expect(value('technology', 'Anschlüsse')).toBe('Typ 2 · Buchse · 22 kW Schuko');
    
    expect(value('technology', 'Betreiber')).toBe('Wien Energie GmbH (VIE)');
    expect(value('technology', 'Bezeichnung vor Ort')).toBe('Säule 1');
    expect(value('technology', 'Region')).toBe('AT-9');

    expect(value('access', 'Zahlungsarten')).toBe('Ladekarte (RFID), Kreditkarte');
    expect(text(page(), '#ad-hoc')).toBe('ja');
    expect(value('access', 'Roaming (Ladekarten anderer Anbieter)')).toBe('ja');
    expect(value('access', 'Zugang')).toBe('öffentlich');
    expect(text(page(), '#opening-times')).toBe('rund um die Uhr');
    expect(value('access', 'Ökostrom')).toBe('ja');
    expect(value('access', 'Kostenloses Parken')).toBe('nein');
    expect(value('access', 'Überdacht')).toBe('unbekannt');
    expect(value('access', 'Barrierefreie Plätze')).toBe('2');
    expect(page().querySelector('#price-url')!.getAttribute('href')).toBe('https://example.org/preise');

    expect(Array.from(page().querySelectorAll('#sources tbody tr')).map((row) => text(row, 'td:nth-child(2)'))).toEqual(['Standort', 'Ladepunkt']);
    expect(Array.from(page().querySelectorAll('#provenance tbody tr')).map((row) => text(row, 'td'))).toEqual([
      'Adresse (Standort)',
      'maximale Leistung',
    ]);
    expect(text(page(), '#evse-attribution')).toBe('Datenquelle: E-Control');

    expect(text(page(), '.timeline-state')).toBe('Verlauf wird geladen …');
    expect(text(page(), '.heatmap-state')).toBe('Auslastung wird geladen …');
    const request = historyRequest();
    expect(Date.parse(request.request.params.get('to')!) - Date.parse(request.request.params.get('from')!)).toBe(7 * DAY);
    history(request, LISTED_INTERVALS + 5);
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancyOf(4, 0.25));
    await settle();

    const rows = page().querySelectorAll('#intervals tbody tr');
    expect(rows.length).toBe(LISTED_INTERVALS);
    expect(text(rows[0], 'td:nth-child(3)')).toBe('andauernd');
    expect(page().querySelectorAll('app-status-timeline .segment').length).toBe(LISTED_INTERVALS + 5);
    expect(text(page(), '#utilization')).toBe('In den letzten 4 Wochen lud hier ein Fahrzeug bei 25% der 10 Statusmeldungen.');
    expect(page().querySelectorAll('#heatmap tbody tr').length).toBe(7);
  });

  it('switches the ranges and drops answers for the range shown before', async () => {
    await open(response(view()));
    history(historyRequest(), 0);
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancyOf(4, null));
    await settle();
    expect(text(page(), '.timeline-state')).toBe('Keine Statusmeldungen in diesem Zeitraum.');
    expect(text(page(), '#utilization')).toBe('Keine Statusmeldungen in den letzten 4 Wochen.');

    await toggle('history-range', 0);
    const day = historyRequest();
    expect(Date.parse(day.request.params.get('to')!) - Date.parse(day.request.params.get('from')!)).toBe(DAY);
    await toggle('history-range', 2);
    const month = historyRequest();
    expect(Date.parse(month.request.params.get('to')!) - Date.parse(month.request.params.get('from')!)).toBe(30 * DAY);
    history(day, 3);
    await settle();
    expect(text(page(), '.timeline-state')).toBe('Verlauf wird geladen …');
    month.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();
    expect(text(page(), '.timeline-state')).toBe('Der Statusverlauf konnte nicht geladen werden.');

    await toggle('history-range', 1);
    const week = historyRequest();
    await toggle('history-range', 0);
    const again = historyRequest();
    week.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    history(again, 1);
    await settle();
    expect(page().querySelectorAll('#intervals tbody tr').length).toBe(1);

    await toggle('occupancy-range', 1);
    const twelve = http.expectOne('/api/v1/evses/e-1/occupancy?weeks=12');
    await toggle('occupancy-range', 0);
    const four = http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4');
    twelve.flush(occupancyOf(12, 0.5));
    await settle();
    expect(text(page(), '.heatmap-state')).toBe('Auslastung wird geladen …');
    four.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();
    expect(text(page(), '.heatmap-state')).toBe('Die Auslastung konnte nicht geladen werden.');

    await toggle('occupancy-range', 1);
    const later = http.expectOne('/api/v1/evses/e-1/occupancy?weeks=12');
    await toggle('occupancy-range', 0);
    const latest = http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4');
    later.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    latest.flush(occupancyOf(4, 1));
    await settle();
    expect(text(page(), '#utilization')).toContain('bei 100% der 10');
  });

  it('shows what is unknown and marks surrogates, removed EVSEs and stale statuses', async () => {
    await open(
      response(
        view({
          hasEvseId: false,
          lifecycle: 'REMOVED',
          operator: { id: 'op-2', name: 'Kleinbetreiber', operatorId: null },
          physicalReference: null,
          maxPowerKw: null,
          powerType: null,
          connectors: [],
          authMethods: [],
          roaming: null,
          status: { status: 'UNKNOWN', since: null, observedAt: null, source: null, stale: true },
          lastConfirmedAt: null,
          sources: [],
          provenance: [],
          location: {
            ...view().location,
            name: null,
            region: null,
            access: 'RESTRICTED',
            openingTimes: { twentyFourSeven: false, description: 'MONDAY 06:00–FRIDAY 22:00' },
            facilities: { greenEnergy: null, freeParking: null, roofedParking: null, barrierFreeParkingSpaces: null, priceUrl: null },
          },
        }),
      ),
    );
    history(historyRequest(), 0);
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancyOf(4, null));
    await settle();

    expect(text(page(), '.evse-header')).toContain('die Kennung hat chargeglobe vergeben');
    expect(text(page(), '#lifecycle')).toBe('Dieser Ladepunkt ist nicht mehr aktiv (REMOVED).');
    expect(text(page(), '#status-meta')).toBe('noch keine Statusmeldung (nicht aktuell)');
    expect(text(page(), '#evse-location')).toContain('Ladestandort,');
    expect(value('technology', 'Maximale Leistung')).toBe('–');
    expect(value('technology', 'Anschlüsse')).toBe('keine Angabe');
    expect(value('technology', 'Betreiber')).toBe('Kleinbetreiber');
    expect(page().querySelector('#technology')!.textContent).not.toContain('Bezeichnung vor Ort');
    expect(value('access', 'Zahlungsarten')).toBe('keine Angabe');
    expect(text(page(), '#ad-hoc')).toBe('unbekannt');
    expect(value('access', 'Zugang')).toBe('eingeschränkt');
    expect(text(page(), '#opening-times')).toBe('MONDAY 06:00–FRIDAY 22:00');
    expect(page().querySelector('#price-url')).toBeNull();
    expect(text(page(), '#sources tbody')).toBe('Keine Quelle verknüpft.');
    expect(page().querySelector('#provenance')).toBeNull();
  });

  it('shows a missing operator and opening times', async () => {
    await open(response(view({ operator: null, authMethods: ['APP'], location: { ...view().location, openingTimes: null } })));
    history(historyRequest(), 0);
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush(occupancyOf(4, null));
    await settle();

    expect(value('technology', 'Betreiber')).toBe('–');
    expect(text(page(), '#opening-times')).toBe('keine Angabe');
    expect(text(page(), '#ad-hoc')).toBe('nein');
  });

  it('explains an unknown charge point', async () => {
    await open(404);
    expect(text(page(), '#evse-notice')).toBe('Diesen Ladepunkt gibt es nicht.');
  });

  it('explains a failed request', async () => {
    await open(502);
    expect(text(page(), '#evse-notice')).toContain('konnte nicht geladen werden');
  });
});

describe('labels', () => {
  it('translate the vocabulary and keep unknown values', () => {
    expect([connectorLabel('CHADEMO'), connectorLabel('NEW'), connectorLabel(null)]).toEqual(['CHAdeMO', 'NEW', '–']);
    expect([formatLabel('CABLE'), powerTypeLabel('DC'), authMethodLabel('CASH'), attributeLabel('geom')]).toEqual([
      'Kabel',
      'Gleichstrom',
      'Bargeld',
      'Koordinaten',
    ]);
    expect([yesNo(true), yesNo(false), yesNo(null)]).toEqual(['ja', 'nein', 'unbekannt']);
  });
});

describe('EvsesService.evse', () => {
  it('loads an EVSE by its EVSE-ID', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const result = TestBed.inject(EvsesService).evse('AT*VIE*E1234*1');
    http.expectOne('/api/v1/evses/AT*VIE*E1234*1').flush(response(view()));

    expect((await result).data.evseId).toBe('AT*VIE*E1234*1');
    http.verify();
  });
});

describe('EvseDetailPage with prices', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  const page = () => harness.routeNativeElement!;
  const price = (overrides: Partial<PriceView> = {}): PriceView => ({
    source: 'econtrol',
    currency: 'EUR',
    pricePerKwh: 0.59,
    pricePerMinute: 0.02,
    sessionFee: 0.5,
    blockingFeePerMinute: null,
    blockingAfterMinutes: null,
    freeOfCharge: false,
    exampleCost: 19,
    ...overrides,
  });
  const prices = (
    current: EvsePricesResponse['data']['current'],
    history: EvsePricesResponse['data']['history'],
    emps: EmpPrice[] = [],
  ): EvsePricesResponse => ({
    data: { id: 'e-1', evseId: 'AT*VIE*E1234*1', tariffKind: 'AD_HOC', from: '', to: '', example: { kwh: 30, minutes: 40, powerKw: null, basis: 'REQUEST' }, current, history, emps, unavailable: [], priceUnknown: [] },
    meta: { attribution: [] },
  });

  async function settle(): Promise<void> {
    await flush();
    await harness.fixture.whenStable();
  }

  async function open(answer: EvsePricesResponse | number): Promise<void> {
    harness = await RouterTestingHarness.create('/evses/AT*VIE*E1234*1');
    http.expectOne('/api/v1/evses/AT*VIE*E1234*1').flush(response(view()));
    await settle();
    http.expectOne((request) => request.url === '/api/v1/evses/e-1/status/history').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    http.expectOne('/api/v1/evses/e-1/occupancy?weeks=4').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    const request = http.expectOne('/api/v1/evses/e-1/prices');
    expect(text(page(), '.price-state')).toBe('Preis wird geladen …');
    if (typeof answer === 'number') {
      request.flush({ title: 'Error' }, { status: answer, statusText: 'Error' });
    } else {
      request.flush(answer);
    }
    await settle();
  }

  beforeEach(() => {
    const auth = fakeAuth(ME);
    auth.hasPermission.and.returnValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
        provideRouter([{ path: 'evses/:evseId', component: EvseDetailPage }]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows the current ad-hoc price, the example cost and the price changes', async () => {
    await open(
      prices({ price: price(), since: '2026-09-01T06:00:00Z', observedAt: '2026-10-14T06:00:00Z' }, [
        { price: price({ pricePerKwh: 0.49, exampleCost: 16 }), from: '2026-05-01T06:00:00Z', to: '2026-09-01T06:00:00Z' },
        { price: price(), from: '2026-09-01T06:00:00Z', to: null },
      ]),
    );

    expect(text(page(), '#price-current')).toBe('0,59 €/kWh · 0,02 €/min · Startgebühr 0,50 €');
    expect(text(page(), '#price-example')).toBe('30 kWh in 40 min: 19,00 €');
    expect(text(page(), '#price-since')).toMatch(/^gilt seit 01\.09\.2026 \d\d:00 · bestätigt 14\.10\.2026 \d\d:00 · Quelle econtrol$/);
    const rows = Array.from(page().querySelectorAll('#price-history tbody tr')).map((row) => text(row, 'td:nth-child(2)'));
    expect(rows).toEqual(['01.09.2026', 'heute']);
    expect(text(page(), '#price-history tbody tr:first-child td:nth-child(4)')).toBe('16,00 €');
    expect(page().querySelector('#price-note')).toBeNull();
  });

  it('says when the register reports no ad-hoc price amount instead of its cost', async () => {
    const wrong = price({ pricePerKwh: null, pricePerMinute: null, sessionFee: 0, exampleCost: 0, plausible: false, reason: 'NO_AMOUNT' });
    await open(
      prices({ price: wrong, since: '2026-09-01T06:00:00Z', observedAt: '2026-10-14T06:00:00Z' }, [
        { price: price({ exampleCost: 19 }), from: '2026-05-01T06:00:00Z', to: '2026-09-01T06:00:00Z' },
        { price: wrong, from: '2026-09-01T06:00:00Z', to: null },
      ]),
    );

    expect(text(page(), '#price-example')).toBe('kein Preis gemeldet');
    expect(text(page(), '#price-history tbody tr:first-child td:nth-child(4)')).toBe('19,00 €');
    expect(text(page(), '#price-history tbody tr:last-child td:nth-child(4)')).toBe('kein Preis gemeldet');
  });

  it('compares the ad-hoc price with the cards and marks the cheapest', async () => {
    const card = (tariffId: string, empName: string, monthlyFee: number | null, exampleCost: number): EmpPrice => ({
      emp: tariffId,
      empName,
      tariffId,
      tariff: 'Basic',
      tariffKey: 'basic',
      zone: 'ROAMING',
      pricing: 'FIXED',
      sourceUrl: 'https://example.org/preise',
      sourceDate: null,
      confidence: 'HIGH',
      monthlyFee,
      price: price({ source: `emp:${tariffId}`, pricePerKwh: 0.45, pricePerMinute: null, sessionFee: null, exampleCost }),
    });
    // The user has the ÖAMTC card.
    TestBed.inject(CardsStore).set(['t-2']);
    await open(
      prices({ price: price(), since: '2026-09-01T06:00:00Z', observedAt: '2026-10-14T06:00:00Z' }, [], [
        card('t-1', 'SMATRICS', null, 13.5),
        card('t-2', 'ÖAMTC', 4.9, 25),
      ]),
    );

    const rows = Array.from(page().querySelectorAll('#price-comparison tbody tr'));
    expect(rows.map((row) => text(row, 'td:first-child'))).toEqual(['SMATRICS · Basic (Roaming) günstigste', 'Ad-hoc (ohne Vertrag)', 'ÖAMTC · Basic (Roaming) deine Karte']);
    expect(rows.map((row) => row.classList.contains('cheapest'))).toEqual([true, false, false]);
    expect(rows.map((row) => row.classList.contains('own'))).toEqual([false, false, true]);
    expect(rows.map((row) => text(row, 'td:nth-child(3)'))).toEqual(['–', '–', '4,90 €']);
    expect(text(rows[0], 'td:nth-child(2)')).toBe('0,45 €/kWh');
    expect(text(rows[0], 'td:nth-child(4)')).toBe('13,50 €');
    expect(page().querySelector('#price-history')).toBeNull();
    expect(page().querySelector('#show-all')).toBeNull();

    // With the own tariff: only it and the ad-hoc price, all on demand.
    TestBed.inject(CardsStore).set(['t-2'], ['t-2/basic']);
    await fixture.whenStable();
    expect(text(page(), '#comparison-title')).toBe('Vergleich mit meinen Tarifen');
    expect(Array.from(page().querySelectorAll('#price-comparison tbody tr')).map((row) => text(row, 'td:first-child'))).toEqual([
      'Ad-hoc (ohne Vertrag) günstigste',
      'ÖAMTC · Basic (Roaming) deine Karte',
    ]);
    page().querySelector<HTMLButtonElement>('#show-all')!.click();
    await fixture.whenStable();
    expect(page().querySelectorAll('#price-comparison tbody tr').length).toBe(3);
    expect(text(page(), '#show-all')).toBe('Nur meine Tarife');
  });

  it('says when no price is known or it could not be loaded', async () => {
    const response = prices(null, []);
    response.data.priceNote = { text: 'Preis laut E-Control.', url: null };
    await open(response);
    expect(text(page(), '#price-current')).toBe('Für diesen Ladepunkt ist kein Ad-hoc-Preis bekannt.');
    expect(text(page(), '#price-note')).toBe('Preis laut E-Control.');
    expect(page().querySelector('#price-history')).toBeNull();
    expect(page().querySelector('#price-comparison')).toBeNull();
  });

  it('says when the price could not be loaded', async () => {
    await open(500);
    expect(text(page(), '.price-state')).toBe('Der Preis konnte nicht geladen werden.');
  });
});
