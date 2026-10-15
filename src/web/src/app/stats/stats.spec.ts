import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { StatsPage, bars } from '../pages/stats.page';
import { flush, text } from '../testing';
import { StatsResponse, StatsService } from './stats.service';

function stats(overrides: Partial<StatsResponse['data']> = {}): StatsResponse {
  return {
    data: {
      region: null,
      totals: { locations: 5840, evses: 13210, connectors: 19880 },
      byRegion: [
        { code: 'AT-3', name: 'Niederösterreich', locations: 1410, evses: 3050 },
        { code: 'AT-9', name: 'Wien', locations: 1620, evses: 6100 },
      ],
      byPowerClass: [
        { class: 'UP_TO_22', evses: 10120 },
        { class: 'FROM_23_TO_49', evses: 310 },
        { class: 'FROM_50_TO_149', evses: 1480 },
        { class: 'FROM_150', evses: 1150 },
        { class: 'UNKNOWN', evses: 150 },
        { class: 'NEW_CLASS', evses: 0 },
      ],
      byConnector: [
        { standard: 'IEC_62196_T2', connectors: 12400 },
        { standard: 'IEC_62196_T2_COMBO', connectors: 2930 },
      ],
      topOperators: [{ id: 'op-1', name: 'Wien Energie GmbH', evses: 2310 }],
      status: { available: 6000, occupied: 2000, faulted: 500, unknown: 200, stale: 4510 },
      freshness: { p50Minutes: 4.5, p90Minutes: 12 },
      generatedAt: '2026-10-15T08:00:00Z',
      ...overrides,
    },
    meta: { attribution: [{ source: 'econtrol', text: 'Datenquelle: E-Control', license: 'Terms' }] },
  };
}

describe('StatsService', () => {
  it('asks for all regions or one', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(StatsService);

    const all = service.load(null);
    http.expectOne('/api/v1/stats').flush(stats());
    expect((await all).data.totals.evses).toBe(13210);
    const vienna = service.load('AT-9');
    http.expectOne('/api/v1/stats?region=AT-9').flush(stats({ region: 'AT-9' }));
    expect((await vienna).data.region).toBe('AT-9');
    http.verify();
  });

  it('scales bars to the largest value', () => {
    expect(bars([{ key: 'a', label: 'A', value: 5 }, { key: 'b', label: 'B', value: 10 }]).map((bar) => bar.width)).toEqual([50, 100]);
    expect(bars([{ key: 'a', label: 'A', value: 0 }]).map((bar) => bar.width)).toEqual([0]);
    expect(bars([])).toEqual([]);
  });
});

describe('StatsPage', () => {
  let fixture: ComponentFixture<StatsPage>;
  let http: HttpTestingController;
  const page = () => fixture.nativeElement as HTMLElement;

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function chooseRegion(index: number): Promise<void> {
    page().querySelector<HTMLElement>('#stats-region .mat-mdc-select-trigger')!.click();
    await settle();
    document.querySelectorAll<HTMLElement>('mat-option')[index].click();
    await settle();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(StatsPage);
    await settle();
  });

  afterEach(() => http.verify());

  it('shows totals, bars, operators, status and freshness of all regions', async () => {
    expect(text(page(), '#stats-loading')).toBe('Statistik wird geladen …');
    http.expectOne('/api/v1/stats').flush(stats());
    await settle();

    expect(page().querySelector('#stats-loading')).toBeNull();
    expect(text(page(), '#tile-locations')).toBe('5.840 Standorte');
    expect(text(page(), '#tile-evses')).toBe('13.210 Ladepunkte');
    expect(text(page(), '#tile-connectors')).toBe('19.880 Stecker');
    expect(text(page(), '#tile-available')).toBe('6.000 gerade frei');

    const rows = (id: string) =>
      Array.from(page().querySelectorAll<HTMLElement>(`#${id} .bars li`)).map((row) => [text(row, '.bar-label'), text(row, '.bar-value')]);
    expect(rows('by-region')).toEqual([
      ['Niederösterreich', '3.050'],
      ['Wien', '6.100'],
    ]);
    expect(page().querySelector<HTMLElement>('#by-region .bar-fill')!.style.width).toBe('50%');
    expect(rows('by-power')).toEqual([
      ['bis 22 kW', '10.120'],
      ['23–49 kW', '310'],
      ['50–149 kW', '1.480'],
      ['ab 150 kW', '1.150'],
      ['unbekannt', '150'],
      ['NEW_CLASS', '0'],
    ]);
    expect(rows('by-connector')).toEqual([
      ['Typ 2', '12.400'],
      ['CCS (Combo 2)', '2.930'],
    ]);
    expect(rows('top-operators')).toEqual([['Wien Energie GmbH', '2.310']]);

    expect(Array.from(page().querySelectorAll('#status .status-legend li')).map((item) => item.textContent!.trim())).toEqual([
      'frei: 6.000',
      'belegt: 2.000',
      'außer Betrieb: 500',
      'unbekannt: 200',
      'keine aktuelle Meldung: 4.510',
    ]);
    expect(page().querySelector('#status .status-bar')!.getAttribute('aria-label')).toBe(
      'frei: 6.000, belegt: 2.000, außer Betrieb: 500, unbekannt: 200, keine aktuelle Meldung: 4.510',
    );
    expect(text(page(), '#freshness')).toBe('Alter der Statusmeldungen: Median 4,5 min, 90 % jünger als 12 min.');
    expect(text(page(), '#stats-generated')).toMatch(/^Stand 15\.10\.2026 \d\d:00$/);
    expect(text(page(), '#stats-attribution')).toBe('Datenquelle: E-Control');
  });

  it('counts one region, drops older answers and explains failures', async () => {
    http.expectOne('/api/v1/stats').flush(stats());
    await settle();

    // Two choices in a row: only the newer answer counts.
    await chooseRegion(2);
    await chooseRegion(1);
    const [older, newer] = [http.expectOne('/api/v1/stats?region=AT-9'), http.expectOne('/api/v1/stats?region=AT-3')];
    newer.flush(
      stats({
        region: 'AT-3',
        byRegion: [{ code: 'AT-3', name: 'Niederösterreich', locations: 0, evses: 0 }],
        byConnector: [],
        topOperators: [],
        status: { available: 0, occupied: 0, faulted: 0, unknown: 0, stale: 0 },
        freshness: { p50Minutes: null, p90Minutes: null },
      }),
    );
    await settle();
    older.flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();
    expect(page().querySelector('#stats-error')).toBeNull();
    // One region: no bars per region; empty lists say so.
    expect(page().querySelector('#by-region')).toBeNull();
    expect(text(page(), '#by-connector .bars li')).toBe('Keine Daten.');
    expect(text(page(), '#freshness')).toBe('Keine Statusmeldungen.');
    expect(page().querySelector<HTMLElement>('#status .status-bar span')!.style.width).toBe('0%');

    // Back to all regions: the selection keeps its regions; a failure is told.
    await chooseRegion(0);
    http.expectOne('/api/v1/stats').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await settle();
    expect(text(page(), '#stats-error')).toBe('Die Statistik konnte nicht geladen werden. Bitte versuche es später erneut.');
    expect(page().querySelector('#stats-loading')).toBeNull();
  });
});
