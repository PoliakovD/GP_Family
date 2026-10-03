import { Injectable, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';

/**
 * Заголовок вкладки/окна по маршруту — раньше на всех экранах было просто «FamilyHub»: в истории,
 * переключателе приложений и для скринридера экраны не различались.
 */
@Injectable({ providedIn: 'root' })
export class AppTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    const page = this.buildTitle(snapshot);
    this.title.setTitle(page ? `${page} · FamilyHub` : 'FamilyHub');
  }
}
