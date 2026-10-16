import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AppLoading } from '../design/loading';
import { SEARCH_DELAY_MS } from '../search/location-search';
import { AddressHit, AddressResult, SearchHit, SearchService } from '../search/search.service';
import { flush, text, type } from '../testing';
import { PlaceSearch, addressPlace, hitPlace } from './place-search';
import { Place } from './routes.service';

const HIT: SearchHit = {
  type: 'LOCATION',
  label: 'Parkgarage Stephansplatz',
  detail: 'Stephansplatz 1, 1010 Wien',
  locationId: 'loc-1',
  latitude: 48.2085,
  longitude: 16.3731,
  evseId: null,
  availability: 'AVAILABLE',
  evsesTotal: 4,
  evsesAvailable: 2,
  evsesOccupied: 1,
  evseStatus: null,
};

const ADDRESS: AddressHit = { label: 'Stephansplatz 1, 1010 Wien', latitude: 48.20849, longitude: 16.37269 };

const ADDRESSES: AddressResult = {
  hits: [ADDRESS],
  attribution: [{ source: 'bev-address', text: '© Österreichisches Adressregister, Stichtagsdaten vom 01.10.2026', license: 'CC-BY-4.0' }],
};

const NO_ADDRESSES: AddressResult = { hits: [], attribution: [] };

describe('PlaceSearch', () => {
  let search: jasmine.SpyObj<SearchService>;
  let fixture: ComponentFixture<PlaceSearch>;
  let picked: Place[];

  const root = () => fixture.nativeElement as HTMLElement;
  const options = () => Array.from(document.querySelectorAll<HTMLElement>('mat-option'));

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function enter(query: string): Promise<void> {
    root().querySelector<HTMLInputElement>('input')!.focus();
    type(root(), 'input', query);
    await new Promise((resolve) => setTimeout(resolve, SEARCH_DELAY_MS + 20));
    await settle();
  }

  beforeEach(async () => {
    search = jasmine.createSpyObj<SearchService>('SearchService', ['search', 'geocode']);
    search.geocode.and.resolveTo(NO_ADDRESSES);
    TestBed.configureTestingModule({ providers: [{ provide: SearchService, useValue: search }] });
    fixture = TestBed.createComponent(PlaceSearch);
    fixture.componentRef.setInput('label', 'Ziel suchen');
    picked = [];
    fixture.componentRef.instance.picked.subscribe((place) => picked.push(place));
    await settle();
  });

  afterEach(() => fixture.destroy());

  it('finds charging locations and hands the chosen one over as place', async () => {
    search.search.and.resolveTo([HIT, { ...HIT, type: 'EVSE', label: 'AT*TST*E1*1', detail: null, evseId: 'AT*TST*E1*1' }]);
    expect(text(root(), 'mat-label')).toBe('Ziel suchen');

    await enter(' Stephans ');
    expect(search.search).toHaveBeenCalledOnceWith('Stephans');
    expect(search.geocode).toHaveBeenCalledOnceWith('Stephans', null);
    expect(options().map((option) => text(option, '.hit-label'))).toEqual(['Parkgarage Stephansplatz', 'AT*TST*E1*1']);
    expect(options()[1].querySelector('.hit-detail')).toBeNull();
    expect(document.querySelector('.hits-addresses')).toBeNull();

    options()[0].click();
    await settle();
    expect(picked).toEqual([{ latitude: 48.2085, longitude: 16.3731, name: 'Parkgarage Stephansplatz', kind: 'location', locationId: 'loc-1' }]);
    expect(root().querySelector<HTMLInputElement>('input')!.value).toBe('');
  });

  it('lists addresses near the given position under "Adressen" and hands one over as point', async () => {
    fixture.componentRef.setInput('near', { latitude: 48.3, longitude: 14.29 });
    search.search.and.resolveTo([HIT]);
    search.geocode.and.resolveTo(ADDRESSES);

    await enter('Stephansplatz 1 Wien');
    expect(search.geocode).toHaveBeenCalledOnceWith('Stephansplatz 1 Wien', { latitude: 48.3, longitude: 14.29 });
    expect(text(document.body, '.hits-locations .mat-mdc-optgroup-label')).toBe('Ladestellen');
    expect(text(document.body, '.hits-addresses .mat-mdc-optgroup-label')).toBe('Adressen');
    expect(Array.from(document.querySelectorAll('.hits-addresses .hit-label')).map((label) => label.textContent!.trim())).toEqual([
      'Stephansplatz 1, 1010 Wien',
    ]);
    expect(text(document.body, '.hit-attribution')).toBe('© Österreichisches Adressregister, Stichtagsdaten vom 01.10.2026 (CC-BY-4.0)');

    document.querySelector<HTMLElement>('.hits-addresses mat-option')!.click();
    await settle();
    expect(picked).toEqual([{ latitude: 48.20849, longitude: 16.37269, name: 'Stephansplatz 1, 1010 Wien', kind: 'point', locationId: null }]);
    expect(options()).toEqual([]);
  });

  it('keeps the hits of one search when the other fails', async () => {
    search.search.and.rejectWith(new Error('offline'));
    search.geocode.and.resolveTo(ADDRESSES);
    await enter('Stephansplatz');
    expect(options().map((option) => option.textContent!.trim())).toEqual([
      'Stephansplatz 1, 1010 Wien',
      '© Österreichisches Adressregister, Stichtagsdaten vom 01.10.2026 (CC-BY-4.0)',
    ]);

    search.search.and.resolveTo([HIT]);
    search.geocode.and.rejectWith(new Error('forbidden'));
    await enter('Stephansplatz 1');
    expect(options().map((option) => text(option, '.hit-label'))).toEqual(['Parkgarage Stephansplatz']);
  });

  it('says when nothing is found or the search fails, waits for two characters and drops older answers', async () => {
    search.search.and.resolveTo([]);
    await enter('xy');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Nichts gefunden.']);
    // Addresses need three characters.
    expect(search.geocode).not.toHaveBeenCalled();

    search.search.and.rejectWith(new Error('offline'));
    await enter('ab');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Die Suche ist gerade nicht erreichbar.']);

    search.geocode.and.rejectWith(new Error('offline'));
    await enter('xyz');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Die Suche ist gerade nicht erreichbar.']);
    search.geocode.and.resolveTo(NO_ADDRESSES);

    // One character: no search, older answers are dropped.
    let answer!: (hits: SearchHit[]) => void;
    search.search.and.returnValue(new Promise((resolve) => (answer = resolve)));
    await enter('Wien');
    await enter('W');
    answer([HIT]);
    await settle();
    expect(options()).toEqual([]);
    expect(search.search).toHaveBeenCalledTimes(4);

    // A failure for an older query changes nothing either.
    let late!: () => void;
    search.search.and.returnValue(new Promise((_, reject) => (late = () => reject(new Error('late')))));
    await enter('Wien Mitte');
    search.search.and.resolveTo([HIT]);
    await enter('Wien Mitte 1');
    late();
    await settle();
    expect(options().map((option) => text(option, '.hit-label'))).toEqual(['Parkgarage Stephansplatz']);
  });

  it('names the place of an EVSE hit by its EVSE-ID and of an address by the address', () => {
    expect(hitPlace({ ...HIT, type: 'EVSE', label: 'AT*TST*E1*1' }).name).toBe('AT*TST*E1*1');
    expect(addressPlace(ADDRESS).name).toBe('Stephansplatz 1, 1010 Wien');
  });
});

describe('AppLoading', () => {
  it('shows a spinner with the text as status', async () => {
    const fixture = TestBed.createComponent(AppLoading);
    fixture.componentRef.setInput('text', 'Preis wird geladen …');
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.getAttribute('role')).toBe('status');
    expect(host.querySelector('mat-spinner')).not.toBeNull();
    expect(text(host, 'span')).toBe('Preis wird geladen …');
  });
});
