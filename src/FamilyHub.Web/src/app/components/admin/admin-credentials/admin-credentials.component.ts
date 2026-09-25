import { Component, HostListener, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AdminApiService, CredentialKind, CredentialsStatus } from '../../../services/admin-api.service';
import { HasUnsavedChanges } from '../../../services/unsaved-changes.guard';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { SidePanelComponent } from '../../../shared/side-panel/side-panel.component';
import { ToastService } from '../../../shared/toast/toast.service';
import { apiErrorCode } from '../shared/admin-errors';
import { AdminStatusPipe } from '../shared/admin-status.pipe';

/** Секрет, показанный один раз после «Сгенерировать». Живёт ТОЛЬКО в сигнале этого компонента. */
interface RevealedSecret {
  kind: CredentialKind;
  lines: string[];
}

const KIND_TITLES: Record<CredentialKind, string> = { Postgres: 'PostgreSQL', Minio: 'MinIO' };

const ERROR_MESSAGES: Record<string, string> = {
  not_least_privilege: 'Приложение работает не под отдельной учёткой — сначала выполните первичную настройку (deploy/README.md, «Учётки приложения»).',
  not_service_account: 'Приложение работает не под service account MinIO — сначала выполните первичную настройку (deploy/README.md, «Учётки приложения»).',
  old_not_revoked: 'Сначала отзовите старую учётку — приложение уже перешло на новую.',
  in_use: 'Нельзя отозвать учётку, под которой приложение работает сейчас.',
  nothing_to_revoke: 'Отзывать нечего: нет старой учётки после применённой ротации.',
  pending_rotation: 'Новая учётка ещё не применена — сначала положите её в PROD_ENV и задеплойте.',
  storage_unavailable: 'Хранилище не ответило. Попробуйте позже.',
  storage_rejected: 'Хранилище отклонило запрос — подробности в логах приложения (AdminAudit).',
};

/**
 * «Безопасность → Учётки БД/MinIO» (ADR-0011): полуавтоматическая ротация пароля/ключа, под которыми
 * приложение ходит в Postgres и MinIO. Порядок всегда один:
 *   1. «Сгенерировать новые» — панель выпускает учётку в запасном слоте и ОДИН раз показывает строки
 *      для PROD_ENV (секрет нигде не сохраняется — ни в БД, ни в браузере, кроме сигнала ниже);
 *   2. оператор кладёт строки в PROD_ENV и деплоит — приложение перезапускается под новой учёткой;
 *   3. панель САМА замечает, что приложение работает под новой (по фактическому подключению, а не по
 *      клику), и предлагает «Отозвать старую» — только после этого.
 *
 * Показанный секрет нельзя потерять случайно: пока он не отмечен как сохранённый, закрытие панели
 * и уход со страницы переспрашиваются (unsavedChangesGuard + beforeunload).
 */
@Component({
  selector: 'app-admin-credentials',
  standalone: true,
  imports: [DatePipe, SidePanelComponent, AdminStatusPipe],
  templateUrl: './admin-credentials.component.html',
})
export class AdminCredentialsComponent implements OnInit, HasUnsavedChanges {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly status = signal<CredentialsStatus | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  /** Какой вид учёток сейчас в работе (блокирует кнопки обоих блоков на время запроса). */
  readonly busy = signal<CredentialKind | null>(null);

  readonly secret = signal<RevealedSecret | null>(null);
  /** Админ подтвердил, что положил значения в PROD_ENV. */
  readonly saved = signal(false);

  readonly kindTitles = KIND_TITLES;

  ngOnInit(): void {
    void this.load();
  }

  hasUnsavedChanges(): boolean {
    return this.secret() !== null && !this.saved();
  }

  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.hasUnsavedChanges()) event.preventDefault();
  }

  /** Вернулись на вкладку (после деплоя в другом окне) — обновляем состояние: панель сама увидит, что
   * приложение перешло на новую учётку, и предложит отзыв. Пока показан секрет — не трогаем страницу. */
  @HostListener('document:visibilitychange')
  onVisibilityChange(): void {
    if (document.visibilityState === 'visible' && this.secret() === null) void this.load(true);
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading.set(true);
    this.error.set(null);
    try {
      this.status.set(await this.api.getCredentials());
    } catch {
      this.error.set('Не удалось загрузить состояние учёток.');
    } finally {
      this.loading.set(false);
    }
  }

  async generate(kind: CredentialKind): Promise<void> {
    const ok = await this.confirm.confirm({
      title: kind === 'Postgres' ? 'Сгенерировать новый пароль БД?' : 'Сгенерировать новый ключ MinIO?',
      message:
        'Значения будут показаны один раз — положите их в PROD_ENV и задеплойте, затем вернитесь сюда и отзовите старую учётку. ' +
        'Приложение продолжит работать под старой учёткой, пока вы не задеплоите. ' +
        'Если уже есть выпущенная, но не применённая учётка, она будет заменена новой.',
      confirmText: 'Сгенерировать',
    });
    if (!ok) return;

    this.busy.set(kind);
    try {
      const generated = await this.api.generateCredentials(kind);
      this.saved.set(false);
      this.secret.set({ kind, lines: generated.envLines });
      await this.load(true);
    } catch (e) {
      this.toast.error(this.errorMessage(e, 'Не удалось сгенерировать новую учётку.'));
      await this.load(true);
    } finally {
      this.busy.set(null);
    }
  }

  async revoke(kind: CredentialKind): Promise<void> {
    const s = this.status();
    const old = kind === 'Postgres' ? s?.postgres.revocableOld : s?.minio.revocableOldMasked;

    const ok = await this.confirm.confirm({
      title: kind === 'Postgres' ? `Отозвать роль ${old}?` : `Отозвать ключ ${old}?`,
      message:
        kind === 'Postgres'
          ? 'Роль будет отключена, а её открытые соединения оборваны. Убедитесь, что деплой прошёл и приложение работает (health зелёный).'
          : 'Ключ будет удалён из MinIO — всё, что ещё работает под ним, потеряет доступ к хранилищу. Убедитесь, что деплой прошёл и приложение работает (health зелёный).',
      confirmText: 'Отозвать',
      danger: true,
    });
    if (!ok) return;

    this.busy.set(kind);
    try {
      const result = await this.api.revokeOldCredentials(kind);
      this.toast.success(
        result.terminatedSessions === null
          ? 'Старый ключ отозван.'
          : `Старая роль отозвана. Оборвано сессий: ${result.terminatedSessions}.`,
      );
    } catch (e) {
      this.toast.error(this.errorMessage(e, 'Не удалось отозвать учётку.'));
    } finally {
      this.busy.set(null);
      await this.load(true);
    }
  }

  async copySecret(): Promise<void> {
    const lines = this.secret()?.lines;
    if (!lines) return;
    try {
      await navigator.clipboard.writeText(lines.join('\n'));
      this.toast.success('Скопировано в буфер обмена.');
    } catch {
      this.toast.error('Не удалось скопировать — выделите текст вручную.');
    }
  }

  /** Закрытие панели (крестик, фон, Esc). Пока секрет не отмечен сохранённым — переспрашиваем:
   * показать его второй раз нельзя. */
  async onSecretPanelClosed(): Promise<void> {
    if (!this.saved()) {
      const ok = await this.confirm.confirm({
        title: 'Закрыть, не сохранив значения?',
        message: 'Эти значения больше не показываются и нигде не хранятся. Если вы их не сохранили, придётся сгенерировать новые.',
        confirmText: 'Закрыть',
        cancelText: 'Вернуться',
        danger: true,
      });
      if (!ok) return;
    }
    this.secret.set(null);
    this.saved.set(false);
  }

  private errorMessage(e: unknown, fallback: string): string {
    const code = apiErrorCode(e);
    return (code && ERROR_MESSAGES[code]) || fallback;
  }
}
