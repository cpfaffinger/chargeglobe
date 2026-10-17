import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminTariffsPage } from '../pages/admin-tariffs.page';
import { flush, text } from '../testing';
import { Emp, Network, Tariff, TariffsService } from './tariffs.service';

const emp = (overrides: Partial<Emp> = {}): Emp => ({ key: 'evn', name: 'EVN', website: 'https://www.evn.at', country: 'AT', tariffs: 2, ...overrides });

const network = (key: string, name: string, operators: string[]): Network => ({ key, name, country: 'AT', operators });

function tariff(overrides: Partial<Tariff> = {}): Tariff {
  return {
    id: 't-1',
    emp: 'evn',
    empName: 'EVN',
    key: 'drive-smart',
    name: '#Drive Smart',
    currency: 'EUR',
    monthlyFee: 4.95,
    prerequisites: 'EVN-Ladekarte',
    markets: ['AT'],
    zones: [
      {
        kind: 'HOME',
        networks: ['evn'],
        operators: [],
        excludedNetworks: [],
        excludedOperators: [],
        prices: [
          { current: 'AC', minKw: null, pricePerKwh: 0.45, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: 0.1, blockingAfterMinutes: 240 },
          { current: 'DC', minKw: null, pricePerKwh: 0.47, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: null, blockingAfterMinutes: null },
          { current: 'DC', minKw: 150, pricePerKwh: 0.49, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: 0.5, blockingAfterMinutes: null },
          { current: 'AC', minKw: null, pricePerKwh: 0.39, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: 0.5, blockingAfterMinutes: 30, startTime: '22:00', endTime: '08:00', maxBlockingFee: 30, blockingFrom: 'CHARGING_END' },
          { current: 'AC', minKw: null, pricePerKwh: 0.35, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: 0.07, blockingAfterMinutes: 210, days: [6, 7], blockingStartTime: '08:00', blockingEndTime: '23:00', maxBlockingMinutes: 500 },
        ],
        notes: null,
        pricing: 'FIXED',
        markupPercent: null,
        markupSessionFee: null,
      },
      {
        kind: 'ROAMING',
        networks: [],
        operators: [],
        excludedNetworks: ['tesla'],
        excludedOperators: ['AT*XYZ'],
        currents: ['DC'],
        prices: [{ current: 'DC', minKw: null, pricePerKwh: null, pricePerMinute: 0.5, sessionFee: 1, blockingFeePerMinute: null, blockingAfterMinutes: null }],
        notes: null,
        pricing: 'FIXED',
        markupPercent: null,
        markupSessionFee: null,
      },
      {
        kind: 'PARTNER',
        networks: ['unknown'],
        operators: ['AT*IOY'],
        excludedNetworks: [],
        excludedOperators: [],
        prices: [{ current: 'AC', minKw: null, pricePerKwh: 0.54, pricePerMinute: null, sessionFee: null, blockingFeePerMinute: null, blockingAfterMinutes: null }],
        notes: null,
        pricing: 'FIXED',
        markupPercent: null,
        markupSessionFee: null,
      },
      {
        kind: 'ROAMING',
        networks: [],
        operators: [],
        excludedNetworks: [],
        excludedOperators: [],
        prices: [],
        notes: null,
        pricing: 'OPERATOR_AD_HOC',
        markupPercent: -10,
        markupSessionFee: 0.39,
      },
    ],
    notes: 'Preisgarantie bis 31.12.2026.',
    sourceUrl: 'https://www.evn.at/preise.pdf',
    sourceDate: '2026-07-01',
    checkedOn: '2026-10-17',
    confidence: 'HIGH',
    validFrom: '2026-10-17T06:00:00Z',
    validTo: null,
    stale: false,
    ...overrides,
  };
}

describe('TariffsService', () => {
  let service: TariffsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(TariffsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads providers, networks and tariffs', async () => {
    const emps = service.emps();
    http.expectOne('/api/v1/emps').flush({ data: [emp()] });
    expect((await emps)[0].country).toBe('AT');

    const networks = service.networks();
    http.expectOne('/api/v1/networks').flush({ data: [network('evn', 'EVN', ['AT*EVN'])] });
    expect((await networks)[0].operators).toEqual(['AT*EVN']);

    const tariffs = service.tariffs();
    http.expectOne('/api/v1/tariffs').flush({ data: [tariff()] });
    expect((await tariffs)[0].zones.length).toBe(4);
  });
});

describe('AdminTariffsPage', () => {
  let service: jasmine.SpyObj<TariffsService>;
  let fixture: ComponentFixture<AdminTariffsPage>;

  const root = () => fixture.nativeElement as HTMLElement;

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminTariffsPage);
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<TariffsService>('TariffsService', ['emps', 'networks', 'tariffs']);
    service.emps.and.resolveTo([emp(), emp({ key: 'oeamtc', name: 'ÖAMTC ePower', website: null, tariffs: 1 })]);
    service.networks.and.resolveTo([network('evn', 'EVN', ['AT*EVN']), network('tesla', 'Tesla Supercharger', ['AT*TSL'])]);
    service.tariffs.and.resolveTo([
      tariff(),
      tariff({
        id: 't-2',
        key: 'drive-easy',
        name: '#Drive Easy',
        monthlyFee: null,
        prerequisites: null,
        notes: null,
        sourceDate: null,
        checkedOn: null,
        confidence: 'LOW',
        stale: true,
        zones: [
        { ...tariff().zones[0], excludedNetworks: [], countries: ['DE', 'IT'] },
        { ...tariff().zones[3], markupPercent: null, markupSessionFee: null, markupPerKwh: -0.25 },
        { ...tariff().zones[3], pricing: 'UNKNOWN', markupPercent: null, markupSessionFee: null },
      ],
      }),
      tariff({ id: 't-3', emp: 'oeamtc', key: 'epower', name: 'ePower', confidence: 'MEDIUM' }),
    ]);
    TestBed.configureTestingModule({ providers: [{ provide: TariffsService, useValue: service }] });
  });

  /** The roaming zone of drive-easy turned into one at the operator's price without markups. */
  function easyOperatorPrice(): string | undefined {
    return text(root().querySelector<HTMLElement>('[data-tariff="drive-easy"]')!, 'tbody tr:nth-child(2) td:nth-child(4)');
  }

  it('shows per provider its tariffs with zones, prices and source', async () => {
    await render();

    const panels = Array.from(root().querySelectorAll<HTMLElement>('.emp'));
    expect(panels.map((panel) => panel.dataset['emp'])).toEqual(['evn', 'oeamtc']);
    expect(text(panels[0], 'mat-panel-description')).toBe('2 Tarife · AT');
    expect(text(panels[1], 'mat-panel-description')).toBe('1 Tarif · AT');

    const smart = root().querySelector<HTMLElement>('[data-tariff="drive-smart"]')!;
    expect(text(smart, 'h2')).toBe('#Drive Smart');
    const meta = text(smart, '.tariff-meta')!;
    expect(meta).toContain('Grundgebühr 4,95');
    expect(meta).toContain('EVN-Ladekarte');
    expect(meta).toContain('Stand 01.07.2026 (aus dem Preisblatt) · geprüft 17.10.2026');
    expect(smart.querySelector<HTMLAnchorElement>('.tariff-meta a')!.href).toBe('https://www.evn.at/preise.pdf');
    const rows = Array.from(smart.querySelectorAll('tbody tr'));
    expect(rows.map((row) => text(row, 'td:nth-child(1)'))).toEqual(['Heimnetz', 'Roaming', 'Partnernetz', 'Roaming']);
    // At the operator's price: the markups instead of prices.
    expect(text(rows[3], 'td:nth-child(3)')).toBe('Preis des Betreibers −10 % + 0,39 € je Ladevorgang');
    expect(text(rows[0], '.covers')).toBe('EVN');
    expect(text(rows[0], 'td:nth-child(3)')).toBe('0,45 €/kWh, Blockieren 0,10 €/min ab Min. 240 · 22:00–08:00 Uhr 0,39 €/kWh, Blockieren 0,50 €/min ab Min. 30 nach Ladeende (max. 30,00 €) · Sa, So 0,35 €/kWh, Blockieren 0,07 €/min ab Min. 210 08:00–23:00 Uhr (max. 500 min)');
    expect(text(rows[0], 'td:nth-child(4)')).toBe('0,47 €/kWh · ab 150 kW 0,49 €/kWh, Blockieren 0,50 €/min');
    expect(text(rows[1], '.covers')).toBe('alle Betreiber mit Fremdkarten-Annahme – nur DC (außer Tesla Supercharger, AT*XYZ)');
    expect(text(rows[1], 'td:nth-child(3)')).toBe('–');
    expect(text(rows[1], 'td:nth-child(4)')).toBe('0,50 €/min, Start 1,00 €');
    expect(text(rows[2], '.covers')).toBe('unknown, AT*IOY');
    expect(easyOperatorPrice()).toBe('Preis des Betreibers −0,25 €/kWh');
    expect(text(root().querySelector<HTMLElement>('[data-tariff="drive-easy"]')!, 'tbody tr:nth-child(1) .covers')).toBe('EVN – nur in DE, IT');
    expect(text(root().querySelector<HTMLElement>('[data-tariff="drive-easy"]')!, 'tbody tr:nth-child(3) td:nth-child(3)')).toBe(
      'Preis je Ladepunkt, nicht veröffentlicht',
    );
    expect(text(smart, 'p.muted')).toBe('Preisgarantie bis 31.12.2026.');

    const easy = root().querySelector<HTMLElement>('[data-tariff="drive-easy"]')!;
    expect(text(easy, '.tariff-meta')).toContain('ohne Grundgebühr · Stand unbekannt (unsicher) Stand prüfen');
    expect(smart.querySelector('.stale')).toBeNull();
    expect(text(root(), '#stale-summary')).toBe('1 Tarif mit altem oder unbekanntem Stand – bitte mit dem Preisblatt des Anbieters vergleichen.');
    expect(easy.querySelector('p.muted')).toBeNull();
    expect(text(root().querySelector<HTMLElement>('[data-tariff="epower"]')!, '.tariff-meta')).toContain('(abgeleitet)');
    expect(root().querySelector('#no-emps')).toBeNull();
  });

  it('shows only the tariffs to check and sorts the longest unchecked first', async () => {
    await render();
    const shown = () => Array.from(root().querySelectorAll<HTMLElement>('.tariff')).map((section) => section.dataset['tariff']);
    expect(shown()).toEqual(['drive-smart', 'drive-easy', 'epower']);

    root().querySelector<HTMLInputElement>('#only-stale input')!.click();
    await fixture.whenStable();
    expect(shown()).toEqual(['drive-easy']);
    expect(Array.from(root().querySelectorAll<HTMLElement>('.emp')).map((panel) => panel.dataset['emp'])).toEqual(['evn']);

    // By the newer of "Stand" and last check: the ePower check of 01.10. before the one of 17.10.
    root().querySelector<HTMLInputElement>('#only-stale input')!.click();
    service.tariffs.and.resolveTo([tariff(), tariff({ id: 't-3', emp: 'oeamtc', key: 'epower', name: 'ePower', checkedOn: '2026-10-01' })]);
    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await flush();
    await fixture.whenStable();
    Array.from(root().querySelectorAll<HTMLElement>('#order mat-button-toggle'))[1].querySelector('button')!.click();
    await fixture.whenStable();
    expect(shown()).toEqual(['epower', 'drive-smart']);
    expect(Array.from(root().querySelectorAll<HTMLElement>('.emp')).map((panel) => panel.dataset['emp'])).toEqual(['oeamtc', 'evn']);

    // Nothing stale: the filter says so.
    root().querySelector<HTMLInputElement>('#only-stale input')!.click();
    await fixture.whenStable();
    expect(text(root(), '#no-emps')).toBe('Kein Tarif muss geprüft werden.');
  });

  it('reloads, reports errors and says when the catalog is empty', async () => {
    service.emps.and.rejectWith(new HttpErrorResponse({ status: 500, error: { title: 'Server kaputt' } }));
    await render();
    expect(root().querySelector('.error')).not.toBeNull();

    service.emps.and.resolveTo([]);
    service.tariffs.and.resolveTo([]);
    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await flush();
    await fixture.whenStable();
    expect(root().querySelector('.error')).toBeNull();
    expect(text(root(), '#no-emps')).toBe('Noch keine Tarife im Katalog.');
    expect(root().querySelector('#stale-summary')).toBeNull();
  });
});
