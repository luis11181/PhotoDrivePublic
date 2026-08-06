/*
 * Opt-in smoke against a running Development installation and its real S3 bucket.
 * The script intentionally leaves its generated catalog item in Trash.
 */
const { randomUUID } = require("node:crypto");
const { createRequire } = require("node:module");
const path = require("node:path");

const requireFromFrontend = createRequire(
  path.join(__dirname, "../../frontend/package.json"),
);
const { chromium } = requireFromFrontend("playwright");

const requiredEnvironment = [
  "MEDIA_EXPLORER_LIVE_SMOKE",
  "MEDIA_EXPLORER_SMOKE_EMAIL",
  "MEDIA_EXPLORER_SMOKE_PASSWORD",
];
const missing = requiredEnvironment.filter((name) => !process.env[name]);
if (missing.length) {
  throw new Error(`Missing required environment variables: ${missing.join(", ")}`);
}
if (process.env.MEDIA_EXPLORER_LIVE_SMOKE !== "1") {
  throw new Error("Set MEDIA_EXPLORER_LIVE_SMOKE=1 to permit the live S3 smoke test.");
}

const frontendUrl = (process.env.MEDIA_EXPLORER_FRONTEND_URL ?? "http://localhost:3000").replace(/\/$/, "");
const apiUrl = (process.env.MEDIA_EXPLORER_API_URL ?? "http://localhost:6045").replace(/\/$/, "");
const fileName = `mediaexplorer-live-smoke-${randomUUID()}.gif`;
const tinyGif = Buffer.from(
  "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==",
  "base64",
);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function main() {
  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  const responses = [];
  const responseChecks = [];
  let uploadedItemId;

  page.on("response", (response) => {
    if (!response.url().startsWith(`${apiUrl}/api/`)) return;
    responseChecks.push((async () => {
      const contentType = response.headers()["content-type"] ?? "";
      const entry = {
        method: response.request().method(),
        path: new URL(response.url()).pathname,
        status: response.status(),
        correlationId: response.headers()["x-correlation-id"] ?? null,
        leakedStorageIdentity: false,
      };
      if (contentType.includes("json")) {
        const body = await response.text().catch(() => "");
        entry.leakedStorageIdentity = /\"(?:sha256|objectKey|bucketName|blobSha256)\"\s*:/i.test(body)
          || /AKIA[0-9A-Z]{16}/.test(body);
        if (entry.path.endsWith("/complete") && response.ok) {
          const envelope = JSON.parse(body);
          uploadedItemId = envelope?.data?.id;
        }
      }
      responses.push(entry);
    })());
  });

  const smokeFile = { name: fileName, mimeType: "image/gif", buffer: tinyGif };
  const card = () => page.locator("article.media-card").filter({ hasText: fileName });

  try {
    await page.goto(`${frontendUrl}/files`, { waitUntil: "domcontentloaded" });
    const email = page.getByLabel("Email");
    await Promise.race([
      email.waitFor({ state: "visible", timeout: 20000 }),
      page.getByRole("heading", { name: "Files", exact: true }).waitFor({ state: "visible", timeout: 20000 }),
    ]);
    if (await email.isVisible().catch(() => false)) {
      await email.fill(process.env.MEDIA_EXPLORER_SMOKE_EMAIL);
      await page.getByLabel("Password").fill(process.env.MEDIA_EXPLORER_SMOKE_PASSWORD);
      await page.getByRole("button", { name: "Sign in", exact: true }).click();
    }
    await page.getByRole("heading", { name: "Files", exact: true }).waitFor({ timeout: 20000 });
    await page.getByText("Media", { exact: true }).first().waitFor();
    await page.getByText("Documents", { exact: true }).first().waitFor();

    await page.goto(`${frontendUrl}/photos`, { waitUntil: "domcontentloaded" });
    await page.getByRole("heading", { name: "Photos", exact: true }).waitFor({ timeout: 20000 });
    await page.getByRole("button", { name: "Upload", exact: true }).click();
    await page.locator('input[type="file"]').setInputFiles(smokeFile);
    await page.getByRole("dialog").waitFor({ state: "hidden", timeout: 60000 });
    await page.getByRole("status").filter({ hasText: "1 created · 0 failed" }).waitFor({ timeout: 20000 });
    await card().waitFor({ timeout: 30000 });

    await page.getByRole("button", { name: "Upload", exact: true }).click();
    await page.locator('input[type="file"]').setInputFiles(smokeFile);
    await page.getByRole("heading", { name: "An item with this name already exists", exact: true }).waitFor({ timeout: 20000 });
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await page.getByRole("button", { name: "Close", exact: true }).click();

    await page.getByRole("button", { name: `Open ${fileName}`, exact: true }).click();
    await page.getByText("1 × 1", { exact: true }).waitFor({ timeout: 45000 });
    await page.getByText("succeeded", { exact: true }).first().waitFor({ timeout: 45000 });
    await page.getByRole("button", { name: "Close details", exact: true }).click();

    await page.getByRole("button", { name: `Select ${fileName}`, exact: true }).click();
    await page.getByRole("button", { name: "Favorite", exact: true }).click();
    await page.getByRole("status").filter({ hasText: "1 favorited" }).waitFor({ timeout: 20000 });

    const typeFilters = page.locator("fieldset").filter({ has: page.locator("legend", { hasText: "Type" }) });
    const favoriteFilters = page.locator("fieldset").filter({ has: page.locator("legend", { hasText: "Favorites" }) });
    await favoriteFilters.getByRole("button", { name: "Favorites", exact: true }).click();
    await card().waitFor();
    await typeFilters.getByRole("button", { name: "Videos", exact: true }).click();
    await page.waitForTimeout(500);
    assert(await card().count() === 0, "Image remained visible under the Videos filter.");
    await typeFilters.getByRole("button", { name: "Photos", exact: true }).click();
    await card().waitFor();

    await page.getByRole("button", { name: "Operation notifications", exact: true }).click();
    const notifications = page.locator(".notifications-panel");
    await notifications.getByText("Upload finished", { exact: true }).waitFor();
    await notifications.getByText("Favorite update finished", { exact: true }).waitFor();
    await page.getByRole("button", { name: "Close notifications", exact: true }).click();

    await page.getByRole("button", { name: `Select ${fileName}`, exact: true }).click();
    await page.getByRole("button", { name: "Trash", exact: true }).click();
    await page.getByRole("status").filter({ hasText: "1 trashed" }).waitFor({ timeout: 20000 });

    await page.goto(`${frontendUrl}/trash`, { waitUntil: "domcontentloaded" });
    await page.getByRole("heading", { name: "Trash", exact: true }).waitFor({ timeout: 20000 });
    await card().waitFor({ timeout: 20000 });
    await page.getByRole("button", { name: `Select ${fileName}`, exact: true }).click();
    await page.getByRole("button", { name: "Restore", exact: true }).click();
    await page.getByRole("status").filter({ hasText: "1 restored" }).waitFor({ timeout: 20000 });

    await page.goto(`${frontendUrl}/photos`, { waitUntil: "domcontentloaded" });
    await page.getByRole("heading", { name: "Photos", exact: true }).waitFor({ timeout: 20000 });
    await card().waitFor({ timeout: 20000 });
    await page.getByRole("button", { name: `Select ${fileName}`, exact: true }).click();
    await page.getByRole("button", { name: "Trash", exact: true }).click();
    await page.getByRole("status").filter({ hasText: "1 trashed" }).waitFor({ timeout: 20000 });

    await Promise.all(responseChecks);
    const unexpectedErrors = responses.filter((response) => response.status >= 400
      && !(response.path === "/api/v1/users/me" && response.status === 401));
    const missingCorrelation = responses.filter((response) => !response.correlationId);
    const leaks = responses.filter((response) => response.leakedStorageIdentity);
    assert(uploadedItemId, "Upload completion did not return an item ID.");
    assert(unexpectedErrors.length === 0, `Unexpected API errors: ${JSON.stringify(unexpectedErrors)}`);
    assert(missingCorrelation.length === 0, `API responses missing correlation IDs: ${JSON.stringify(missingCorrelation)}`);
    assert(leaks.length === 0, `Browser API leaked storage identity: ${JSON.stringify(leaks)}`);

    console.log(JSON.stringify({
      passed: true,
      itemId: uploadedItemId,
      responseCount: responses.length,
      finalState: "trashed_after_restore_verification",
    }, null, 2));
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
