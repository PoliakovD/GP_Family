import { Injectable, isDevMode, signal, computed } from '@angular/core';

export type LogLevel = 'info' | 'warn' | 'error';

export interface LogEntry {
  id: number;
  ts: Date;
  module: string;
  level: LogLevel;
  message: string;
}

const STORAGE_KEY = 'familyhub:devlog:modules';
const MAX_ENTRIES = 300;

@Injectable({ providedIn: 'root' })
export class DevLoggerService {
  private readonly active = isDevMode();
  private counter = 0;

  readonly entries = signal<LogEntry[]>([]);
  readonly moduleEnabled = signal<Record<string, boolean>>(this.loadSettings());

  readonly modules = computed(() => Object.keys(this.moduleEnabled()).sort());

  readonly filteredEntries = computed(() => {
    const enabled = this.moduleEnabled();
    return this.entries().filter((e) => enabled[e.module] !== false);
  });

  log(module: string, level: LogLevel, message: string): void {
    if (!this.active) return;

    const known = this.moduleEnabled();
    if (!(module in known)) {
      this.moduleEnabled.set({ ...known, [module]: true });
      this.saveSettings();
    }

    this.entries.update((prev) => {
      const next = [...prev, { id: ++this.counter, ts: new Date(), module, level, message }];
      return next.length > MAX_ENTRIES ? next.slice(next.length - MAX_ENTRIES) : next;
    });
  }

  toggleModule(module: string): void {
    const current = this.moduleEnabled();
    this.moduleEnabled.set({ ...current, [module]: !current[module] });
    this.saveSettings();
  }

  clear(): void {
    this.entries.set([]);
  }

  private loadSettings(): Record<string, boolean> {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as Record<string, boolean>) : {};
    } catch {
      return {};
    }
  }

  private saveSettings(): void {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(this.moduleEnabled()));
    } catch { /* quota exceeded — ignore */ }
  }
}
