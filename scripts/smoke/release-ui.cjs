const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const { chromium } = require("playwright");

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
    const browser = await chromium.launch({ headless: true });
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
  const context = await browser.newContext({ viewport: journey.viewport });
  const page = await context.newPage();
  const pageErrors = [];
  page.on("pageerror", (error) => {
    pageErrors.push(error.message);
    console.error(`PAGE ERROR ${journey.role}: ${error.message}`);
  });
  await page.route("**/api/v1/**", (route) => fulfillApi(route, journey));

  try {
    await openAndExpectHeading(page, "/files", "Files");
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
  await expectCount(page.locator(".photo-viewer"), 0);

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
  await expectCount(page.locator(".photo-viewer"), 0);

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
  await expectCount(page.locator(".photo-viewer"), 0);
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
  if (path.endsWith("/users/me")) {
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
  } else if (path.endsWith("/items/storage-summary")) {
    data = {
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
  } else if (path.endsWith("/developer/maintenance/backup-status")) {
    data = {
      lastRemoteVerifiedAt: new Date().toISOString(),
      ageHours: 0,
      freshnessWindowHours: 12,
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
  } else if (/\/albums\/[^/]+$/.test(path)) {
    data = { album: releaseAlbum(), items: releasePhotoItems() };
  } else if (path.endsWith("/albums")) {
    data = { albums: [releaseAlbum()] };
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
      data = { items: releasePhotoItems() };
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
