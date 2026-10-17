import { RouterLink } from '@angular/router';
import { DatePipe, DecimalPipe, NgTemplateOutlet } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { HttpErrorResponse } from '@angular/common/http';
import { MatMenuModule } from '@angular/material/menu';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { ThemeService, Viewport } from '../design/theme.service';
import { LocationDetails } from '../evses/location-details';
import { money } from '../evses/prices';
import { CardsStore } from '../preferences/cards.store';
import { BATTERY, CONSUMPTION, DEFAULT_ARRIVAL_SOC, DEFAULT_START_SOC, PlannedStop, SOC, Vehicle, VehicleStore, planCharging } from '../map/charging-plan';
import { FavoriteButton } from '../favorites/favorite-button';
import { MARKERS, MARKER_KEYS, MarkerKey, busyText, markerOf, resolveColors, tokenColor } from '../map/availability';
import { BrowserCapabilities } from '../map/browser-capabilities';
import { MapConfig, MapConfigService } from '../map/map-config.service';
import { GeolocateEvent, MapEngine, MapHandle, MapTheme } from '../map/map-engine';
import { MapFilterPanel } from '../map/map-filter-panel';
import { MapOptionsPanel } from '../map/map-options-panel';
import { MapOptionsStore } from '../map/map-options';
import { PlaceSearch } from '../map/place-search';
import { MapFilterStore, filterParams } from '../map/map-filters';
import { MapHints, TRUNCATED_NOTICE_MS } from '../map/map-hints';
import { MapLink, ShareResult } from '../map/map-link';
import { MapView, MapViewStore } from '../map/map-view.store';
import {
  MAX_CHARGING_STOPS,
  PRICE_SESSION_KWH,
  REACH_MINUTES,
  ReachAreas,
  smallestArea,
  RouteLocation,
  RouteResponse,
  RoutesService,
  Place,
  StopPrice,
  Trip,
  locationPlace,
  ownPlace,
  pointPlace,
  byPrice,
  cheapest,
  kilometres,
  routeSummary,
  stopsSummary,
  withStop,
} from '../map/routes.service';
import { Bbox, LocationSummary, LocationsService } from '../map/locations.service';
import { MapFocus } from '../search/search.service';

/** Free locations listed in reach. */
export const FASTEST_FREE = 10;

/** "bis 15 min", "15–30 min", … for the band ending at the value with this index. */
function bandLabel(values: number[], index: number): string {
  return index === 0 ? `bis ${values[0]} min` : `${values[index - 1]}–${values[index]} min`;
}

/** Zoom of the map on a search hit: streets and single locations visible. */
export const FOCUS_ZOOM = 16;

/**
 * Age up to which loaded locations count as current: zooming into an area that was loaded completely within this
 * time needs no new request.
 */
export const LOCATIONS_FRESH_MS = 60_000;

/** Reasons the map or the own position cannot be shown. */
export type MapNotice =
  | 'config'
  | 'webgl'
  | 'map'
  | 'locations'
  | 'insecure'
  | 'unsupported'
  | 'denied'
  | 'unavailable'
  | 'not-ready'
  | 'route-unavailable'
  | 'route-none'
  | 'route-failed'
  | 'reach-none'
  | 'reach-failed';

const NOTICES: Record<MapNotice, string> = {
  config: 'Die Kartenkonfiguration konnte nicht geladen werden. Bitte lade die Seite später neu.',
  webgl: 'Dein Browser unterstützt kein WebGL, das die Karte zum Zeichnen braucht. Aktiviere die Hardwarebeschleunigung oder nutze einen aktuellen Browser.',
  map: 'Die Karte konnte nicht angezeigt werden. Bitte lade die Seite neu.',
  locations: 'Die Ladepunkte konnten nicht geladen werden. Verschiebe die Karte, um es erneut zu versuchen.',
  insecure: 'Der Standort ist nur über eine sichere Verbindung (HTTPS) verfügbar.',
  unsupported: 'Dein Browser bietet keine Standortbestimmung an.',
  denied: 'Die Standortfreigabe wurde abgelehnt. Du kannst sie in den Website-Einstellungen deines Browsers erlauben.',
  unavailable: 'Dein Standort konnte nicht ermittelt werden. Bitte versuche es erneut.',
  'not-ready': 'Die Standortfunktion ist noch nicht bereit. Bitte versuche es gleich noch einmal.',
  'route-unavailable': 'Die Routenberechnung ist gerade nicht verfügbar. Bitte versuche es später.',
  'route-none': 'Zu diesem Ladestandort wurde keine Straßenverbindung gefunden.',
  'route-failed': 'Die Route konnte nicht berechnet werden. Bitte versuche es erneut.',
  'reach-none': 'In der Nähe deines Standorts wurde keine Straße gefunden.',
  'reach-failed': 'Die Reichweite konnte nicht berechnet werden. Bitte versuche es erneut.',
};

const SHARE_MESSAGES: Record<ShareResult, string | null> = {
  shared: null,
  cancelled: null,
  copied: 'Der Link zu dieser Ansicht ist in der Zwischenablage.',
  failed: 'Der Link konnte nicht kopiert werden. Kopiere die Adresse aus der Adresszeile.',
};

/** Whether the box `inner` lies within `outer`. */
const covers = (outer: Bbox, inner: Bbox): boolean =>
  inner.west >= outer.west && inner.east <= outer.east && inner.south >= outer.south && inner.north <= outer.north;

/**
 * Start page, usable without an account: the base map (docs/adr/0007-karte-standort-routing.md) with the charging
 * locations of the visible area and the own position. A click on a location opens its details in the info card
 * next to the map (a bottom sheet on small screens), so the map keeps its position. The map opens at the last
 * view; "Startposition" returns to the view the user chose (both stay in the browser), else to the default of the
 * configuration. The position is processed in the browser only and never sent to the API.
 */
@Component({
  imports: [AppLoading, AppIcon, PlaceSearch, DatePipe, DecimalPipe, FavoriteButton, LocationDetails, MapFilterPanel, MapOptionsPanel, MatButtonModule,
    MatButtonToggleModule, MatCardModule, MatMenuModule, MatProgressSpinnerModule, NgTemplateOutlet, RouterLink],
  selector: 'app-home',
  host: { '[class.has-card]': 'selected() !== null || shownStopsState() !== null || shownReachState() !== null', '[class.has-filters]': 'filtersOpen() || optionsOpen()' },
  template: `
    <ng-template #card>
      @if (selected(); as location) {
        <mat-card id="location-card" appearance="outlined" role="region" aria-label="Ladestandort">
          <mat-card-content>
            <div class="card-head">
              <div>
                <h2>{{ location.name ?? 'Ladestandort' }}</h2>
                <p class="address">{{ location.address }}, {{ location.postalCode }} {{ location.city }}</p>
              </div>
              <app-favorite-button [locationId]="location.id" />
              <button matIconButton type="button" id="close-card" aria-label="Schließen" (click)="selected.set(null)">
                <app-icon name="close" />
              </button>
            </div>
            @if (location.operator) {
              <p class="operator">{{ location.operator }}</p>
            }
            <p class="availability">
              <span class="status-dot" [style.background]="color(marker(location))"><app-icon [name]="markers[marker(location)].icon" /></span>
              {{ location.evsesAvailable }} von {{ location.evsesTotal }} Ladepunkten frei{{ busyParts(location) }}
              @if (location.maxPowerKw !== null) {
                · bis {{ location.maxPowerKw | number: '1.0-1' : 'en-US' }} kW
              }
            </p>
            <p class="status-age">
              @if (location.statusObservedAt) {
                Status von {{ location.statusObservedAt | date: 'dd.MM.yyyy HH:mm' }}
              } @else {
                Kein aktueller Status
              }
            </p>
            @if (canRoute()) {
              <div class="route-actions">
                <button matButton="tonal" type="button" id="route-here" [disabled]="routing() || !handle()" (click)="routeTo(location)">
                  <app-icon name="directions" />{{ routing() ? 'Route wird berechnet …' : 'Route hierher' }}
                </button>
                <button matButton type="button" id="route-from" [disabled]="routing() || !handle()" (click)="routeFrom(location)">Route von hier</button>
              </div>
            }
            <!-- The details are a chunk of their own and load after the card is visible. -->
            @defer (on immediate) {
              <app-location-details [locationId]="location.id" (openLocation)="selected.set($event)" />
            } @placeholder {
              <app-loading class="details-state" text="Ladepunkte werden geladen …" />
            }
          </mat-card-content>
        </mat-card>
      } @else if (shownStopsState(); as state) {
        <mat-card id="route-stops" appearance="outlined" role="region" aria-label="Unterwegs laden">
          <mat-card-content>
            <div class="card-head">
              <h2>Unterwegs laden</h2>
              <button matIconButton type="button" id="close-stops" aria-label="Liste schließen" title="Liste schließen" (click)="stopsHidden.set(true)">
                <app-icon name="close" />
              </button>
            </div>
            @switch (state) {
              @case ('loading') {
                <app-loading class="muted" text="Ladepunkte entlang der Route werden gesucht …" />
              }
              @case ('error') {
                <p class="error">Die Ladepunkte entlang der Route konnten nicht geladen werden.</p>
              }
              @default {
                <details id="vehicle">
                  @let car = vehicle();
                  <summary>
                    Fahrzeug: {{ decimal(car.consumptionKwhPer100Km, 1) }} kWh/100 km · Akku {{ decimal(car.batteryKwh, 1) }} kWh ·
                    Start {{ car.startSoc ?? defaultStartSoc }} % · Ankunft {{ car.arrivalSoc ?? defaultArrivalSoc }} %
                  </summary>
                  <div class="vehicle-fields">
                    <label>Verbrauch (kWh/100 km)
                      <input type="number" id="vehicle-consumption" [min]="consumption.min" [max]="consumption.max" step="0.5"
                        [value]="car.consumptionKwhPer100Km" (change)="setVehicle('consumptionKwhPer100Km', $event)" />
                    </label>
                    <label>Akku nutzbar (kWh)
                      <input type="number" id="vehicle-battery" [min]="battery.min" [max]="battery.max" step="1"
                        [value]="car.batteryKwh" (change)="setVehicle('batteryKwh', $event)" />
                    </label>
                    <label>Ladezustand bei Abfahrt (%)
                      <input type="number" id="vehicle-start" [min]="soc.min" [max]="soc.max" step="1" [placeholder]="defaultStartSoc"
                        [value]="car.startSoc ?? ''" (change)="setVehicle('startSoc', $event)" />
                    </label>
                    <label>Gewünschter Ladezustand bei Ankunft (%)
                      <input type="number" id="vehicle-arrival" [min]="soc.min" [max]="soc.max" step="1" [placeholder]="defaultArrivalSoc"
                        [value]="car.arrivalSoc ?? ''" (change)="setVehicle('arrivalSoc', $event)" />
                    </label>
                  </div>
                </details>
                @if (chargingPlan(); as plan) {
                  <div id="charging-plan" class="map-message" [class.infeasible]="!plan.feasible">
                    @if (!plan.feasible) {
                      Mit diesen Werten reichen die Ladepunkte entlang der Route nicht bis zum Ziel. Prüfe Verbrauch, Akku oder die Filter der Karte.
                    } @else if (plan.stops.length === 0) {
                      Ohne Ladestopp erreichbar: Ankunft mit etwa {{ percent(plan.arrivalSoc) }} % (Energiebedarf {{ decimal(plan.energyKwh, 0) }} kWh).
                    } @else {
                      Empfohlen: {{ plan.stops.length }} {{ plan.stops.length === 1 ? 'Ladestopp' : 'Ladestopps' }}, Ankunft mit etwa
                      {{ percent(plan.arrivalSoc) }} % (Energiebedarf {{ decimal(plan.energyKwh, 0) }} kWh).
                      @if (canApplyPlan(plan.stops)) {
                        <button matButton="tonal" type="button" id="apply-plan" [disabled]="routing()" (click)="applyPlan(plan.stops)">Empfehlung übernehmen</button>
                      }
                    }
                  </div>
                }
                <p class="muted" id="stops-hint">{{ stopsTruncated() ? 'Die ersten ' : '' }}{{ stops().length }} Standorte bis 1 km neben der Route, mit den Filtern der Karte.</p>
                @if (canPrice() && stops().length > 0) {
                  <div class="stops-order">
                    <span id="stops-order-label">Reihenfolge:</span>
                    <mat-button-toggle-group id="stops-order" hideSingleSelectionIndicator aria-labelledby="stops-order-label"
                      [value]="stopsOrder()" (change)="stopsOrder.set($event.value)">
                      <mat-button-toggle value="route">Route</mat-button-toggle>
                      <mat-button-toggle value="price">günstigste zuerst</mat-button-toggle>
                    </mat-button-toggle-group>
                    @if (cards.tariffs().length > 0) {
                      <span class="muted" id="stops-own">Preise mit deinen Tarifen</span>
                    }
                  </div>
                }
                <ol>
                  @for (stop of shownStops(); track stop.id) {
                    <li [class.planned]="isPlanned(stop)" [class.recommended]="recommended().has(stop.id)">
                      <button type="button" class="open-stop" [attr.data-stop]="stop.id" (click)="openStop(stop)">
                        <span class="stop-name">
                          {{ km(stop.positionOnRouteMeters) }} · {{ stop.name ?? 'Ladestandort' }}
                          @if (isPlanned(stop)) {
                            <span class="stop-badge">Ladestopp</span>
                          }
                        </span>
                        @if (recommended().get(stop.id); as planned) {
                          <span class="stop-recommended">Empfohlen: an mit {{ percent(planned.arrivalSoc) }} %, laden auf {{ percent(planned.departureSoc) }} %</span>
                        }
                        <span class="stop-state">
                          <span class="status-dot" [style.background]="color(marker(stop))"><app-icon [name]="markers[marker(stop)].icon" /></span>
                          {{ stop.evsesAvailable }} von {{ stop.evsesTotal }} frei{{ busyParts(stop) }}
                          @if (stop.maxAvailablePowerKw !== null) {
                            · bis {{ stop.maxAvailablePowerKw | number: '1.0-1' : 'en-US' }} kW frei
                          }
                        </span>
                        @if (stopPrices().get(stop.id); as price) {
                          <span class="stop-price">ab {{ money(price.cost, price.currency) }} ({{ sessionKwh }} kWh · {{ price.label }})</span>
                        }
                      </button>
                      @if (canPlan(stop)) {
                        <button matButton type="button" class="add-stop" [disabled]="routing()" (click)="addStop(stop)">als Ladestopp</button>
                      }
                    </li>
                  } @empty {
                    <li class="muted">Keine Ladepunkte entlang der Route.</li>
                  }
                </ol>
              }
            }
          </mat-card-content>
        </mat-card>
      } @else if (shownReachState(); as state) {
        <mat-card id="reach-locations" appearance="outlined" role="region" aria-label="In Reichweite">
          <mat-card-content>
            <div class="card-head">
              <h2>In Reichweite</h2>
              <button matIconButton type="button" id="close-reach-list" aria-label="Liste schließen" title="Liste schließen" (click)="reachHidden.set(true)">
                <app-icon name="close" />
              </button>
            </div>
            @switch (state) {
              @case ('loading') {
                <app-loading class="muted" text="Ladepunkte in Reichweite werden gesucht …" />
              }
              @case ('error') {
                <p class="error">Die Ladepunkte in Reichweite konnten nicht geladen werden.</p>
              }
              @default {
                <p class="muted" id="reach-hint">
                  {{ reachTotals().get(reachLargest()) ?? 0 }} Standorte in Reichweite, mit den Filtern der Karte.
                  @if (reachTruncated()) {
                    Die Liste nutzt die nächsten {{ reachLocations().length }}.
                  }
                </p>
                <ul id="reach-counts">
                  @for (band of reachBands(); track band.minutes) {
                    <li [attr.data-minutes]="band.minutes"><span class="band-count">{{ band.count }}</span><span class="band-label">{{ band.label }}</span></li>
                  }
                </ul>
                <h3>Schnellste freie Ladepunkte</h3>
                <ol>
                  @for (location of fastestFree(); track location.id) {
                    <li>
                      <button type="button" class="open-stop" [attr.data-location]="location.id" (click)="openStop(location)">
                        <span class="stop-name">{{ location.name ?? 'Ladestandort' }}</span>
                        <span class="stop-state">
                          bis {{ location.maxAvailablePowerKw | number: '1.0-1' : 'en-US' }} kW frei · {{ location.evsesAvailable }} von {{ location.evsesTotal }} frei{{ busyParts(location) }}
                          @if (bandLabel(location); as label) {
                            · {{ label }}
                          }
                        </span>
                      </button>
                    </li>
                  } @empty {
                    <li class="muted">Gerade kein freier Ladepunkt in Reichweite.</li>
                  }
                </ol>
              }
            }
          </mat-card-content>
        </mat-card>
      }
    </ng-template>
    <div #map id="map" role="region" aria-label="Karte"></div>
    @if (loading()) {
      <div id="map-loading" role="progressbar" aria-label="Ladepunkte werden geladen"></div>
    }
    <ng-template #legend>
      @if (hints.legendVisible()) {
        <div class="legend-box">
          <ul id="legend" aria-label="Legende">
            @for (key of markerKeys; track key) {
              <li>
                <span class="status-dot" [style.background]="color(key)"><app-icon [name]="markers[key].icon" /></span>{{ markers[key].label }}
              </li>
            }
          </ul>
          <a routerLink="/help/begriffe" id="legend-help" title="Was ist ein Ladepunkt, eine Ladestation, ein Anschluss?">Begriffe</a>
          <button type="button" id="legend-hide" aria-label="Legende ausblenden" title="Legende ausblenden" (click)="hints.setLegend(false)">
            <app-icon name="close" />
          </button>
        </div>
      }
    </ng-template>
    <div id="map-overlay">
      @if (viewport.mobile()) {
        <!-- On phones the legend heads the column, so notices, route and reach stack below it. -->
        <ng-container [ngTemplateOutlet]="legend" />
      }
      @if (notice(); as key) {
        <p id="map-notice" class="map-message" role="status">{{ notices[key] }}</p>
      }
      @if (working(); as text) {
        <p id="working" class="map-message" role="status"><mat-spinner diameter="18" />{{ text }}</p>
      }
      @if (planner(); as plan) {
        <div id="route-planner" class="map-message" role="group" aria-label="Route planen">
          <div class="planner-row">
            <span class="planner-label">Start</span>
            <span id="planner-start" class="planner-place">{{ plan.start?.name ?? ownPositionName }}</span>
            @if (plan.start) {
              <button matButton type="button" id="planner-own" (click)="useOwnPosition()">{{ ownPositionName }}</button>
            }
            <button matButton type="button" id="pick-start" [class.picking]="plan.picking === 'start'" (click)="pick('start')">Auf Karte wählen</button>
            <app-place-search id="planner-start-search" label="Start suchen" [near]="view()" (picked)="setPlace('start', $event)" />
          </div>
          <div class="planner-row">
            <span class="planner-label">Ziel</span>
            <span id="planner-destination" class="planner-place">{{ plan.destination?.name ?? 'noch nicht gewählt' }}</span>
            <button matButton type="button" id="pick-destination" [class.picking]="plan.picking === 'destination'" (click)="pick('destination')">Auf Karte wählen</button>
            <app-place-search id="planner-destination-search" label="Ziel suchen" [near]="view()" (picked)="setPlace('destination', $event)" />
          </div>
          @if (plan.picking) {
            <p id="planner-hint">Tippe auf die Karte oder einen Ladestandort, um {{ plan.picking === 'start' ? 'den Start' : 'das Ziel' }} festzulegen.</p>
          }
          <div class="planner-actions">
            <button matButton="filled" type="button" id="planner-calculate" [disabled]="!plan.destination || routing()" (click)="calculatePlanned()">Route berechnen</button>
            <button matButton type="button" id="planner-cancel" (click)="planner.set(null)">Abbrechen</button>
          </div>
        </div>
      }
      @if (route(); as summary) {
        <div id="route-summary" class="map-message" role="status">
          <button matIconButton type="button" id="route-edit" class="summary-edit" aria-label="Start und Ziel ändern" title="Start und Ziel ändern"
            (click)="editRoute()">
            <app-icon name="directions" />
          </button>
          Route: {{ summary }}
          <button matButton type="button" id="route-clear" (click)="clearRoute()">Route entfernen</button>
          @if (stopsHidden()) {
            <button matButton type="button" id="show-stops" (click)="stopsHidden.set(false)">Liste zeigen</button>
          }
          @if (trip()?.stops?.length) {
            <div class="trip-stops">
              @for (stop of trip()!.stops; track stop.id) {
                <button matButton="tonal" type="button" class="remove-stop" [attr.data-stop]="stop.id" [disabled]="routing()"
                  [attr.aria-label]="'Stopp entfernen: ' + (stop.name ?? 'Ladestandort')" (click)="removeStop(stop)">
                  ✕ {{ stop.name ?? 'Ladestandort' }}
                </button>
              }
            </div>
          }
        </div>
      }
      @if (reach(); as minutes) {
        <div id="reach-summary" class="map-message" role="status">
          Reichweite mit dem Auto: {{ minutes.join(' · ') }} min
          <button matButton type="button" id="reach-clear" (click)="clearReach()">Reichweite entfernen</button>
          @if (reachHidden()) {
            <button matButton type="button" id="show-reach-list" (click)="reachHidden.set(false)">Liste zeigen</button>
          }
        </div>
      }
      @if (homeSaved()) {
        <p id="home-saved" class="map-message" role="status">Dieser Ausschnitt ist jetzt deine Startposition.</p>
      }
      @if (shareMessage(); as message) {
        <p id="share-message" class="map-message" role="status">{{ message }}</p>
      }
      @if (truncatedShown()) {
        <p id="truncated" class="map-message dismissible" role="status">
          Nur die nächsten {{ limit }} Standorte – zoome hinein.
          <button matIconButton type="button" id="truncated-close" aria-label="Hinweis ausblenden" title="Hinweis ausblenden" (click)="dismissTruncated()">
            <app-icon name="close" />
          </button>
        </p>
      }
      @if (!viewport.mobile()) {
        <ng-container [ngTemplateOutlet]="card" />
      }
    </div>
    @if (viewport.mobile()) {
      <ng-container [ngTemplateOutlet]="card" />
    }
    @if (filtersOpen()) {
      <!-- The filter controls are a chunk of their own, loaded when opened for the first time. -->
      @defer (on immediate) {
        <app-map-filter-panel (closed)="filtersOpen.set(false)" />
      }
    }
    @if (optionsOpen()) {
      @defer (on immediate) {
        <app-map-options-panel (closed)="optionsOpen.set(false)" />
      }
    }
    <div id="map-actions">
      <div class="home-actions" [hidden]="viewport.mobile() && !actionsOpen()">
        @if (viewport.mobile()) {
          <button matMiniFab type="button" id="filter-toggle" title="Filter" aria-label="Filter" [attr.aria-expanded]="filtersOpen()" (click)="toggleFilters()">
            <app-icon name="filter_list" />
            @if (filterStore.count() > 0) {
              <span class="filter-count fab-count" [attr.aria-label]="filterStore.count() + ' aktiv'">{{ filterStore.count() }}</span>
            }
          </button>
        }
        <button matMiniFab type="button" id="home" [disabled]="!handle()" title="Zur Startposition" aria-label="Zur Startposition" (click)="goHome()">
          <app-icon name="home" />
        </button>
        <button matMiniFab type="button" id="options-toggle" title="Kartenoptionen" aria-label="Kartenoptionen" [attr.aria-expanded]="optionsOpen()" (click)="toggleOptions()">
          <app-icon name="layers" />
        </button>
        <button matMiniFab type="button" id="share" [disabled]="!view()" title="Ansicht teilen" aria-label="Ansicht teilen" (click)="share()">
          <app-icon name="share" />
        </button>
        <button matMiniFab type="button" id="home-menu" title="Startposition festlegen" aria-label="Startposition festlegen" [matMenuTriggerFor]="homeMenu">
          <app-icon name="edit_location" />
        </button>
        <mat-menu #homeMenu="matMenu">
          <button mat-menu-item type="button" id="set-home" [disabled]="!view()" (click)="setHome()">Aktuellen Ausschnitt als Startposition</button>
          <button mat-menu-item type="button" id="reset-home" [disabled]="!views.home()" (click)="views.resetHome()">Startposition zurücksetzen</button>
        </mat-menu>
      </div>
      @if (viewport.mobile()) {
        <!-- The toggle keeps its place below the unfolded actions, so that the same spot closes them again. -->
        <button matMiniFab type="button" id="actions-toggle" [attr.aria-expanded]="actionsOpen()"
          [attr.aria-label]="actionsOpen() ? 'Kartenfunktionen schließen' : 'Weitere Kartenfunktionen'" (click)="actionsOpen.set(!actionsOpen())">
          <app-icon [name]="actionsOpen() ? 'close' : 'more_vert'" />
          <!-- Active filters stay visible while the filter button is folded away. -->
          @if (!actionsOpen() && filterStore.count() > 0) {
            <span class="filter-count fab-count" [attr.aria-label]="filterStore.count() + ' Filter aktiv'">{{ filterStore.count() }}</span>
          }
        </button>
      }
      @if (canRoute()) {
        <button matFab [extended]="!viewport.mobile()" type="button" id="reach" [disabled]="!handle() || reaching()" title="Reichweite"
          aria-label="Reichweite" (click)="showReach()">
          @if (reaching()) {
            <mat-spinner diameter="20" />
          } @else {
            <app-icon name="explore" />
          }
          @if (!viewport.mobile()) {
            {{ reaching() ? 'Reichweite wird berechnet …' : 'Reichweite' }}
          }
        </button>
      }
      <button matFab [extended]="!viewport.mobile()" type="button" id="locate" [disabled]="!handle()" title="Mein Standort" aria-label="Mein Standort"
        (click)="locate()">
        @if (locating()) {
          <mat-spinner diameter="20" />
        } @else {
          <app-icon name="my_location" />
        }
        @if (!viewport.mobile()) {
          {{ locating() ? 'Standort wird gesucht …' : 'Mein Standort' }}
        }
      </button>
    </div>
    <div id="map-legend-bar">
      @if (!viewport.mobile()) {
        <ng-container [ngTemplateOutlet]="legend" />
      }
      @if (!hints.legendVisible()) {
        <!-- The folded legend: a small symbol next to the filter button. -->
        <button matMiniFab type="button" id="legend-show" aria-label="Legende einblenden" title="Legende einblenden" (click)="hints.setLegend(true)">
          <app-icon name="info" />
        </button>
      }
      <!-- On phones the filter button is part of the folded map actions. -->
      @if (!viewport.mobile()) {
        <button matButton="tonal" type="button" id="filter-toggle" [attr.aria-expanded]="filtersOpen()" (click)="toggleFilters()">
          <app-icon name="filter_list" />Filter
          @if (filterStore.count() > 0) {
            <span class="filter-count" [attr.aria-label]="filterStore.count() + ' aktiv'">{{ filterStore.count() }}</span>
          }
        </button>
      }
    </div>
    @if (attribution().length > 0) {
      <p id="data-attribution">{{ attribution().join(' · ') }}</p>
    }
  `,
})
export class HomePage {
  private readonly configService = inject(MapConfigService);
  private readonly engine = inject(MapEngine);
  private readonly browser = inject(BrowserCapabilities);
  private readonly locationsService = inject(LocationsService);
  private readonly theme = inject(ThemeService);
  private readonly focus = inject(MapFocus);
  protected readonly views = inject(MapViewStore);
  protected readonly filterStore = inject(MapFilterStore);
  private readonly link = inject(MapLink);
  private readonly routes = inject(RoutesService);
  private readonly auth = inject(AuthService);
  /** Routing needs route:read (an account). */
  protected readonly canRoute = () => this.auth.hasPermission('route:read');
  protected readonly routing = signal(false);
  /** Length and time of the shown route. */
  protected readonly route = signal<string | null>(null);
  /** Stops along the shown route; null while there is no route. */
  protected readonly stopsState = signal<'loading' | 'error' | 'ready' | null>(null);
  protected readonly stops = signal<RouteLocation[]>([]);
  protected readonly stopsTruncated = signal(false);
  protected readonly km = kilometres;
  /** Answers for an earlier route are dropped. */
  private routeRequest = 0;
  /** Start and destination being chosen (start null: the own position); null while the planner is closed. */
  protected readonly planner = signal<{ start: Place | null; destination: Place | null; picking: 'start' | 'destination' | null } | null>(null);
  protected readonly ownPositionName = 'Mein Standort';
  /** Start, charging stops and destination of the shown route; null while there is none. */
  protected readonly trip = signal<Trip | null>(null);
  /** Line of the shown route, for loading its stops again when the filters change. */
  private routeLine: RouteResponse['data']['geometry'] | null = null;
  protected readonly cards = inject(CardsStore);
  private readonly vehicles = inject(VehicleStore);
  protected readonly vehicle = this.vehicles.vehicle;
  protected readonly consumption = CONSUMPTION;
  protected readonly battery = BATTERY;
  protected readonly soc = SOC;
  protected readonly defaultStartSoc = DEFAULT_START_SOC;
  protected readonly defaultArrivalSoc = DEFAULT_ARRIVAL_SOC;
  /** Length of the shown route as the routing engine measured it. */
  private readonly routeMeters = signal<number | null>(null);
  /** Recommended charging stops among the list along the route, for the car of the viewer. */
  protected readonly chargingPlan = computed(() => {
    const meters = this.routeMeters();
    return meters !== null && this.stopsState() === 'ready' ? planCharging(meters, this.stops(), this.vehicle()) : null;
  });
  protected readonly recommended = computed(
    () => new Map((this.chargingPlan()?.stops ?? []).map((planned) => [planned.location.id, planned])),
  );
  protected readonly canPrice = () => this.auth.hasPermission('price:read');
  /** Lowest price per stop of the list (ad hoc or own card), loaded after the list. */
  protected readonly stopPrices = signal<ReadonlyMap<string, StopPrice>>(new Map());
  protected readonly stopsOrder = signal<'route' | 'price'>('route');
  protected readonly shownStops = computed(() => (this.stopsOrder() === 'price' ? byPrice(this.stops(), this.stopPrices()) : this.stops()));
  protected readonly money = money;
  protected readonly sessionKwh = PRICE_SESSION_KWH;
  /** The lists along the route and in reach can be closed and shown again from their summary. */
  protected readonly stopsHidden = signal(false);
  protected readonly reachHidden = signal(false);
  protected readonly shownStopsState = computed(() => (this.stopsHidden() ? null : this.stopsState()));
  protected readonly shownReachState = computed(() => (this.reachHidden() ? null : this.reachState()));
  /** Phones: the secondary map actions are folded behind one button. */
  protected readonly actionsOpen = signal(false);
  /** What is being calculated right now, shown with a spinner. */
  protected readonly working = computed(() =>
    this.routing() ? 'Route wird berechnet …' : this.reaching() ? 'Reichweite wird berechnet …' : null,
  );
  /** Driving times of the shown reach; null while none is shown. */
  protected readonly reach = signal<number[] | null>(null);
  protected readonly reaching = signal(false);
  /** Answers for a reach that was removed meanwhile are dropped. */
  private reachRequest = 0;
  /** The areas of the shown reach and where it starts, for loading its locations again when the filters change. */
  private readonly reachAreas = signal<ReachAreas | null>(null);
  private reachCenter: { latitude: number; longitude: number } | null = null;
  protected readonly reachState = signal<'loading' | 'error' | 'ready' | null>(null);
  protected readonly reachLocations = signal<LocationSummary[]>([]);
  protected readonly reachTruncated = signal(false);
  /** All matching locations per area (by its minutes), also beyond the list. */
  protected readonly reachTotals = signal<ReadonlyMap<number, number>>(new Map());
  protected readonly reachLargest = () => REACH_MINUTES[REACH_MINUTES.length - 1];
  /** Answers for older filters or a removed reach are dropped. */
  private reachListRequest = 0;
  /** Per location the driving time of the smallest area that contains it. */
  private readonly reachMinutes = computed(() => {
    const areas = this.reachAreas();
    return new Map(this.reachLocations().map((location) => [location.id, areas ? smallestArea(location, areas) : null]));
  });
  /**
   * The locations per band of driving time (up to 15, 15–30, 30–60 minutes): the totals of the nested areas minus the one
   * inside. Counted by the API, because the list of a large area ends at its limit.
   */
  protected readonly reachBands = computed(() => {
    const totals = this.reachTotals();
    const total = (minutes: number) => totals.get(minutes) ?? 0;
    return REACH_MINUTES.map((limit, index) => ({
      minutes: limit,
      label: bandLabel(REACH_MINUTES, index),
      count: Math.max(0, total(limit) - (index === 0 ? 0 : total(REACH_MINUTES[index - 1]))),
    }));
  });
  /** The free locations with the most power free, at most ten. */
  protected readonly fastestFree = computed(() =>
    this.reachLocations()
      .filter((location) => location.maxAvailablePowerKw !== null)
      .sort((a, b) => b.maxAvailablePowerKw! - a.maxAvailablePowerKw!)
      .slice(0, FASTEST_FREE),
  );
  /** The newest visible area, loaded again when the filters change. */
  private box: Bbox | null = null;
  /** Query parameters of the filters of the shown locations. */
  private filterKey: string | null = null;
  /** Location of a search hit whose info card opens once the map has loaded it. */
  private pending: string | null = null;
  protected readonly viewport = inject(Viewport);
  private config: MapConfig | null = null;
  /** The newest visible area that still has to be loaded; at most one request runs at a time. */
  private queued: Bbox | null = null;
  private busy = false;
  /** Area, completeness and time of the shown locations. */
  private shown: { box: Bbox; complete: boolean; at: number } | null = null;
  private readonly container = viewChild.required<ElementRef<HTMLElement>>('map');
  private destroyed = false;

  protected readonly notices = NOTICES;
  protected readonly notice = signal<MapNotice | null>(null);
  protected readonly locating = signal(false);
  protected readonly loading = signal(false);
  protected readonly handle = signal<MapHandle | null>(null);
  /** The current view (center and zoom), known after the map has loaded. */
  protected readonly view = signal<MapView | null>(null);
  protected readonly homeSaved = signal(false);
  protected readonly shareMessage = signal<string | null>(null);
  protected readonly filtersOpen = signal(false);
  protected readonly optionsOpen = signal(false);
  private readonly mapOptions = inject(MapOptionsStore);
  protected readonly locations = signal<LocationSummary[]>([]);
  protected readonly attribution = signal<string[]>([]);
  protected readonly truncated = signal(false);
  protected readonly hints = inject(MapHints);
  private readonly truncatedNoticeMs = inject(TRUNCATED_NOTICE_MS);
  /** The notice about a truncated map: shown for a few seconds when the map becomes truncated, unless snoozed. */
  protected readonly truncatedShown = signal(false);
  private truncatedTimer: ReturnType<typeof setTimeout> | undefined;
  protected readonly selected = signal<LocationSummary | null>(null);
  protected readonly markers = MARKERS;
  protected readonly markerKeys = MARKER_KEYS;
  protected readonly marker = markerOf;
  protected readonly limit = LocationsService.limit;
  protected readonly color = (key: MarkerKey) => tokenColor(MARKERS[key].token);

  constructor() {
    // The renderer is the largest download; it loads while the configuration is on its way.
    this.engine.prepare();
    afterNextRender(() => void this.show());
    // Light/dark switch: the map takes the matching style and marker colours. A map created later reads them itself.
    effect(() => {
      const dark = this.theme.dark();
      untracked(() => this.handle()?.setTheme(this.mapTheme(dark)));
    });
    // A search hit: center the map on it; its card opens when the locations there have arrived.
    effect(() => {
      const target = this.focus.target();
      const handle = this.handle();
      if (target && handle) {
        untracked(() => {
          this.focus.target.set(null);
          this.pending = target.locationId;
          handle.flyTo(target.latitude, target.longitude, FOCUS_ZOOM);
          this.openPending();
        });
      }
    });
    // Only the recommended charging stops get a ring on the map.
    effect(() => {
      const plan = this.chargingPlan();
      untracked(() => this.handle()?.highlightLocations(plan?.stops.map((planned) => planned.location) ?? []));
    });
    // Other cluster options: the map combines the locations anew.
    effect(() => {
      const options = this.mapOptions.options();
      untracked(() => this.handle()?.setCluster(options));
    });
    // Other filters: the shown locations no longer fit; the visible area loads again.
    effect(() => {
      const key = JSON.stringify(filterParams(this.filterStore.filters()));
      untracked(() => {
        if (this.filterKey !== null && key !== this.filterKey) {
          if (this.box) {
            this.shown = null;
            this.queue(this.box);
          }
          // The stops along a shown route follow the filters, without a new route.
          if (this.routeLine) {
            void this.loadStops(this.routeLine);
          }
          // Likewise the locations in a shown reach, without a new reach.
          if (untracked(this.reachAreas)) {
            void this.loadReachLocations();
          }
        }
        this.filterKey = key;
      });
    });
    // A truncated map is told briefly; closing the notice snoozes it (MapHints).
    effect(() => {
      const truncated = this.truncated();
      untracked(() => {
        clearTimeout(this.truncatedTimer);
        const show = truncated && !this.hints.truncatedSnoozed();
        this.truncatedShown.set(show);
        if (show) {
          this.truncatedTimer = setTimeout(() => this.truncatedShown.set(false), this.truncatedNoticeMs);
        }
      });
    });
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(this.truncatedTimer);
      this.destroyed = true;
      this.handle()?.destroy();
    });
  }

  /** Whether the browser may ask for the position; otherwise the notice says why not. */
  private async positionAllowed(): Promise<boolean> {
    if (!this.browser.secureContext()) {
      this.notice.set('insecure');
      return false;
    }
    if (!this.browser.geolocation()) {
      this.notice.set('unsupported');
      return false;
    }
    // After an earlier refusal the browser does not ask again, and MapLibre disables its control.
    if ((await this.browser.geolocationPermission()) === 'denied') {
      this.notice.set('denied');
      return false;
    }
    this.notice.set(null);
    return true;
  }

  protected async locate(): Promise<void> {
    if (!(await this.positionAllowed())) {
      return;
    }
    if (this.handle()!.locate()) {
      this.locating.set(true);
    } else {
      this.notice.set('not-ready');
    }
  }

  /**
   * Route by car from the own position to the location, drawn on the map. The position goes to the own API only
   * (ADR-0007), which neither stores nor logs it.
   */
  protected async routeTo(location: LocationSummary): Promise<void> {
    // The list of stops replaces the card of the destination.
    if (await this.calculate(null, locationPlace(location))) {
      this.selected.set(null);
    }
  }

  /** The route planner with this location as start; the destination is picked on the map next. */
  protected routeFrom(location: LocationSummary): void {
    this.planner.set({ start: locationPlace(location), destination: null, picking: 'destination' });
    this.selected.set(null);
  }

  /** The planner with start and destination of the shown route. */
  protected editRoute(): void {
    const trip = this.trip()!;
    this.planner.set({ start: trip.start.kind === 'own' ? null : trip.start, destination: trip.destination, picking: null });
  }

  protected pick(target: 'start' | 'destination'): void {
    this.planner.update((plan) => ({ ...plan!, picking: target }));
  }

  /** A place from the search field of start or destination. */
  protected setPlace(target: 'start' | 'destination', place: Place): void {
    this.planner.update((plan) => ({ ...plan!, [target]: place, picking: null }));
  }

  protected useOwnPosition(): void {
    this.planner.update((plan) => ({ ...plan!, start: null }));
  }

  protected async calculatePlanned(): Promise<void> {
    const plan = this.planner()!;
    if (await this.calculate(plan.start, plan.destination!)) {
      this.planner.set(null);
    }
  }

  /** The picked place goes to the field the planner waits for. */
  private place(place: Place): void {
    this.planner.update((plan) => ({ ...plan!, [plan!.picking!]: place, picking: null }));
  }

  /** A click on the map picks a point while the planner waits for one. */
  private clicked(latitude: number, longitude: number): void {
    if (this.planner()?.picking) {
      this.place(pointPlace(latitude, longitude));
    }
  }

  /**
   * Route from the start (null: the own position, asked for now) to the destination, without stops; true if it worked.
   * The own position goes to the own API only (ADR-0007), which neither stores nor logs it.
   */
  private async calculate(start: Place | null, destination: Place): Promise<boolean> {
    if (start === null && !(await this.positionAllowed())) {
      return false;
    }
    this.routing.set(true);
    try {
      let from = start;
      if (from === null) {
        try {
          from = ownPlace(await this.browser.currentPosition());
        } catch (error) {
          this.notice.set((error as GeolocationPositionError).code === 1 ? 'denied' : 'unavailable');
          return false;
        }
      }
      return await this.plan({ start: from, destination, stops: [] });
    } finally {
      this.routing.set(false);
    }
  }

  /**
   * Areas reachable by car within 15, 30 and 60 minutes from the own position. Like a route, the position goes to the own
   * API only (ADR-0007), which neither stores nor logs it.
   */
  protected async showReach(): Promise<void> {
    if (!(await this.positionAllowed())) {
      return;
    }
    const request = ++this.reachRequest;
    this.reaching.set(true);
    try {
      const position = await this.browser.currentPosition();
      const response = await this.routes.reach(position, REACH_MINUTES);
      if (request === this.reachRequest) {
        this.handle()!.showReach(response.data);
        this.reach.set(REACH_MINUTES);
        this.reachAreas.set(response.data);
        this.reachHidden.set(false);
        this.reachCenter = position;
        void this.loadReachLocations();
      }
    } catch (error) {
      if (error instanceof HttpErrorResponse) {
        this.notice.set(error.status === 503 ? 'route-unavailable' : error.status === 422 ? 'reach-none' : 'reach-failed');
      } else {
        this.notice.set((error as GeolocationPositionError).code === 1 ? 'denied' : 'unavailable');
      }
    } finally {
      this.reaching.set(false);
    }
  }

  protected clearReach(): void {
    this.reachRequest++;
    this.reachListRequest++;
    this.reach.set(null);
    this.reachAreas.set(null);
    this.reachCenter = null;
    this.reachState.set(null);
    this.reachLocations.set([]);
    this.handle()?.showReach(null);
  }

  protected bandLabel(location: LocationSummary): string | null {
    const minutes = this.reachMinutes().get(location.id);
    return minutes ? bandLabel(REACH_MINUTES, REACH_MINUTES.indexOf(minutes)) : null;
  }

  /** The locations inside the largest area and the counts of the smaller ones, with the filters of the map. */
  private async loadReachLocations(): Promise<void> {
    const request = ++this.reachListRequest;
    const areas = this.reachAreas()!;
    const filters = this.filterStore.filters();
    this.reachState.set('loading');
    try {
      const responses = await Promise.all(
        areas.features.map((feature, index) => this.routes.within(feature.geometry, this.reachCenter!, filters, index === 0 ? undefined : 0)),
      );
      if (request === this.reachListRequest) {
        this.reachLocations.set(responses[0].data);
        this.reachTruncated.set(responses[0].meta.truncated);
        this.reachTotals.set(new Map(areas.features.map((feature, index) => [feature.properties.minutes!, responses[index].meta.total])));
        this.reachState.set('ready');
      }
    } catch {
      if (request === this.reachListRequest) {
        this.reachState.set('error');
      }
    }
  }

  protected isPlanned(stop: RouteLocation): boolean {
    return this.trip()?.stops.some((planned) => planned.id === stop.id) ?? false;
  }

  /** Neither the destination nor a planned stop, and room for one more. */
  protected canPlan(stop: RouteLocation): boolean {
    const trip = this.trip()!;
    return stop.id !== trip.destination.locationId && !this.isPlanned(stop) && trip.stops.length < MAX_CHARGING_STOPS;
  }

  /** The route anew via this location, in the order of the current route. */
  protected async addStop(stop: RouteLocation): Promise<void> {
    const trip = this.trip()!;
    await this.replan({ ...trip, stops: withStop(trip.stops, stop, this.stops()) });
  }

  protected async removeStop(stop: RouteLocation): Promise<void> {
    const trip = this.trip()!;
    await this.replan({ ...trip, stops: trip.stops.filter((planned) => planned.id !== stop.id) });
  }

  private async replan(trip: Trip): Promise<void> {
    this.routing.set(true);
    try {
      await this.plan(trip);
    } finally {
      this.routing.set(false);
    }
  }

  /**
   * Calculates the route of the trip (start, stops, destination), draws it and loads its stops; true if it worked. A
   * failed route leaves the shown one as it was and explains why.
   */
  private async plan(trip: Trip): Promise<boolean> {
    const waypoints = [trip.start, ...trip.stops, trip.destination].map(({ latitude, longitude }) => ({ latitude, longitude }));
    try {
      const response = await this.routes.route(waypoints);
      this.trip.set(trip);
      this.route.set(routeSummary(response.data.distanceMeters, response.data.durationSeconds) + stopsSummary(trip.stops.length));
      this.routeMeters.set(response.data.distanceMeters);
      this.stopsHidden.set(false);
      this.routeLine = response.data.geometry;
      this.handle()!.showRoute(response.data.geometry.coordinates);
      void this.loadStops(response.data.geometry);
      return true;
    } catch (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      this.notice.set(status === 503 ? 'route-unavailable' : status === 422 ? 'route-none' : 'route-failed');
      return false;
    }
  }

  protected clearRoute(): void {
    this.routeRequest++;
    this.trip.set(null);
    this.routeLine = null;
    this.route.set(null);
    this.stopsState.set(null);
    this.stops.set([]);
    this.routeMeters.set(null);
    this.handle()?.showRoute(null);
  }

  protected dismissTruncated(): void {
    clearTimeout(this.truncatedTimer);
    this.truncatedShown.set(false);
    this.hints.snoozeTruncated();
  }

  protected readonly busyParts = busyText;

  protected percent(value: number): number {
    return Math.round(value);
  }

  /** A number in German notation with at most this many decimals. */
  protected decimal(value: number, digits: number): string {
    return new Intl.NumberFormat('de-DE', { maximumFractionDigits: digits }).format(value);
  }

  /** A changed value of the car; an empty field means the default. */
  protected setVehicle(field: keyof Vehicle, event: Event): void {
    const raw = (event.target as HTMLInputElement).value;
    this.vehicles.set({ [field]: raw === '' ? null : Number(raw) });
  }

  /** The recommendation fits into the waypoints and is not the route already. */
  protected canApplyPlan(stops: PlannedStop[]): boolean {
    const planned = this.trip()!.stops.map((stop) => stop.id).join();
    return stops.length <= MAX_CHARGING_STOPS && stops.map((stop) => stop.location.id).join() !== planned;
  }

  /** The route anew via the recommended stops. */
  protected async applyPlan(stops: PlannedStop[]): Promise<void> {
    await this.replan({ ...this.trip()!, stops: stops.map((stop) => stop.location) });
  }

  /** A stop of the list: the map flies there and opens its card; closing the card returns to the list. */
  protected openStop(stop: LocationSummary): void {
    this.handle()!.flyTo(stop.latitude, stop.longitude, FOCUS_ZOOM);
    this.selected.set(stop);
  }

  /** The locations along the route with the filters of the map, highlighted on the map. */
  private async loadStops(geometry: RouteResponse['data']['geometry']): Promise<void> {
    const request = ++this.routeRequest;
    this.stopsState.set('loading');
    this.stopPrices.set(new Map());
    try {
      const response = await this.routes.along(geometry, this.filterStore.filters());
      if (request !== this.routeRequest) {
        return;
      }
      this.stops.set(response.data);
      this.stopsTruncated.set(response.meta.truncated);
      this.stopsState.set('ready');
      if (this.canPrice() && response.data.length > 0) {
        void this.loadPrices(request, response.data);
      }
    } catch {
      if (request === this.routeRequest) {
        this.stopsState.set('error');
      }
    }
  }

  /** The lowest price per stop with the own tariffs (or cards); an answer for an older list is dropped, a failure leaves none. */
  private async loadPrices(request: number, stops: RouteLocation[]): Promise<void> {
    try {
      const response = await this.routes.prices(
        stops.map((stop) => stop.id),
        this.cards.cards(),
        this.cards.tariffs(),
      );
      if (request === this.routeRequest) {
        const prices = new Map<string, StopPrice>();
        for (const item of response.data) {
          const price = cheapest(item);
          if (price) {
            prices.set(item.locationId, price);
          }
        }
        this.stopPrices.set(prices);
      }
    } catch {
      // The list stays without prices.
    }
  }

  /** Back to the start position of the user, else to the default view of the configuration. */
  protected goHome(): void {
    const home = this.views.home() ?? this.defaultView();
    this.handle()!.flyTo(home.latitude, home.longitude, home.zoom);
  }

  protected toggleFilters(): void {
    this.filtersOpen.update((open) => !open);
    // The panel covers the folded actions on phones; they close with it opening.
    this.actionsOpen.set(false);
    this.optionsOpen.set(false);
    this.selected.set(null);
  }

  protected toggleOptions(): void {
    this.optionsOpen.update((open) => !open);
    this.filtersOpen.set(false);
    this.selected.set(null);
  }

  /** Shares the address with view and filters (share sheet or clipboard). */
  protected async share(): Promise<void> {
    this.shareMessage.set(SHARE_MESSAGES[await this.link.share()]);
  }

  protected setHome(): void {
    this.views.setHome(this.view()!);
    this.homeSaved.set(true);
  }

  private defaultView(): MapView {
    return { latitude: this.config!.center.latitude, longitude: this.config!.center.longitude, zoom: this.config!.zoom };
  }

  private async show(): Promise<void> {
    let config;
    try {
      config = this.config = await this.configService.load();
    } catch {
      this.notice.set('config');
      return;
    }
    if (this.destroyed) {
      return;
    }
    if (!this.browser.webGl()) {
      this.notice.set('webgl');
      return;
    }
    // A shared link wins over the last view of this browser.
    const start = this.link.read() ?? this.views.last() ?? this.views.home() ?? this.defaultView();
    try {
      const handle = await this.engine.create({
        container: this.container().nativeElement,
        theme: this.mapTheme(this.theme.dark()),
        attribution: config.attribution,
        center: { latitude: start.latitude, longitude: start.longitude },
        zoom: start.zoom,
        cluster: this.mapOptions.options(),
        onGeolocate: (event) => this.geolocated(event),
        onViewChange: (bbox, view) => this.viewChanged(bbox, view),
        onSelect: (id) => this.select(id),
        onClick: (latitude, longitude) => this.clicked(latitude, longitude),
      });
      if (this.destroyed) {
        handle.destroy();
        return;
      }
      this.handle.set(handle);
      // Locations may have arrived before the engine handed out its handle.
      handle.showLocations(this.locations());
    } catch {
      this.notice.set('map');
    }
  }

  private select(id: string): void {
    const location = this.locations().find((item) => item.id === id) ?? null;
    // While the planner waits for a start or destination, a location is picked instead of opened.
    if (this.planner()?.picking) {
      if (location) {
        this.place(locationPlace(location));
      }
      return;
    }
    this.selected.set(location);
    if (this.selected()) {
      this.filtersOpen.set(false);
      this.optionsOpen.set(false);
    }
  }

  /**
   * Remembers the view (browser storage and address) and loads the locations of the visible area; the box is clamped
   * to valid coordinates.
   */
  private viewChanged(bbox: Bbox, view: MapView): void {
    this.view.set(view);
    this.views.saveLast(view);
    this.link.write(view);
    this.homeSaved.set(false);
    this.shareMessage.set(null);
    const box: Bbox = {
      west: Math.max(-180, bbox.west),
      south: Math.max(-90, bbox.south),
      east: Math.min(180, bbox.east),
      north: Math.min(90, bbox.north),
    };
    // Zoomed into an area whose locations are all shown and current: nothing new to load.
    this.box = box;
    const shown = this.shown;
    if (!this.busy && shown?.complete && Date.now() - shown.at < LOCATIONS_FRESH_MS && covers(shown.box, box)) {
      return;
    }
    this.queue(box);
  }

  private queue(box: Bbox): void {
    this.queued = box;
    if (!this.busy) {
      void this.loadLocations();
    }
  }

  /**
   * Loads the newest queued area until none is left. Areas passed over while a request runs are skipped, and an
   * answer is only shown if no newer area is waiting, so fast panning causes one request at a time.
   */
  private async loadLocations(): Promise<void> {
    this.busy = true;
    this.loading.set(true);
    while (this.queued && !this.destroyed) {
      const box = this.queued;
      this.queued = null;
      try {
        const response = await this.locationsService.inBox(box, this.filterStore.filters());
        if (this.queued || this.destroyed) {
          continue;
        }
        this.locations.set(response.data);
        this.attribution.set([...new Set(response.meta.attribution.map((source) => source.text))]);
        this.truncated.set(response.meta.truncated);
        this.shown = { box, complete: !response.meta.truncated, at: Date.now() };
        this.handle()?.showLocations(response.data);
        this.openPending();
        if (this.notice() === 'locations') {
          this.notice.set(null);
        }
      } catch {
        if (!this.queued) {
          this.notice.set('locations');
        }
      }
    }
    this.busy = false;
    this.loading.set(false);
  }

  /** Opens the card of the searched location as soon as it is among the loaded ones. */
  private openPending(): void {
    const location = this.locations().find((item) => item.id === this.pending);
    if (location) {
      this.selected.set(location);
      this.pending = null;
    }
  }

  /** Style and marker colours for the map renderer, resolved from the theme tokens of the current mode. */
  private mapTheme(dark: boolean): MapTheme {
    const tokens = MARKER_KEYS.map((key) => MARKERS[key].token);
    const colors = resolveColors([
      ...tokens,
      '--cg-on-status',
      '--mat-sys-surface',
      '--mat-sys-surface-container-highest',
      '--mat-sys-on-surface',
      '--mat-sys-tertiary',
    ]);
    return {
      styleUrl: dark ? this.config!.styleUrlDark : this.config!.styleUrl,
      status: Object.fromEntries(MARKER_KEYS.map((key) => [key, colors[MARKERS[key].token]])) as MapTheme['status'],
      onStatus: colors['--cg-on-status'],
      ring: colors['--mat-sys-surface'],
      // Neutral grey in the middle of the cluster donuts, so the status colours of the ring stand out.
      cluster: colors['--mat-sys-surface-container-highest'],
      onCluster: colors['--mat-sys-on-surface'],
      route: colors['--mat-sys-tertiary'],
    };
  }

  private geolocated(event: GeolocateEvent): void {
    this.locating.set(false);
    if (event.type === 'error') {
      this.notice.set(event.code === 1 ? 'denied' : 'unavailable');
    } else if (event.type === 'found') {
      this.notice.set(null);
    }
  }
}
