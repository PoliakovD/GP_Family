import { Component, Input } from '@angular/core';
import type { ZoomPanTransform } from '../zoom-pan';

/** Чисто презентационный — зум/панорама/поворот считает ZoomPanController в file-viewer.component
 * (единая точка обработки жестов на всех рендерерах, см. докстринг там), этот компонент только
 * применяет готовый transform к &lt;img&gt;. */
@Component({
  selector: 'app-image-preview',
  standalone: true,
  templateUrl: './image-preview.component.html',
  styleUrl: './image-preview.component.scss',
})
export class ImagePreviewComponent {
  @Input({ required: true }) url!: string;
  @Input({ required: true }) transform!: ZoomPanTransform;
  @Input() alt = '';
}
