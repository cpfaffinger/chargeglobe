import { Injectable } from '@angular/core';
import { MatPaginatorIntl } from '@angular/material/paginator';

/** German texts of the Material paginator, e.g. "Seite 2 von 3 (60 Einträge)". */
@Injectable()
export class GermanPaginatorIntl extends MatPaginatorIntl {
  override itemsPerPageLabel = 'Einträge je Seite';
  override nextPageLabel = 'Nächste Seite';
  override previousPageLabel = 'Vorherige Seite';
  override firstPageLabel = 'Erste Seite';
  override lastPageLabel = 'Letzte Seite';

  override getRangeLabel = (page: number, pageSize: number, length: number): string =>
    `Seite ${page + 1} von ${Math.max(1, Math.ceil(length / pageSize))} (${length} Einträge)`;
}
