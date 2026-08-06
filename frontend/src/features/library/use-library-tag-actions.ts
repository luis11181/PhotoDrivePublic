"use client";

import { useQueryClient } from "@tanstack/react-query";
import type { Dispatch, SetStateAction } from "react";
import {
  createTag,
  deleteTag,
  setTagItems as setTagItemsApi,
  updateTag,
} from "@/lib/api";
import { usePreferences } from "@/lib/preferences";
import type { CatalogTag, MediaItem } from "@/lib/types";
import type { LibraryAnnounce } from "./use-library-mutations";

export function useLibraryTagActions({
  tagItems,
  setTagItems,
  tags,
  selectedTagId,
  setSelectedTagId,
  setDetails,
  setBusy,
  announce,
}: {
  tagItems: MediaItem[];
  setTagItems: Dispatch<SetStateAction<MediaItem[]>>;
  tags: CatalogTag[];
  selectedTagId: string;
  setSelectedTagId: Dispatch<SetStateAction<string>>;
  setDetails: Dispatch<SetStateAction<MediaItem | null>>;
  setBusy: Dispatch<SetStateAction<boolean>>;
  announce: LibraryAnnounce;
}) {
  const queryClient = useQueryClient();
  const { t } = usePreferences();

  async function refreshTags(includeYears = false) {
    await Promise.allSettled([
      queryClient.invalidateQueries({ queryKey: ["tags"] }),
      queryClient.invalidateQueries({ queryKey: ["library"] }),
      queryClient.invalidateQueries({ queryKey: ["photo-timeline-anchors"] }),
      queryClient.invalidateQueries({ queryKey: ["catalog-search"] }),
      ...(includeYears
        ? [queryClient.invalidateQueries({ queryKey: ["photo-years"] })]
        : []),
    ]);
  }

  async function createAndAssignTag(name: string, color: CatalogTag["color"]) {
    setBusy(true);
    try {
      const tag = await createTag({ name, color });
      const result = await setTagItemsApi(
        tag.id,
        tagItems.map((item) => item.id),
        true,
      );
      const updatedIds = successfulItemIds(result.results);
      setTagItems((items) =>
        items.map((item) => ({
          ...item,
          tags: updatedIds.has(item.id)
            ? [
                ...(item.tags ?? []),
                { id: tag.id, name: tag.name, color: tag.color },
              ]
            : item.tags,
        })),
      );
      await refreshTags();
      announce(
        `Tag “${tag.name}” created · ${result.succeededCount + result.unchangedCount} assigned · ${result.failedCount} failed`,
      );
    } catch (reason) {
      announce(
        reason instanceof Error ? reason.message : "Could not create the tag",
      );
    } finally {
      setBusy(false);
    }
  }

  async function updateSelectedTag(tagId: string, included: boolean) {
    setBusy(true);
    try {
      const result = await setTagItemsApi(
        tagId,
        tagItems.map((item) => item.id),
        included,
      );
      const tag = tags.find((candidate) => candidate.id === tagId);
      const updatedIds = successfulItemIds(result.results);
      if (tag) {
        setTagItems((items) =>
          items.map((item) => ({
            ...item,
            tags: !updatedIds.has(item.id)
              ? item.tags
              : included
                ? item.tags?.some((candidate) => candidate.id === tagId)
                  ? item.tags
                  : [
                      ...(item.tags ?? []),
                      { id: tag.id, name: tag.name, color: tag.color },
                    ]
                : (item.tags ?? []).filter(
                    (candidate) => candidate.id !== tagId,
                  ),
          })),
        );
      }
      await refreshTags();
      announce(
        `${result.succeededCount + result.unchangedCount} items updated · ${result.failedCount} failed`,
      );
    } catch (reason) {
      announce(
        reason instanceof Error ? reason.message : "Could not update tags",
      );
    } finally {
      setBusy(false);
    }
  }

  async function updateManagedTag(
    tag: CatalogTag,
    name: string,
    color: CatalogTag["color"],
  ) {
    setBusy(true);
    try {
      await updateTag(tag.id, { name, color });
      await refreshTags();
      announce("Tag updated");
    } finally {
      setBusy(false);
    }
  }

  async function deleteManagedTag(tag: CatalogTag) {
    setBusy(true);
    try {
      await deleteTag(tag.id);
      if (selectedTagId === tag.id) setSelectedTagId("");
      await refreshTags(true);
      announce("Tag deleted");
    } finally {
      setBusy(false);
    }
  }

  async function removeItemTag(item: MediaItem, tagId: string) {
    setBusy(true);
    try {
      const result = await setTagItemsApi(tagId, [item.id], false);
      const failure = result.results.find(
        (outcome) => outcome.status === "failed",
      );
      if (failure) throw new Error(failure.message ?? t("tags.removeFailed"));
      const updated = {
        ...item,
        tags: (item.tags ?? []).filter((tag) => tag.id !== tagId),
      };
      setDetails((current) => (current?.id === item.id ? updated : current));
      await refreshTags();
      announce(t("tags.removed"));
    } catch (reason) {
      announce(
        reason instanceof Error ? reason.message : t("tags.removeFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  return {
    createAndAssignTag,
    updateSelectedTag,
    updateManagedTag,
    deleteManagedTag,
    removeItemTag,
  };
}

function successfulItemIds(results: Array<{ itemId: string; status: string }>) {
  return new Set(
    results
      .filter((outcome) => outcome.status !== "failed")
      .map((outcome) => outcome.itemId),
  );
}
