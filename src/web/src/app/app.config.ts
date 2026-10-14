import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { restoreSession } from './auth/auth.service';
import { loadSetupState } from './setup/setup.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideAppInitializer(restoreSession),
    provideAppInitializer(loadSetupState),
    provideRouter(routes),
  ],
};
