import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';

// Duotone-иконки Phosphor (~230 кБ CSS) подгружаются отдельным файлом, не блокируя первый рендер, и не входят
// в initial-бандл (см. angular.json → assets `phosphor-duotone`). Ссылка добавляется из кода, а не inline-обработчиком
// onload в index.html: CSP не разрешает inline-скрипты. Пока файл грузится, дуотон-иконки просто пусты.
const duotone = document.createElement('link');
duotone.rel = 'stylesheet';
duotone.href = '/phosphor-duotone/style.css';
document.head.appendChild(duotone);

bootstrapApplication(AppComponent, appConfig).catch((err) => console.error(err));
