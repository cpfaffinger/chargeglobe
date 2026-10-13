import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { fakeAuth, flush } from '../testing';
import { authInterceptor } from './auth.interceptor';
import { AuthService, SKIP_REFRESH } from './auth.service';

describe('authInterceptor', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let client: HttpClient;
  let http: HttpTestingController;

  const unauthorized = { status: 401, statusText: 'Unauthorized' };

  beforeEach(() => {
    auth = fakeAuth();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
      ],
    });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends anonymous requests without an Authorization header', () => {
    auth.accessToken.and.returnValue(null);

    client.get('/api/v1/health').subscribe();

    const request = http.expectOne('/api/v1/health');
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({});
  });

  it('adds the bearer token to API calls, but not to auth endpoints or other URLs', () => {
    auth.accessToken.and.returnValue('token-1');

    client.get('/api/v1/me').subscribe();
    client.post('/api/v1/auth/logout', null).subscribe();
    client.get('/favicon.ico').subscribe();

    const api = http.expectOne('/api/v1/me');
    expect(api.request.headers.get('Authorization')).toBe('Bearer token-1');
    api.flush({});
    const authCall = http.expectOne('/api/v1/auth/logout');
    expect(authCall.request.headers.has('Authorization')).toBeFalse();
    authCall.flush(null);
    const other = http.expectOne('/favicon.ico');
    expect(other.request.headers.has('Authorization')).toBeFalse();
    other.flush(null);
  });

  it('refreshes the session once on 401 and repeats the call with the new token', async () => {
    auth.accessToken.and.returnValue('expired');
    auth.refresh.and.callFake(async () => {
      auth.accessToken.and.returnValue('fresh');
      return true;
    });

    const result = firstValueFrom(client.get('/api/v1/api-keys'));
    http.expectOne('/api/v1/api-keys').flush(null, unauthorized);
    await flush();
    const retried = http.expectOne('/api/v1/api-keys');
    expect(retried.request.headers.get('Authorization')).toBe('Bearer fresh');
    retried.flush(['key']);

    expect(await result).toEqual(['key']);
    expect(auth.refresh).toHaveBeenCalledTimes(1);
  });

  it('passes the 401 on when the session cannot be refreshed', async () => {
    auth.accessToken.and.returnValue('expired');
    auth.refresh.and.resolveTo(false);

    const result = firstValueFrom(client.get('/api/v1/api-keys'));
    http.expectOne('/api/v1/api-keys').flush(null, unauthorized);

    await expectAsync(result).toBeRejectedWith(jasmine.objectContaining({ status: 401 }));
  });

  it('does not refresh for anonymous calls, auth endpoints, opted-out requests or other errors', async () => {
    auth.accessToken.and.returnValue(null);
    const anonymous = firstValueFrom(client.get('/api/v1/me'));
    http.expectOne('/api/v1/me').flush(null, unauthorized);
    await expectAsync(anonymous).toBeRejected();

    auth.accessToken.and.returnValue('token-1');
    const login = firstValueFrom(client.post('/api/v1/auth/login', {}));
    http.expectOne('/api/v1/auth/login').flush(null, unauthorized);
    await expectAsync(login).toBeRejected();

    const optedOut = firstValueFrom(
      client.get('/api/v1/me', { context: new HttpContext().set(SKIP_REFRESH, true) }),
    );
    http.expectOne('/api/v1/me').flush(null, unauthorized);
    await expectAsync(optedOut).toBeRejected();

    const serverError = firstValueFrom(client.get('/api/v1/health'));
    http.expectOne('/api/v1/health').flush(null, { status: 500, statusText: 'Server Error' });
    await expectAsync(serverError).toBeRejected();

    expect(auth.refresh).not.toHaveBeenCalled();
  });
});
