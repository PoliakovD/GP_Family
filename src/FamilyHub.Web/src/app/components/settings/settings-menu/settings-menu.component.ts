import { Component, OnInit, inject } from '@angular/core';
import { RouterLink, Router, ActivatedRoute } from '@angular/router';
import { AuthService } from '../../../services/auth.service';
import { PushNotificationService } from '../../../services/push-notification.service';
import { BreakpointService } from '../../../services/breakpoint.service';
import { AvatarComponent } from '../../../shared/avatar/avatar.component';
import { formatPersonName } from '../../../shared/util/person-name';

/**
 * Корневой экран `/settings` (редизайн v3, PR8). На десктопе («Профиль» в сайдбаре всегда ведёт
 * сразу к содержимому, как «Здоровье» → «Аптечка») сразу редиректит на 'profile' — этот экран на
 * широких сам себя закрывает и никогда не виден. На узких (заход через нижний лист «Ещё» →
 * «Профиль») показывает список разделов с шевронами — мокап `Screen - Profile settings.dc.html`
 * (мобильный корень), а не таб-строку `.seg`, которую заменил этот редизайн.
 */
@Component({
  selector: 'app-settings-menu',
  standalone: true,
  imports: [RouterLink, AvatarComponent],
  templateUrl: './settings-menu.component.html',
  styleUrl: './settings-menu.component.scss',
})
export class SettingsMenuComponent implements OnInit {
  readonly auth = inject(AuthService);
  readonly push = inject(PushNotificationService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly breakpoints = inject(BreakpointService);

  readonly formatPersonName = formatPersonName;

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  ngOnInit(): void {
    if (this.isWide) {
      void this.router.navigate(['profile'], { relativeTo: this.route, replaceUrl: true });
      return;
    }
    void this.auth.loadMe();
    void this.push.refreshStatus();
  }

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
