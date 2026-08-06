"use client";

import {
  AlertTriangle,
  CheckCircle2,
  LoaderCircle,
  RefreshCw,
  UploadCloud,
  X,
  XCircle,
} from "lucide-react";
import { useMemo, useState } from "react";
import { formatBytes } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";
import { useUploadQueue, type UploadQueueItem } from "./upload-queue-provider";

export function UploadQueueOverlay() {
  const { t } = usePreferences();
  const {
    items,
    retry,
    keepBoth,
    keepAllConflicts,
    skipAllConflicts,
    cancelAllPending,
    remove,
    clearCompleted,
  } = useUploadQueue();
  const [open, setOpen] = useState(false);
  const [dismissedAttentionKey, setDismissedAttentionKey] = useState("");
  const [bulkBusy, setBulkBusy] = useState(false);
  const [cancelConfirmationKey, setCancelConfirmationKey] = useState("");
  const attentionCount = items.filter(
    (item) =>
      item.state === "conflict" ||
      item.state === "failed" ||
      item.state === "paused",
  ).length;
  const activeCount = items.filter(
    (item) => item.state === "queued" || item.state === "uploading",
  ).length;
  const completedCount = items.filter(
    (item) => item.state === "completed",
  ).length;
  const conflictCount = items.filter(
    (item) => item.state === "conflict",
  ).length;
  const pendingCount = items.filter(
    (item) => item.state !== "completed" && item.state !== "skipped",
  ).length;
  const pendingKey = items
    .filter((item) => item.state !== "completed" && item.state !== "skipped")
    .map((item) => item.id)
    .sort()
    .join("|");
  const confirmCancelAll =
    Boolean(pendingKey) && cancelConfirmationKey === pendingKey;
  const sortedItems = useMemo(
    () =>
      [...items].sort((left, right) =>
        right.createdAt.localeCompare(left.createdAt),
      ),
    [items],
  );
  const attentionKey = items
    .filter(
      (item) =>
        item.state === "conflict" ||
        item.state === "failed" ||
        item.state === "paused",
    )
    .map((item) => `${item.id}:${item.state}`)
    .sort()
    .join("|");
  const panelOpen =
    open || (Boolean(attentionKey) && attentionKey !== dismissedAttentionKey);
  const runBulkAction = async (action: () => Promise<void>) => {
    setBulkBusy(true);
    try {
      await action();
    } finally {
      setBulkBusy(false);
    }
  };

  if (!items.length && !panelOpen) return null;

  return (
    <>
      {!panelOpen && (
        <button
          className={`upload-queue-toggle ${attentionCount ? "attention" : ""}`}
          type="button"
          onClick={() => setOpen(true)}
          aria-label={t("upload.queueOpen", { count: items.length })}
        >
          {activeCount > 0 ? (
            <LoaderCircle className="catalog-spinner" />
          ) : attentionCount > 0 ? (
            <AlertTriangle />
          ) : (
            <UploadCloud />
          )}
          <span>
            {t("upload.queueToggle", {
              count: items.length,
              attention: attentionCount,
            })}
          </span>
        </button>
      )}
      {panelOpen && (
        <aside
          className="upload-queue-panel"
          aria-label={t("upload.queueTitle")}
        >
          <header className="upload-queue-header">
            <div>
              <p className="eyebrow">{t("upload.queueEyebrow")}</p>
              <h2>{t("upload.queueTitle")}</h2>
            </div>
            <div>
              {completedCount > 0 && (
                <button
                  className="text-button"
                  type="button"
                  onClick={() => void clearCompleted()}
                >
                  {t("upload.queueClear")}
                </button>
              )}
              <button
                className="icon-button"
                type="button"
                onClick={() => {
                  setOpen(false);
                  setDismissedAttentionKey(attentionKey);
                  setCancelConfirmationKey("");
                }}
                aria-label={t("upload.queueClose")}
              >
                <X />
              </button>
            </div>
          </header>
          <p className="upload-queue-explanation">
            {t("upload.queueDurable")}
          </p>
          {(conflictCount > 1 || pendingCount > 0) && (
            <div className="upload-queue-bulk">
              {!confirmCancelAll ? (
                <div className="upload-queue-bulk-actions">
                  {conflictCount > 1 && (
                    <>
                      <button
                        type="button"
                        disabled={bulkBusy}
                        onClick={() =>
                          void runBulkAction(keepAllConflicts)
                        }
                      >
                        {t("upload.queueKeepAll", { count: conflictCount })}
                      </button>
                      <button
                        type="button"
                        disabled={bulkBusy}
                        onClick={() =>
                          void runBulkAction(skipAllConflicts)
                        }
                      >
                        {t("upload.queueSkipAll", { count: conflictCount })}
                      </button>
                    </>
                  )}
                  {pendingCount > 0 && (
                    <button
                      className="danger"
                      type="button"
                      disabled={bulkBusy}
                      onClick={() => setCancelConfirmationKey(pendingKey)}
                    >
                      {t("upload.queueCancelPending", {
                        count: pendingCount,
                      })}
                    </button>
                  )}
                </div>
              ) : (
                <div
                  className="upload-queue-cancel-confirmation"
                  role="alert"
                >
                  <p>
                    {t("upload.queueCancelAllConfirmation", {
                      count: pendingCount,
                    })}
                  </p>
                  <div>
                    <button
                      type="button"
                      disabled={bulkBusy}
                      onClick={() => setCancelConfirmationKey("")}
                    >
                      {t("upload.queueKeepUploading")}
                    </button>
                    <button
                      className="danger"
                      type="button"
                      disabled={bulkBusy}
                      onClick={() =>
                        void runBulkAction(cancelAllPending).finally(() =>
                          setCancelConfirmationKey(""),
                        )
                      }
                    >
                      {t("upload.queueConfirmCancelAll")}
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}
          <div className="upload-queue-list" aria-live="polite">
            {sortedItems.length ? (
              sortedItems.map((item) => (
                <UploadQueueRow
                  key={item.id}
                  item={item}
                  onRetry={() => void retry(item.id)}
                  onKeepBoth={() => void keepBoth(item.id)}
                  onRemove={() => void remove(item.id)}
                />
              ))
            ) : (
              <div className="upload-queue-empty">
                <CheckCircle2 />
                <span>{t("upload.queueEmpty")}</span>
              </div>
            )}
          </div>
        </aside>
      )}
    </>
  );
}

function UploadQueueRow({
  item,
  onRetry,
  onKeepBoth,
  onRemove,
}: {
  item: UploadQueueItem;
  onRetry: () => void;
  onKeepBoth: () => void;
  onRemove: () => void;
}) {
  const { t } = usePreferences();
  const percentage =
    item.sizeBytes === 0
      ? item.state === "completed"
        ? 100
        : 0
      : Math.round((item.receivedBytes / item.sizeBytes) * 100);
  const label = statusLabel(item, t);
  const attention =
    item.state === "conflict" ||
    item.state === "failed" ||
    item.state === "paused";

  return (
    <article className={`upload-queue-row ${item.state}`}>
      <div className="upload-queue-row-icon" aria-hidden="true">
        {item.state === "uploading" || item.state === "queued" ? (
          <LoaderCircle className="catalog-spinner" />
        ) : item.state === "completed" ? (
          <CheckCircle2 />
        ) : attention ? (
          <AlertTriangle />
        ) : (
          <XCircle />
        )}
      </div>
      <div className="upload-queue-row-copy">
        <strong title={item.name}>{item.name}</strong>
        <span>
          {label} · {formatBytes(item.receivedBytes)} /{" "}
          {formatBytes(item.sizeBytes)}
        </span>
        {item.error && <small>{item.error}</small>}
        {item.state === "completed" && item.duplicateState === "active" && (
          <small>{t("upload.queueDuplicateActive")}</small>
        )}
        {item.state === "completed" && item.duplicateState === "trashed" && (
          <small>{t("upload.queueDuplicateTrashed")}</small>
        )}
        {(item.state === "queued" || item.state === "uploading") && (
          <div
            className="notification-progress"
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percentage}
            aria-label={t("upload.queueProgress", {
              name: item.name,
              percentage,
            })}
          >
            <span style={{ width: `${percentage}%` }} />
          </div>
        )}
        <div className="upload-queue-actions">
          {item.state === "conflict" && (
            <button type="button" onClick={onKeepBoth}>
              {t("upload.keepBoth")}
            </button>
          )}
          {(item.state === "failed" || item.state === "paused") && (
            <button type="button" onClick={onRetry}>
              <RefreshCw /> {t("upload.queueRetry")}
            </button>
          )}
          {item.state !== "completed" && (
            <button type="button" onClick={onRemove}>
              {item.state === "conflict"
                ? t("upload.queueSkip")
                : t("upload.queueCancel")}
            </button>
          )}
          {item.state === "completed" && (
            <button type="button" onClick={onRemove}>
              {t("upload.queueDismiss")}
            </button>
          )}
        </div>
      </div>
    </article>
  );
}

function statusLabel(
  item: UploadQueueItem,
  t: ReturnType<typeof usePreferences>["t"],
) {
  switch (item.state) {
    case "queued":
      return t("upload.queueQueued");
    case "uploading":
      return t("upload.queueUploading");
    case "paused":
      return t("upload.queuePaused");
    case "conflict":
      return t("upload.queueConflict");
    case "failed":
      return t("upload.queueFailed");
    case "completed":
      return t("upload.queueCompleted");
    case "skipped":
      return t("upload.queueSkipped");
  }
}
