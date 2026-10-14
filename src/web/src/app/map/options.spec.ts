import { ComponentFixture, TestBed } from '@angular/core/testing';
import { flush, text } from '../testing';
import { DEFAULT_MAP_OPTIONS, MapOptionsStore, OPTIONS_STORAGE_KEY } from './map-options';
import { MapOptionsPanel } from './map-options-panel';

describe('MapOptionsStore', () => {
  beforeEach(() => localStorage.removeItem(OPTIONS_STORAGE_KEY));
  afterEach(() => localStorage.removeItem(OPTIONS_STORAGE_KEY));

  it('starts with the defaults, keeps changes in the browser and resets them', () => {
    const store = TestBed.inject(MapOptionsStore);
    expect(store.options()).toEqual(DEFAULT_MAP_OPTIONS);
    expect(store.isDefault()).toBeTrue();

    store.set({ clusterRadius: 80 });
    expect(store.options()).toEqual({ clusterRadius: 80, clusterMaxZoom: 13 });
    expect(store.isDefault()).toBeFalse();
    expect(JSON.parse(localStorage.getItem(OPTIONS_STORAGE_KEY)!)).toEqual({ clusterRadius: 80, clusterMaxZoom: 13 });

    store.reset();
    expect(store.options()).toEqual(DEFAULT_MAP_OPTIONS);
    expect(localStorage.getItem(OPTIONS_STORAGE_KEY)).toBeNull();
  });

  it('reads stored options and replaces values out of range', () => {
    localStorage.setItem(OPTIONS_STORAGE_KEY, JSON.stringify({ clusterRadius: 0, clusterMaxZoom: 30 }));
    expect(TestBed.inject(MapOptionsStore).options()).toEqual({ clusterRadius: 0, clusterMaxZoom: 13 });
  });

  it('falls back to the defaults for unreadable storage', () => {
    localStorage.setItem(OPTIONS_STORAGE_KEY, '{');
    expect(TestBed.inject(MapOptionsStore).options()).toEqual(DEFAULT_MAP_OPTIONS);
  });

  it('works without storage', () => {
    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
    spyOn(Storage.prototype, 'removeItem').and.throwError('blocked');
    const store = TestBed.inject(MapOptionsStore);

    store.set({ clusterMaxZoom: 16 });
    expect(store.options().clusterMaxZoom).toBe(16);
    store.reset();
    expect(store.options()).toEqual(DEFAULT_MAP_OPTIONS);
  });
});

describe('MapOptionsPanel', () => {
  let fixture: ComponentFixture<MapOptionsPanel>;
  let store: MapOptionsStore;

  const root = () => fixture.nativeElement as HTMLElement;

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function slide(id: string, value: number): Promise<void> {
    const input = root().querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = String(value);
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
    await settle();
  }

  beforeEach(async () => {
    localStorage.removeItem(OPTIONS_STORAGE_KEY);
    store = TestBed.inject(MapOptionsStore);
    fixture = TestBed.createComponent(MapOptionsPanel);
    await settle();
  });

  afterEach(() => localStorage.removeItem(OPTIONS_STORAGE_KEY));

  it('changes radius and zoom level at once and resets them to the defaults', async () => {
    expect(text(root(), '#cluster-radius-value')).toBe('im Umkreis von 50 px');
    expect(text(root(), '#cluster-zoom-value')).toBe('13');
    expect(root().querySelector<HTMLButtonElement>('#reset-options')!.disabled).toBeTrue();

    await slide('cluster-zoom', 16);
    expect(store.options().clusterMaxZoom).toBe(16);
    await slide('cluster-radius', 0);
    expect(text(root(), '#cluster-radius-value')).toBe('nie');
    expect(root().querySelector<HTMLInputElement>('#cluster-zoom')!.disabled).toBeTrue();

    root().querySelector<HTMLButtonElement>('#reset-options')!.click();
    await settle();
    expect(store.options()).toEqual(DEFAULT_MAP_OPTIONS);

    const closed: unknown[] = [];
    fixture.componentInstance.closed.subscribe(() => closed.push(true));
    root().querySelector<HTMLButtonElement>('#apply-options')!.click();
    root().querySelector<HTMLButtonElement>('#close-options')!.click();
    expect(closed.length).toBe(2);
  });
});
