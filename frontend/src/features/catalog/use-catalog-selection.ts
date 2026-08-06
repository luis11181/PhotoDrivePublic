"use client";

import { useCallback, useMemo, useState } from "react";
import type { MediaItem } from "@/lib/types";

type CatalogSelectionOptions = {
  /** All items that may remain selected, including items outside the current page. */
  items: MediaItem[];
  /** Items affected by range selection and Select all. Defaults to `items`. */
  visibleItems?: MediaItem[];
};

export function useCatalogSelection({
  items,
  visibleItems = items,
}: CatalogSelectionOptions) {
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [lastSelectedId, setLastSelectedId] = useState<string | null>(null);

  const selectedItems = useMemo(
    () => items.filter((item) => selectedIds.includes(item.id)),
    [items, selectedIds],
  );
  const allVisibleSelected =
    visibleItems.length > 0 &&
    visibleItems.every((item) => selectedIds.includes(item.id));

  const clearSelection = useCallback(() => {
    setSelectedIds([]);
    setLastSelectedId(null);
  }, []);

  const selectItem = useCallback(
    (item: MediaItem, range: boolean) => {
      const itemIndex = visibleItems.findIndex(
        (candidate) => candidate.id === item.id,
      );
      const anchorIndex = visibleItems.findIndex(
        (candidate) => candidate.id === lastSelectedId,
      );
      setSelectedIds((current) => {
        if (range && anchorIndex >= 0 && itemIndex >= 0) {
          const [start, end] = [anchorIndex, itemIndex].sort(
            (left, right) => left - right,
          );
          return Array.from(
            new Set([
              ...current,
              ...visibleItems
                .slice(start, end + 1)
                .map((candidate) => candidate.id),
            ]),
          );
        }
        return current.includes(item.id)
          ? current.filter((id) => id !== item.id)
          : [...current, item.id];
      });
      if (!range || anchorIndex < 0) setLastSelectedId(item.id);
    },
    [lastSelectedId, visibleItems],
  );

  const selectItemById = useCallback(
    (itemId: string) => {
      const item = visibleItems.find((candidate) => candidate.id === itemId);
      if (item) selectItem(item, true);
    },
    [selectItem, visibleItems],
  );

  const toggleAllVisibleItems = useCallback(() => {
    const visibleIds = visibleItems.map((item) => item.id);
    const visibleIdSet = new Set(visibleIds);
    setSelectedIds((current) =>
      allVisibleSelected
        ? current.filter((id) => !visibleIdSet.has(id))
        : Array.from(new Set([...current, ...visibleIds])),
    );
    setLastSelectedId(
      allVisibleSelected ? null : (visibleIds[visibleIds.length - 1] ?? null),
    );
  }, [allVisibleSelected, visibleItems]);

  return {
    selectedIds,
    setSelectedIds,
    setLastSelectedId,
    selectedItems,
    allVisibleSelected,
    clearSelection,
    selectItem,
    selectItemById,
    toggleAllVisibleItems,
  };
}
