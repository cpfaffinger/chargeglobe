import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService, SKIP_REFRESH } from './auth.service';

/**
 * Adds the bearer token to API calls. If a call fails with 401 because the access token
 * expired, the session is refreshed once and the call repeated.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  // The auth endpoints work with the refresh cookie, never with the access token.
  const usesToken = request.url.startsWith('/api/') && !request.url.startsWith('/api/v1/auth/');

  const withToken = (original: HttpRequest<unknown>) => {
    const token = auth.accessToken();
    return usesToken && token
      ? original.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
      : original;
  };

  return next(withToken(request)).pipe(
    catchError((error: HttpErrorResponse) => {
      const canRefresh =
        error.status === 401 && usesToken && !!auth.accessToken() && !request.context.get(SKIP_REFRESH);
      if (!canRefresh) {
        return throwError(() => error);
      }

      return from(auth.refresh()).pipe(
        switchMap((refreshed) => (refreshed ? next(withToken(request)) : throwError(() => error))),
      );
    }),
  );
};
