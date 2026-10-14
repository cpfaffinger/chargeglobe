import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { OperatorSummary, OperatorsService } from '../map/operators.service';
import { AdminTariffsPage, EMP_KEY, dateInput, isoDate } from '../pages/admin-tariffs.page';
import { flush, text, type } from '../testing';
import { Emp, Tariff, TariffsService } from './tariffs.service';

const emp = (overrides: Partial<Emp> = {}): Emp => ({ key: 'smatrics', name: 'SMATRICS', website: 'https://smatrics.com', tariffs: 1, ...overrides });

function tariff(overrides: Partial<Tariff> = {}): Tariff {
  return {
    id: 't-1',
    emp: 'smatrics',
    name: 'Basic',
    currency: 'EUR',
    validFrom: '2026-01-01T00:00:00Z',
    validTo: null,
    pricePerKwhAc: 0.45,
    pricePerKwhDc: 0.65,
    pricePerMinute: null,
    sessionFee: null,
    blockingFeePerMinute: null,
    blockingAfterMinutes: null,
    monthlyFee: null,
    roamingOnly: true,
    operatorIds: [],
    ...overrides,
  };
}

const operator = (id: string, name: string, operatorId: string | null = null): OperatorSummary => ({ id, name, operatorId, locations: 10 });

describe('TariffsService', () => {
  let service: TariffsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(TariffsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('calls the EMP and tariff endpoints', async () => {
    const emps = service.emps();
    http.expectOne('/api/v1/emps').flush({ data: [emp()] });
    expect((await emps)[0].key).toBe('smatrics');

    const put = service.putEmp('öamtc', 'ÖAMTC', null);
    const putRequest = http.expectOne('/api/v1/emps/%C3%B6amtc');
    expect([putRequest.request.method, putRequest.request.body]).toEqual(['PUT', { name: 'ÖAMTC', website: null }]);
    putRequest.flush({ data: emp({ key: 'oeamtc' }) });
    expect((await put).key).toBe('oeamtc');

    const removed = service.deleteEmp('smatrics');
    const deleteRequest = http.expectOne('/api/v1/emps/smatrics');
    expect(deleteRequest.request.method).toBe('DELETE');
    deleteRequest.flush(null);
    await removed;

    const tariffs = service.tariffs();
    http.expectOne('/api/v1/tariffs').flush({ data: [tariff()] });
    expect((await tariffs)[0].id).toBe('t-1');

    const { id, ...input } = tariff();
    expect(id).toBe('t-1');
    const stored = service.putTariff('t-2', input);
    const tariffRequest = http.expectOne('/api/v1/tariffs/t-2');
    expect([tariffRequest.request.method, tariffRequest.request.body]).toEqual(['PUT', input]);
    tariffRequest.flush({ data: tariff({ id: 't-2' }) });
    expect((await stored).id).toBe('t-2');

    const deleted = service.deleteTariff('t-2');
    const deleteTariffRequest = http.expectOne('/api/v1/tariffs/t-2');
    expect(deleteTariffRequest.request.method).toBe('DELETE');
    deleteTariffRequest.flush(null);
    await deleted;
  });
});

describe('date fields of the tariff form', () => {
  it('turn ISO times into local days and back', () => {
    expect(dateInput(null)).toBe('');
    expect(dateInput(new Date(2026, 0, 31, 12).toISOString())).toBe('2026-01-31');
    expect(isoDate('')).toBeNull();
    expect(isoDate('2026-01-31')).toBe(new Date(2026, 0, 31).toISOString());
    expect(EMP_KEY.test('oeamtc-epower')).toBeTrue();
    expect(EMP_KEY.test('Ö')).toBeFalse();
  });
});

describe('AdminTariffsPage', () => {
  let service: jasmine.SpyObj<TariffsService>;
  let operators: jasmine.SpyObj<OperatorsService>;
  let snackBar: jasmine.SpyObj<MatSnackBar>;
  let fixture: ComponentFixture<AdminTariffsPage>;

  const root = () => fixture.nativeElement as HTMLElement;
  const form = (id: string) => root().querySelector<HTMLFormElement>(`#${id}`)!;
  const row = (selector: string) => root().querySelector<HTMLElement>(selector)!;
  const submit = (element: HTMLFormElement) => element.dispatchEvent(new Event('submit'));

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminTariffsPage);
    await settle();
  }

  function select(selector: string, values: string[]): void {
    const element = root().querySelector<HTMLSelectElement>(selector)!;
    for (const option of Array.from(element.options)) {
      option.selected = values.includes(option.value);
    }
    element.dispatchEvent(new Event('change'));
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<TariffsService>('TariffsService', ['emps', 'putEmp', 'deleteEmp', 'tariffs', 'putTariff', 'deleteTariff']);
    service.emps.and.resolveTo([emp(), emp({ key: 'oeamtc', name: 'ÖAMTC', website: null, tariffs: 0 })]);
    service.tariffs.and.resolveTo([
      tariff(),
      tariff({
        id: 't-2',
        emp: 'gone',
        name: 'Netz',
        validTo: '2027-01-01T00:00:00Z',
        pricePerKwhAc: null,
        pricePerMinute: 0.02,
        sessionFee: 1,
        blockingFeePerMinute: 0.1,
        blockingAfterMinutes: 60,
        monthlyFee: 4.9,
        roamingOnly: false,
        operatorIds: ['op-1', 'op-9'],
      }),
      tariff({ id: 't-3', name: 'Überall', roamingOnly: false, pricePerKwhDc: null }),
    ]);
    service.putEmp.and.resolveTo(emp());
    service.deleteEmp.and.resolveTo();
    service.putTariff.and.resolveTo(tariff());
    service.deleteTariff.and.resolveTo();
    operators = jasmine.createSpyObj<OperatorsService>('OperatorsService', ['search', 'byIds']);
    operators.search.and.resolveTo([operator('op-1', 'Wien Energie', 'VIE'), operator('op-2', 'EVN')]);
    operators.byIds.and.resolveTo([operator('op-9', 'Kleiner Betreiber')]);
    snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    TestBed.configureTestingModule({
      providers: [
        { provide: TariffsService, useValue: service },
        { provide: OperatorsService, useValue: operators },
        { provide: MatSnackBar, useValue: snackBar },
      ],
    });
  });

  it('lists the EMPs and the tariffs with prices and where they apply', async () => {
    await render();

    expect(operators.search).toHaveBeenCalledWith('', 100);
    expect(operators.byIds).toHaveBeenCalledWith(['op-9']);
    expect(Array.from(root().querySelectorAll('#emps tbody tr')).map((item) => text(item, 'td:nth-child(2)'))).toEqual(['SMATRICS', 'ÖAMTC']);
    expect(text(row('[data-emp="oeamtc"]'), 'td:nth-child(3)')).toBe('–');
    // Only EMPs without tariffs can be deleted.
    expect(row('[data-emp="smatrics"]').querySelector('.delete')).toBeNull();

    const basic = row('[data-tariff="t-1"]');
    expect(text(basic, 'td:nth-child(1)')).toBe('SMATRICS');
    expect(text(basic, 'td:nth-child(3)')).toBe('01.01.2026 – offen');
    expect(text(basic, 'td:nth-child(4)')).toBe('0,45 €');
    expect(text(basic, 'td:nth-child(6)')).toBe('–');
    expect(text(basic, '.scope')).toBe('Roaming');
    const network = row('[data-tariff="t-2"]');
    expect(text(network, 'td:nth-child(1)')).toBe('gone');
    expect(text(network, 'td:nth-child(3)')).toBe('01.01.2026 – 01.01.2027');
    expect(text(network, 'td:nth-child(4)')).toBe('–');
    expect(text(network, 'td:nth-child(6)')).toBe('0,02 €/min · Start 1,00 € · Blockieren 0,10 €/min ab 60');
    expect(text(network, 'td:nth-child(7)')).toBe('4,90 €');
    expect(text(network, '.scope')).toBe('nur Wien Energie, Kleiner Betreiber');
    expect(text(row('[data-tariff="t-3"]'), '.scope')).toBe('überall');
    expect(Array.from(root().querySelectorAll<HTMLOptionElement>('#tariff-operators option')).map((option) => option.textContent)).toEqual([
      'Wien Energie (VIE)',
      'EVN',
      'Kleiner Betreiber',
    ]);
  });

  it('shows empty tables and skips unknown operators when nothing exists yet', async () => {
    service.emps.and.resolveTo([]);
    service.tariffs.and.resolveTo([]);
    await render();

    expect(text(root(), '#emps tbody')).toBe('Noch keine Anbieter.');
    expect(text(root(), '#tariffs tbody')).toBe('Noch keine Tarife.');
    expect(operators.byIds).not.toHaveBeenCalled();
  });

  it('creates, edits and deletes EMPs', async () => {
    await render();

    // An invalid key only shows the field error.
    type(form('emp-form'), '#emp-key', 'Ö');
    type(form('emp-form'), '#emp-name', 'ÖAMTC');
    submit(form('emp-form'));
    await settle();
    expect(service.putEmp).not.toHaveBeenCalled();

    type(form('emp-form'), '#emp-key', 'oeamtc');
    type(form('emp-form'), '#emp-website', ' https://oeamtc.at ');
    submit(form('emp-form'));
    await settle();
    expect(service.putEmp).toHaveBeenCalledWith('oeamtc', 'ÖAMTC', 'https://oeamtc.at');
    expect(snackBar.open).toHaveBeenCalledWith('Anbieter ÖAMTC gespeichert.', 'OK', { duration: 4000 });
    expect(service.emps).toHaveBeenCalledTimes(2);

    row('[data-emp="smatrics"]').querySelector<HTMLButtonElement>('.edit')!.click();
    await settle();
    expect(root().querySelector<HTMLInputElement>('#emp-key')!.readOnly).toBeTrue();
    expect(root().querySelector<HTMLInputElement>('#emp-website')!.value).toBe('https://smatrics.com');
    expect(text(form('emp-form'), 'button[type="submit"]')).toBe('Anbieter speichern');
    root().querySelector<HTMLButtonElement>('#emp-cancel')!.click();
    await settle();
    expect(root().querySelector<HTMLInputElement>('#emp-key')!.value).toBe('');

    row('[data-emp="oeamtc"]').querySelector<HTMLButtonElement>('.edit')!.click();
    await settle();
    type(form('emp-form'), '#emp-website', '');
    submit(form('emp-form'));
    await settle();
    expect(service.putEmp).toHaveBeenCalledWith('oeamtc', 'ÖAMTC', null);

    row('[data-emp="oeamtc"]').querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(service.deleteEmp).toHaveBeenCalledWith('oeamtc');
    expect(snackBar.open).toHaveBeenCalledWith('Anbieter ÖAMTC gelöscht.', 'OK', { duration: 4000 });
  });

  it('creates a tariff with a new id, edits and deletes tariffs', async () => {
    spyOn(crypto, 'randomUUID').and.returnValue('00000000-0000-4000-8000-000000000001');
    await render();

    select('#tariff-emp', ['oeamtc']);
    type(form('tariff-form'), '#tariff-name', ' Standard ');
    type(form('tariff-form'), '#tariff-currency', 'eur');
    type(form('tariff-form'), '#tariff-valid-from', '2026-02-01');
    type(form('tariff-form'), '#tariff-pricePerKwhAc', '0.55');
    type(form('tariff-form'), '#tariff-pricePerKwhDc', '0.79');
    type(form('tariff-form'), '#tariff-monthlyFee', '4.9');
    type(form('tariff-form'), '#tariff-blockingFeePerMinute', '0.1');
    type(form('tariff-form'), '#tariff-blockingAfterMinutes', '180');
    select('#tariff-operators', ['op-1', 'op-2']);
    submit(form('tariff-form'));
    await settle();

    expect(service.putTariff).toHaveBeenCalledWith('00000000-0000-4000-8000-000000000001', {
      emp: 'oeamtc',
      name: 'Standard',
      currency: 'EUR',
      validFrom: new Date(2026, 1, 1).toISOString(),
      validTo: null,
      pricePerKwhAc: 0.55,
      pricePerKwhDc: 0.79,
      pricePerMinute: null,
      sessionFee: null,
      blockingFeePerMinute: 0.1,
      blockingAfterMinutes: 180,
      monthlyFee: 4.9,
      roamingOnly: false,
      operatorIds: ['op-1', 'op-2'],
    });
    expect(snackBar.open).toHaveBeenCalledWith('Tarif Standard gespeichert.', 'OK', { duration: 4000 });

    row('[data-tariff="t-2"]').querySelector<HTMLButtonElement>('.edit')!.click();
    await settle();
    expect(root().querySelector<HTMLInputElement>('#tariff-valid-to')!.value).toBe(dateInput('2027-01-01T00:00:00Z'));
    expect(text(form('tariff-form'), 'button[type="submit"]')).toBe('Tarif speichern');
    root().querySelector<HTMLButtonElement>('#tariff-cancel')!.click();
    await settle();
    expect(root().querySelector<HTMLInputElement>('#tariff-name')!.value).toBe('');

    row('[data-tariff="t-1"]').querySelector<HTMLButtonElement>('.edit')!.click();
    await settle();
    type(form('tariff-form'), '#tariff-name', 'Basic 2027');
    submit(form('tariff-form'));
    await settle();
    expect(service.putTariff.calls.mostRecent().args[0]).toBe('t-1');
    expect(service.putTariff.calls.mostRecent().args[1].name).toBe('Basic 2027');

    row('[data-tariff="t-3"]').querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(service.deleteTariff).toHaveBeenCalledWith('t-3');
    expect(snackBar.open).toHaveBeenCalledWith('Tarif Überall gelöscht.', 'OK', { duration: 4000 });
  });

  it('sends nothing for an invalid tariff and shows the errors of the API', async () => {
    await render();

    submit(form('tariff-form'));
    await settle();
    expect(service.putTariff).not.toHaveBeenCalled();

    service.putTariff.and.rejectWith(
      new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { operatorIds: ['Must be operator ids from listOperators.'] } } }),
    );
    select('#tariff-emp', ['smatrics']);
    type(form('tariff-form'), '#tariff-name', 'Netz');
    submit(form('tariff-form'));
    await settle();
    expect(text(root(), '.error')).toBe('Must be operator ids from listOperators.');
  });
});
