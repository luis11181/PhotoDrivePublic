"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type Dispatch, type SetStateAction } from "react";
import {
  batchItems,
  clearTrash,
  getTrashSummary,
  isDemoMode,
  trashItem,
} from "@/lib/api";
import { logError, logInfo } from "@/lib/logger";
import { usePreferences } from "@/lib/preferences";
import type { LibraryView, MediaItem } from "@/lib/types";
import type { useOperationNotifications } from "./use-operation-notifications";
import type {
  LibraryAnnounce,
  MutateSelectedOptions,
} from "./use-library-mutations";

const MAX_MANUAL_TRASH_ITEMS = 300;

export type TrashSource = "selection" | "details" | "viewer";
export type PendingTrash = { items: MediaItem[]; source: TrashSource };

export function useLibraryTrashActions({
  view,
  demoMode,
  mutateItem,
  mutateSelected,
  announce,
  setBusy,
  clearSelection,
  operationNotifications,
  onDetailsTrashed,
  onViewerTrashed,
  onTrashCleared,
}: {
  view: LibraryView;
  demoMode: boolean;
  mutateItem: (
    action: () => Promise<MediaItem>,
    success: string,
    notification?: { title: string; successLabel: string },
  ) => Promise<boolean>;
  mutateSelected: (options: MutateSelectedOptions) => Promise<void>;
  announce: LibraryAnnounce;
  setBusy: Dispatch<SetStateAction<boolean>>;
  clearSelection: () => void;
  operationNotifications: ReturnType<typeof useOperationNotifications>;
  onDetailsTrashed: () => void;
  onViewerTrashed: (item: MediaItem) => void;
  onTrashCleared: () => void;
}) {
  const queryClient = useQueryClient();
  const { t } = usePreferences();
  const [pendingTrash, setPendingTrash] = useState<PendingTrash | null>(null);
  const trashSummaryQuery = useQuery({
    queryKey: ["trash-summary"],
    queryFn: getTrashSummary,
    enabled: view === "trash" && !demoMode && !isDemoMode,
    staleTime: 15_000,
  });

  function requestTrash(items: MediaItem[], source: TrashSource) {
    const eligible = items.filter(
      (item) => item.capabilities.canTrash && !item.trashedAt,
    );
    if (!eligible.length) {
      announce("None of these items can be moved to Trash");
      return;
    }
    if (eligible.length > MAX_MANUAL_TRASH_ITEMS) {
      announce(
        `Select ${MAX_MANUAL_TRASH_ITEMS} or fewer items to move to Trash at once`,
      );
      return;
    }
    setPendingTrash({ items: eligible, source });
  }

  async function confirmTrash() {
    if (!pendingTrash) return;
    const { items, source } = pendingTrash;
    let completed = false;
    if (source === "selection") {
      await mutateSelected({
        title: "Moving selected items to trash",
        completedTitle: "Trash operation finished",
        successLabel: "trashed",
        maxItems: MAX_MANUAL_TRASH_ITEMS,
        eligible: (item) => item.capabilities.canTrash && !item.trashedAt,
        batch: { action: "trash" },
        sourceItems: items,
      });
      completed = true;
    } else {
      const item = items[0];
      completed = await mutateItem(
        () => trashItem(item.id),
        "Item moved to trash",
        {
          title: "Trash operation finished",
          successLabel: "trashed",
        },
      );
      if (source === "details") onDetailsTrashed();
      if (source === "viewer") onViewerTrashed(item);
    }
    setPendingTrash(null);
    if (!completed) return;
    announce(
      items.length === 1
        ? t("files.itemTrashed")
        : t("files.itemsTrashed", { count: items.length }),
      {
        actionLabel: t("files.undo"),
        action: async () => {
          const result = await batchItems({
            action: "restore",
            itemIds: items.map((item) => item.id),
          });
          await Promise.all([
            queryClient.invalidateQueries({ queryKey: ["library"] }),
            queryClient.invalidateQueries({ queryKey: ["catalog-search"] }),
            queryClient.invalidateQueries({ queryKey: ["trash-summary"] }),
          ]);
          if (result.failedCount > 0)
            throw new Error(
              t("files.undoRestoreFailed", { count: result.failedCount }),
            );
          announce(t("files.changeUndone"));
        },
      },
    );
  }

  async function clearTrashedItems(confirmationPhrase: string) {
    if (demoMode || isDemoMode) {
      announce("This action requires the backend");
      return;
    }
    setBusy(true);
    logInfo("frontend.trash", "clear requested");
    try {
      const summary = trashSummaryQuery.data;
      if (!summary) {
        announce("The current Trash count is still loading");
        return;
      }
      const result = await clearTrash({
        confirmationPhrase,
        expectedFileCount: summary.fileCount,
        expectedFolderCount: summary.folderCount,
        expectedOriginalCount: summary.originalCount,
        expectedOriginalBytes: summary.originalBytes,
      });
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["library"] }),
        queryClient.invalidateQueries({ queryKey: ["photo-timeline-anchors"] }),
        queryClient.invalidateQueries({ queryKey: ["catalog-search"] }),
        queryClient.invalidateQueries({ queryKey: ["trash-summary"] }),
      ]);
      clearSelection();
      onTrashCleared();
      const deletedCount = result.deletedFileCount + result.deletedFolderCount;
      operationNotifications.addResult({
        title:
          result.failedOriginalCount > 0
            ? "Trash cleared; storage cleanup pending"
            : "Trash and storage cleared",
        successLabel: "permanently deleted",
        succeeded: deletedCount + result.deletedOriginalCount,
        failed: result.failedOriginalCount,
        detail:
          result.failedOriginalCount > 0
            ? "Use Developer maintenance to review and clean failed storage deletion."
            : undefined,
      });
      announce(
        result.failedOriginalCount > 0
          ? `${deletedCount} Trash ${deletedCount === 1 ? "item" : "items"} removed; ${result.failedOriginalCount} stored ${result.failedOriginalCount === 1 ? "original needs" : "originals need"} review in Developer maintenance`
          : `${deletedCount} Trash ${deletedCount === 1 ? "item" : "items"} and ${result.deletedOriginalCount} stored ${result.deletedOriginalCount === 1 ? "original" : "originals"} permanently deleted`,
      );
      logInfo("frontend.trash", "clear completed", result);
    } catch (reason) {
      const message =
        reason instanceof Error ? reason.message : "Could not clear trash";
      logError("frontend.trash", "clear failed", { reason: message });
      announce(message);
    } finally {
      setBusy(false);
    }
  }

  return {
    pendingTrash,
    setPendingTrash,
    requestTrash,
    confirmTrash,
    trashSummaryQuery,
    clearTrashedItems,
  };
}
