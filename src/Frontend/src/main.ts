import { provideZoneChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
bootstrapApplication(AppComponent, { providers: [provideZoneChangeDetection()] }).catch(() => {
  document.body.textContent = 'Не вдалося запустити панель. Оновіть сторінку.';
});
