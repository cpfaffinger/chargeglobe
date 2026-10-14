import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type AccountState = 'ACTIVE' | 'PENDING_CONFIRMATION' | 'PENDING_APPROVAL' | 'DISABLED';

/** Element of GET /api/v1/users. */
export interface UserSummary {
  id: string;
  email: string;
  state: AccountState;
  emailConfirmed: boolean;
  createdAt: string;
  approvedAt: string | null;
  disabledAt: string | null;
  roles: string[];
}

export interface UserPage {
  data: UserSummary[];
  meta: { total: number; page: number; pageSize: number };
}

/** Element of GET /api/v1/roles. */
export interface RoleSummary {
  name: string;
  permissions: string[];
  users: number;
  fullCatalog: boolean;
  default: boolean;
}

/** Element of GET /api/v1/permissions. */
export interface PermissionDefinition {
  key: string;
  description: string;
}

export const USERS_PAGE_SIZE = 25;

/** Client of the user and role administration (user:read, user:manage, role:manage). */
@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly http = inject(HttpClient);

  /** One page of the accounts, newest first; an empty search means all. */
  users(search: string, page: number): Promise<UserPage> {
    const params: Record<string, string | number> = { page, pageSize: USERS_PAGE_SIZE };
    if (search) {
      params['q'] = search;
    }

    return firstValueFrom(this.http.get<UserPage>('/api/v1/users', { params }));
  }

  async setRoles(id: string, roles: string[]): Promise<UserSummary> {
    return (await firstValueFrom(this.http.put<{ data: UserSummary }>(`/api/v1/users/${id}/roles`, { roles }))).data;
  }

  async setApproved(id: string, approved: boolean): Promise<UserSummary> {
    return (await firstValueFrom(this.http.put<{ data: UserSummary }>(`/api/v1/users/${id}/approval`, { approved }))).data;
  }

  async setEnabled(id: string, enabled: boolean): Promise<UserSummary> {
    return (await firstValueFrom(this.http.put<{ data: UserSummary }>(`/api/v1/users/${id}/enabled`, { enabled }))).data;
  }

  async sendConfirmation(id: string): Promise<void> {
    await firstValueFrom(this.http.post(`/api/v1/users/${id}/confirmation-mail`, null));
  }

  async roles(): Promise<RoleSummary[]> {
    return (await firstValueFrom(this.http.get<{ data: RoleSummary[] }>('/api/v1/roles'))).data;
  }

  async putRole(name: string, permissions: string[]): Promise<RoleSummary> {
    return (await firstValueFrom(this.http.put<{ data: RoleSummary }>(`/api/v1/roles/${encodeURIComponent(name)}`, { permissions }))).data;
  }

  async deleteRole(name: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/v1/roles/${encodeURIComponent(name)}`));
  }

  permissions(): Promise<PermissionDefinition[]> {
    return firstValueFrom(this.http.get<PermissionDefinition[]>('/api/v1/permissions'));
  }
}
