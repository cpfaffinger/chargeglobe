import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * Route guard: anonymous visitors are sent to /login (with the requested URL as returnUrl),
 * signed-in users without the permission to the start page. Without an argument any
 * signed-in user may pass. Mirrors `x-required-permission` of the API; role names are never checked.
 */
export function requirePermission(permission?: string): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);

    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
    }

    return !permission || auth.hasPermission(permission) || router.createUrlTree(['/']);
  };
}

/** Like {@link requirePermission}, but one of the permissions is enough (e.g. for the overview of the administration). */
export function requireAnyPermission(permissions: string[]): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);

    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
    }

    return permissions.some((permission) => auth.hasPermission(permission)) || router.createUrlTree(['/']);
  };
}
