/** A page of the administration; it is visible with its permission. */
export interface AdminPageLink {
  path: string;
  label: string;
  permission: string;
  category: 'Benutzer' | 'Betrieb' | 'Konfiguration';
  description: string;
}

/**
 * The subpages of /admin in the order of the navigation; each permission mirrors x-required-permission of the
 * endpoints the page reads.
 */
export const ADMIN_PAGES: AdminPageLink[] = [
  {
    path: 'users',
    label: 'Nutzer',
    permission: 'user:read',
    category: 'Benutzer',
    description: 'Konten suchen, freischalten, sperren und Rollen zuweisen.',
  },
  {
    path: 'roles',
    label: 'Rollen',
    permission: 'user:read',
    category: 'Benutzer',
    description: 'Rollen als Bündel von Berechtigungen anlegen und bearbeiten.',
  },
  {
    path: 'crawler',
    label: 'Crawler',
    permission: 'jobs:read',
    category: 'Betrieb',
    description: 'Abrufe der Datenquellen live: Budget, Pausen, Abfragepunkte und letzte Läufe.',
  },
  {
    path: 'mail',
    label: 'E-Mail',
    permission: 'settings:manage',
    category: 'Betrieb',
    description: 'Testmail senden und den Postausgang prüfen.',
  },
  {
    path: 'settings',
    label: 'Einstellungen',
    permission: 'settings:read',
    category: 'Konfiguration',
    description: 'Alle fachlichen Einstellungen: Datenquellen, Anmeldung, E-Mail, Karte.',
  },
];

/** Permissions that open the administration (any one of them). */
export const ADMIN_PERMISSIONS = [...new Set(ADMIN_PAGES.map((page) => page.permission))];
