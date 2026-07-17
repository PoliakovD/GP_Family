import {Component, OnInit, inject} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {ApiService, ApiError} from './services/api.service';
import {TelegramService} from './services/telegram.service';
import {MemberStatus, type FamilySummary} from './models/types';
import {FamiliesTabComponent} from './components/families-tab/families-tab.component';
import {MedicationsTabComponent} from './components/medications-tab/medications-tab.component';
import {BirthdaysTabComponent} from './components/birthdays-tab/birthdays-tab.component';
import {MedicalRecordsTabComponent} from './components/medical-records-tab/medical-records-tab.component';
import {NotificationsTabComponent} from './components/notifications-tab/notifications-tab.component';

type Tab = 'families' | 'medications' | 'birthdays' | 'records' | 'notifications';

@Component({
    selector: 'app-root',
    standalone: true,
    imports: [
        FormsModule,
        FamiliesTabComponent,
        MedicationsTabComponent,
        BirthdaysTabComponent,
        MedicalRecordsTabComponent,
        NotificationsTabComponent,
    ],
    templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
    private readonly api = inject(ApiService);
    private readonly tg = inject(TelegramService);

    tab: Tab = 'families';
    families: FamilySummary[] = [];
    activeFamilyId: string | null = null;
    error: string | null = null;
    loading = true;

    readonly tabs: { id: Tab; label: string }[] = [
        {id: 'families', label: 'Семьи'},
        {id: 'medications', label: 'Аптечка'},
        {id: 'birthdays', label: 'Дни рождения'},
        {id: 'records', label: 'Анализы'},
        {id: 'notifications', label: 'Оповещения'},
    ];

    ngOnInit(): void {
        this.tg.init();
        this.refreshFamilies();
    }

    async refreshFamilies(): Promise<void> {
        try {
            const result = await this.api.getFamilies();
            this.families = result;
            const current = this.activeFamilyId;
            if (!current || !result.some((f) => f.id === current)) {
                const firstActive = result.find((f) => f.myStatus === MemberStatus.Active);
                this.activeFamilyId = firstActive?.id ?? null;
            }
            this.error = null;
        } catch (err) {
            this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить семьи.';
        } finally {
            this.loading = false;
        }
    }

    get activeFamilies(): FamilySummary[] {
        return this.families.filter((f) => f.myStatus === MemberStatus.Active);
    }

    get needsFamily(): boolean {
        return this.tab === 'medications' || this.tab === 'birthdays';
    }
}
