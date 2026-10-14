import { HttpClient, HttpContext, HttpContextToken } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Response of GET /api/v1/me. */
export interface Me {
  id: string;
  email: string;
  roles: string[];
  permissions: string[];
  authenticatedVia: string;
}

interface TokenResponse {
  accessToken: string;
}

interface MessageResponse {
  message: string;
}

/** Marks requests the interceptor must not answer with a token refresh. */
export const SKIP_REFRESH = new HttpContextToken<boolean>(() => false);

const API = '/api/v1';

/**
 * Session state of the SPA. The access token lives in memory only; the refresh token is an
 * HttpOnly cookie the browser sends to /api/v1/auth, so a page reload restores the session
 * through {@link refresh}.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly token = signal<string | null>(null);
  private readonly current = signal<Me | null>(null);
  private refreshing: Promise<boolean> | null = null;

  /** The signed-in user, or null for anonymous visitors. */
  readonly me = this.current.asReadonly();
  readonly isAuthenticated = computed(() => this.current() !== null);

  accessToken(): string | null {
    return this.token();
  }

  hasPermission(permission: string): boolean {
    return this.current()?.permissions.includes(permission) ?? false;
  }

  async login(email: string, password: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<TokenResponse>(`${API}/auth/login`, { email, password }),
    );
    await this.startSession(response.accessToken);
  }

  /**
   * Exchanges the refresh cookie for a new access token. Resolves to false if there is no
   * valid session. Parallel calls share one request, because every refresh token works once.
   */
  refresh(): Promise<boolean> {
    this.refreshing ??= this.exchangeRefreshCookie().finally(() => (this.refreshing = null));
    return this.refreshing;
  }

  /** Creates the first administrator of a new instance (setup wizard) and signs it in like login. */
  async createInitialAdmin(setupToken: string, email: string, password: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<TokenResponse>(`${API}/setup/admin`, { setupToken, email, password }),
    );
    await this.startSession(response.accessToken);
  }

  async logout(): Promise<void> {
    // The local session ends in any case; a failed request leaves at most a cookie that expires.
    await firstValueFrom(this.http.post(`${API}/auth/logout`, null)).catch(() => undefined);
    this.clear();
  }

  async register(email: string, password: string, language: string): Promise<string> {
    const response = await firstValueFrom(
      this.http.post<MessageResponse>(`${API}/auth/register`, { email, password, language }),
    );
    return response.message;
  }

  async confirmEmail(userId: string, token: string): Promise<void> {
    await firstValueFrom(this.http.post(`${API}/auth/confirm-email`, { userId, token }));
  }

  async requestPasswordReset(email: string, language: string): Promise<string> {
    const response = await firstValueFrom(
      this.http.post<MessageResponse>(`${API}/auth/password/forgot`, { email, language }),
    );
    return response.message;
  }

  async resetPassword(userId: string, token: string, newPassword: string): Promise<void> {
    await firstValueFrom(
      this.http.post(`${API}/auth/password/reset`, { userId, token, newPassword }),
    );
  }

  private async exchangeRefreshCookie(): Promise<boolean> {
    try {
      const response = await firstValueFrom(
        this.http.post<TokenResponse>(`${API}/auth/refresh`, null),
      );
      await this.startSession(response.accessToken);
      return true;
    } catch {
      this.clear();
      return false;
    }
  }

  private async startSession(accessToken: string): Promise<void> {
    this.token.set(accessToken);
    this.current.set(
      await firstValueFrom(
        this.http.get<Me>(`${API}/me`, { context: new HttpContext().set(SKIP_REFRESH, true) }),
      ),
    );
  }

  private clear(): void {
    this.token.set(null);
    this.current.set(null);
  }
}

/** App initializer: restores the session from the refresh cookie before the first navigation. */
export const restoreSession = (): Promise<boolean> => inject(AuthService).refresh();
