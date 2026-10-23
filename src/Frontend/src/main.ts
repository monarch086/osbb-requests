import { provideZoneChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideRouter, withRouterConfig } from '@angular/router';
import { routes } from './app/app.routes';
import { AppComponent } from './app/app.component';
bootstrapApplication(AppComponent, {
  providers: [
    provideZoneChangeDetection(),
    provideRouter(routes, withRouterConfig({ canceledNavigationResolution: 'computed' })),
  ],
}).catch(() => {
  document.body.textContent = 'Не вдалося запустити панель. Оновіть сторінку.';
});
