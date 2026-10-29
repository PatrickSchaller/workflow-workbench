import { defineConfig } from '@playwright/test';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

export default defineConfig({
  testDir: './tests/browser', workers: 1, reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:5099', browserName: 'chromium', trace: 'retain-on-failure' },
  webServer: {
    command: 'dotnet run --project src/WorkflowWorkbench -c Release --no-build --no-launch-profile',
    url: 'http://127.0.0.1:5099/api/session', reuseExistingServer: !process.env.CI,
    env: { Workbench__DatabasePath: path.resolve('artifacts', `browser-${randomUUID()}.db`), Logging__LogLevel__Default: 'Warning' }
  }
});
