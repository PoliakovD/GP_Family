import { ArrowRight, Check, MessageCircle, Paperclip, Plus } from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import {
  Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, input, signal, untracked, viewChild,
  ChangeDetectionStrategy
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { ChatRealtimeService } from '../../services/chat-realtime.service';
import {
  AttachmentPreviewStatus,
  ChatChange,
  ChatLinkKind,
  MedicalRecordKind,
  NotificationRelatedKind,
  type Attachment,
  type ChatAttachment,
  type ChatConfig,
  type ChatLink,
  type ChatLinkRequest,
  type ChatMessage,
  type ChatMessageChangedEvent,
} from '../../models/types';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { ActionMenuComponent, type ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { FileViewerComponent } from '../../shared/file-viewer/file-viewer.component';
import { InfiniteScrollSentinelComponent } from '../../shared/infinite-scroll-sentinel/infinite-scroll-sentinel.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { ToastService } from '../../shared/toast/toast.service';
import { compressImage } from '../../shared/util/image-compression';
import { ACCEPTED_ATTACHMENT_TYPES } from '../../shared/util/attachment-upload';
import { describeFile, formatFileSize } from '../../shared/util/file-type';
import { formatDayMonth, formatDayMonthYear } from '../../shared/util/date-format';
import { relatedEntityRoute } from '../../shared/util/related-kind-route';

/** Строка ленты: сообщение + разметка группировки (разделитель дня, «тот же автор подряд»). */
interface FeedRow {
  message: ChatMessage;
  dayLabel: string | null;
  showAuthor: boolean;
  mine: boolean;
}

/** Кандидат в пикере ссылок. */
interface LinkCandidate {
  kind: ChatLinkKind;
  entityId: string;
  title: string;
  subtitle: string;
}

/** ISO-время → «yyyy-MM-dd» в часовом поясе устройства (разделители дней — по местной дате, не UTC). */
function localDateKey(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** Склейка подряд идущих сообщений одного автора — в пределах этого окна аватар/имя не повторяются. */
const GROUP_WINDOW_MS = 5 * 60_000;
/** Сжимаем фото крупнее этого — снимки с телефона по 4–8 МБ упёрлись бы в лимит файла. */
const COMPRESS_ABOVE_BYTES = 700 * 1024;
const COMPRESSIBLE = new Set(['image/jpeg', 'image/png', 'image/webp']);

/**
 * Общий чат семьи (ADR-0019) — вкладка `?tab=chat` страницы семьи. Лента грузится по REST страницами от
 * новых к старым (вверх — бесконечная прокрутка), новое приходит событием хаба «сообщение N изменилось»,
 * и сообщение перечитывается по id (карточки ссылок — под текущего зрителя).
 */
@Component({
  selector: 'app-family-chat',
  imports: [
    FormsModule, AvatarComponent, ActionMenuComponent, BottomSheetComponent, FileViewerComponent,
    InfiniteScrollSentinelComponent, IconComponent,
  ],
  templateUrl: './family-chat.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './family-chat.component.scss',
})
export class FamilyChatComponent implements OnInit, OnDestroy {
  protected readonly icons = { ArrowRight, Check, MessageCircle, Paperclip, Plus };

  readonly familyId = input.required<string>();
  /** Админ семьи может удалять чужие сообщения — права приходят с сервера (canDelete), здесь только подпись. */
  readonly isAdmin = input(false);

  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(ChatRealtimeService);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly textarea = viewChild<ElementRef<HTMLTextAreaElement>>('textarea');
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  readonly accept = ACCEPTED_ATTACHMENT_TYPES;
  readonly formatFileSize = formatFileSize;

  readonly config = signal<ChatConfig | null>(null);
  readonly messages = signal<ChatMessage[]>([]); // по возрастанию времени
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly loadingOlder = signal(false);
  private nextCursor: string | null = null;
  readonly hasOlder = signal(false);

  readonly text = signal('');
  readonly files = signal<File[]>([]);
  readonly links = signal<LinkCandidate[]>([]);
  readonly replyTo = signal<ChatMessage | null>(null);
  readonly editing = signal<ChatMessage | null>(null);
  readonly sending = signal(false);

  readonly reactionPickerFor = signal<string | null>(null);
  readonly linkPickerOpen = signal(false);
  readonly linkCandidates = signal<{ title: string; items: LinkCandidate[] }[] | null>(null);

  readonly viewerOpen = signal(false);
  readonly viewerAttachments = signal<Attachment[]>([]);
  readonly viewerIndex = signal(0);
  /** attachmentId → подписанный URL картинки для миниатюры в ленте (ссылки живут 5 минут — только на показ). */
  readonly imageUrls = signal<Record<string, string>>({});
  private readonly requestedImages = new Set<string>();

  private unsubscribe: (() => void) | null = null;
  private stickToBottom = true;
  private readTimer: ReturnType<typeof setTimeout> | null = null;

  readonly myUserId = computed(() => this.auth.me()?.userId ?? null);

  readonly rows = computed<FeedRow[]>(() => {
    const me = this.myUserId();
    const list = this.messages();
    const rows: FeedRow[] = [];
    let prev: ChatMessage | null = null;
    for (const m of list) {
      const day = localDateKey(m.createdAt);
      const prevDay = prev ? localDateKey(prev.createdAt) : null;
      const dayLabel = day !== prevDay ? this.dayLabel(m.createdAt) : null;
      const sameAuthor = prev !== null && dayLabel === null && prev.author.userId === m.author.userId
        && new Date(m.createdAt).getTime() - new Date(prev.createdAt).getTime() < GROUP_WINDOW_MS;
      rows.push({ message: m, dayLabel, showAuthor: !sameAuthor, mine: m.author.userId === me });
      prev = m;
    }
    return rows;
  });

  readonly canSend = computed(() => {
    if (this.sending()) return false;
    const hasText = this.text().trim().length > 0;
    if (this.editing()) return hasText || (this.editing()!.attachments.length + this.editing()!.links.length) > 0;
    return hasText || this.files().length > 0 || this.links().length > 0;
  });

  constructor() {
    // Переподключение хаба — события за время обрыва потеряны, перечитываем свежую страницу.
    effect(() => {
      const tick = this.realtime.resyncTick();
      if (tick > 0) untracked(() => void this.reloadLatest());
    });
  }

  ngOnInit(): void {
    void this.realtime.start();
    this.realtime.setViewing(this.familyId());
    this.unsubscribe = this.realtime.onMessageChanged((e) => void this.onEvent(e));
    void this.realtime.getConfig().then((c) => this.config.set(c)).catch(() => undefined);
    void this.loadInitial();
  }

  ngOnDestroy(): void {
    this.realtime.setViewing(null);
    this.unsubscribe?.();
    if (this.readTimer) clearTimeout(this.readTimer);
  }

  // ─── Загрузка ленты ─────────────────────────────────────────────────────────────────────────

  retry(): void {
    void this.loadInitial();
  }

  private async loadInitial(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const page = await this.api.getChatMessages(this.familyId(), null);
      this.messages.set([...page.items].reverse());
      this.nextCursor = page.nextCursor;
      this.hasOlder.set(page.nextCursor !== null);
      this.loadImages(page.items);
      this.scrollToBottom();
      this.scheduleMarkRead();
    } catch (e) {
      this.loadError.set(e instanceof ApiError ? e.message : 'Не удалось загрузить сообщения.');
    } finally {
      this.loading.set(false);
    }
  }

  /** После переподключения: последняя страница вливается в уже загруженное (старые страницы не трогаем). */
  private async reloadLatest(): Promise<void> {
    try {
      const page = await this.api.getChatMessages(this.familyId(), null);
      const byId = new Map(this.messages().map((m) => [m.id, m]));
      for (const m of page.items) byId.set(m.id, m);
      this.messages.set([...byId.values()].sort((a, b) => a.createdAt.localeCompare(b.createdAt)));
      this.loadImages(page.items);
      this.scheduleMarkRead();
    } catch {
      // Тихо: следующее событие или открытие чата всё равно дочитают.
    }
  }

  async loadOlder(): Promise<void> {
    if (this.loadingOlder() || !this.nextCursor) return;
    this.loadingOlder.set(true);
    const el = this.scroller()?.nativeElement;
    const before = el ? el.scrollHeight - el.scrollTop : 0;
    try {
      const page = await this.api.getChatMessages(this.familyId(), this.nextCursor);
      this.messages.update((list) => [...[...page.items].reverse(), ...list]);
      this.nextCursor = page.nextCursor;
      this.hasOlder.set(page.nextCursor !== null);
      this.loadImages(page.items);
      // Сохраняем позицию: подгруженное сверху не должно сдвигать то, что человек читает.
      requestAnimationFrame(() => {
        if (el) el.scrollTop = el.scrollHeight - before;
      });
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось загрузить историю.');
    } finally {
      this.loadingOlder.set(false);
    }
  }

  private async onEvent(e: ChatMessageChangedEvent): Promise<void> {
    if (e.familyId !== this.familyId()) return;
    try {
      const message = await this.api.getChatMessage(e.familyId, e.messageId);
      this.upsert(message);
      if (e.change === ChatChange.Created) {
        this.loadImages([message]);
        if (this.stickToBottom || message.author.userId === this.myUserId()) this.scrollToBottom();
        this.scheduleMarkRead();
      }
    } catch {
      // Сообщение могло исчезнуть вместе с аккаунтом автора — просто не показываем.
    }
  }

  private upsert(message: ChatMessage): void {
    this.messages.update((list) => {
      const i = list.findIndex((m) => m.id === message.id);
      if (i >= 0) return list.map((m) => (m.id === message.id ? message : m));
      return [...list, message].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    });
  }

  onScroll(): void {
    const el = this.scroller()?.nativeElement;
    if (!el) return;
    this.stickToBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    if (this.stickToBottom) this.scheduleMarkRead();
  }

  private scrollToBottom(): void {
    this.stickToBottom = true;
    requestAnimationFrame(() => {
      const el = this.scroller()?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }

  /** «Прочитано» — когда низ ленты на экране и вкладка видима; со склейкой, чтобы не слать на каждое событие. */
  private scheduleMarkRead(): void {
    if (this.readTimer) clearTimeout(this.readTimer);
    this.readTimer = setTimeout(() => {
      if (document.visibilityState !== 'visible' || !this.stickToBottom) return;
      const last = this.messages().at(-1);
      if (!last) return;
      this.realtime.markReadLocal(this.familyId());
      void this.api.markChatRead(this.familyId(), last.createdAt).catch(() => undefined);
    }, 600);
  }

  // ─── Отправка/правка ────────────────────────────────────────────────────────────────────────

  async submit(): Promise<void> {
    if (!this.canSend()) return;
    const editing = this.editing();
    if (editing) {
      await this.saveEdit(editing);
      return;
    }

    this.sending.set(true);
    try {
      const sent = await this.api.sendChatMessage(this.familyId(), {
        text: this.text().trim(),
        replyToMessageId: this.replyTo()?.id ?? null,
        links: this.links().map<ChatLinkRequest>((l) => ({ kind: l.kind, entityId: l.entityId })),
        files: this.files(),
      });
      this.upsert(sent);
      this.loadImages([sent]);
      this.resetComposer();
      this.scrollToBottom();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось отправить сообщение.');
    } finally {
      this.sending.set(false);
    }
  }

  private async saveEdit(message: ChatMessage): Promise<void> {
    this.sending.set(true);
    try {
      await this.api.editChatMessage(this.familyId(), message.id, this.text().trim());
      this.upsert(await this.api.getChatMessage(this.familyId(), message.id));
      this.resetComposer();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось сохранить изменения.');
    } finally {
      this.sending.set(false);
    }
  }

  onKeydown(event: KeyboardEvent): void {
    // Enter — отправить, Shift+Enter — перенос строки. На сенсорных экранах Enter — всегда перенос
    // (отправка — кнопкой), иначе на телефоне нельзя написать абзац.
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return;
    if (window.matchMedia('(pointer: coarse)').matches) return;
    event.preventDefault();
    void this.submit();
  }

  autoGrow(): void {
    const el = this.textarea()?.nativeElement;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 180)}px`;
  }

  private resetComposer(): void {
    this.text.set('');
    this.files.set([]);
    this.links.set([]);
    this.replyTo.set(null);
    this.editing.set(null);
    requestAnimationFrame(() => this.autoGrow());
  }

  startReply(message: ChatMessage): void {
    this.editing.set(null);
    this.replyTo.set(message);
    this.focusComposer();
  }

  startEdit(message: ChatMessage): void {
    this.replyTo.set(null);
    this.files.set([]);
    this.links.set([]);
    this.editing.set(message);
    this.text.set(message.text);
    this.focusComposer();
  }

  cancelContext(): void {
    if (this.editing()) this.text.set('');
    this.editing.set(null);
    this.replyTo.set(null);
  }

  private focusComposer(): void {
    requestAnimationFrame(() => {
      this.autoGrow();
      this.textarea()?.nativeElement.focus();
    });
  }

  async remove(message: ChatMessage): Promise<void> {
    const mine = message.author.userId === this.myUserId();
    const ok = await this.confirm.confirm({
      title: 'Удалить сообщение?',
      message: mine
        ? 'Сообщение и его вложения удалятся у всех участников.'
        : 'Вы удаляете чужое сообщение как администратор семьи — оно и его вложения удалятся у всех.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;
    try {
      await this.api.deleteChatMessage(this.familyId(), message.id);
      this.upsert(await this.api.getChatMessage(this.familyId(), message.id));
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось удалить сообщение.');
    }
  }

  messageActions(row: FeedRow): ActionMenuItem[] {
    const m = row.message;
    const items: ActionMenuItem[] = [
      { label: 'Ответить', icon: 'ph ph-arrow-bend-up-left', handler: () => this.startReply(m) },
      { label: 'Реакция', icon: 'ph ph-smiley', handler: () => this.reactionPickerFor.set(m.id) },
    ];
    if (m.text) items.push({ label: 'Копировать текст', icon: 'ph ph-copy', handler: () => void this.copy(m.text) });
    if (m.canEdit) items.push({ label: 'Изменить', icon: 'ph ph-pencil-simple', handler: () => this.startEdit(m) });
    if (m.canDelete) {
      items.push({ label: row.mine ? 'Удалить' : 'Удалить (админ)', icon: 'ph ph-trash', danger: true, handler: () => void this.remove(m) });
    }
    return items;
  }

  private async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.toast.success('Текст скопирован.');
    } catch {
      this.toast.error('Не удалось скопировать.');
    }
  }

  // ─── Реакции ────────────────────────────────────────────────────────────────────────────────

  async toggleReaction(message: ChatMessage, emoji: string): Promise<void> {
    this.reactionPickerFor.set(null);
    const on = !message.reactions.some((r) => r.emoji === emoji && r.mine);
    try {
      await this.api.setChatReaction(this.familyId(), message.id, emoji, on);
      this.upsert(await this.api.getChatMessage(this.familyId(), message.id));
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось поставить реакцию.');
    }
  }

  // ─── Вложения ───────────────────────────────────────────────────────────────────────────────

  pickFiles(): void {
    this.fileInput()?.nativeElement.click();
  }

  async onFilesSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const picked = Array.from(input.files ?? []);
    input.value = '';
    const config = this.config();
    const maxFiles = config?.maxFilesPerMessage ?? 6;
    const maxSize = config?.maxFileSizeBytes ?? 5 * 1024 * 1024;

    const prepared: File[] = [];
    for (const file of picked) {
      prepared.push(await this.maybeCompress(file));
    }
    const tooLarge = prepared.filter((f) => f.size > maxSize);
    const accepted = prepared.filter((f) => f.size <= maxSize);
    const room = Math.max(0, maxFiles - this.files().length);
    if (tooLarge.length > 0) this.toast.error(`Файл больше ${formatFileSize(maxSize)}: ${tooLarge.map((f) => f.name).join(', ')}`);
    if (accepted.length > room) this.toast.info(`В одном сообщении — не больше ${maxFiles} файлов.`);
    this.files.update((list) => [...list, ...accepted.slice(0, room)]);
  }

  private async maybeCompress(file: File): Promise<File> {
    if (!COMPRESSIBLE.has(file.type) || file.size <= COMPRESS_ABOVE_BYTES) return file;
    try {
      const blob = await compressImage(file);
      const name = file.name.replace(/\.[^.]+$/, '') + '.jpg';
      return blob.size < file.size ? new File([blob], name, { type: 'image/jpeg' }) : file;
    } catch {
      return file;
    }
  }

  removeFile(index: number): void {
    this.files.update((list) => list.filter((_, i) => i !== index));
  }

  isImage(a: ChatAttachment): boolean {
    return a.previewStatus === AttachmentPreviewStatus.Ready && a.contentType.startsWith('image/');
  }

  fileIcon(a: ChatAttachment): string {
    return describeFile(a.contentType, a.fileName).icon;
  }

  /** Подписанные ссылки на картинки — для миниатюр в ленте; по одной на вложение за жизнь компонента. */
  private loadImages(messages: ChatMessage[]): void {
    for (const m of messages) {
      for (const a of m.attachments) {
        if (!this.isImage(a) || this.requestedImages.has(a.id)) continue;
        this.requestedImages.add(a.id);
        void this.api.getAttachmentPreview(a.id).then((p) => {
          const url = p.thumbnailUrl ?? p.contentUrl;
          if (url) this.imageUrls.update((map) => ({ ...map, [a.id]: url }));
        }).catch(() => undefined);
      }
    }
  }

  openViewer(message: ChatMessage, attachment: ChatAttachment): void {
    this.viewerAttachments.set(message.attachments.map<Attachment>((a) => ({
      id: a.id, fileName: a.fileName, contentType: a.contentType, sizeBytes: a.sizeBytes,
      uploadedAt: message.createdAt, extractedAt: null, previewStatus: a.previewStatus,
    })));
    this.viewerIndex.set(message.attachments.findIndex((a) => a.id === attachment.id));
    this.viewerOpen.set(true);
  }

  // ─── Ссылки на сущности ─────────────────────────────────────────────────────────────────────

  async openLinkPicker(): Promise<void> {
    this.linkPickerOpen.set(true);
    if (this.linkCandidates()) return;

    const [records, courses, medkits] = await Promise.all([
      this.api.getMedicalRecords({ subject: 'me', pageSize: 20 }).then((p) => p.items).catch(() => []),
      this.api.getCourses().catch(() => []),
      this.api.getMedkits(this.familyId()).catch(() => []),
    ]);
    this.linkCandidates.set([
      {
        title: 'Мои анализы и приёмы',
        items: records.map((r) => ({
          kind: ChatLinkKind.MedicalRecord, entityId: r.id,
          title: r.title || (r.kind === MedicalRecordKind.DoctorVisit ? 'Приём врача' : 'Анализ'),
          subtitle: formatDayMonthYear(r.recordDate),
        })),
      },
      {
        title: 'Курсы приёма',
        items: courses.map((c) => ({ kind: ChatLinkKind.MedicationCourse, entityId: c.id, title: c.drugName, subtitle: 'Курс приёма' })),
      },
      {
        title: 'Аптечки семьи',
        items: medkits.map((k) => ({ kind: ChatLinkKind.Medkit, entityId: k.id, title: k.name, subtitle: 'Аптечка' })),
      },
    ].filter((g) => g.items.length > 0));
  }

  addLink(candidate: LinkCandidate): void {
    const max = this.config()?.maxLinksPerMessage ?? 5;
    if (this.links().some((l) => l.entityId === candidate.entityId && l.kind === candidate.kind)) {
      this.linkPickerOpen.set(false);
      return;
    }
    if (this.links().length >= max) {
      this.toast.info(`В одном сообщении — не больше ${max} ссылок.`);
      return;
    }
    this.links.update((list) => [...list, candidate]);
    this.linkPickerOpen.set(false);
  }

  removeLink(index: number): void {
    this.links.update((list) => list.filter((_, i) => i !== index));
  }

  linkIcon(kind: ChatLinkKind): string {
    switch (kind) {
      case ChatLinkKind.MedicalRecord: return 'ph ph-file-text';
      case ChatLinkKind.MedicationCourse: return 'ph ph-pill';
      case ChatLinkKind.Medkit: return 'ph ph-first-aid-kit';
    }
  }

  openLink(link: ChatLink): void {
    if (!link.accessible || link.routeKind === null) return;
    void this.router.navigate(relatedEntityRoute(link.routeKind as NotificationRelatedKind, link.entityId));
  }

  // ─── Разное ─────────────────────────────────────────────────────────────────────────────────

  scrollToMessage(id: string): void {
    const el = this.scroller()?.nativeElement.querySelector<HTMLElement>(`[data-message-id="${id}"]`);
    if (!el) {
      this.toast.info('Это сообщение выше в истории — прокрутите ленту вверх.');
      return;
    }
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    el.classList.add('flash');
    setTimeout(() => el.classList.remove('flash'), 1200);
  }

  authorName(a: { firstName: string | null; lastName: string | null }): string {
    const name = [a.firstName, a.lastName].filter(Boolean).join(' ');
    return name || 'Участник семьи';
  }

  avatarPerson(a: { userId: string; firstName: string | null; lastName: string | null }) {
    return { key: a.userId, firstName: a.firstName, lastName: a.lastName };
  }

  time(iso: string): string {
    return new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  }

  private dayLabel(iso: string): string {
    const d = new Date(iso);
    const today = new Date();
    const yesterday = new Date();
    yesterday.setDate(today.getDate() - 1);
    if (d.toDateString() === today.toDateString()) return 'Сегодня';
    if (d.toDateString() === yesterday.toDateString()) return 'Вчера';
    const key = localDateKey(iso);
    return d.getFullYear() === today.getFullYear() ? formatDayMonth(key) : formatDayMonthYear(key);
  }
}
