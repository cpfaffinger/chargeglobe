import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminPricesPage } from '../pages/admin-prices.page';
import { flush, text } from '../testing';
import { ImplausiblePrices, PriceQualityService, sampleText } from './price-quality.service';

const RESULT: ImplausiblePrices = {
  at: '2026-10-17T08:15:00Z',
  minKwh: 0.05,
  maxKwh: 2,
  evses: 20,
  operators: [
    {
      operatorId: 'AT*F9B',
      name: 'CAS Immobilien GmbH',
      evses: 16,
      examples: [{ pricePerKwh: 0.005, pricePerMinute: null, sessionFee: null, evses: 16 }],
      lastObservedAt: '2026-10-17T07:30:00Z',
    },
    { operatorId: null, name: null, evses: 4, examples: [{ pricePerKwh: 0, pricePerMinute: 0, sessionFee: 0, evses: 4 }], lastObservedAt: '2026-10-16T07:30:00Z' },
  ],
};

describe('PriceQualityService', () => {
  it('reads the implausible ad-hoc prices and describes a price', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const result = TestBed.inject(PriceQualityService).implausible();
    http.expectOne('/api/v1/admin/prices/implausible').flush({ data: RESULT });

    expect((await result).evses).toBe(20);
    expect(sampleText({ pricePerKwh: 0.005, pricePerMinute: 0.2, sessionFee: 0.5, evses: 1 })).toBe('0,005 €/kWh · 0,20 €/min · Start 0,50 €');
    expect(sampleText({ pricePerKwh: 0, pricePerMinute: null, sessionFee: 0, evses: 1 })).toBe('nichts berechnet');
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

  beforeEach(() => {
    service = jasmine.createSpyObj<PriceQualityService>('PriceQualityService', ['implausible']);
    TestBed.configureTestingModule({ providers: [{ provide: PriceQualityService, useValue: service }] });
  });

  it('lists the operators with their implausible prices, most charge points first', async () => {
    service.implausible.and.resolveTo(RESULT);
    await render();

    expect(text(root(), '#prices-summary')).toMatch(/^Spanne 0,05–2,00 €\/kWh: 20 Ladepunkte bei 2 Betreibern · Stand 17\.10\.2026 \d\d:15$/);
    const rows = Array.from(root().querySelectorAll('#implausible tbody tr'));
    expect(rows.map((row) => text(row, 'td:first-child'))).toEqual(['AT*F9B · CAS Immobilien GmbH', 'unbekannt']);
    expect(rows.map((row) => text(row, 'td:nth-child(2)'))).toEqual(['16', '4']);
    expect(text(rows[0], 'td:nth-child(3)')).toBe('0,005 €/kWh (16)');
    expect(text(rows[1], 'td:nth-child(3)')).toBe('nichts berechnet (4)');
    expect(root().querySelector('#no-implausible')).toBeNull();
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
