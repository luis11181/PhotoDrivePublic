"use client";

import { useQueryClient } from "@tanstack/react-query";
import type { Dispatch, SetStateAction } from "react";
import { isDemoMode, setCaptureDate } from "@/lib/api";
import type { MediaItem } from "@/lib/types";
import type { useOperationNotifications } from "./use-operation-notifications";
import type { LibraryAnnounce } from "./use-library-mutations";

export function useLibraryCaptureDateActions({
  demoMode,
  items,
  setSelectedIds,
  setLastSelectedId,
  clearSelection,
  setBusy,
  announce,
  operationNotifications,
  openNotifications,
}: {
  demoMode: boolean;
  items: MediaItem[];
  setSelectedIds: Dispatch<SetStateAction<string[]>>;
  setLastSelectedId: Dispatch<SetStateAction<string | null>>;
  clearSelection: () => void;
  setBusy: Dispatch<SetStateAction<boolean>>;
  announce: LibraryAnnounce;
  operationNotifications: ReturnType<typeof useOperationNotifications>;
  openNotifications: () => void;
}) {
  const queryClient = useQueryClient();

  async function saveCaptureDate(capturedOn?: string, reset = false) {
    if (demoMode || isDemoMode)
      throw new Error("This action requires the backend");
    setBusy(true);
    try {
      const result = await setCaptureDate(
        items.map((item) => item.id),
        reset ? { reset: true } : { capturedOn },
      );
      const failed = result.results.filter((item) => item.status === "failed");
      await Promise.allSettled([
        queryClient.invalidateQueries({ queryKey: ["library"] }),
        queryClient.invalidateQueries({ queryKey: ["photo-timeline-anchors"] }),
        queryClient.invalidateQueries({ queryKey: ["catalog-search"] }),
        queryClient.invalidateQueries({ queryKey: ["photo-years"] }),
      ]);
      operationNotifications.addResult({
        title: reset ? "Capture date restored" : "Capture date corrected",
        successLabel: reset ? "restored" : "corrected",
        succeeded: result.succeededCount + result.unchangedCount,
        failed: result.failedCount,
        detail: failed[0]?.message,
      });
      if (failed.length) {
        setSelectedIds(failed.map((item) => item.itemId));
        setLastSelectedId(failed.at(-1)?.itemId ?? null);
        openNotifications();
      } else {
        clearSelection();
      }
      announce(
        `${result.succeededCount + result.unchangedCount} ${reset ? "restored" : "corrected"} · ${result.failedCount} failed`,
      );
    } finally {
      setBusy(false);
    }
  }

  return { saveCaptureDate };
}
