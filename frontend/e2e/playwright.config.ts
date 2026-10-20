import { defineConfig, devices } from "@playwright/test";

/**
 * Core browser regression suite. It drives an already running local stack (API in Development + the web app);
 * see e2e/README.md. One worker: every spec shares one test clinic built through the API.
 */
export default defineConfig({
  testDir: ".",
  testMatch: "*.spec.ts",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [["list"]],
  outputDir: "../test-results/e2e",
  use: {
    baseURL: (process.env.E2E_WEB_URL ?? "http://localhost:3000").replace(/\/+$/, ""),
    ...devices["Desktop Chrome"],
    viewport: { width: 1440, height: 900 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
});
