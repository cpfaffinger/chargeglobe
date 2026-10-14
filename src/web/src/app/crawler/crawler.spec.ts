import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminCrawlerPage, CRAWLER_REFRESH_MS } from '../pages/admin-crawler.page';
import { flush, text } from '../testing';
import { CrawlerRun, CrawlerService, CrawlerStatus, ProviderCrawlStatus } from './crawler.service';

function provider(overrides: Partial<ProviderCrawlStatus> = {}): ProviderCrawlStatus {
  return {
    key: 'econtrol',
    name: 'E-Control Ladestellenverzeichnis',
    enabled: true,
    budgetPerHour: 600,
    requestsLastHour: 150,
    pause: null,
    tiles: { total: 212, due: 3, hot: 31, normal: 104, quiet: 77, onDemand: 1, oldestPoll: '2026-10-14T07:17:00Z' },
    plans: [
      {
        jobType: 'TILE_PLAN',
        target: 'AT-9,AT-3,AT-4,AT-1',
        nextRunAt: '2026-10-14T08:55:00Z',
        leasedBy: null,
        lastRunAt: '2026-10-14T07:55:00Z',
        lastResult: 'OK: 212 records, 0 requests',
      },
      { jobType: 'INVENTORY_FULL', target: 'AT-9,AT-3,AT-4,AT-1', nextRunAt: '2026-10-15T04:00:00Z', leasedBy: 'crawler-1', lastRunAt: null, lastResult: null },
    ],
    ...overrides,
  };
}

function status(providers: ProviderCrawlStatus[] = [provider()]): CrawlerStatus {
  return { at: '2026-10-14T08:15:00Z', providers, queue: { pending: 1234, failed: 2 } };
}

function run(overrides: Partial<CrawlerRun> = {}): CrawlerRun {
  return {
    id: 'run-1',
    provider: 'econtrol',
    jobType: 'STATUS_POLL',
    target: 'point 17/212 (48.2085,16.3731, 90 locations)',
    startedAt: '2026-10-14T08:15:00Z',
    finishedAt: '2026-10-14T08:15:01Z',
    requests: 1,
    records: 318,
    result: 'OK',
    error: null,
    cooldownUntil: null,
    ...overrides,
  };
}

describe('CrawlerService', () => {
  let service: CrawlerService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CrawlerService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the status and the latest runs', async () => {
    const current = service.status();
    http.expectOne('/api/v1/admin/crawler/status').flush({ data: status() });
    expect((await current).providers[0].key).toBe('econtrol');

    const runs = service.runs();
    http.expectOne('/api/v1/admin/crawler/runs?limit=50').flush({ data: [run()], meta: { returned: 1, limit: 50 } });
    expect((await runs).length).toBe(1);
  });
});

describe('AdminCrawlerPage', () => {
  let service: jasmine.SpyObj<CrawlerService>;
  let fixture: ComponentFixture<AdminCrawlerPage>;
  let tick: () => void;

  const root = () => fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(root().querySelectorAll('#runs tbody tr.mat-mdc-row:not(.no-data)'));

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminCrawlerPage);
    await settle();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<CrawlerService>('CrawlerService', ['status', 'runs']);
    service.status.and.resolveTo(status());
    service.runs.and.resolveTo([]);
    spyOn(window, 'setInterval').and.callFake(((handler: () => void, timeout: number) => {
      expect(timeout).toBe(CRAWLER_REFRESH_MS);
      tick = handler;
      return 42;
    }) as unknown as typeof setInterval);
    spyOn(window, 'clearInterval');
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: CrawlerService, useValue: service }] });
  });

  it('shows budget, query points, plans and the queue of every source', async () => {
    await render();

    const card = root().querySelector('#source-econtrol')!;
    expect(text(card, 'mat-card-title')).toBe('E-Control Ladestellenverzeichnis');
    expect(text(card, '.state')).toBe('aktiv');
    expect(card.querySelector('#pause')).toBeNull();
    expect(text(card, '.budget')).toBe('150 / 600 Abrufe in der letzten Stunde');
    expect(card.querySelector('mat-progress-bar')!.getAttribute('aria-valuenow')).toBe('25');
    expect(text(card, '#tiles')).toMatch(
      /^212 Abfragepunkte · 3 fällig · 31 lebhaft · 104 normal · 77 ruhig · 1 angefordert · ältester Abruf \d{2}:17$/,
    );
    const plans = Array.from(card.querySelectorAll('.plans tbody tr')).map((row) =>
      Array.from(row.querySelectorAll('td'))
        .map((cell) => cell.textContent!.trim())
        .join(' '),
    );
    expect(plans[0]).toMatch(/^Abfragepunkte AT-9,AT-3,AT-4,AT-1 14\.10\. \d{2}:55 OK: 212 records, 0 requests$/);
    expect(plans[1]).toBe('Inventar AT-9,AT-3,AT-4,AT-1 läuft (crawler-1) –');
    expect(text(root(), '#queue')).toBe('Ingest-Warteschlange: 1,234 offen, 2 mit Fehler');
    expect(text(root(), '#runs-empty')).toBe('Noch keine Läufe.');
    expect(text(root(), '#updated')).toMatch(/^Stand \d{2}:15:00$/);
  });

  it('shows a cooldown, a block and a source without plans or polls', async () => {
    service.status.and.resolveTo(
      status([
        provider({ key: 'a', name: null, enabled: false, requestsLastHour: 900, plans: [], pause: { failures: 3, until: '2026-10-14T08:30:00Z', blocked: false, reason: 'E-Control search returned 503.' } }),
        provider({ key: 'b', pause: { failures: 1, until: null, blocked: true, reason: null }, tiles: { ...provider().tiles, oldestPoll: null } }),
      ]),
    );
    await render();

    const cooling = root().querySelector('#source-a')!;
    expect(text(cooling, 'mat-card-title')).toBe('a');
    expect(text(cooling, '.state')).toBe('ausgeschaltet');
    expect(text(cooling, '#pause')).toMatch(/^pausiert bis \d{2}:30$/);
    expect(text(cooling, '.muted')).toBe('Letzter Fehler (3× in Folge): E-Control search returned 503.');
    expect(cooling.querySelector('mat-progress-bar')!.getAttribute('aria-valuenow')).toBe('100');
    expect(text(cooling, '.plans tbody')).toBe('Keine Pläne – die Quelle ist ausgeschaltet oder hat keine Regionen.');

    const blocked = root().querySelector('#source-b')!;
    expect(text(blocked, '#pause')).toBe('gesperrt bis zur nächsten Einstellungsänderung');
    expect(blocked.querySelector('.muted')).toBeNull();
    expect(text(blocked, '#tiles')).not.toContain('ältester Abruf');
  });

  it('lists the latest runs with result, error and cooldown', async () => {
    service.runs.and.resolveTo([
      run(),
      run({ id: 'run-2', jobType: 'INVENTORY_FULL', result: 'FAILED', requests: 3, records: 0, error: 'E-Control search/stations returned 503.', cooldownUntil: '2026-10-14T08:25:00Z' }),
      run({ id: 'run-3', jobType: 'SOMETHING_NEW', result: 'RUNNING', finishedAt: null, records: 0 }),
    ]);
    await render();

    expect(rows().length).toBe(3);
    expect(rows()[0].textContent!.replace(/\s+/g, ' ')).toContain('Status · econtrol');
    expect(text(rows()[0], '.run-result')).toBe('OK');
    expect(rows()[0].querySelector('.run-result')!.classList).toContain('ok');
    const notes = Array.from(rows()[1].querySelectorAll('.note')).map((note) => note.textContent!.trim());
    expect(notes[0]).toBe('E-Control search/stations returned 503.');
    expect(notes[1]).toMatch(/^Pause bis \d{2}:25$/);
    expect(rows()[1].querySelector('.run-result')!.classList).toContain('failed');
    expect(rows()[2].textContent).toContain('SOMETHING_NEW · econtrol');
  });

  it('refreshes every five seconds, skips a refresh while one runs and stops when left', async () => {
    await render();
    expect(service.status).toHaveBeenCalledTimes(1);

    let release!: (value: CrawlerStatus) => void;
    service.status.and.returnValue(new Promise((resolve) => (release = resolve)));
    tick();
    tick();
    expect(service.status).toHaveBeenCalledTimes(2);
    release(status([provider({ requestsLastHour: 300 })]));
    await settle();
    expect(text(root(), '.budget')).toBe('300 / 600 Abrufe in der letzten Stunde');

    service.status.and.resolveTo(status());
    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await settle();
    expect(service.status).toHaveBeenCalledTimes(3);

    fixture.destroy();
    expect(window.clearInterval).toHaveBeenCalledWith(42);
  });

  it('shows errors of the API', async () => {
    service.status.and.rejectWith(new HttpErrorResponse({ status: 503, statusText: 'Service Unavailable' }));
    await render();

    expect(root().querySelector('.error[role="alert"]')).not.toBeNull();
    expect(root().querySelector('mat-card')).toBeNull();
  });
});
