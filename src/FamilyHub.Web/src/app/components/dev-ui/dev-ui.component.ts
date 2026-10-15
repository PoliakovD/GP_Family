import { ChangeDetectionStrategy, Component, OnDestroy, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  Bell, BriefcaseMedical, Calendar, ChevronRight, FileText, FlaskConical, Heart, HeartPulse, House, Link,
  Moon, NotebookPen, Pill, Plus, Search, Smile, Syringe, Users, Zap,
} from 'lucide';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { BackLinkComponent } from '../../shared/back-link/back-link.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { ReferenceScaleComponent } from '../../shared/reference-scale/reference-scale.component';
import { StatusChipComponent } from '../../shared/status-chip/status-chip.component';
import {
  ChatBubbleComponent, ChatComposerComponent, ChatDayComponent, ChatRecordCardComponent, ChatSystemComponent,
} from '../../shared/ui/chat/chat.components';
import { EmptyStateComponent } from '../../shared/ui/empty-state/empty-state.component';
import { EntryRowComponent } from '../../shared/ui/entry-row/entry-row.component';
import { FabComponent } from '../../shared/ui/fab/fab.component';
import { FieldDirective } from '../../shared/ui/field/field.directive';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { MetricCardComponent } from '../../shared/ui/metric-card/metric-card.component';
import { NavRowComponent } from '../../shared/ui/nav-row/nav-row.component';
import { PersonRowComponent } from '../../shared/ui/person-row/person-row.component';
import { PillTab, PillTabsComponent } from '../../shared/ui/pill-tabs/pill-tabs.component';
import { StatFilterComponent } from '../../shared/ui/stat-filter/stat-filter.component';
import { TaskRowComponent } from '../../shared/ui/task-row/task-row.component';
import { Tone } from '../../shared/ui/tone';

/**
 * Витрина компонентов редизайна (docs/redesign/PLAN.md, фаза 2): все варианты и состояния на одной
 * странице — для ревью по скриншотам на 1440 и 390, в светлой и тёмной теме (`?theme=dark`).
 * Только dev-сборка (см. app.routes.ts); данные — как в макетах.
 */
@Component({
  selector: 'app-dev-ui',
  standalone: true,
  imports: [
    FormsModule, AvatarComponent, BackLinkComponent, LoadingSpinnerComponent, ReferenceScaleComponent,
    StatusChipComponent, ChatBubbleComponent, ChatComposerComponent, ChatDayComponent, ChatRecordCardComponent,
    ChatSystemComponent, EmptyStateComponent, EntryRowComponent, FabComponent, FieldDirective, IconComponent,
    IconTileComponent, MetricCardComponent, NavRowComponent, PersonRowComponent, PillTabsComponent,
    StatFilterComponent, TaskRowComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dev-ui.component.html',
  styleUrl: './dev-ui.component.scss',
})
export class DevUiComponent implements OnDestroy {
  protected readonly i = {
    Bell, BriefcaseMedical, Calendar, ChevronRight, FileText, FlaskConical, Heart, HeartPulse, House, Link,
    Moon, NotebookPen, Pill, Plus, Search, Smile, Syringe, Users, Zap,
  };

  protected readonly tones: Tone[] = [
    'neutral', 'accent', 'danger', 'warning', 'success', 'measure', 'mood', 'sleep', 'note', 'symptom', 'medication', 'warm',
  ];

  protected readonly familyTabs: PillTab[] = [
    { id: 'chat', label: 'Чат', count: 3 },
    { id: 'members', label: 'Участники' },
    { id: 'medkits', label: 'Аптечки' },
    { id: 'birthdays', label: 'Дни рождения' },
    { id: 'dependents', label: 'Близкие и питомцы' },
  ];
  protected readonly familyTab = signal('chat');
  protected readonly sort = signal('deviations');
  protected readonly whose = signal('me');
  protected readonly filterOut = signal(true);
  protected readonly filterIn = signal(false);
  protected readonly filterNone = signal(false);
  protected readonly email = signal('daniil@');
  protected readonly toggled = signal(false);

  constructor() {
    const theme = inject(ActivatedRoute).snapshot.queryParamMap.get('theme');
    if (theme) document.documentElement.setAttribute('data-theme', theme);
  }

  ngOnDestroy(): void {
    document.documentElement.removeAttribute('data-theme');
  }

  protected noop(): void {
    // Витрина: действия ничего не делают.
  }
}
