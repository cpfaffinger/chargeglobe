/** German labels of the API vocabulary (OCPI names); unknown values are shown as they are. */

const CONNECTORS: Record<string, string> = {
  IEC_62196_T2: 'Typ 2',
  IEC_62196_T2_COMBO: 'CCS (Combo 2)',
  IEC_62196_T1: 'Typ 1',
  IEC_62196_T1_COMBO: 'CCS (Combo 1)',
  CHADEMO: 'CHAdeMO',
  DOMESTIC_F: 'Schuko',
  TESLA_S: 'Tesla',
  UNKNOWN: 'unbekannter Stecker',
};

const FORMATS: Record<string, string> = { SOCKET: 'Buchse', CABLE: 'Kabel' };

const POWER_TYPES: Record<string, string> = {
  AC_1_PHASE: 'Wechselstrom, 1-phasig',
  AC_2_PHASE: 'Wechselstrom, 2-phasig',
  AC_2_PHASE_SPLIT: 'Wechselstrom, 2-phasig (split)',
  AC_3_PHASE: 'Wechselstrom, 3-phasig',
  DC: 'Gleichstrom',
};

const AUTH_METHODS: Record<string, string> = {
  RFID_READER: 'Ladekarte (RFID)',
  APP: 'App',
  QR_CODE: 'QR-Code',
  WEBSITE: 'Webseite',
  CHARGING_CONTRACT: 'Ladevertrag',
  CREDIT_CARD_PAYABLE: 'Kreditkarte',
  DEBIT_CARD_PAYABLE: 'Debitkarte',
  CONTACTLESS_CARD_SUPPORT: 'kontaktlose Kartenzahlung',
  CHIP_CARD_SUPPORT: 'Chipkarte',
  PED_TERMINAL: 'Bezahlterminal mit PIN',
  CASH: 'Bargeld',
};

/** Payment without contract or account (AFIR ad hoc); same list as the API filter adHoc. */
export const AD_HOC_METHODS = [
  'QR_CODE',
  'WEBSITE',
  'CREDIT_CARD_PAYABLE',
  'DEBIT_CARD_PAYABLE',
  'CONTACTLESS_CARD_SUPPORT',
  'CHIP_CARD_SUPPORT',
  'PED_TERMINAL',
  'CASH',
];

const ATTRIBUTES: Record<string, string> = {
  name: 'Name',
  address: 'Adresse',
  postal_code: 'Postleitzahl',
  city: 'Ort',
  geom: 'Koordinaten',
  operator_id: 'Betreiber',
  access: 'Zugang',
  opening_times: 'Öffnungszeiten',
  green_energy: 'Ökostrom',
  free_parking: 'kostenloses Parken',
  roofed_parking: 'überdachtes Parken',
  barrier_free_parking_spaces: 'barrierefreie Plätze',
  price_url: 'Preisseite',
  location_id: 'Standort',
  max_power_kw: 'maximale Leistung',
  power_type: 'Stromart',
  auth_methods: 'Zahlungsarten',
  roaming: 'Roaming',
  physical_reference: 'Bezeichnung vor Ort',
  connectors: 'Stecker',
};

const label = (labels: Record<string, string>) => (value: string | null): string => (value === null ? '–' : (labels[value] ?? value));

export const connectorLabel = label(CONNECTORS);
export const formatLabel = label(FORMATS);
export const powerTypeLabel = label(POWER_TYPES);
export const authMethodLabel = label(AUTH_METHODS);
export const attributeLabel = label(ATTRIBUTES);

/** Ja / nein / unbekannt for optional flags. */
export const yesNo = (value: boolean | null): string => (value === null ? 'unbekannt' : value ? 'ja' : 'nein');
