import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AppLoading } from '../design/loading';
import { SEARCH_DELAY_MS } from '../search/location-search';
import { SearchHit, SearchService } from '../search/search.service';
import { flush, text, type } from '../testing';
import { PlaceSearch, hitPlace } from './place-search';
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
    search = jasmine.createSpyObj<SearchService>('SearchService', ['search']);
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
    expect(options().map((option) => text(option, '.hit-label'))).toEqual(['Parkgarage Stephansplatz', 'AT*TST*E1*1']);
    expect(options()[1].querySelector('.hit-detail')).toBeNull();

    options()[0].click();
    await settle();
    expect(picked).toEqual([{ latitude: 48.2085, longitude: 16.3731, name: 'Parkgarage Stephansplatz', kind: 'location', locationId: 'loc-1' }]);
    expect(root().querySelector<HTMLInputElement>('input')!.value).toBe('');
  });

  it('says when nothing is found or the search fails, waits for two characters and drops older answers', async () => {
    search.search.and.resolveTo([]);
    await enter('xy');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Nichts gefunden.']);

    search.search.and.rejectWith(new Error('offline'));
    await enter('xyz');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Die Suche ist gerade nicht erreichbar.']);

    // One character: no search, older answers are dropped.
    let answer!: (hits: SearchHit[]) => void;
    search.search.and.returnValue(new Promise((resolve) => (answer = resolve)));
    await enter('Wien');
    await enter('W');
    answer([HIT]);
    await settle();
    expect(options()).toEqual([]);
    expect(search.search).toHaveBeenCalledTimes(3);

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

  it('names the place of an EVSE hit by its EVSE-ID', () => {
    expect(hitPlace({ ...HIT, type: 'EVSE', label: 'AT*TST*E1*1' }).name).toBe('AT*TST*E1*1');
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
