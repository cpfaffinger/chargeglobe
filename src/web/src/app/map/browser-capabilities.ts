import { DOCUMENT, Injectable, inject } from '@angular/core';

/** Browser features the map depends on; a service so the specs can replace them. */
@Injectable({ providedIn: 'root' })
export class BrowserCapabilities {
  private readonly document = inject(DOCUMENT);

  /** True if the browser can create a WebGL context, which MapLibre needs to render. */
  webGl(): boolean {
    const canvas = this.document.createElement('canvas');
    return (canvas.getContext('webgl2') ?? canvas.getContext('webgl')) !== null;
  }

  /** Geolocation is only available in a secure context (HTTPS or localhost). */
  secureContext(): boolean {
    return this.document.defaultView?.isSecureContext === true;
  }

  geolocation(): boolean {
    return this.document.defaultView?.navigator.geolocation !== undefined;
  }

  /** State of the geolocation permission; null if the browser cannot tell (no Permissions API). */
  async geolocationPermission(): Promise<PermissionState | null> {
    try {
      return (await this.document.defaultView!.navigator.permissions.query({ name: 'geolocation' })).state;
    } catch {
      return null;
    }
  }
}
