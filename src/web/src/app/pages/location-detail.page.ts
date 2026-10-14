import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AppIcon } from '../design/icon';
import { LocationDetails } from '../evses/location-details';
import { FavoriteButton } from '../favorites/favorite-button';

/**
 * Page of a charging location for direct links (guard with evse:read in app.routes.ts); on the map the same
 * details open in the info card, so the map keeps its position.
 */
@Component({
  imports: [AppIcon, FavoriteButton, LocationDetails, MatButtonModule, RouterLink],
  selector: 'app-location-detail',
  template: `
    <div class="page-actions">
      <a matButton routerLink="/" id="back"><app-icon name="arrow_back" />Zur Karte</a>
      <app-favorite-button [locationId]="id" />
    </div>
    <app-location-details [locationId]="id" [header]="true" />
  `,
})
export class LocationDetailPage {
  protected readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;
}
