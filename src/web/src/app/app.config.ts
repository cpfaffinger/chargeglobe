import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  ErrorHandler,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { ReloadOnStaleChunk } from './design/stale-chunk';
import { restoreSession } from './auth/auth.service';
import { loadSetupState } from './setup/setup.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // A lazy chunk of an old release that is gone after a deploy reloads the page once.
    { provide: ErrorHandler, useClass: ReloadOnStaleChunk },
    provideHttpClient(withInterceptors([authInterceptor])),
    provideAppInitializer(restoreSession),
    provideAppInitializer(loadSetupState),
    provideRouter(routes),
    { provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline', subscriptSizing: 'dynamic' } },
  ],
};
