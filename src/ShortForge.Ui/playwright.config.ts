import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './browser-tests',
  use: { channel: 'msedge', baseURL: 'http://127.0.0.1:4175' },
  webServer: {
    command: 'npm run preview -- --port 4175 --strictPort',
    url: 'http://127.0.0.1:4175',
    reuseExistingServer: false,
  },
})
