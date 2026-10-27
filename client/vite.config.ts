import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// The API runs on ASP.NET Core; in development Vite forwards /api to it.
const api = process.env.WORKBENCH_API ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, strictPort: true, proxy: { '/api': api } },
  preview: { port: 4173, strictPort: true, proxy: { '/api': api } },
  build: { outDir: '../server/src/Workbench.Web/wwwroot', emptyOutDir: true },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
