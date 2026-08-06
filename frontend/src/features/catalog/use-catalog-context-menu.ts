"use client";

import { useCallback, useState, type MouseEvent } from "react";
import type { MediaItem } from "@/lib/types";

export type CatalogContextMenuState = {
  item: MediaItem;
  x: number;
  y: number;
};

export function useCatalogContextMenu({
  width = 238,
  height = 390,
}: { width?: number; height?: number } = {}) {
  const [contextMenu, setContextMenu] =
    useState<CatalogContextMenuState | null>(null);

  const openContextMenu = useCallback(
    (event: MouseEvent<HTMLElement>, item: MediaItem) => {
      event.preventDefault();
      event.stopPropagation();
      setContextMenu({
        item,
        x: Math.max(8, Math.min(event.clientX, window.innerWidth - width)),
        y: Math.max(8, Math.min(event.clientY, window.innerHeight - height)),
      });
    },
    [height, width],
  );

  const closeContextMenu = useCallback(() => setContextMenu(null), []);

  return {
    contextMenu,
    setContextMenu,
    openContextMenu,
    closeContextMenu,
  };
}
