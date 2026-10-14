import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';

/**
 * Whether the instance still needs its initial setup (GET /api/v1/setup): true while no user account exists.
 * Loaded once before the first navigation; an unreachable API counts as "set up", so the map stays usable.
 */
@Injectable({ providedIn: 'root' })
export class SetupService {
  private readonly http = inject(HttpClient);
  private readonly state = signal(false);

  readonly required = this.state.asReadonly();

  async load(): Promise<void> {
    try {
      const status = await firstValueFrom(this.http.get<{ required: boolean }>('/api/v1/setup'));
      this.state.set(status.required);
    } catch {
      this.state.set(false);
    }
  }

  /** The first administrator exists now. */
  done(): void {
    this.state.set(false);
  }
}

/** App initializer: reads the setup state before the first navigation. */
export const loadSetupState = (): Promise<void> => inject(SetupService).load();

/** Guard of all regular pages: while the instance is not set up, every page leads to the setup wizard. */
export const requireSetupDone: CanActivateFn = () =>
  !inject(SetupService).required() || inject(Router).createUrlTree(['/setup']);

/**
 * Guard of /setup: open to everybody while no user exists; afterwards it is the onboarding of the remaining
 * configuration and needs settings:manage (anonymous visitors log in first, others go to the map).
 */
export const setupAccess: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (inject(SetupService).required()) {
    return true;
  }
  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }
  return auth.hasPermission('settings:manage') || router.createUrlTree(['/']);
};
