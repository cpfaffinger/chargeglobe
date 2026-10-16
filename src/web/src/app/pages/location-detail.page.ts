import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { AppIcon } from '../design/icon';
import { LocationDetails } from '../evses/location-details';
import { FavoriteButton } from '../favorites/favorite-button';

/**
 * Page of a charging location for direct links (guard with evse:read in app.routes.ts); on the map the same
 * details open in the info card, so the map keeps its position. Another station of its charging park opens in place.
 */
@Component({
  imports: [AppIcon, FavoriteButton, LocationDetails, MatButtonModule, RouterLink],
  selector: 'app-location-detail',
  template: `
    <div class="page-actions">
      <a matButton routerLink="/" id="back"><app-icon name="arrow_back" />Zur Karte</a>
      <app-favorite-button [locationId]="id()" />
    </div>
    <app-location-details [locationId]="id()" [header]="true" (openLocation)="open($event.id)" />
  `,
})
export class LocationDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly id = toSignal(this.route.paramMap.pipe(map((params) => params.get('id')!)), {
    initialValue: this.route.snapshot.paramMap.get('id')!,
  });

  protected open(id: string): void {
    void this.router.navigate(['/locations', id]);
  }
}
