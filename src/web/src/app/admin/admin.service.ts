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
  /** Permissions granted directly, besides those of the roles. */
  extraPermissions: string[];
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

/** Element of GET /api/v1/users/{id}/api-keys; the key itself is never returned. */
export interface ApiKeyInfo {
  id: string;
  name: string;
  prefix: string;
  permissions: string[];
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

/** Element of GET /api/v1/audit-log. */
export interface AuditEntry {
  id: number;
  occurredAt: string;
  action: string;
  target: string;
  actor: { userId: string | null; email: string | null; apiKeyId: string | null; apiKeyName: string | null };
}

export interface AuditPage {
  data: AuditEntry[];
  meta: { returned: number; limit: number; nextCursor: string | null };
}

/** Filters of the audit log; empty values are left out. */
export interface AuditFilter {
  actor: string;
  action: string;
  entity: string;
  from: string;
  to: string;
}

export const USERS_PAGE_SIZE = 25;

export const AUDIT_PAGE_SIZE = 100;

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

  async setPermissions(id: string, permissions: string[]): Promise<UserSummary> {
    return (await firstValueFrom(this.http.put<{ data: UserSummary }>(`/api/v1/users/${id}/permissions`, { permissions }))).data;
  }

  /** Deletes an account for good (user:manage); idempotent. */
  async deleteUser(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/v1/users/${id}`));
  }

  async sendConfirmation(id: string): Promise<void> {
    await firstValueFrom(this.http.post(`/api/v1/users/${id}/confirmation-mail`, null));
  }

  /** All API keys of an account, oldest first (user:read). */
  apiKeys(userId: string): Promise<ApiKeyInfo[]> {
    return firstValueFrom(this.http.get<ApiKeyInfo[]>(`/api/v1/users/${userId}/api-keys`));
  }

  /** Revokes a key of an account (user:manage); idempotent. */
  async revokeApiKey(userId: string, keyId: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/v1/users/${userId}/api-keys/${keyId}`));
  }

  /** One page of the audit log, newest first; the cursor continues with older entries. */
  auditLog(filter: AuditFilter, cursor: string | null): Promise<AuditPage> {
    const params: Record<string, string | number> = { limit: AUDIT_PAGE_SIZE };
    for (const [name, value] of Object.entries(filter)) {
      if (value) {
        params[name] = value;
      }
    }

    if (cursor) {
      params['cursor'] = cursor;
    }

    return firstValueFrom(this.http.get<AuditPage>('/api/v1/audit-log', { params }));
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
