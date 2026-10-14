import { DOCUMENT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { flush, text, type } from '../testing';
import { MapFilterPanel, OPERATOR_DELAY_MS } from './map-filter-panel';
import { FILTERS_STORAGE_KEY, MapFilterStore, MapFilters, NO_FILTERS, countFilters, filterParams, parseFilters } from './map-filters';
import { MapLink } from './map-link';

const ALL: MapFilters = {
  connector: ['IEC_62196_T2_COMBO', 'CHADEMO'],
  minPowerKw: 150,
  operator: 'Wien Energie',
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
      minPowerKw: '150',
      operator: 'Wien Energie',
      availableOnly: 'true',
      adHoc: 'true',
      roaming: 'true',
      greenEnergy: 'true',
      barrierFree: 'true',
    });
    expect(parseFilters(params)).toEqual({ ...ALL, connector: ['IEC_62196_T2_COMBO', 'CHADEMO'] });
    expect(countFilters(ALL)).toBe(8);
    expect(filterParams(NO_FILTERS)).toEqual({});

    expect(parseFilters({ connector: 'CHADEMO,FOO', minPowerKw: '0', operator: `  ${'x'.repeat(120)} `, roaming: 'yes', adHoc: true })).toEqual({
      ...NO_FILTERS,
      connector: ['CHADEMO'],
      operator: 'x'.repeat(100),
      adHoc: true,
    });
    expect(parseFilters({ minPowerKw: '2000' }).minPowerKw).toBeNull();
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
    expect(extras.queryParams).toEqual(jasmine.objectContaining({ minPowerKw: '50', roaming: 'true', connector: null, operator: null, adHoc: null }));
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

  const root = () => fixture.nativeElement as HTMLElement;
  const chip = (group: string, label: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`#${group} mat-chip-option`)).find((option) => text(option, '.mdc-evolution-chip__text-label') === label)!;

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function click(element: HTMLElement): Promise<void> {
    element.querySelector<HTMLElement>('button')!.click();
    await settle();
  }

  beforeEach(async () => {
    localStorage.removeItem(FILTERS_STORAGE_KEY);
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    filters = TestBed.inject(MapFilterStore);
    set = spyOn(filters, 'set').and.callThrough();
    fixture = TestBed.createComponent(MapFilterPanel);
    await settle();
  });

  afterEach(() => localStorage.removeItem(FILTERS_STORAGE_KEY));

  it('applies connectors, power and properties at once and resets them', async () => {
    expect(root().querySelector<HTMLButtonElement>('#reset-filters')!.disabled).toBeTrue();

    await click(chip('filter-connector', 'CCS'));
    await click(chip('filter-connector', 'CHAdeMO'));
    expect(filters.filters().connector).toEqual(['IEC_62196_T2_COMBO', 'CHADEMO']);

    await click(chip('filter-power', 'ab 150 kW'));
    expect(filters.filters().minPowerKw).toBe(150);
    await click(chip('filter-power', 'ab 150 kW'));
    expect(filters.filters().minPowerKw).toBeNull();

    await click(chip('filter-flags', 'Ad-hoc-Zahlung'));
    await click(chip('filter-flags', 'Ökostrom'));
    expect(filters.filters()).toEqual(jasmine.objectContaining({ adHoc: true, greenEnergy: true, roaming: false }));
    expect(filters.count()).toBe(3);

    root().querySelector<HTMLButtonElement>('#reset-filters')!.click();
    await settle();
    expect(filters.filters()).toEqual(NO_FILTERS);
  });

  it('applies the operator after a pause in typing and stops the timer when closed', async () => {
    jasmine.clock().install();
    try {
      type(root(), '#filter-operator', ' Wien ');
      type(root(), '#filter-operator', ' Wien Energie ');
      jasmine.clock().tick(OPERATOR_DELAY_MS);
    } finally {
      jasmine.clock().uninstall();
    }
    expect(set).toHaveBeenCalledOnceWith({ ...NO_FILTERS, operator: 'Wien Energie' });

    const closed: unknown[] = [];
    fixture.componentInstance.closed.subscribe(() => closed.push(true));
    root().querySelector<HTMLButtonElement>('#apply-filters')!.click();
    root().querySelector<HTMLButtonElement>('#close-filters')!.click();
    expect(closed.length).toBe(2);

    type(root(), '#filter-operator', 'x');
    fixture.destroy();
    await new Promise((resolve) => setTimeout(resolve, OPERATOR_DELAY_MS + 20));
    expect(set).toHaveBeenCalledTimes(1);
  });
});
