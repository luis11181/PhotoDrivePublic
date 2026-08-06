"use client";

import {
  Check,
  FileText,
  Folder,
  Heart,
  Image as ImageIcon,
  LockKeyhole,
  MoreVertical,
  Video,
} from "lucide-react";
import type { KeyboardEvent, MouseEvent } from "react";
import { formatBytes, formatDate } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function FileListRow({
  item,
  selected,
  onSelect,
  onOpen,
  onContextMenu,
  onKeyboardRange,
  onSelectAll,
}: {
  item: MediaItem;
  selected: boolean;
  onSelect: (range?: boolean) => void;
  onOpen: () => void;
  onContextMenu: (event: MouseEvent<HTMLElement>) => void;
  onKeyboardRange: (itemId: string) => void;
  onSelectAll: () => void;
}) {
  const { locale, t } = usePreferences();
  const KindIcon =
    item.kind === "folder"
      ? Folder
      : item.kind === "image"
        ? ImageIcon
        : item.kind === "video"
          ? Video
          : FileText;

  function handleKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "a") {
      event.preventDefault();
      onSelectAll();
      return;
    }
    if (event.key === " ") {
      event.preventDefault();
      onSelect(event.shiftKey);
      return;
    }
    if (
      event.key === "Enter" &&
      event.currentTarget.classList.contains("file-list-name")
    ) {
      event.preventDefault();
      onOpen();
      return;
    }
    if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
    const rows = Array.from(
      document.querySelectorAll<HTMLElement>(
        ".file-list-row[data-media-item-id]",
      ),
    );
    const current = event.currentTarget.closest<HTMLElement>(".file-list-row");
    const index = current ? rows.indexOf(current) : -1;
    if (index < 0) return;
    const target =
      event.key === "Home"
        ? rows[0]
        : event.key === "End"
          ? rows.at(-1)
          : rows[index + (event.key === "ArrowUp" ? -1 : 1)];
    if (!target) return;
    event.preventDefault();
    target.querySelector<HTMLButtonElement>(".file-list-name")?.focus();
    if (event.shiftKey && target.dataset.mediaItemId)
      onKeyboardRange(target.dataset.mediaItemId);
  }

  return (
    <article
      className={`file-list-row ${item.kind === "folder" ? "folder-row" : "file-row"} ${selected ? "selected" : ""}`}
      data-media-item-id={item.id}
      onClick={(event) => {
        if ((event.target as HTMLElement).closest("button, a")) return;
        onSelect(event.shiftKey);
      }}
      onDoubleClick={(event) => {
        if ((event.target as HTMLElement).closest("button, a")) return;
        event.preventDefault();
        onOpen();
      }}
      onContextMenu={onContextMenu}
    >
      <button
        type="button"
        className="file-list-selection"
        onClick={(event) => onSelect(event.shiftKey)}
        onKeyDown={handleKeyDown}
        aria-label={`${selected ? t("media.deselect") : t("media.select")} ${item.name}`}
        aria-pressed={selected}
      >
        {selected && <Check />}
      </button>
      <button
        className="file-list-name"
        type="button"
        onClick={(event) => {
          if (event.shiftKey) onSelect(true);
          else onOpen();
        }}
        onKeyDown={handleKeyDown}
      >
        <span className={`file-list-kind ${item.kind}`}>
          <KindIcon />
        </span>
        <span>
          <strong title={item.name}>{item.name}</strong>
          {item.catalogPath && <small>{item.catalogPath}</small>}
        </span>
        {item.systemRole && (
          <LockKeyhole aria-label={t("media.protectedFolder")} />
        )}
        {item.favorite && (
          <Heart className="file-list-favorite" fill="currentColor" />
        )}
      </button>
      <span className="file-list-type">{typeLabel(item, t)}</span>
      <span className="file-list-modified">
        {formatDate(item.modifiedAt, locale === "es" ? "es-ES" : "en-US")}
      </span>
      <span className="file-list-size">
        {item.sizeBytes === undefined ? "—" : formatBytes(item.sizeBytes)}
      </span>
      <button
        type="button"
        className="icon-button file-list-more"
        aria-label={t("files.moreActions", { name: item.name })}
        onClick={onContextMenu}
      >
        <MoreVertical />
      </button>
    </article>
  );
}

type Translate = ReturnType<typeof usePreferences>["t"];

function typeLabel(item: MediaItem, t: Translate) {
  if (item.kind === "folder") return t("details.folder");
  if (item.kind === "image") return t("details.image");
  if (item.kind === "video") return t("details.video");
  if (item.mimeType === "application/pdf") return "PDF";
  return t("details.document");
}
