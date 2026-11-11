const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const { createRequire } = require("node:module");
const { chromium } = createRequire(
  path.resolve(__dirname, "../../frontend/package.json"),
)("playwright");

const frontendDirectory = path.resolve(__dirname, "../../frontend");
const outputDirectory = path.join(frontendDirectory, "out");
let siteUrl = "";

const journeys = [
  { role: "Owner", isOwner: true, viewport: { width: 1440, height: 900 } },
  { role: "Owner", isOwner: true, viewport: { width: 390, height: 844 } },
  { role: "Member", isOwner: false, viewport: { width: 1440, height: 900 } },
  { role: "Member", isOwner: false, viewport: { width: 390, height: 844 } },
];

async function main() {
  const server = await startStaticServer();
  try {
    const browser = await chromium.launch({
      headless: true,
      ...(process.env.PHOTODRIVE_BROWSER_CHANNEL
        ? { channel: process.env.PHOTODRIVE_BROWSER_CHANNEL }
        : {}),
    });
    try {
      for (const journey of journeys) await runJourney(browser, journey);
    } finally {
      await browser.close();
    }
  } finally {
    server.close();
    server.closeAllConnections();
  }
}

async function runJourney(browser, journey) {
  const context = await browser.newContext({
    viewport: journey.viewport,
    locale: "en-US",
  });
  const page = await context.newPage();
  const pageErrors = [];
  page.on("pageerror", (error) => {
    pageErrors.push(error.message);
    console.error(`PAGE ERROR ${journey.role} ${page.url()}: ${error.stack || error.message}`);
  });
  await context.route("**/api/v1/**", (route) => fulfillApi(route, journey));

  try {
    if (process.argv.includes("--uploads-only")) {
      await checkAlbumUploadThroughput(page);
      if (pageErrors.length)
        throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);
      console.log(
        `PASS album upload throughput ${journey.role} ${journey.viewport.width < 700 ? "phone" : "desktop"}`,
      );
      return;
    }
    if (process.argv.includes("--imports-only")) {
      await checkServerImports(page, journey);
      if (pageErrors.length)
        throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);
      console.log(
        `PASS server imports ${journey.role} ${journey.viewport.width < 700 ? "phone" : "desktop"}`,
      );
      return;
    }
    if (process.argv.includes("--albums-only")) {
      await openAndExpectHeading(
        page,
        "/albums?album=30000000-0000-0000-0000-000000000001",
        "Release album",
      );
      await checkAlbumCollaboration(page, journey);
      if (pageErrors.length)
        throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);
      console.log(
        `PASS album collaboration ${journey.role} ${journey.viewport.width < 700 ? "phone" : "desktop"}`,
      );
      return;
    }
    await checkStorageUsage(page);
    if (process.argv.includes("--storage-only")) { console.log(`PASS storage ${journey.role} ${journey.viewport.width}`); return; }
    await openAndExpectHeading(page, "/files", "Files");
    await page.getByLabel("Type").selectOption("pdf");
    await page.getByRole("button", { name: "Reset", exact: true }).click();
    if ((await page.getByLabel("Type").inputValue()) !== "all")
      throw new Error("Files reset did not clear the type filter.");
    if (
      (await page
        .getByLabel("Sort by")
        .locator("option:checked")
        .textContent()) !== "Recent"
    )
      throw new Error("Files did not default to the Recent sort.");
    const cardBackgrounds = await page
      .locator(".media-grid")
      .evaluate((grid) => ({
        folder: getComputedStyle(grid.querySelector(".folder-card"))
          .backgroundColor,
        file: getComputedStyle(grid.querySelector(".file-card"))
          .backgroundColor,
      }));
    if (cardBackgrounds.folder === cardBackgrounds.file)
      throw new Error("Folder and file cards did not have distinct styling.");
    if (
      (await page
        .locator(".folder-card .file-art")
        .evaluate((element) => getComputedStyle(element).boxShadow)) !== "none"
    )
      throw new Error("Folder artwork retained the stray inset arch.");
    await expectCount(
      page.locator(".sidebar").getByText("Favorites", { exact: true }),
      0,
    );
    const favoriteRequest = page.waitForRequest((request) => {
      const requestUrl = new URL(request.url());
      return (
        requestUrl.pathname.endsWith("/items") &&
        requestUrl.searchParams.get("favorite") === "true" &&
        requestUrl.searchParams.get("location") === "all"
      );
    });
    await page.getByRole("button", { name: "Show favorites only" }).click();
    await favoriteRequest;
    await page.getByLabel("Look in").waitFor();
    if ((await page.getByLabel("Look in").inputValue()) !== "all")
      throw new Error(
        "Files discovery did not expose the all-Files scope at the root.",
      );
    await page.getByRole("button", { name: "Show favorites only" }).click();
    await page.evaluate(() => {
      document.documentElement.dataset.theme = "dark";
    });
    await page.getByLabel("Search files").fill("test");
    await page.locator(".search-options summary").click();
    const advancedSearchBackground = await page
      .locator(".search-options > div")
      .evaluate((element) => getComputedStyle(element).backgroundColor);
    if (advancedSearchBackground === "rgb(248, 250, 249)")
      throw new Error(
        "Advanced search retained its light-only background in dark mode.",
      );
    await page.getByLabel("Search files").fill("");
    await page.getByRole("button", { name: "Upload", exact: true }).click();
    await page.getByRole("button", { name: "Choose a folder" }).waitFor();
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await page.getByRole("button", { name: "List view" }).click();
    await expectCount(page.locator(".file-list-row"), 3);
    await page
      .locator(
        '.file-list-row[data-media-item-id="10000000-0000-0000-0000-000000000002"] .file-list-name',
      )
      .click();
    await page.locator(".document-viewer").waitFor();
    await page.locator(".document-text-preview").waitFor();
    await page
      .locator(".document-viewer")
      .getByRole("button", { name: "Close" })
      .click();
    const pdfPagePromise = context.waitForEvent("page");
    await page
      .locator(
        '.file-list-row[data-media-item-id="10000000-0000-0000-0000-000000000003"] .file-list-name',
      )
      .click();
    const pdfPage = await pdfPagePromise;
    await pdfPage.waitForURL("**/api/v1/items/*/content");
    if (
      !pdfPage
        .url()
        .includes("/api/v1/items/10000000-0000-0000-0000-000000000003/content")
    )
      throw new Error(
        "PDF did not open its authenticated content URL in a new tab.",
      );
    await pdfPage.close();
    await page.locator(".file-list-more").nth(1).click();
    await page.locator(".item-context-menu").waitFor();
    await expectCount(
      page.getByRole("menuitem", { name: "Open in new tab" }),
      1,
    );
    await expectCount(page.getByRole("menuitem", { name: "Make a copy" }), 1);
    await page.getByRole("menuitem", { name: "Details" }).click();
    await page.locator(".details-panel").waitFor();
    await page.getByRole("button", { name: "Close details" }).click();
    await page.getByRole("button", { name: "Keyboard shortcuts" }).click();
    await page.getByRole("heading", { name: "Keyboard shortcuts" }).waitFor();
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await openAndExpectHeading(
      page,
      "/files?folder=10000000-0000-0000-0000-000000000001&name=test",
      "test",
    );
    await expectCount(
      page.locator(".folder-breadcrumbs").getByText("Documents", {
        exact: true,
      }),
      0,
    );
    await expectCount(
      page.locator(".folder-breadcrumbs").getByText("test", { exact: true }),
      1,
    );
    await expectDropOverlay(page, "Drop files to upload");
    await openAndExpectHeading(page, "/photos", "Photos");
    const photoScopeSelect = page
      .locator(".photo-filter-row .filter-select select")
      .first();
    if ((await photoScopeSelect.inputValue()) !== "folder")
      throw new Error("Photos did not default to the Photos folder scope.");
    await expectCount(photoScopeSelect.locator('option[value="library"]'), 0);
    await expectCount(photoScopeSelect.getByText("Photos folder"), 1);
    await expectPreviewDeepLinkRecovery(page);
    await openAndExpectHeading(page, "/photos", "Photos");
    await page.getByLabel("Search photos, events, or a date").fill("reunion");
    const eventSeekRequest = page.waitForRequest((request) => {
      const requestUrl = new URL(request.url());
      return (
        requestUrl.pathname.endsWith("/photos") &&
        requestUrl.searchParams.get("seekDate") === "2026-06-18"
      );
    });
    await page
      .locator(".event-suggestions")
      .getByRole("option", { name: /Summer reunion/ })
      .click();
    await eventSeekRequest;
    const selectedEvent = page.locator(".timeline-event-annotation.selected");
    await selectedEvent.getByText("Summer reunion").waitFor();
    await page
      .locator(".photo-scrubber-preview")
      .getByText("Summer reunion", { exact: true })
      .waitFor();
    await expectCount(page.locator(".photo-scrubber-event.active"), 1);
    await expectTimelineTargetPosition(
      selectedEvent,
      journey.viewport.width <= 600 ? 64 : 72,
      { allowBottomClamp: true },
    );
    await expectCount(page.locator(".selected-timeline-event"), 0);
    const surroundingTimelineRequests = Promise.all([
      page.waitForRequest((request) =>
        request.url().includes("cursor=release-newer-page"),
      ),
      page.waitForRequest((request) =>
        request.url().includes("cursor=release-older-page"),
      ),
    ]);
    const middleTimelineRequest = page.waitForRequest((request) =>
      request.url().includes("cursor=release-anchor-middle"),
    );
    const scrubber = await page.locator(".photo-scrubber-track").boundingBox();
    if (!scrubber) throw new Error("Timeline scrubber was not visible.");
    await page.mouse.click(
      scrubber.x + scrubber.width / 2,
      scrubber.y + scrubber.height / 2,
    );
    await middleTimelineRequest;
    await surroundingTimelineRequests;
    await page
      .locator('[data-photo-month-key="2025-08"]')
      .waitFor({ state: "attached" });
    await page
      .locator('[data-photo-month-key="2024-09"]')
      .waitFor({ state: "attached" });
    await page
      .locator('[data-photo-month-key="2017-01"]')
      .first()
      .waitFor({ state: "attached" });
    await expectTimelineTargetPosition(
      page.locator('[data-photo-month-key="2024-09"]').first(),
      journey.viewport.width <= 600 ? 64 : 72,
    );
    await page.getByRole("button", { name: "Show favorites only" }).click();
    await expectCount(
      page.locator(".active-filter-chips").getByRole("button", {
        name: "Favorites",
      }),
      1,
    );
    await page
      .locator(".active-filter-chips")
      .getByRole("button", { name: "Favorites" })
      .click();
    await expectDropOverlay(page, "Drop photos or videos to upload");
    await page.locator(".media-card").first().click({ button: "right" });
    const photoNewTab = page.getByRole("menuitem", {
      name: "Open in new tab",
    });
    await expectCount(photoNewTab, 1);
    if (!(await photoNewTab.getAttribute("href"))?.includes("preview="))
      throw new Error("Photo new-tab action did not preserve a preview URL.");
    await page.keyboard.press("Escape");
    const loadedPhotoCount = await page.locator(".media-card").count();
    await page.keyboard.press("Control+a");
    await expectCount(page.locator(".media-card.selected"), loadedPhotoCount);
    await page.keyboard.press("Escape");
    await openAndExpectHeading(
      page,
      "/albums?album=30000000-0000-0000-0000-000000000001",
      "Release album",
    );
    await page.locator(".media-card").first().click({ button: "right" });
    await expectCount(
      page.getByRole("menuitem", { name: "Open in new tab" }),
      1,
    );
    await page.keyboard.press("Escape");
    await checkAlbumCollaboration(page, journey);
    await openAndExpectHeading(page, "/shared", "Shared with me");

    if (journey.viewport.width < 700) {
      await page.locator(".menu-button").first().click();
      await page.locator(".sidebar.sidebar-open").waitFor();
    }
    const sidebar = page.locator(".sidebar");
    if (journey.isOwner) {
      await expectCount(
        sidebar.getByText("Family accounts", { exact: true }),
        1,
      );
      await expectCount(
        sidebar.getByText("Developer tools", { exact: true }),
        1,
      );
      await openAndExpectHeading(page, "/developer", "Developer tools");
      // The last verified backup date is shown on the always-visible Backup health card.
      await page
        .locator(".developer-health-card-meta", { hasText: "Last remotely verified backup:" })
        .waitFor({ state: "visible", timeout: 10000 });
      await capturePreview(page, "developer");
    } else {
      await expectCount(
        sidebar.getByText("Family accounts", { exact: true }),
        0,
      );
      await expectCount(
        sidebar.getByText("Developer tools", { exact: true }),
        0,
      );
      await openAndExpectHeading(page, "/developer", "Owner access required");
    }
    if (pageErrors.length)
      throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);
    console.log(
      `PASS ${journey.role} ${journey.viewport.width < 700 ? "phone" : "desktop"}`,
    );
  } finally {
    await context.close();
  }
}

async function checkAlbumCollaboration(page, journey) {
  const albumPath = "/albums?album=30000000-0000-0000-0000-000000000001";
  await page.setViewportSize({ width: journey.viewport.width, height: 420 });
  await page.getByRole("button", { name: "Share", exact: true }).click();
  const sharing = page.getByRole("dialog", { name: "Share “Release album”" });
  await assertScrollableWithinViewport(page, sharing);
  await sharing
    .getByRole("combobox", { name: "Family member", exact: true })
    .selectOption("00000000-0000-0000-0000-000000000003");
  await sharing
    .getByRole("combobox", { name: "Access", exact: true })
    .selectOption("collaborator");
  const grant = page.waitForRequest(
    (request) =>
      request.method() === "PUT" &&
      request.url().includes("/shares/") &&
      request.postDataJSON().role === "collaborator",
  );
  await sharing.getByRole("button", { name: "Share as collaborator" }).click();
  await grant;
  const existingRole = sharing.getByLabel("Access for Release recipient");
  await existingRole.waitFor();
  const downgrade = page.waitForRequest(
    (request) =>
      request.method() === "PUT" &&
      request.url().includes("/shares/") &&
      request.postDataJSON().role === "viewer",
  );
  await existingRole.selectOption("viewer");
  await downgrade;
  await sharing.getByRole("button", { name: "Close", exact: true }).click();
  await page.setViewportSize(journey.viewport);

  journey.albumRole = "collaborator";
  await openAndExpectHeading(page, albumPath, "Release album");
  await expectCount(page.getByRole("button", { name: "Edit", exact: true }), 0);
  await expectCount(
    page.getByRole("button", { name: "Share", exact: true }),
    0,
  );
  await expectCount(
    page.getByRole("button", { name: "Delete", exact: true }),
    0,
  );
  await page.getByRole("button", { name: "Add photos", exact: true }).click();
  const picker = page.getByRole("dialog", {
    name: "Add photos to “Release album”",
  });
  await picker.waitFor();
  await picker.getByRole("button", { name: "Close", exact: true }).click();
  await checkAlbumUpload(page, journey, albumPath);
  await page.setViewportSize({ width: journey.viewport.width, height: 240 });
  await page.locator(".media-card").first().click({ button: "right" });
  await assertScrollableWithinViewport(page, page.getByRole("menu"));
  const removal = page.waitForRequest(
    (request) =>
      request.method() === "POST" &&
      request.url().includes("/items/batch") &&
      request.postDataJSON().included === false,
  );
  await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
  await removal;
  await page.setViewportSize(journey.viewport);

  journey.albumRole = "viewer";
  await openAndExpectHeading(page, albumPath, "Release album");
  await expectCount(
    page.getByRole("button", { name: "Add photos", exact: true }),
    0,
  );
  await expectCount(
    page.getByRole("button", { name: "Upload photos", exact: true }),
    0,
  );
  await page.locator(".media-card").first().click({ button: "right" });
  await expectCount(
    page.getByRole("menuitem", { name: "Remove", exact: true }),
    0,
  );
  await page.keyboard.press("Escape");
  journey.albumRole = "owner";
}

async function assertScrollableWithinViewport(page, locator) {
  await locator.waitFor();
  const box = await locator.boundingBox();
  const viewport = page.viewportSize();
  if (
    !box ||
    box.y < 0 ||
    box.y + box.height > viewport.height + 1 ||
    box.x < 0 ||
    box.x + box.width > viewport.width + 1
  )
    throw new Error("Overlay extends outside the viewport.");
  const scroll = await locator.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    return {
      height: element.clientHeight,
      content: element.scrollHeight,
      top: element.scrollTop,
    };
  });
  if (scroll.content > scroll.height && scroll.top <= 0)
    throw new Error("Oversized overlay cannot scroll.");
}

async function checkAlbumUpload(page, journey, albumPath) {
  await page
    .getByRole("button", { name: "Upload photos", exact: true })
    .click();
  const dialog = page.getByRole("dialog", {
    name: "Upload to “Release album”",
  });
  await dialog
    .locator('input[type="file"]')
    .first()
    .setInputFiles({
      name: "Album upload.png",
      mimeType: "image/png",
      buffer: Buffer.from(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7ioAAAAASUVORK5CYII=",
        "base64",
      ),
    });
  await page.locator(".upload-queue-row.failed").waitFor();
  await page
    .getByText(
      "The upload is saved in your library, but could not be added to the album.",
      { exact: false },
    )
    .waitFor();
  await openAndExpectHeading(page, "/photos", "Photos");
  await page
    .locator(`[data-media-item-id="${journey.uploadItem.id}"]`)
    .waitFor();
  await page.reload();
  await page.locator(".upload-queue-row.failed").waitFor();
  await page
    .locator(".upload-queue-row.failed")
    .getByRole("button", { name: "Retry", exact: true })
    .click();
  await page.locator(".upload-queue-toggle").click();
  await page.locator(".upload-queue-row.completed").waitFor();
  if (
    journey.uploadCreates !== 1 ||
    journey.uploadCompletes !== 1 ||
    journey.albumUploadAttempts !== 2
  )
    throw new Error("Retrying album membership repeated the original upload.");
  await page
    .locator(".upload-queue-row.completed")
    .getByRole("button", { name: "Dismiss", exact: true })
    .click();
  await openAndExpectHeading(page, albumPath, "Release album");
  await page
    .locator(`[data-media-item-id="${journey.uploadItem.id}"]`)
    .waitFor();
}

async function checkAlbumUploadThroughput(page) {
  const sessions = new Map();
  const memberships = new Set();
  let releaseTransfers;
  const transfers = new Promise((resolve) => {
    releaseTransfers = resolve;
  });
  let releaseRefresh;
  const refresh = new Promise((resolve) => {
    releaseRefresh = resolve;
  });
  let refreshStarted = false;
  let cancelledId;
  const reply = (route, data) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ successful: true, data }),
    });
  await page.route(/\/api\/v1\/uploads(?:\/.*)?$/, async (route) => {
    const request = route.request();
    const parts = new URL(request.url()).pathname.split("/");
    if (parts.at(-1) === "uploads") {
      const input = request.postDataJSON();
      if (!input.useDefaultMediaFolder || input.parentId)
        throw new Error("Album batch changed the upload destination.");
      if (
        [...sessions.values()].some(
          (s) => s.clientUploadId === input.clientUploadId,
        )
      )
        throw new Error("An album batch file was created twice.");
      const session = {
        ...input,
        id: input.clientUploadId,
        receivedBytes: 0,
        chunkSizeBytes: 32,
        state: "pending",
      };
      sessions.set(session.id, session);
      return reply(route, session);
    }
    const completing = parts.at(-1) === "complete";
    const session = sessions.get(parts.at(completing ? -2 : -1));
    if (!session) throw new Error("Unknown synthetic upload session.");
    if (request.method() === "DELETE") {
      cancelledId = session.id;
      return reply(route, {});
    }
    if (request.method() === "PATCH") {
      await transfers;
      if (session.id === cancelledId) return;
      const range = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(
        request.headers()["content-range"],
      );
      if (
        !range ||
        Number(range[1]) !== session.receivedBytes ||
        Number(range[3]) !== session.sizeBytes
      )
        throw new Error("Concurrent uploads lost their ordered chunk offsets.");
      session.receivedBytes = Number(range[2]) + 1;
    }
    if (completing) {
      if (session.receivedBytes !== session.sizeBytes)
        throw new Error("Upload completed before all chunks arrived.");
      const crypto = require("node:crypto");
      const expectedHash = crypto
        .createHash("sha256")
        .update(Buffer.alloc(80, 7))
        .digest("hex");
      if (request.postDataJSON().sha256 !== expectedHash)
        throw new Error("Concurrent uploads supplied an incorrect checksum.");
      session.state = "completed";
      return reply(route, {
        item: releasePhotoItem(
          session.id,
          session.name,
          "2026-10-22T12:00:00Z",
        ),
        duplicateState: "none",
      });
    }
    return reply(route, session);
  });
  await page.route(/\/api\/v1\/albums\/[^/]+\/items\/batch$/, async (route) => {
    const { itemIds } = route.request().postDataJSON();
    itemIds.forEach((id) => memberships.add(id));
    return reply(route, {
      requestedCount: itemIds.length,
      succeededCount: itemIds.length,
      unchangedCount: 0,
      failedCount: 0,
      results: itemIds.map((itemId) => ({ itemId, status: "succeeded" })),
    });
  });
  await page.route(
    /\/api\/v1\/albums(?:\/[^/?]+)?(?:\?.*)?$/,
    async (route) => {
      if (!memberships.size) return route.fallback();
      refreshStarted = true;
      await refresh;
      return route.fallback();
    },
  );
  try {
    await openAndExpectHeading(
      page,
      "/albums?album=30000000-0000-0000-0000-000000000001",
      "Release album",
    );
    for (const indices of [[1], [2, 3, 4, 5, 6, 7, 8]]) {
      await page
        .getByRole("button", { name: "Upload photos", exact: true })
        .click();
      await page
        .getByRole("dialog")
        .locator('input[type="file"]')
        .first()
        .setInputFiles(
          indices.map((index) => ({
            name: `Batch ${index}.png`,
            mimeType: "image/png",
            buffer: Buffer.alloc(80, 7),
          })),
        );
      await page.getByRole("dialog").waitFor({ state: "hidden" });
      await waitForCondition(
        () => sessions.size >= 1,
        "The first upload did not start",
      );
    }
    await page.locator(".upload-queue-toggle").click();
    await waitForCondition(
      () => sessions.size >= 3,
      "Three files did not start together",
    );
    await page.locator(".upload-queue-row").nth(7).waitFor();
    await expectCount(page.locator(".upload-queue-row.uploading"), 3);
    if (sessions.size !== 3)
      throw new Error("Too many concurrent uploads started.");
    const cancelledRow = page
      .locator(".upload-queue-row")
      .filter({ hasText: "Batch 1.png" });
    await cancelledRow
      .getByRole("button", { name: "Cancel", exact: true })
      .click();
    await waitForCondition(
      () => cancelledId && sessions.size >= 4,
      "Cancelling one upload did not release its worker",
    );
    releaseTransfers();
    // Keep all album refresh responses blocked: the remaining files must still
    // finish, including multiple ordered chunks and album membership per file.
    await waitForCondition(
      () => memberships.size === 7 && refreshStarted,
      "Album refresh blocked the remaining uploads",
    );
    await page.waitForFunction(
      () =>
        document.querySelectorAll(".upload-queue-row.completed").length === 7,
    );
    await expectCount(page.locator(".upload-queue-row.failed"), 0);
    if (memberships.has(cancelledId) || sessions.size !== 8)
      throw new Error("Cancellation affected the wrong album upload.");
    console.log(
      "Verified: three concurrent files, isolated cancellation, seven completions while album refresh is blocked.",
    );
  } finally {
    releaseTransfers();
    releaseRefresh();
  }
}

async function waitForCondition(predicate, message) {
  const deadline = Date.now() + 15000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

async function expectDropOverlay(page, copy) {
  await page.locator(".workspace").evaluate((workspace) => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(["release smoke"], "smoke.txt"));
    workspace.dispatchEvent(
      new DragEvent("dragenter", {
        bubbles: true,
        cancelable: true,
        dataTransfer: transfer,
      }),
    );
  });
  await page.locator(".upload-drop-overlay").getByText(copy).waitFor();
  await page.locator(".workspace").evaluate((workspace) => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(["release smoke"], "smoke.txt"));
    workspace.dispatchEvent(
      new DragEvent("dragleave", {
        bubbles: true,
        cancelable: true,
        dataTransfer: transfer,
      }),
    );
  });
}

async function expectPreviewDeepLinkRecovery(page) {
  const photos = releasePhotoItems();
  const first = photos[0];
  const second = photos[1];

  await page
    .locator(`[data-media-item-id="${second.id}"] .media-visual`)
    .click();
  await page.waitForURL(`**/photos?preview=${second.id}`);
  await page
    .getByRole("dialog", {
      name: `${second.name} media viewer`,
      exact: true,
    })
    .waitFor();

  await page.goBack();
  await page.waitForURL("**/photos");
  await expectGone(page.locator(".photo-viewer"));

  await page.goForward();
  await page.waitForURL(`**/photos?preview=${second.id}`);
  await page.locator(".photo-viewer").waitFor();
  await page.reload({ waitUntil: "domcontentloaded" });
  await page
    .getByRole("dialog", {
      name: `${second.name} media viewer`,
      exact: true,
    })
    .waitFor();

  await page.getByRole("button", { name: "Previous photo" }).click();
  await page.waitForURL(`**/photos?preview=${first.id}`);
  await page
    .getByRole("dialog", {
      name: `${first.name} media viewer`,
      exact: true,
    })
    .waitFor();

  await page.getByRole("button", { name: "Next photo" }).click();
  await page.waitForURL(`**/photos?preview=${second.id}`);
  await page
    .getByRole("dialog", {
      name: `${second.name} media viewer`,
      exact: true,
    })
    .waitFor();

  await page
    .locator('.photo-viewer-toolbar button[aria-label="Close viewer"]')
    .click();
  await page.waitForURL("**/photos");
  await expectGone(page.locator(".photo-viewer"));

  const unavailableId = "20000000-0000-0000-0000-999999999999";
  await page.goto(`${siteUrl}/photos?preview=${unavailableId}`, {
    waitUntil: "domcontentloaded",
  });
  await page
    .getByRole("heading", { name: "Preview unavailable", exact: true })
    .waitFor();
  const retryRequest = page.waitForRequest((request) =>
    request.url().endsWith(`/api/v1/items/${unavailableId}`),
  );
  await page.getByRole("button", { name: "Try again" }).click();
  await retryRequest;
  await page
    .getByRole("heading", { name: "Preview unavailable", exact: true })
    .waitFor();
  await page.getByRole("button", { name: "Close viewer" }).click();
  await page.waitForURL("**/photos");
  await expectGone(page.locator(".photo-viewer"));
}

async function openAndExpectHeading(page, path, heading) {
  await page.goto(`${siteUrl}${path}`, { waitUntil: "domcontentloaded" });
  try {
    await page
      .getByRole("heading", { name: heading, exact: true })
      .waitFor({ timeout: 10_000 });
  } catch (error) {
    throw new Error(
      `Expected ${path} heading ${heading}. Page text: ${(await page.locator("body").innerText()).slice(0, 1000)}`,
      { cause: error },
    );
  }
  const filters = page.getByRole("button", { name: /Filters & sort/ });
  if (await filters.isVisible()) {
    if ((await filters.getAttribute("aria-expanded")) !== "false")
      throw new Error("Phone filters should start collapsed.");
    await filters.focus();
    await page.keyboard.press("Enter");
    if ((await filters.getAttribute("aria-expanded")) !== "true")
      throw new Error("Phone filters did not expand from the keyboard.");
  }
}

async function expectCount(locator, expected) {
  const count = await locator.count();
  if (count !== expected)
    throw new Error(`Expected locator count ${expected}, received ${count}`);
}

async function expectTimelineTargetPosition(
  locator,
  expectedTop,
  { allowBottomClamp = false } = {},
) {
  let bounds;
  let acceptable = false;
  for (let attempt = 0; attempt < 20 && !acceptable; attempt++) {
    bounds = await locator.boundingBox();
    acceptable = Boolean(
      bounds &&
      (Math.abs(bounds.y - expectedTop) <= 5 ||
        (allowBottomClamp && bounds.y >= expectedTop)),
    );
    if (!acceptable) await new Promise((resolve) => setTimeout(resolve, 50));
  }
  if (!acceptable)
    throw new Error(
      `Expected timeline target near ${expectedTop}px, received ${bounds?.y ?? "no bounds"}.`,
    );
}

async function checkServerImports(page, journey) {
  await openAndExpectHeading(
    page,
    "/imports",
    journey.isOwner ? "Import from server" : "Owner access required",
  );
  await expectCount(page.locator(".topbar .notification-button"), 0);
  const navigationButton = page.locator(".topbar .menu-button");
  if (journey.viewport.width <= 900) {
    await navigationButton.click();
    await page.locator(".sidebar-open").waitFor();
    await page.locator(".sidebar-close").click();
  } else if (await navigationButton.isVisible()) {
    throw new Error("An inactive desktop navigation button is visible.");
  }
  await expectCount(
    page
      .locator(".sidebar")
      .getByRole("link", { name: "Import from server", exact: true }),
    journey.isOwner ? 1 : 0,
  );
  if (!journey.isOwner) {
    if (journey.importRequests)
      throw new Error("A member page sent a server-import API request.");
    return;
  }
  const picker = page.getByRole("region", { name: "Choose a server folder" });
  await picker.getByRole("alert").waitFor();
  await picker.getByRole("button", { name: "Refresh imports" }).click();
  await picker.getByRole("button", { name: "Takeout", exact: true }).waitFor();
  if (
    !(await page
      .getByRole("button", { name: "Scan and start import", exact: true })
      .isDisabled())
  )
    throw new Error("An import could start without a selected source.");
  await picker.getByRole("button", { name: "More folders" }).click();
  await picker
    .getByRole("button", { name: "Another album", exact: true })
    .waitFor();
  await picker.getByRole("button", { name: "Takeout", exact: true }).click();
  await picker
    .getByRole("button", { name: "Google Photos", exact: true })
    .click();
  await picker.getByText("No subfolders here.", { exact: false }).waitFor();
  await picker.getByRole("button", { name: "Use this folder" }).click();
  await picker.getByRole("status").waitFor();
  if (process.env.PHOTODRIVE_UI_SCREENSHOTS) {
    fs.mkdirSync(process.env.PHOTODRIVE_UI_SCREENSHOTS, { recursive: true });
    for (const theme of ["light", "dark"]) {
      await page.evaluate((value) => {
        document.documentElement.dataset.theme = value;
      }, theme);
      await page.screenshot({
        path: path.join(
          process.env.PHOTODRIVE_UI_SCREENSHOTS,
          `imports-${journey.viewport.width}-${theme}.png`,
        ),
        fullPage: true,
        animations: "disabled",
      });
    }
  }
  await page
    .getByRole("button", { name: "Scan and start import", exact: true })
    .click();
  await page.getByText("Import started:", { exact: false }).waitFor();
  if (journey.importBody?.sourcePath !== "Takeout/Google Photos")
    throw new Error("The folder picker did not submit a relative source.");
  await expectCount(picker.locator("input"), 0);
  const fits = await picker.evaluate((element) => {
    const bounds = element.getBoundingClientRect();
    return bounds.left >= 0 && bounds.right <= innerWidth;
  });
  if (!fits)
    throw new Error("The server folder picker extends outside the viewport.");

  // A finished import with failures can be removed from the list after confirming.
  page.once("dialog", (dialog) => void dialog.accept());
  await page.getByRole("button", { name: "Remove from list", exact: true }).click();
  await page.getByText("The import was removed from the list.", { exact: true }).waitFor();
  if (!journey.importRemoved)
    throw new Error("Remove from list did not send DELETE /imports/:id.");
  await expectCount(page.getByRole("button", { name: "Remove from list", exact: true }), 0);
}

async function fulfillApi(route, journey) {
  const url = new URL(route.request().url());
  const path = url.pathname;
  if (path.endsWith("/items/20000000-0000-0000-0000-999999999999")) {
    await route.fulfill({
      status: 404,
      contentType: "application/json",
      headers: {
        "Access-Control-Allow-Origin": siteUrl,
        "Access-Control-Allow-Credentials": "true",
        "X-CSRF-Token": "release-smoke-csrf",
      },
      body: JSON.stringify({
        successful: false,
        error: { detail: "The requested item was not found." },
      }),
    });
    return;
  }
  let data = {};
  if (path.includes("/imports")) {
    journey.importRequests = (journey.importRequests || 0) + 1;
    if (route.request().method() === "DELETE" && path.endsWith("/imports/finished-job")) {
      journey.importRemoved = true;
      data = null;
    } else if (path.endsWith("/imports")) {
      data = journey.importRemoved
        ? []
        : [
            {
              id: "finished-job",
              destinationFolderId: "import-media",
              destinationPath: "Media",
              sourceKind: "google_takeout",
              nameConflictPolicy: "skip",
              skipIdenticalFiles: true,
              state: "completed_with_errors",
              discoveredCount: 3,
              pendingCount: 0,
              processingCount: 0,
              importedCount: 1,
              duplicateCount: 0,
              nameConflictCount: 0,
              failedCount: 2,
              warningCount: 0,
              orphanSidecarCount: 0,
              ignoredJsonCount: 0,
              outcomes: { failed: 2 },
              createdAt: "2026-10-20T12:00:00Z",
              completedAt: "2026-10-20T12:05:00Z",
            },
          ];
    } else if (path.endsWith("/imports/options"))
      data = {
        destinations: [
          {
            id: "import-media",
            name: "Media",
            path: "Media",
            systemRole: "media_root",
          },
        ],
      };
    else if (path.endsWith("/imports/source-folders")) {
      if (!journey.sourceFailureShown) {
        journey.sourceFailureShown = true;
        await route.fulfill({
          status: 400,
          contentType: "application/json",
          body: JSON.stringify({
            successful: false,
            error: { detail: "Source unavailable" },
          }),
        });
        return;
      }
      const relative = url.searchParams.get("path") || "";
      data = {
        path: relative,
        parentPath: relative ? (relative === "Takeout" ? "" : "Takeout") : null,
        folders:
          relative === ""
            ? url.searchParams.has("cursor")
              ? [{ name: "Another album", path: "Another album" }]
              : [{ name: "Takeout", path: "Takeout" }]
            : relative === "Takeout"
              ? [{ name: "Google Photos", path: "Takeout/Google Photos" }]
              : [],
        nextCursor:
          !relative && !url.searchParams.has("cursor") ? "next-folders" : null,
      };
    } else if (path.endsWith("/imports/google-takeout")) {
      journey.importBody = route.request().postDataJSON();
      data = {
        id: "import-job",
        destinationFolderId: "import-media",
        destinationPath: "Media",
        sourceKind: "google_takeout",
        state: "completed",
        discoveredCount: 0,
        pendingCount: 0,
        processingCount: 0,
        importedCount: 0,
        duplicateCount: 0,
        nameConflictCount: 0,
        failedCount: 0,
        warningCount: 0,
        orphanSidecarCount: 0,
        ignoredJsonCount: 0,
        outcomes: {},
        createdAt: "2026-10-22T12:00:00Z",
      };
    } else data = [];
  } else if (path.endsWith("/users/me")) {
    data = {
      id: journey.isOwner
        ? "00000000-0000-0000-0000-000000000001"
        : "00000000-0000-0000-0000-000000000002",
      email: `${journey.role.toLowerCase()}@example.test`,
      displayName: `Release ${journey.role}`,
      hasPassword: true,
      emailVerified: true,
      isOwner: journey.isOwner,
      googleLoginAllowed: false,
      passwordLoginAllowed: true,
      mustChangePassword: false,
    };
  } else if (path.endsWith("/users/sign-in-options")) {
    data = { googleEnabled: false, passwordEnabled: true };
  } else if (path.endsWith("/storage-usage/files")) {
    const category = url.searchParams.get("category");
    data = { files: category === "versions" ? [] : [{ id: "20000000-0000-0000-0000-000000000001", name: category === "video" ? "large-video.mp4" : "archived-photo.jpg", bytes: 1000000, trashed: category === "trash", versionId: "30000000-0000-0000-0000-000000000001", current: true }], hasMore: false };
  } else if (path.endsWith("/storage-usage")) {
    data = { physicalBytes: 1000000, logicalBytes: 2000000, trashBytes: 0, retainedVersionBytes: 0, videoBytes: 1000000, calculatedAt: "2026-11-05T12:00:00Z", growth: [{ key: "2026-11", bytes: 1000000, count: 1 }], folders: [{ id: "10000000-0000-0000-0000-000000000001", name: "Archive folder", bytes: 1000000 }] };
  } else if (path.endsWith("/items/storage-summary")) {
    data = {
      catalogFileCount: 0,
      storedOriginalBytes: 0,
      uniqueOriginalCount: 0,
      reclaimableTrashBytes: 0,
    };
  } else if (path.endsWith("/items/trash/summary")) {
    data = { fileCount: 0, folderCount: 0, originalCount: 0, originalBytes: 0 };
  } else if (path.endsWith("/photos/years")) {
    data = { years: [] };
  } else if (path.endsWith("/photos/timeline-anchors")) {
    data = {
      anchors: [
        { year: 2026, month: 8, count: 1, cursor: "release-anchor-newest" },
        { year: 2024, month: 9, count: 1, cursor: "release-anchor-middle" },
        { year: 2017, month: 1, count: 1, cursor: "release-anchor-oldest" },
      ],
      totalCount: 3,
    };
  } else if (path.endsWith("/timeline-events")) {
    data = { events: [releaseTimelineEvent()] };
  } else if (path.endsWith("/tags")) {
    data = [];
  } else if (path.endsWith("/uploads") && route.request().method() === "POST") {
    const request = route.request().postDataJSON();
    if (!request.useDefaultMediaFolder || request.parentId)
      throw new Error(
        "Album uploads must be placed in the uploader's Photos library.",
      );
    journey.uploadCreates = (journey.uploadCreates ?? 0) + 1;
    journey.uploadSession = {
      id: "50000000-0000-0000-0000-000000000001",
      sizeBytes: request.sizeBytes,
      receivedBytes: 0,
      chunkSizeBytes: 5242880,
      state: "uploading",
    };
    data = journey.uploadSession;
  } else if (/\/uploads\/[^/]+\/complete$/.test(path)) {
    journey.uploadCompletes = (journey.uploadCompletes ?? 0) + 1;
    journey.uploadItem = releasePhotoItem(
      "20000000-0000-0000-0000-000000000099",
      "Album upload.png",
      "2026-10-22T12:00:00Z",
    );
    journey.uploadSession.state = "completed";
    data = { item: journey.uploadItem, duplicateState: "none" };
  } else if (/\/uploads\/[^/]+$/.test(path)) {
    if (route.request().method() === "PATCH")
      journey.uploadSession.receivedBytes = journey.uploadSession.sizeBytes;
    data = journey.uploadSession;
  } else if (path.endsWith("/system/cache-builds/options")) {
    data = {
      originalCacheMaxBytes: 53687091200,
      originalCacheBytes: 0,
      originalCacheEntryCount: 0,
      availableDiskBytes: 107374182400,
      catalogMediaItemCount: 0,
      distinctOriginalCount: 0,
      gridThumbnailCount: 0,
      cachedOriginalCount: 0,
    };
  } else if (path.endsWith("/system/cache-builds")) {
    data = [];
  } else if (path.endsWith("/developer/maintenance/hosts")) {
    data = { hosts: [] };
  } else if (path.endsWith("/developer/maintenance/backup-status")) {
    data = {
      lastRemoteVerifiedAt: new Date().toISOString(),
      ageHours: 0,
      freshnessWindowHours: 168,
      state: "current",
    };
  } else if (path.endsWith("/developer/maintenance/health")) {
    data = {
      checkedAt: new Date().toISOString(),
      state: "healthy",
      consecutiveBackupFailureCount: 0,
      pendingUploadCount: 0,
      staleUploadCount: 0,
      pendingMetadataCount: 0,
      staleMetadataCount: 0,
      failedMetadataCount: 0,
      repeatedWorkerFailureCount: 0,
      availableDiskBytes: 107374182400,
      originalCacheBytes: 0,
      originalCacheMaxBytes: 53687091200,
      lowDiskSpace: false,
      cachePressure: false,
      issues: [],
    };
  } else if (path.endsWith("/shares/recipients")) {
    data = {
      recipients: [
        {
          userId: "00000000-0000-0000-0000-000000000003",
          displayName: "Release recipient",
          email: "recipient@example.test",
        },
      ],
    };
  } else if (/\/albums\/[^/]+\/shares\/[^/]+$/.test(path)) {
    data = {
      recipientUserId: path.split("/").at(-1),
      recipientDisplayName: "Release recipient",
      recipientEmail: "recipient@example.test",
      role: route.request().postDataJSON().role,
      sharedAt: new Date().toISOString(),
    };
    journey.albumShares = [data];
  } else if (/\/albums\/[^/]+\/shares$/.test(path)) {
    data = { shares: journey.albumShares ?? [] };
  } else if (/\/albums\/[^/]+\/items\/batch$/.test(path)) {
    const { itemIds, included } = route.request().postDataJSON();
    const albumUpload = included && itemIds.includes(journey.uploadItem?.id);
    if (albumUpload)
      journey.albumUploadAttempts = (journey.albumUploadAttempts ?? 0) + 1;
    const fail = albumUpload && journey.albumUploadAttempts === 1;
    if (albumUpload && !fail) journey.uploadAdded = true;
    data = {
      requestedCount: itemIds.length,
      succeededCount: fail ? 0 : itemIds.length,
      unchangedCount: 0,
      failedCount: fail ? itemIds.length : 0,
      results: itemIds.map((itemId) => ({
        itemId,
        status: fail ? "failed" : "succeeded",
        message: fail ? "Simulated album membership failure" : undefined,
      })),
    };
  } else if (/\/albums\/[^/]+$/.test(path)) {
    data = {
      album: { ...releaseAlbum(), accessRole: journey.albumRole ?? "owner" },
      items: [
        ...releasePhotoItems(),
        ...(journey.uploadAdded ? [journey.uploadItem] : []),
      ],
      availableYears: [],
    };
  } else if (path.endsWith("/albums")) {
    data = {
      albums: [{ ...releaseAlbum(), accessRole: journey.albumRole ?? "owner" }],
    };
  } else if (path.endsWith("/photos")) {
    const cursor = url.searchParams.get("cursor");
    if (cursor === "release-anchor-middle") {
      data = {
        items: [
          releasePhotoItem(
            "20000000-0000-0000-0000-000000000011",
            "Middle timeline photo",
            "2024-09-15T12:00:00Z",
          ),
        ],
        previousCursor: "release-newer-page",
        nextCursor: "release-older-page",
      };
    } else if (cursor === "release-newer-page") {
      await new Promise((resolve) => setTimeout(resolve, 80));
      data = {
        items: [
          releasePhotoItem(
            "20000000-0000-0000-0000-000000000012",
            "Newer timeline photo",
            "2025-08-01T12:00:00Z",
          ),
        ],
        previousCursor: null,
        nextCursor: "release-anchor-middle",
      };
    } else if (cursor === "release-older-page") {
      await new Promise((resolve) => setTimeout(resolve, 80));
      data = {
        items: Array.from({ length: 30 }, (_, index) =>
          releasePhotoItem(
            `20000000-0000-0000-0000-${String(13 + index).padStart(12, "0")}`,
            `Older timeline photo ${index + 1}`,
            `2017-01-${String(31 - index).padStart(2, "0")}T12:00:00Z`,
          ),
        ),
        previousCursor: "release-anchor-middle",
        nextCursor: null,
      };
    } else {
      data = {
        items: [
          ...releasePhotoItems(),
          ...(journey.uploadItem ? [journey.uploadItem] : []),
        ],
      };
    }
  } else if (path.endsWith("/items/favorite-folders")) {
    data = {
      items: releaseItems()
        .filter((item) => item.kind === "folder")
        .map((item) => ({ ...item, favorite: true })),
    };
  } else if (path.endsWith("/items/trash")) {
    data = { items: [] };
  } else if (path.endsWith("/items")) {
    data = {
      items:
        url.searchParams.get("favorite") === "true"
          ? releaseItems()
              .filter((item) => item.kind === "folder")
              .map((item) => ({ ...item, favorite: true }))
          : releaseItems(),
    };
  } else if (/\/items\/[^/]+$/.test(path)) {
    const itemId = path.split("/").at(-1);
    data = [...releaseItems(), releaseDocumentsRoot()].find(
      (item) => item.id === itemId,
    );
  } else if (path.endsWith("/shares") || path.endsWith("/search")) {
    data = { items: releaseItems() };
  }
  await route.fulfill({
    status: 200,
    contentType: "application/json",
    headers: {
      "Access-Control-Allow-Origin": siteUrl,
      "Access-Control-Allow-Credentials": "true",
      "X-CSRF-Token": "release-smoke-csrf",
    },
    body: JSON.stringify({ successful: true, data }),
  });
}

function releasePhotoItems() {
  return [
    releasePhotoItem(
      "20000000-0000-0000-0000-000000000001",
      "Family picnic.jpg",
      "2026-05-20T12:00:00Z",
    ),
    releasePhotoItem(
      "20000000-0000-0000-0000-000000000002",
      "Garden afternoon.jpg",
      "2026-05-19T12:00:00Z",
    ),
    releasePhotoItem(
      "20000000-0000-0000-0000-000000000003",
      "Boardwalk.jpg",
      "2026-05-18T12:00:00Z",
    ),
  ];
}

function releasePhotoItem(id, name, capturedAt) {
  return {
    id,
    name,
    parentId: null,
    kind: "image",
    mimeType: "image/jpeg",
    sizeBytes: 2457600,
    createdAt: "2026-07-01T12:00:00Z",
    modifiedAt: "2026-07-30T12:00:00Z",
    capturedAt,
    favorite: false,
    localState: "remote",
    syncState: "synced",
    tags: [],
    capabilities: {
      canRename: true,
      canMove: true,
      canTrash: true,
      canFavorite: true,
      canShare: true,
    },
  };
}

function releaseTimelineEvent() {
  return {
    id: "40000000-0000-0000-0000-000000000001",
    title: "Summer reunion",
    startDate: "2026-06-18",
    endDate: "2026-06-21",
    description: "A synthetic family timeline event",
    createdAt: "2026-07-01T12:00:00Z",
    modifiedAt: "2026-07-01T12:00:00Z",
  };
}

function releaseAlbum() {
  return {
    id: "30000000-0000-0000-0000-000000000001",
    name: "Release album",
    description: "Synthetic release smoke album",
    memberCount: 1,
    coverFileIds: [],
    createdAt: "2026-07-01T12:00:00Z",
    modifiedAt: "2026-07-30T12:00:00Z",
    accessRole: "owner",
  };
}

function releaseItems() {
  const shared = {
    parentId: null,
    createdAt: "2026-07-01T12:00:00Z",
    modifiedAt: "2026-07-30T12:00:00Z",
    favorite: false,
    localState: "remote",
    syncState: "synced",
    tags: [],
    capabilities: {
      canRename: true,
      canMove: true,
      canTrash: true,
      canFavorite: true,
      canShare: true,
    },
  };
  return [
    {
      ...shared,
      id: "10000000-0000-0000-0000-000000000001",
      name: "test",
      parentId: "10000000-0000-0000-0000-000000000000",
      kind: "folder",
      childCount: 3,
    },
    {
      ...shared,
      id: "10000000-0000-0000-0000-000000000002",
      name: "Insurance metadata.json",
      kind: "document",
      mimeType: "application/json",
      sizeBytes: 3276,
    },
    {
      ...shared,
      id: "10000000-0000-0000-0000-000000000003",
      name: "Insurance 2026.pdf",
      kind: "document",
      mimeType: "application/pdf",
      sizeBytes: 3276800,
    },
  ];
}

function releaseDocumentsRoot() {
  return {
    ...releaseItems()[0],
    id: "10000000-0000-0000-0000-000000000000",
    name: "Documents",
    parentId: null,
    systemRole: "documents_root",
  };
}

function startStaticServer() {
  if (!fs.existsSync(path.join(outputDirectory, "index.html"))) {
    throw new Error("The frontend export is missing. Run npm run build first.");
  }
  const server = http.createServer((request, response) => {
    const pathname = decodeURIComponent(
      new URL(request.url, "http://127.0.0.1").pathname,
    );
    const relative = pathname === "/" ? "index.html" : pathname.slice(1);
    const candidate = path.resolve(
      outputDirectory,
      path.extname(relative) ? relative : `${relative}.html`,
    );
    if (
      !candidate.startsWith(`${outputDirectory}${path.sep}`) ||
      !fs.existsSync(candidate)
    ) {
      response.writeHead(404).end("Not found");
      return;
    }
    const extension = path.extname(candidate);
    const contentType =
      extension === ".html"
        ? "text/html; charset=utf-8"
        : extension === ".js"
          ? "text/javascript; charset=utf-8"
          : extension === ".css"
            ? "text/css; charset=utf-8"
            : extension === ".svg"
              ? "image/svg+xml"
              : "application/octet-stream";
    response.writeHead(200, { "Content-Type": contentType });
    fs.createReadStream(candidate).pipe(response);
  });
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      siteUrl = `http://127.0.0.1:${address.port}`;
      resolve(server);
    });
  });
}

main().then(
  () => process.exit(0),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);

async function checkStorageUsage(page) {
  await openAndExpectHeading(page, "/storage", "Storage");
  await page.getByText("Originals stored", { exact: true }).waitFor();
  if (await page.getByRole("heading", { name: "Growth by month" }).count()) throw new Error("Monthly growth should be removed");
  await page.getByRole("button", { name: /^Videos/ }).click();
  await page.getByRole("link", { name: /large-video.mp4/ }).waitFor();
  await page.getByRole("button", { name: "Only in older versions" }).click();
  await page.getByText("No files in this breakdown.").waitFor();
  await page.getByRole("button", { name: /Archive folder/ }).click();
  await page.getByRole("heading", { name: "Archive folder", exact: true }).waitFor();
  await page.getByRole("button", { name: "All files", exact: true }).click();
  await page.getByRole("heading", { name: "Largest files", exact: true }).waitFor();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
  if (overflow) throw new Error("Storage page overflows viewport");
  await capturePreview(page, "storage");
  await page.getByRole("button", { name: "Account menu", exact: true }).click();
  await page.getByRole("menuitem", { name: "Account settings" }).click();
  const settings = page.getByRole("dialog", { name: "Account settings" });
  await settings.getByRole("combobox", { name: /Language/ }).waitFor();
  if (await settings.getByLabel("Current password", { exact: true }).count()) throw new Error("Password form should be under Security");
  await settings.getByRole("combobox", { name: /Appearance/ }).selectOption("dark");
  await capturePreview(page, "account");
  await settings.getByRole("combobox", { name: /Appearance/ }).selectOption("light");
  await settings.getByRole("button", { name: "Security", exact: true }).click();
  await settings.getByLabel("Current password", { exact: true }).waitFor();
  await page.keyboard.press("Escape");
  await settings.waitFor({ state: "hidden" });

}

async function capturePreview(page, name) {
  if (!process.env.PHOTODRIVE_SCREENSHOT_DIR) return;
  fs.mkdirSync(process.env.PHOTODRIVE_SCREENSHOT_DIR, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({path: path.join(process.env.PHOTODRIVE_SCREENSHOT_DIR, name + "-" + page.viewportSize().width + ".png"),fullPage:true, animations:"disabled"});
}

// Closing an overlay updates the URL before React commits the unmount, so wait for it to leave the DOM.
async function expectGone(locator, timeout = 5000) {
  await locator.first().waitFor({ state: "detached", timeout });
}
