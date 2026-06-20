import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { initTelegram } from './telegram'

// Сообщаем Telegram, что Mini App готова к показу, и разворачиваем на весь экран.
// Вне Telegram (обычный браузер) — no-op, см. telegram.ts.
initTelegram()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
