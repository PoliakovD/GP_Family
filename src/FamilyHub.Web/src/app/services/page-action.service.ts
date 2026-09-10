import {Injectable, signal} from '@angular/core';

/** Контекстное действие в верхней строке каркаса — редизайн v2 (PR2). Топбар не знает про
 * внутренности конкретной страницы: страница сама выставляет своё действие в ngOnInit и
 * обязательно очищает его в ngOnDestroy (иначе оно "протечёт" на следующий экран без действия). */
export interface PageAction {
    label: string;
    icon?: string; // класс Phosphor-иконки, например "ph-bold ph-plus"
    handler: () => void;
}

/** Редизайн v2.1 — раньше страница со своим локальным поиском (Анализы/Врачи/Аптечка) просто
 * ПОДАВЛЯЛА общий поиск шапки (suppressGlobalSearch) и рисовала собственное поле НИЖЕ заголовка
 * экрана (жалоба — «поиск находится под тайтлом экрана, а должен быть сверху»). Теперь страница
 * отдаёт топбару своё поле целиком: value — геттер, а не снимок, потому что панель работает на
 * zone-based CD с обычным полем searchQuery, а не на сигнале — снимок не отразил бы программный
 * сброс (resetFilters() и т.п.). */
export interface PageSearch {
    placeholder: string;
    value: () => string;
    onChange: (v: string) => void;
}

@Injectable({providedIn: 'root'})
export class PageActionService {
    readonly action = signal<PageAction | null>(null);
    readonly pageSearch = signal<PageSearch | null>(null);

    /** Редизайн v2.2 — «открытая запись»/деталь-страница (аптечка, анализ, показатель на мобиле)
     * владеет ВСЕЙ верхней строкой каркаса сама: там нет ни общего поиска, ни кнопки действия —
     * топбар просто не нужен целиком, экран рисует свою шапку (см. shared/back-link) прямо в
     * контенте. Третий сигнал, а не переиспользование pageSearch=null — «поиск не задан» уже
     * означало «показать общий <app-search>» (app.component.html), нужен отдельный флаг именно
     * для «топбара нет вовсе». */
    readonly immersive = signal(false);

    set(action: PageAction): void {
        this.action.set(action);
    }

    setPageSearch(search: PageSearch | null): void {
        this.pageSearch.set(search);
    }

    setImmersive(v: boolean): void {
        this.immersive.set(v);
    }

    /** Сбрасывается вместе с action()/pageSearch() — иначе «протечёт» на следующий экран без
     * своего поиска или с навсегда скрытым топбаром (тот же класс бага, для которого уже
     * существует эта симметрия у action). */
    clear(): void {
        this.action.set(null);
        this.pageSearch.set(null);
        this.immersive.set(false);
    }
}
