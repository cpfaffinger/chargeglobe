import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { AdminConflictsPage } from '../pages/admin-conflicts.page';
import { flush, text } from '../testing';
import { Conflict, ConflictCandidate, ConflictsResponse, ConflictsService, describeConflict } from './conflicts.service';

function conflict(overrides: Partial<Conflict> = {}): Conflict {
  return {
    id: 'c-1',
    entityType: 'LOCATION',
    entityId: 'loc-1',
    entityLabel: 'Parkgarage Stephansplatz, Stephansplatz 1, Wien',
    attribute: 'identity',
    values: { source: 'ocm', externalId: '148527', candidates: ['loc-1', 'loc-2'] },
    detectedAt: '2026-10-15T08:00:00Z',
    resolvedAt: null,
    resolution: null,
    candidates: [],
    record: null,
    suggestion: null,
    ...overrides,
  };
}

function candidate(overrides: Partial<ConflictCandidate> = {}): ConflictCandidate {
  return {
    locationId: 'loc-1',
    label: 'Parkgarage Stephansplatz, Wien',
    distanceMeters: 18.4,
    operatorId: 'VIE',
    operatorName: 'Wien Energie',
    sameOperator: true,
    evses: 2,
    surrogateEvses: 0,
    evseIds: ['AT*VIE*E1*1', 'AT*VIE*E1*2'],
    sources: ['econtrol'],
    ...overrides,
  };
}

const response = (data: Conflict[], open = data.length): ConflictsResponse => ({ data, meta: { returned: data.length, limit: 200, open, nextCursor: null } });

describe('ConflictsService', () => {
  it('asks for open or resolved conflicts', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(ConflictsService);

    const open = service.list(false);
    http.expectOne('/api/v1/conflicts?limit=200').flush(response([conflict()]));
    expect((await open).data.length).toBe(1);

    const resolved = service.list(true);
    http.expectOne('/api/v1/conflicts?limit=200&resolved=true').flush(response([]));
    expect((await resolved).meta.open).toBe(0);

    const merged = service.resolve('c-1', 'merge', 'loc-2');
    const request = http.expectOne('/api/v1/conflicts/c-1/resolve');
    expect([request.request.method, request.request.body]).toEqual(['POST', { action: 'merge', targetId: 'loc-2' }]);
    request.flush({ data: conflict({ resolvedAt: '2026-10-16T08:00:00Z' }) });
    expect((await merged).resolvedAt).toBe('2026-10-16T08:00:00Z');

    const kept = service.resolve('c-2', 'keep');
    const keep = http.expectOne('/api/v1/conflicts/c-2/resolve');
    expect(keep.request.body).toEqual({ action: 'keep', targetId: null });
    keep.flush({ data: conflict({ id: 'c-2' }) });
    await kept;
    http.verify();
  });

  it('describes the values in short German', () => {
    expect(describeConflict(conflict())).toBe('ocm 148527 passt zu 2 Standorten');
    expect(
      describeConflict(
        conflict({
          record: {
            source: 'ocm',
            externalId: '148527',
            name: 'Parkgarage',
            address: null,
            postalCode: null,
            city: null,
            operatorId: 'VIE',
            operatorName: 'Wien Energie',
            latitude: 48.2,
            longitude: 16.4,
            evses: [{ evseId: null, maxPowerKw: 22 }],
          },
        }),
      ),
    ).toBe('ocm 148527 passt zu 2 Standorten (Parkgarage, Wien Energie; 1 Ladepunkte)');
    expect(
      describeConflict(conflict({ attribute: 'evse_id', values: { stored: 'AT*TST*E1', ocm: 'AT-TST-E1', count: 2, source: 'ocm', externalId: 'x', locationId: 'loc-1' } })),
    ).toBe('stored: AT*TST*E1 · ocm: AT-TST-E1 · count: 2');
  });
});

describe('AdminConflictsPage', () => {
  let service: jasmine.SpyObj<ConflictsService>;
  let snackBar: jasmine.SpyObj<MatSnackBar>;
  let fixture: ComponentFixture<AdminConflictsPage>;
  const root = () => fixture.nativeElement as HTMLElement;

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminConflictsPage);
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<ConflictsService>('ConflictsService', ['list', 'resolve']);
    snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ConflictsService, useValue: service }, { provide: MatSnackBar, useValue: snackBar }],
    });
  });

  it('lists the open conflicts with a link to what they concern and switches to the resolved ones', async () => {
    service.list.and.callFake(async (resolved) =>
      resolved
        ? response([conflict({ id: 'c-9', resolvedAt: '2026-10-16T08:00:00Z', resolution: 'merged by hand' })], 2)
        : response([
            conflict(),
            conflict({ id: 'c-2', entityType: 'EVSE', entityId: 'e-1', entityLabel: 'AT*TST*E1*1', attribute: 'evse_id', values: { stored: 'AT*TST*E1*1' } }),
            conflict({ id: 'c-3', entityId: null, entityLabel: null, attribute: 'other' }),
          ]),
    );
    await render();

    expect(text(root(), '#open-count')).toBe('3');
    const link = (id: string) => root().querySelector<HTMLAnchorElement>(`[data-conflict="${id}"] a`);
    expect(link('c-1')!.getAttribute('href')).toBe('/locations/loc-1');
    expect(link('c-2')!.getAttribute('href')).toBe('/evses/e-1');
    expect(link('c-3')).toBeNull();
    expect(text(root(), '[data-conflict="c-3"] td:nth-child(2)')).toBe('nicht mehr vorhanden');
    expect(Array.from(root().querySelectorAll('#conflicts tbody tr')).map((row) => text(row, 'td:nth-child(3)'))).toEqual([
      'Zuordnung mehrdeutig',
      'EVSE-ID-Schreibweise',
      'other',
    ]);
    expect(text(root(), '[data-conflict="c-1"] td:nth-child(5)')).toBe('eigener Standort');
    expect(text(root(), '[data-conflict="c-2"] .same')).toBe('gleicher Ladepunkt');
    expect(text(root(), '[data-conflict="c-2"] .keep')).toBe('behalten');

    root().querySelectorAll<HTMLButtonElement>('#conflict-filter button')[1].click();
    await flush();
    await fixture.whenStable();
    expect(service.list).toHaveBeenCalledWith(true);
    expect(text(root(), '[data-conflict="c-9"] td:nth-child(5)')).toBe('16.10.2026 · merged by hand');
    expect(text(root(), '#open-count')).toBe('2');
  });

  it('assigns a record to a candidate, keeps it separate or keeps an EVSE-ID, then reloads', async () => {
    const identity = conflict({
      candidates: [
        candidate(),
        candidate({ locationId: 'loc-2', label: 'Teststation Dom, Wien', distanceMeters: null, operatorName: null, operatorId: null, sameOperator: null, evses: 4, sources: ['econtrol', 'ocm'] }),
        candidate({ locationId: 'loc-3', label: null, distanceMeters: null }),
      ],
    });
    const evseId = conflict({ id: 'c-2', entityType: 'EVSE', entityId: 'e-1', attribute: 'evse_id', values: { stored: 'AT*TST*E1*1' } });
    service.list.and.resolveTo(response([identity, evseId, conflict({ id: 'c-3', attribute: 'other' })]));
    service.resolve.and.resolveTo(conflict({ resolvedAt: '2026-10-16T08:00:00Z' }));
    await render();

    const candidateOf = (id: string) => root().querySelector<HTMLElement>(`[data-candidate="${id}"]`)!;
    expect(text(candidateOf('loc-1'), '.distance')).toBe('18 m');
    expect(text(candidateOf('loc-1'), '.details')).toBe('Wien Energie · 2 Ladepunkte · econtrol');
    expect(candidateOf('loc-2').querySelector('.distance')).toBeNull();
    expect(text(candidateOf('loc-2'), '.details')).toBe('Betreiber unbekannt · 4 Ladepunkte · econtrol, ocm');
    // A candidate that no longer exists cannot be chosen.
    expect(text(candidateOf('loc-3'), 'a')).toBe('nicht mehr vorhanden');
    expect(candidateOf('loc-3').querySelector('button')).toBeNull();
    expect(candidateOf('loc-3').querySelector('.details')).toBeNull();
    expect(text(root(), '[data-conflict="c-3"] td:nth-child(5)')).toBe('–');

    candidateOf('loc-2').querySelector<HTMLButtonElement>('.merge')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-1', 'merge', 'loc-2');
    expect(snackBar.open).toHaveBeenCalledWith('Datensatz dem Standort zugeordnet.', 'OK', { duration: 4000 });
    expect(service.list).toHaveBeenCalledTimes(2);

    root().querySelector<HTMLButtonElement>('[data-conflict="c-1"] .separate')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-1', 'separate', null);
    expect(snackBar.open).toHaveBeenCalledWith('Datensatz wird ein eigener Standort.', 'OK', { duration: 4000 });

    root().querySelector<HTMLButtonElement>('[data-conflict="c-2"] .keep')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-2', 'keep', null);
    expect(snackBar.open).toHaveBeenCalledWith('EVSE-ID bleibt wie gespeichert.', 'OK', { duration: 4000 });

    root().querySelector<HTMLButtonElement>('[data-conflict="c-2"] .same')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-2', 'same', null);
    expect(snackBar.open).toHaveBeenCalledWith('Datensatz dem gespeicherten Ladepunkt zugeordnet.', 'OK', { duration: 4000 });

    service.resolve.and.rejectWith(new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { targetId: ['Must be one of the candidates.'] } } }));
    candidateOf('loc-1').querySelector<HTMLButtonElement>('.merge')!.click();
    await flush();
    await fixture.whenStable();
    expect(text(root(), '.error')).toBe('Must be one of the candidates.');
  });

  it('shows the suggestion of a conflict and applies it', async () => {
    const suggested = conflict({
      candidates: [candidate()],
      suggestion: { action: 'merge', targetId: 'loc-1', confidence: 0.8, reason: 'The only candidate of the operator Wien Energie with the same number of charge points (2), 18 m away.' },
    });
    const decided = conflict({ id: 'c-9', resolvedAt: '2026-10-16T08:00:00Z', resolution: 'done', suggestion: { action: 'same', targetId: null, confidence: 0.95, reason: 'stale' } });
    service.list.and.resolveTo(response([suggested, decided]));
    service.resolve.and.resolveTo(conflict({ resolvedAt: '2026-10-16T08:00:00Z' }));
    await render();

    expect(text(root(), '[data-conflict="c-1"] .suggestion')).toContain('Vorschlag (80%): The only candidate');
    expect(root().querySelector('[data-conflict="c-9"] .suggestion')).toBeNull();

    root().querySelector<HTMLButtonElement>('[data-conflict="c-1"] .apply')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-1', 'merge', 'loc-1');
    expect(snackBar.open).toHaveBeenCalledWith('Datensatz dem Standort zugeordnet.', 'OK', { duration: 4000 });
  });

  it('says when there are none and shows errors', async () => {
    service.list.and.resolveTo(response([]));
    await render();
    expect(text(root(), '#conflicts tbody')).toBe('Keine Konflikte.');

    service.list.and.rejectWith(new HttpErrorResponse({ status: 503, error: { title: 'Service unavailable', detail: 'Database down.' } }));
    root().querySelectorAll<HTMLButtonElement>('#conflict-filter button')[1].click();
    await flush();
    await fixture.whenStable();
    expect(text(root(), '.error')).toBe('Database down.');
  });
});
