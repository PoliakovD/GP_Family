// Свой минимальный жестовый контроллер (без @angular/cdk — не подключаем его в этом проекте, см.
// .claude/patterns/frontend_web.md): колесо/трекпад — зум на десктопе, два пальца — пинч-зум на
// мобильном, драг одним пальцем/мышью — панорама, когда уже приближено. Общий для картинок и PDF
// (shared/file-viewer/renderers) — оборачивает содержимое в transform: translate() scale() rotate().

export interface ZoomPanTransform {
  scale: number;
  x: number;
  y: number;
  rotationDeg: number;
}

const MIN_SCALE = 1;
const MAX_SCALE = 6;

export class ZoomPanController {
  private scale = MIN_SCALE;
  private x = 0;
  private y = 0;
  private rotationDeg = 0;

  private readonly pointers = new Map<number, { x: number; y: number }>();
  private lastPinchDistance: number | null = null;
  private dragStart: { x: number; y: number; originX: number; originY: number } | null = null;

  constructor(private readonly onChange: (t: ZoomPanTransform) => void) {}

  get transform(): ZoomPanTransform {
    return { scale: this.scale, x: this.x, y: this.y, rotationDeg: this.rotationDeg };
  }

  get isZoomed(): boolean {
    return this.scale > MIN_SCALE;
  }

  reset(): void {
    this.scale = MIN_SCALE;
    this.x = 0;
    this.y = 0;
    this.emit();
  }

  /** Поворот не сбрасывает зум/панораму — независимая ось трансформации. */
  rotate90(): void {
    this.rotationDeg = (this.rotationDeg + 90) % 360;
    this.emit();
  }

  zoomBy(factor: number): void {
    const next = clamp(this.scale * factor, MIN_SCALE, MAX_SCALE);
    this.scale = next;
    if (this.scale === MIN_SCALE) {
      this.x = 0;
      this.y = 0;
    }
    this.emit();
  }

  onWheel(event: WheelEvent): void {
    event.preventDefault();
    this.zoomBy(event.deltaY < 0 ? 1.15 : 1 / 1.15);
  }

  onPointerDown(event: PointerEvent): void {
    this.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (this.pointers.size === 1) {
      this.dragStart = { x: event.clientX, y: event.clientY, originX: this.x, originY: this.y };
    } else if (this.pointers.size === 2) {
      this.lastPinchDistance = this.currentPinchDistance();
      this.dragStart = null;
    }
  }

  onPointerMove(event: PointerEvent): void {
    if (!this.pointers.has(event.pointerId)) return;
    this.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

    if (this.pointers.size === 2) {
      const distance = this.currentPinchDistance();
      if (this.lastPinchDistance !== null && this.lastPinchDistance > 0) {
        this.zoomBy(distance / this.lastPinchDistance);
      }
      this.lastPinchDistance = distance;
      return;
    }

    if (this.dragStart && this.isZoomed) {
      this.x = this.dragStart.originX + (event.clientX - this.dragStart.x);
      this.y = this.dragStart.originY + (event.clientY - this.dragStart.y);
      this.emit();
    }
  }

  onPointerUp(event: PointerEvent): void {
    this.pointers.delete(event.pointerId);
    if (this.pointers.size < 2) this.lastPinchDistance = null;
    if (this.pointers.size === 0) this.dragStart = null;
  }

  /** Была ли это чистая панорама (двигали дальше нескольких px) — чтобы вызывающий код отличил
   * "потаскали приближенную картинку" от "тап/клик" и не закрывал/не листал вьюер по ошибке. */
  wasDragged(event: PointerEvent, thresholdPx = 4): boolean {
    return !!this.dragStart && Math.hypot(event.clientX - this.dragStart.x, event.clientY - this.dragStart.y) > thresholdPx;
  }

  private currentPinchDistance(): number {
    const points = [...this.pointers.values()];
    return Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y);
  }

  private emit(): void {
    this.onChange(this.transform);
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
