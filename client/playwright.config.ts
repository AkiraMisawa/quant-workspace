import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig } from '@playwright/test';

// Layer 3: the built client served by the ASP.NET Core host, driven in a real browser at machine
// speed. A test waits for what it reads, never for a fixed time.
const port = Number(process.env.WORKBENCH_PORT ?? 5080);
const baseURL = `http://localhost:${port}`;

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    // A browser preinstalled outside this project's Playwright version is named explicitly.
    ...(process.env.PLAYWRIGHT_CHROMIUM === undefined ? {} : { launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROMIUM } }),
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: {
    // Run from a `nix develop` shell, after `npm run build` has written the client into the host's wwwroot.
    command: `dotnet run --project ../server/src/Workbench.Web --urls ${baseURL}`,
    url: `${baseURL}/api/catalog`,
    // A database of the run's own, so one run never reads what another left.
    env: { Workbench__Database: process.env.WORKBENCH_DB ?? join(tmpdir(), `workbench-e2e-${port}-${Date.now()}.db`) },
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
