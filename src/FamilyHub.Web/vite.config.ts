/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Mini App собирается прямо в wwwroot API: FamilyHub.Api раздаёт её как статику
// и отдаёт index.html по SPA-fallback (см. Program.cs: UseStaticFiles + MapFallbackToFile).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../FamilyHub.Api/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/setupTests.ts'],
    globals: true,
  },
})
