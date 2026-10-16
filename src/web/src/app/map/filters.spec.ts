import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DOCUMENT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { flush, text, type } from '../testing';
import { MapFilterPanel, OPERATOR_DELAY_MS } from './map-filter-panel';
import { FILTERS_STORAGE_KEY, MapFilterStore, MapFilters, NO_FILTERS, countFilters, filterParams, parseFilters } from './map-filters';
import { MapLink } from './map-link';
import { OperatorSummary, OperatorsService } from './operators.service';

const ALL: MapFilters = {
  connector: ['IEC_62196_T2_COMBO', 'CHADEMO'],
  minPowerKw: 50,
  maxPowerKw: 150,
  operatorId: ['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40'],
  availableOnly: true,
  adHoc: true,
  roaming: true,
  greenEnergy: true,
  barrierFree: true,
};

describe('map filters', () => {
  it('turn into query parameters and back, dropping unknown values', () => {
    const params = filterParams(ALL);
    expect(params).toEqual({
      connector: ['IEC_62196_T2_COMBO', 'CHADEMO'],
      minPowerKw: '50',
      maxPowerKw: '150',
      operatorId: ['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40'],
      availableOnly: 'true',
      adHoc: 'true',
      roaming: 'true',
      greenEnergy: 'true',
      barrierFree: 'true',
    });
    expect(parseFilters(params)).toEqual({ ...ALL, connector: ['IEC_62196_T2_COMBO', 'CHADEMO'] });
    expect(countFilters(ALL)).toBe(9);
    expect(filterParams(NO_FILTERS)).toEqual({});

    expect(parseFilters({ connector: 'CHADEMO,FOO', minPowerKw: '0', operatorId: 'nope,01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40,01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40', roaming: 'yes', adHoc: true })).toEqual({
      ...NO_FILTERS,
      connector: ['CHADEMO'],
      operatorId: ['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40'],
      adHoc: true,
    });
    expect(parseFilters({ minPowerKw: '2000' }).minPowerKw).toBeNull();
    // A maximum below the minimum is dropped.
    expect(parseFilters({ minPowerKw: '150', maxPowerKw: '50' })).toEqual({ ...NO_FILTERS, minPowerKw: 150 });
    expect(parseFilters({ maxPowerKw: '22' })).toEqual({ ...NO_FILTERS, maxPowerKw: 22 });
  });
});

describe('MapFilterStore', () => {
  let navigate: jasmine.Spy;
  let fragment: string | undefined;

  function store(query: Record<string, string> = {}): MapFilterStore {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: query } } },
        { provide: MapLink, useValue: { fragment: () => fragment } },
      ],
    });
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    return TestBed.inject(MapFilterStore);
  }

  beforeEach(() => {
    localStorage.removeItem(FILTERS_STORAGE_KEY);
    fragment = undefined;
  });

  afterEach(() => localStorage.removeItem(FILTERS_STORAGE_KEY));

  it('starts with the filters of a link, else with the stored ones, else without', () => {
    localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify({ greenEnergy: 'true' }));
    expect(store({ connector: 'CHADEMO' }).filters()).toEqual({ ...NO_FILTERS, connector: ['CHADEMO'] });
  });

  it('takes filters from elsewhere without touching the address and knows whether a link brought filters', () => {
    const filters = store();
    expect(filters.fromLink).toBeFalse();

    filters.apply({ ...NO_FILTERS, adHoc: true });

    expect(filters.filters().adHoc).toBeTrue();
    expect(JSON.parse(localStorage.getItem(FILTERS_STORAGE_KEY)!)).toEqual({ adHoc: 'true' });
    expect(navigate).not.toHaveBeenCalled();
  });

  it('knows that a link brought filters', () => {
    expect(store({ greenEnergy: 'true' }).fromLink).toBeTrue();
  });

  it('starts with the stored filters without filters in the link', () => {
    localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify({ greenEnergy: 'true' }));
    const filters = store();
    expect(filters.filters()).toEqual({ ...NO_FILTERS, greenEnergy: true });
    expect(filters.count()).toBe(1);
  });

  it('starts without filters when the storage is unreadable', () => {
    localStorage.setItem(FILTERS_STORAGE_KEY, '{');
    expect(store().filters()).toEqual(NO_FILTERS);
  });

  it('keeps every change in the storage and in the address, with the view fragment', () => {
    const filters = store();
    fragment = 'map=12.00/48.20000/16.40000';

    filters.set({ ...NO_FILTERS, minPowerKw: 50, roaming: true });

    expect(filters.count()).toBe(2);
    expect(JSON.parse(localStorage.getItem(FILTERS_STORAGE_KEY)!)).toEqual({ minPowerKw: '50', roaming: 'true' });
    const [commands, extras] = navigate.calls.mostRecent().args;
    expect(commands).toEqual([]);
    expect(extras.queryParams).toEqual(jasmine.objectContaining({ minPowerKw: '50', roaming: 'true', connector: null, maxPowerKw: null, operatorId: null, adHoc: null }));
    expect(extras).toEqual(jasmine.objectContaining({ queryParamsHandling: 'merge', fragment, replaceUrl: true }));
  });

  it('works without storage', () => {
    spyOn(Storage.prototype, 'getItem').and.throwError('blocked');
    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
    const filters = store();

    filters.set({ ...NO_FILTERS, adHoc: true });

    expect(filters.filters().adHoc).toBeTrue();
  });
});

describe('MapLink', () => {
  let document: Document;
  let link: MapLink;
  let hash: string;
  let navigator: { share?: jasmine.Spy; clipboard: { writeText: jasmine.Spy } };
  let replaceState: jasmine.Spy;

  beforeEach(() => {
    hash = '';
    navigator = { clipboard: { writeText: jasmine.createSpy('writeText').and.resolveTo() } };
    replaceState = jasmine.createSpy('replaceState');
    document = {
      location: {
        get hash() {
          return hash;
        },
        pathname: '/',
        search: '?roaming=true',
        href: 'https://chargeglobe.net/?roaming=true#map=12.00/48.20000/16.40000',
      },
      defaultView: { navigator, history: { state: { id: 1 }, replaceState } },
    } as unknown as Document;
    TestBed.configureTestingModule({ providers: [{ provide: DOCUMENT, useValue: document }] });
    link = TestBed.inject(MapLink);
  });

  it('reads and writes the view as #map=zoom/latitude/longitude', () => {
    expect(link.read()).toBeNull();
    expect(link.fragment()).toBeUndefined();

    hash = '#map=12.5/48.2085/-16.3731';
    expect(link.read()).toEqual({ latitude: 48.2085, longitude: -16.3731, zoom: 12.5 });
    expect(link.fragment()).toBe('map=12.5/48.2085/-16.3731');

    for (const invalid of ['#map=25/48/16', '#map=10/91/16', '#map=10/48/181', '#map=a/48/16', '#other']) {
      hash = invalid;
      expect(link.read()).withContext(invalid).toBeNull();
    }

    link.write({ latitude: 48.20851234, longitude: 16.37314, zoom: 13.456 });
    expect(replaceState).toHaveBeenCalledOnceWith({ id: 1 }, '', '/?roaming=true#map=13.46/48.20851/16.37314');
  });

  it('shares with the share sheet, falls back to the clipboard and reports failures', async () => {
    expect(await link.share()).toBe('copied');
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith('https://chargeglobe.net/?roaming=true#map=12.00/48.20000/16.40000');

    navigator.share = jasmine.createSpy('share').and.resolveTo();
    expect(await link.share()).toBe('shared');
    expect(navigator.share).toHaveBeenCalledWith({ title: 'chargeglobe', url: 'https://chargeglobe.net/?roaming=true#map=12.00/48.20000/16.40000' });

    navigator.share.and.rejectWith(new DOMException('cancelled', 'AbortError'));
    expect(await link.share()).toBe('cancelled');

    navigator.share.and.rejectWith(new DOMException('not allowed', 'NotAllowedError'));
    navigator.clipboard.writeText.and.rejectWith(new Error('denied'));
    expect(await link.share()).toBe('failed');
  });
});

describe('MapFilterPanel', () => {
  let fixture: ComponentFixture<MapFilterPanel>;
  let filters: MapFilterStore;
  let set: jasmine.Spy;
  let operators: jasmine.SpyObj<OperatorsService>;

  const root = () => fixture.nativeElement as HTMLElement;
  const chip = (group: string, label: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`#${group} mat-chip-option`)).find((option) => text(option, '.mdc-evolution-chip__text-label') === label)!;
  const options = () => Array.from(document.querySelectorAll<HTMLElement>('mat-option'));
  const wien: OperatorSummary = { id: '01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40', name: 'Wien Energie GmbH', operatorId: 'VIE', locations: 1124 };
  const evn: OperatorSummary = { id: '01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c41', name: 'EVN', operatorId: 'EVN', locations: 1 };

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function click(element: HTMLElement): Promise<void> {
    element.querySelector<HTMLElement>('button')!.click();
    await settle();
  }

  async function render(): Promise<void> {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: OperatorsService, useValue: operators }] });
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    filters = TestBed.inject(MapFilterStore);
    set = spyOn(filters, 'set').and.callThrough();
    fixture = TestBed.createComponent(MapFilterPanel);
    await settle();
  }

  /** Focus on the search field: the panel loads the operators and the autocomplete opens. */
  function openOperators(): void {
    const input = root().querySelector<HTMLInputElement>('#filter-operator')!;
    input.dispatchEvent(new Event('focus'));
    input.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
  }

  async function slide(thumb: 'power-min' | 'power-max', stop: number): Promise<void> {
    const input = root().querySelector<HTMLInputElement>(`#${thumb}`)!;
    input.value = String(stop);
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
    await settle();
  }

  beforeEach(() => {
    localStorage.removeItem(FILTERS_STORAGE_KEY);
    operators = jasmine.createSpyObj<OperatorsService>('OperatorsService', ['search', 'byIds']);
    operators.search.and.resolveTo([wien, evn]);
  });

  afterEach(() => {
    fixture.destroy();
    localStorage.removeItem(FILTERS_STORAGE_KEY);
  });

  it('applies connectors, the power range and properties at once and resets them', async () => {
    await render();
    expect(root().querySelector<HTMLButtonElement>('#reset-filters')!.disabled).toBeTrue();
    expect(text(root(), '#power-range')).toBe('beliebig');
    expect(operators.byIds).not.toHaveBeenCalled();

    await click(chip('filter-connector', 'CCS'));
    await click(chip('filter-connector', 'CHAdeMO'));
    expect(filters.filters().connector).toEqual(['IEC_62196_T2_COMBO', 'CHADEMO']);

    await slide('power-min', 4);
    expect(filters.filters()).toEqual(jasmine.objectContaining({ minPowerKw: 50, maxPowerKw: null }));
    expect(text(root(), '#power-range')).toBe('ab 50 kW');
    await slide('power-max', 6);
    expect(text(root(), '#power-range')).toBe('50 kW bis 150 kW');
    await slide('power-min', 0);
    expect(filters.filters()).toEqual(jasmine.objectContaining({ minPowerKw: null, maxPowerKw: 150 }));
    expect(text(root(), '#power-range')).toBe('bis 150 kW');
    await slide('power-max', 8);
    expect(text(root(), '#power-range')).toBe('beliebig');

    await click(chip('filter-flags', 'Ad-hoc-Zahlung'));
    await click(chip('filter-flags', 'Ökostrom'));
    expect(filters.filters()).toEqual(jasmine.objectContaining({ adHoc: true, greenEnergy: true, roaming: false }));
    expect(filters.count()).toBe(3);

    root().querySelector<HTMLButtonElement>('#reset-filters')!.click();
    await settle();
    expect(filters.filters()).toEqual(NO_FILTERS);

    const closed: unknown[] = [];
    fixture.componentInstance.closed.subscribe(() => closed.push(true));
    root().querySelector<HTMLButtonElement>('#apply-filters')!.click();
    root().querySelector<HTMLButtonElement>('#close-filters')!.click();
    expect(closed.length).toBe(2);
  });

  it('places a saved power range on the stops of the slider', async () => {
    localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify({ minPowerKw: '3.7', maxPowerKw: '500' }));
    await render();

    expect(text(root(), '#power-range')).toBe('3,7 kW bis 500 kW');
    expect(root().querySelector<HTMLInputElement>('#power-min')!.value).toBe('1');
    expect(root().querySelector<HTMLInputElement>('#power-max')!.value).toBe('8');
  });

  it('finds operators, most locations first, adds them as chips and removes them', async () => {
    await render();

    openOperators();
    await settle();
    expect(operators.search).toHaveBeenCalledWith('');
    expect(options().map((option) => text(option, '.hit-label'))).toEqual(['Wien Energie GmbH', 'EVN']);
    expect(text(options()[0], '.hit-detail')).toBe('1124 Standorte');
    expect(text(options()[1], '.hit-detail')).toBe('1 Standort');

    options()[0].click();
    await settle();
    expect(filters.filters().operatorId).toEqual(['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40']);
    expect(root().querySelector<HTMLInputElement>('#filter-operator')!.value).toBe('');
    expect(text(root(), '#filter-operators mat-chip')).toContain('Wien Energie GmbH');

    jasmine.clock().install();
    try {
      type(root(), '#filter-operator', ' ev ');
      type(root(), '#filter-operator', ' evn ');
      jasmine.clock().tick(OPERATOR_DELAY_MS);
    } finally {
      jasmine.clock().uninstall();
    }
    await settle();
    // Only the text after the pause is searched.
    expect(operators.search.calls.mostRecent().args).toEqual(['evn']);
    expect(operators.search.calls.allArgs()).not.toContain(['ev']);

    root().querySelector<HTMLButtonElement>('#filter-operators button[matChipRemove]')!.click();
    await settle();
    expect(filters.filters().operatorId).toEqual([]);
    expect(root().querySelector('#filter-operators')).toBeNull();
  });

  it('shows the names of saved operators, drops old answers and reports failures', async () => {
    localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify({ operatorId: ['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40', '01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c41'] }));
    operators.byIds.and.resolveTo([wien]);
    let answer!: (operators: OperatorSummary[]) => void;
    let calls = 0;
    operators.search.and.callFake(() => (++calls === 1 ? new Promise((resolve) => (answer = resolve)) : Promise.reject(new Error('offline'))));
    await render();

    expect(operators.byIds).toHaveBeenCalledOnceWith(['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40', '01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c41']);
    expect(Array.from(root().querySelectorAll('#filter-operators mat-chip')).map((item) => text(item, '.mdc-evolution-chip__text-label'))).toEqual([
      'Wien Energie GmbH',
      'Betreiber',
    ]);

    openOperators();
    await settle();
    // While the search runs, the list says so.
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Betreiber werden gesucht …']);
    openOperators();
    await settle();
    answer([wien]);
    await settle();
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Die Betreiber sind gerade nicht erreichbar.']);
  });

  it('keeps the chips without names when the operators cannot be loaded, and stops the search timer when closed', async () => {
    localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify({ operatorId: ['01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40'] }));
    operators.byIds.and.rejectWith(new Error('offline'));
    operators.search.and.resolveTo([]);
    await render();
    expect(text(root(), '#filter-operators mat-chip .mdc-evolution-chip__text-label')).toBe('Betreiber');

    openOperators();
    await settle();
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Kein Betreiber gefunden.']);

    type(root(), '#filter-operator', 'x');
    fixture.destroy();
    await new Promise((resolve) => setTimeout(resolve, OPERATOR_DELAY_MS + 20));
    expect(operators.search).toHaveBeenCalledTimes(1);
    expect(set).not.toHaveBeenCalled();
  });
});

describe('OperatorsService', () => {
  it('searches operators and resolves ids', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(OperatorsService);

    const all = service.search('');
    http.expectOne('/api/v1/operators?limit=20').flush({ data: [], meta: { returned: 0, limit: 20 } });
    expect(await all).toEqual([]);

    const found = service.search('wien', 5);
    http.expectOne('/api/v1/operators?limit=5&q=wien').flush({ data: [{ id: 'a', name: 'Wien', operatorId: null, locations: 1 }], meta: { returned: 1, limit: 5 } });
    expect((await found)[0].name).toBe('Wien');

    const named = service.byIds(['a', 'b']);
    http.expectOne('/api/v1/operators?id=a&id=b&limit=100').flush({ data: [], meta: { returned: 0, limit: 100 } });
    expect(await named).toEqual([]);
    http.verify();
  });
});
