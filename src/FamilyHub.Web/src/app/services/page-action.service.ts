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

    set(action: PageAction): void {
        this.action.set(action);
    }

    setPageSearch(search: PageSearch | null): void {
        this.pageSearch.set(search);
    }

    /** Сбрасывается вместе с action() — иначе «протечёт» на следующий экран без своего поиска
     * (тот же класс бага, для которого уже существует эта симметрия у action). */
    clear(): void {
        this.action.set(null);
        this.pageSearch.set(null);
    }
}
