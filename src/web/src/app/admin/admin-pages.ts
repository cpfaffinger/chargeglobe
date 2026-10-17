/** A page of the administration; it is visible with its permission. */
export interface AdminPageLink {
  path: string;
  label: string;
  permission: string;
  category: 'Benutzer' | 'Betrieb' | 'Konfiguration' | 'Preise';
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
    path: 'audit',
    label: 'Audit-Log',
    permission: 'audit:read',
    category: 'Benutzer',
    description: 'Wer hat wann was geändert: Konten, Rollen, API-Keys, Einstellungen, Tarife.',
  },
  {
    path: 'crawler',
    label: 'Crawler',
    permission: 'jobs:read',
    category: 'Betrieb',
    description: 'Abrufe der Datenquellen live: Budget, Pausen, Abfragepunkte und letzte Läufe.',
  },
  {
    path: 'conflicts',
    label: 'Konflikte',
    permission: 'conflict:resolve',
    category: 'Betrieb',
    description: 'Widersprüche zwischen Datenquellen, etwa Datensätze, die zu mehreren Standorten passen.',
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
  {
    path: 'tariffs',
    label: 'Tarife',
    permission: 'tariff:write',
    category: 'Preise',
    description: 'Tarifkatalog: Ladekarten und Apps mit Heimnetz, Partnernetz und Roaming je Tarif.',
  },
  {
    path: 'prices',
    label: 'Ad-hoc-Preise',
    permission: 'jobs:read',
    category: 'Preise',
    description: 'Vermutlich falsche Ad-hoc-Preise im Register, je Betreiber mit Beispielen.',
  },
];

/** Permissions that open the administration (any one of them). */
export const ADMIN_PERMISSIONS = [...new Set(ADMIN_PAGES.map((page) => page.permission))];
