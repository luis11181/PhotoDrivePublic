"use client";

import {
  useCallback,
  useEffect,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";
import type { MediaItem } from "@/lib/types";

type CatalogKeyboardShortcutOptions = {
  disabled: boolean;
  selectedItems: MediaItem[];
  onSelectAll: () => void;
  onClearSelection: () => void;
  onOpen: (item: MediaItem) => void;
  onRename: (item: MediaItem) => void;
  onTrash: (item: MediaItem) => void;
  onDetails: (item: MediaItem) => void;
  onRestore?: (item: MediaItem) => void;
  onEscape?: () => void;
};

export function useCatalogKeyboardShortcuts({
  disabled,
  selectedItems,
  onSelectAll,
  onClearSelection,
  onOpen,
  onRename,
  onTrash,
  onDetails,
  onRestore,
  onEscape,
}: CatalogKeyboardShortcutOptions) {
  const [shortcutsOpen, setShortcutsOpen] = useState(false);

  useEffect(() => {
    function openShortcutHelp(event: globalThis.KeyboardEvent) {
      if ((event.ctrlKey || event.metaKey) && event.key === "/") {
        event.preventDefault();
        setShortcutsOpen(true);
      }
    }
    window.addEventListener("keydown", openShortcutHelp);
    return () => window.removeEventListener("keydown", openShortcutHelp);
  }, []);

  const handleCatalogKeyDown = useCallback(
    (event: ReactKeyboardEvent<HTMLElement>) => {
      const target = event.target as HTMLElement;
      if (event.defaultPrevented) return;
      if (target.closest("input, textarea, select, [contenteditable='true']"))
        return;
      if (event.key === "Enter" && target.closest("button, a")) return;
      if (disabled || shortcutsOpen) return;
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "a") {
        event.preventDefault();
        onSelectAll();
        return;
      }
      if (event.key === "Escape") {
        onEscape?.();
        onClearSelection();
        return;
      }
      if (selectedItems.length !== 1) return;
      const item = selectedItems[0];
      if (event.key === "Enter") {
        event.preventDefault();
        onOpen(item);
      } else if (
        event.key === "F2" &&
        item.capabilities.canRename &&
        !item.trashedAt
      ) {
        event.preventDefault();
        onRename(item);
      } else if (
        event.key === "Delete" &&
        item.capabilities.canTrash &&
        !item.trashedAt
      ) {
        event.preventDefault();
        onTrash(item);
      } else if (event.key.toLowerCase() === "i") {
        event.preventDefault();
        onDetails(item);
      } else if (event.key.toLowerCase() === "r" && item.trashedAt) {
        event.preventDefault();
        onRestore?.(item);
      }
    },
    [
      disabled,
      onClearSelection,
      onDetails,
      onEscape,
      onOpen,
      onRename,
      onRestore,
      onSelectAll,
      onTrash,
      selectedItems,
      shortcutsOpen,
    ],
  );

  return { shortcutsOpen, setShortcutsOpen, handleCatalogKeyDown };
}
