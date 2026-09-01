import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Хаб «Настройки» (редизайн v3, PR8) — разделы (Аккаунт/Безопасность/Оповещения/Данные) больше
 * не собственные табы этого компонента (.seg поверх сайдбара — два независимых уровня навигации),
 * а подпункты сайдбара каркаса, тот же паттерн, что «Здоровье» (см. app.component.ts
 * sidebarItems). Мобильный корневой список — отдельный SettingsMenuComponent на дочернем роуте
 * '' (settings-menu/settings-menu.component.ts), не таб-строка здесь.
 */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [RouterOutlet],
  templateUrl: './settings.component.html',
})
export class SettingsComponent {}
