import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { TelegramService } from './telegram.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tg = inject(TelegramService);
  const initData = tg.getInitData();

  let headers = req.headers;
  if (initData) {
    headers = headers.set('Authorization', `tma ${initData}`);
  } else {
    const devId = tg.getDevTelegramId();
    if (devId) {
      headers = headers.set('X-Dev-TelegramId', devId);
    }
  }

  return next(req.clone({ headers }));
};
