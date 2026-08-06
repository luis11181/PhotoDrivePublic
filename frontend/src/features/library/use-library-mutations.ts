"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useState, type Dispatch, type SetStateAction } from "react";
import { batchItems, isDemoMode } from "@/lib/api";
import { logError, logInfo } from "@/lib/logger";
import type { MediaItem } from "@/lib/types";
import type { useOperationNotifications } from "./use-operation-notifications";

export type LibraryAnnounce = (
  message: string,
  action?: {
    actionLabel?: string;
    action?: () => Promise<void> | void;
  },
) => void;

type MutationNotification = { title: string; successLabel: string };

export type MutateSelectedOptions = {
  title: string;
  completedTitle: string;
  successLabel: string;
  maxItems?: number;
  eligible: (item: MediaItem) => boolean;
  action?: (item: MediaItem) => Promise<MediaItem>;
  batch?: {
    action: "favorite" | "move" | "trash" | "restore";
    favorite?: boolean;
    parentId?: string;
  };
  sourceItems?: MediaItem[];
};

type OperationNotifications = ReturnType<typeof useOperationNotifications>;

export function useLibraryMutations({
  demoMode,
  selectedItems,
  setDetails,
  setSelectedIds,
  setLastSelectedId,
  announce,
  operationNotifications,
  openNotifications,
}: {
  demoMode: boolean;
  selectedItems: MediaItem[];
  setDetails: Dispatch<SetStateAction<MediaItem | null>>;
  setSelectedIds: Dispatch<SetStateAction<string[]>>;
  setLastSelectedId: Dispatch<SetStateAction<string | null>>;
  announce: LibraryAnnounce;
  operationNotifications: OperationNotifications;
  openNotifications: () => void;
}) {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);

  async function refreshCatalog() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["library"] }),
      queryClient.invalidateQueries({ queryKey: ["folder-breadcrumbs"] }),
      queryClient.invalidateQueries({ queryKey: ["photo-timeline-anchors"] }),
      queryClient.invalidateQueries({ queryKey: ["photo-years"] }),
      queryClient.invalidateQueries({ queryKey: ["quick-access-folders"] }),
    ]);
  }

  async function mutateItem(
    action: () => Promise<MediaItem>,
    success: string,
    notification?: MutationNotification,
  ) {
    if (demoMode || isDemoMode) {
      announce("This action requires the backend");
      return false;
    }
    setBusy(true);
    logInfo("frontend.action", "mutation started", { action: success });
    try {
      const updated = await action();
      setDetails((current) => (current?.id === updated.id ? updated : current));
      await refreshCatalog();
      logInfo("frontend.action", "mutation completed", {
        action: success,
        itemId: updated.id,
        kind: updated.kind,
      });
      if (notification) {
        operationNotifications.addResult({
          title: notification.title,
          successLabel: notification.successLabel,
          succeeded: 1,
        });
      }
      announce(success);
      return true;
    } catch (reason) {
      logError("frontend.action", "mutation failed", {
        action: success,
        reason: reason instanceof Error ? reason.message : "unknown_error",
      });
      if (notification) {
        operationNotifications.addResult({
          title: notification.title,
          successLabel: notification.successLabel,
          succeeded: 0,
          failed: 1,
          detail:
            reason instanceof Error ? reason.message : "The operation failed.",
        });
      }
      announce(
        reason instanceof Error ? reason.message : "Could not update this item",
      );
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function mutateSelected(options: MutateSelectedOptions) {
    if (demoMode || isDemoMode) {
      announce("This action requires the backend");
      return;
    }
    const sourceItems = options.sourceItems ?? selectedItems;
    const items = sourceItems.filter(options.eligible);
    let skipped = sourceItems.length - items.length;
    if (!items.length) {
      announce("None of the selected items support this action");
      return;
    }
    if (options.maxItems !== undefined && items.length > options.maxItems) {
      announce(
        `Select ${options.maxItems} or fewer items to move to Trash at once`,
      );
      logInfo("frontend.batch", "operation rejected by manual item limit", {
        operation: options.successLabel,
        eligible: items.length,
        limit: options.maxItems,
      });
      return;
    }

    const operationId = operationNotifications.startOperation({
      title: options.title,
      total: items.length,
      successLabel: options.successLabel,
    });
    if (items.length > 1) openNotifications();
    setBusy(true);
    logInfo("frontend.batch", "operation started", {
      operation: options.successLabel,
      selected: sourceItems.length,
      eligible: items.length,
      skipped,
    });
    let succeeded = 0;
    let failed = 0;
    const failedIds: string[] = [];
    const failureMessages: string[] = [];
    try {
      if (options.batch) {
        const result = await batchItems({
          ...options.batch,
          itemIds: items.map((item) => item.id),
        });
        succeeded = result.succeededCount + result.unchangedCount;
        failed = result.failedCount;
        skipped += result.skippedCount;
        for (const outcome of result.results) {
          if (outcome.status === "failed") {
            failedIds.push(outcome.itemId);
            if (outcome.message) failureMessages.push(outcome.message);
          }
        }
        operationNotifications.updateOperation(operationId, {
          completed: items.length,
          succeeded,
          failed,
        });
      } else {
        for (const [index, item] of items.entries()) {
          try {
            await options.action!(item);
            succeeded++;
          } catch (reason) {
            failed++;
            failedIds.push(item.id);
            failureMessages.push(
              reason instanceof Error ? reason.message : "The operation failed",
            );
          }
          operationNotifications.updateOperation(operationId, {
            completed: index + 1,
            succeeded,
            failed,
          });
        }
      }
      if (succeeded > 0) {
        try {
          await refreshCatalog();
        } catch (reason) {
          logError("frontend.query", "post-batch refresh failed", {
            operation: options.successLabel,
            reason: reason instanceof Error ? reason.message : "unknown_error",
          });
        }
      }

      const details: string[] = [];
      if (failureMessages.length) {
        details.push(
          `${failureMessages[0]}${failureMessages.length > 1 ? ` · ${failureMessages.length - 1} more failures` : ""}`,
        );
      }
      if (skipped > 0)
        details.push(`${skipped} unsupported or protected skipped`);
      operationNotifications.updateOperation(operationId, {
        title: options.completedTitle,
        status:
          failed > 0 || skipped > 0
            ? succeeded > 0
              ? "warning"
              : "error"
            : "success",
        completed: items.length,
        succeeded,
        failed,
        detail: details.join(" · ") || undefined,
      });
      setSelectedIds(failedIds);
      setLastSelectedId(failedIds[failedIds.length - 1] ?? null);
      if (failed > 0 || skipped > 0) openNotifications();
      announce(
        `${succeeded} ${options.successLabel} · ${failed} failed${skipped ? ` · ${skipped} skipped` : ""}`,
      );
      logInfo("frontend.batch", "operation completed", {
        operation: options.successLabel,
        succeeded,
        failed,
        skipped,
      });
    } finally {
      setBusy(false);
    }
  }

  return { busy, setBusy, mutateItem, mutateSelected };
}
