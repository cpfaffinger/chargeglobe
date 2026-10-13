import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { App } from './app';
import { appConfig } from './app.config';
import { routes } from './app.routes';

describe('App', () => {
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;

  const text = (selector: string) =>
    (fixture.nativeElement as HTMLElement).querySelector(selector)?.textContent?.trim();

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(App);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows a placeholder until the health request completes', async () => {
    await fixture.whenStable();

    expect(text('#api-status')).toBe('API: …');
    expect(text('#db-status')).toBe('DB: …');
    http.expectOne('/api/v1/health');
  });

  it('shows "API: ok" when the API reports ok', async () => {
    http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });
    await fixture.whenStable();

    expect(text('#api-status')).toBe('API: ok');
    expect(text('#db-status')).toBe('DB: ok');
  });

  it('shows the database state reported by the API', async () => {
    http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'unavailable' });
    await fixture.whenStable();

    expect(text('#api-status')).toBe('API: ok');
    expect(text('#db-status')).toBe('DB: unavailable');
  });

  it('shows "API: unavailable" when the request fails', async () => {
    http.expectOne('/api/v1/health').flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
    await fixture.whenStable();

    expect(text('#api-status')).toBe('API: unavailable');
    expect(text('#db-status')).toBe('DB: unknown');
  });
});

describe('appConfig', () => {
  it('provides the HTTP client and an empty route table', () => {
    expect(appConfig.providers.length).toBeGreaterThan(0);
    expect(routes).toEqual([]);
  });
});
