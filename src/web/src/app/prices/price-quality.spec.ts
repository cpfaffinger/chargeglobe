import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminPricesPage } from '../pages/admin-prices.page';
import { AuthService } from '../auth/auth.service';
import { fakeAuth, flush, text } from '../testing';
import { ImplausiblePrices, PriceQualityService, sampleText } from './price-quality.service';

const RESULT: ImplausiblePrices = {
  at: '2026-10-17T08:15:00Z',
  minKwh: 0.05,
  maxKwh: 2,
  evses: 20,
  noAmount: 4,
  belowMin: 16,
  aboveMax: 0,
  operators: [
    {
      operatorId: 'AT*F9B',
      name: 'CAS Immobilien GmbH',
      evses: 16,
      examples: [{ pricePerKwh: 0.005, pricePerMinute: null, sessionFee: null, evses: 16, reason: 'BELOW_MIN', intendedPricePerKwh: 0.5 }],
      lastObservedAt: '2026-10-17T07:30:00Z',
    },
    { operatorId: null, name: null, evses: 4, examples: [{ pricePerKwh: 0, pricePerMinute: 0, sessionFee: 0, evses: 4, reason: 'NO_AMOUNT' }], lastObservedAt: '2026-10-16T07:30:00Z' },
  ],
};

describe('PriceQualityService', () => {
  it('reads the implausible ad-hoc prices and describes a price', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const result = TestBed.inject(PriceQualityService).implausible();
    http.expectOne('/api/v1/admin/prices/implausible').flush({ data: RESULT });

    expect((await result).evses).toBe(20);
    expect(sampleText({ pricePerKwh: 0.005, pricePerMinute: 0.2, sessionFee: 0.5, evses: 1, reason: 'BELOW_MIN' }).replace(/\u00a0/g, ' ')).toBe(
      '0,005 €/kWh · 0,20 €/min · Start 0,50 € – unter der Spanne',
    );
    expect(sampleText({ pricePerKwh: 0, pricePerMinute: null, sessionFee: 0, evses: 1, reason: 'NO_AMOUNT' })).toBe('nichts berechnet – kein Preis gemeldet');

    const csv = TestBed.inject(PriceQualityService).export();
    http.expectOne('/api/v1/admin/prices/implausible/export').flush(new Blob(['operator_id\n']));
    expect((await csv).size).toBe(12);
    http.verify();
  });
});

describe('AdminPricesPage', () => {
  let service: jasmine.SpyObj<PriceQualityService>;
  let fixture: ComponentFixture<AdminPricesPage>;

  const root = () => fixture.nativeElement as HTMLElement;

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminPricesPage);
    await flush();
    await fixture.whenStable();
  }

  let auth: ReturnType<typeof fakeAuth>;

  beforeEach(() => {
    service = jasmine.createSpyObj<PriceQualityService>('PriceQualityService', ['implausible', 'export']);
    auth = fakeAuth();
    auth.hasPermission.and.callFake((permission) => permission === 'jobs:read');
    TestBed.configureTestingModule({ providers: [{ provide: PriceQualityService, useValue: service }, { provide: AuthService, useValue: auth }] });
  });

  it('saves the CSV report with export:read', async () => {
    auth.hasPermission.and.returnValue(true);
    service.implausible.and.resolveTo(RESULT);
    service.export.and.resolveTo(new Blob(['operator_id\n']));
    const created = spyOn(URL, 'createObjectURL').and.returnValue('blob:report');
    const revoked = spyOn(URL, 'revokeObjectURL');
    const clicked = spyOn(HTMLAnchorElement.prototype, 'click');
    await render();

    root().querySelector<HTMLButtonElement>('#export')!.click();
    await flush();
    await fixture.whenStable();

    expect(service.export).toHaveBeenCalled();
    expect(created).toHaveBeenCalled();
    expect(clicked).toHaveBeenCalled();
    expect(revoked).toHaveBeenCalledWith('blob:report');
  });

  it('lists the operators with their implausible prices, most charge points first', async () => {
    service.implausible.and.resolveTo(RESULT);
    await render();

    expect(text(root(), '#prices-summary')).toMatch(/^Spanne 0,05–2,00 €\/kWh: 20 Ladepunkte bei 2 Betreibern · Stand 17\.10\.2026 \d\d:15$/);
    const rows = Array.from(root().querySelectorAll('#implausible tbody tr'));
    expect(rows.map((row) => text(row, 'td:first-child'))).toEqual(['AT*F9B · CAS Immobilien GmbH', 'unbekannt']);
    expect(rows.map((row) => text(row, 'td:nth-child(2)'))).toEqual(['16', '4']);
    expect(text(rows[0], 'td:nth-child(3)')).toBe('0,005 €/kWh – unter der Spanne, vermutlich 0,50 €/kWh (16)');
    expect(text(rows[1], 'td:nth-child(3)')).toBe('nichts berechnet – kein Preis gemeldet (4)');
    expect(text(root(), '#prices-reasons')).toBe('kein Preis gemeldet: 4 · unter der Spanne: 16 · über der Spanne: 0');
    expect(root().querySelector('#export')).toBeNull();
    expect(root().querySelector('#no-implausible')).toBeNull();
  });

  it('opens a mail draft per operator to copy, sending nothing', async () => {
    service.implausible.and.resolveTo(RESULT);
    const write = spyOn(navigator.clipboard, 'writeText').and.resolveTo();
    await render();

    const toggle = () => root().querySelector<HTMLButtonElement>('[data-operator="AT*F9B"] .draft-toggle')!;
    toggle().click();
    await fixture.whenStable();
    const raw = root().querySelector<HTMLTextAreaElement>('.mail-draft')!.value;
    const draft = raw.replace(/\u00a0/g, ' ');
    expect(draft).toContain('Betreff: Ad-hoc-Preise im Ladestellenverzeichnis – CAS Immobilien GmbH (AT*F9B)');
    expect(draft).toContain('für 16 Ladepunkte von CAS Immobilien GmbH (AT*F9B)');
    expect(draft).toContain('- 0,005 €/kWh – unter der Spanne, vermutlich 0,50 €/kWh (16 Ladepunkte)');
    expect(toggle().textContent!.trim()).toBe('Entwurf schließen');

    root().querySelector<HTMLButtonElement>('.copy-draft')!.click();
    await flush();
    await fixture.whenStable();
    expect(write).toHaveBeenCalledWith(raw);
    expect(text(root(), '.copied')).toBe('kopiert');

    // Another operator's draft (one EVSE, without name), then closed again.
    root().querySelectorAll<HTMLButtonElement>('.draft-toggle')[1].click();
    await fixture.whenStable();
    expect(root().querySelector<HTMLTextAreaElement>('.mail-draft')!.value).toContain('für 4 Ladepunkte von Betreiber Ad-hoc-Preise');
    expect(root().querySelector('.copied')).toBeNull();
    root().querySelectorAll<HTMLButtonElement>('.draft-toggle')[1].click();
    await fixture.whenStable();
    expect(root().querySelector('.mail-draft')).toBeNull();
  });

  it('says when every price is plausible, reloads and reports errors', async () => {
    service.implausible.and.resolveTo({ ...RESULT, evses: 1, operators: [{ ...RESULT.operators[0], evses: 1 }] });
    await render();
    expect(text(root(), '#prices-summary')).toContain('1 Ladepunkt bei 1 Betreiber');

    service.implausible.and.resolveTo({ ...RESULT, evses: 0, operators: [] });
    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await flush();
    await fixture.whenStable();
    expect(text(root(), '#no-implausible')).toBe('Keine unplausiblen Ad-hoc-Preise.');

    service.implausible.and.rejectWith(new HttpErrorResponse({ status: 500, error: { title: 'Server kaputt' } }));
    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await flush();
    await fixture.whenStable();
    expect(root().querySelector('.error')).not.toBeNull();
  });
});
