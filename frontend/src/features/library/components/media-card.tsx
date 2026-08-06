"use client";

/* eslint-disable @next/next/no-img-element -- thumbnails are authenticated backend responses. */

import {
  Check,
  Cloud,
  FileText,
  Folder,
  HardDrive,
  Heart,
  Image as ImageIcon,
  LockKeyhole,
  MoreVertical,
  Video,
} from "lucide-react";
import { type KeyboardEvent, type MouseEvent, useRef, useState } from "react";
import type { MediaItem } from "@/lib/types";
import { formatBytes, formatDate } from "@/lib/formatting";
import { logError, logInfo } from "@/lib/logger";
import { usePreferences } from "@/lib/preferences";

export function MediaCard({
  item,
  selected,
  selectionMode,
  onSelect,
  onKeyboardRange,
  onSelectAll,
  onNavigate,
  onOpen,
  photo,
  priority,
  onContextMenu,
}: {
  item: MediaItem;
  selected: boolean;
  selectionMode: boolean;
  onSelect: (range?: boolean) => void;
  onKeyboardRange: (itemId: string) => void;
  onSelectAll: () => void;
  onNavigate?: (key: string, range: boolean) => void;
  onOpen: () => void;
  photo?: boolean;
  priority?: boolean;
  onContextMenu?: (event: MouseEvent<HTMLElement>) => void;
}) {
  const card = useRef<HTMLElement>(null);
  const [thumbnailLoaded, setThumbnailLoaded] = useState(false);
  const { locale, t } = usePreferences();
  const KindIcon =
    item.kind === "folder"
      ? Folder
      : item.kind === "image"
        ? ImageIcon
        : item.kind === "video"
          ? Video
          : FileText;

  function activate(event: MouseEvent<HTMLElement>) {
    if (selectionMode || event.shiftKey || event.ctrlKey || event.metaKey) {
      onSelect(event.shiftKey);
      return;
    }
    onOpen();
  }

  function handleCardClick(event: MouseEvent<HTMLElement>) {
    if ((event.target as HTMLElement).closest("button, a, input, select"))
      return;
    activate(event);
  }

  function handleVisualClick(event: MouseEvent<HTMLButtonElement>) {
    // Keyboard and assistive-technology button activation should keep the
    // explicit "open" behavior even while pointer clicks extend selection.
    if (event.detail === 0) {
      onOpen();
      return;
    }
    activate(event);
  }

  function handleGridKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "a") {
      event.preventDefault();
      onSelectAll();
      return;
    }
    if (
      event.key === " " &&
      event.currentTarget.classList.contains("media-visual")
    ) {
      event.preventDefault();
      onSelect(event.shiftKey);
      return;
    }
    if (
      event.key !== "ArrowLeft" &&
      event.key !== "ArrowRight" &&
      event.key !== "ArrowUp" &&
      event.key !== "ArrowDown" &&
      event.key !== "Home" &&
      event.key !== "End"
    )
      return;

    if (onNavigate) {
      event.preventDefault();
      onNavigate(event.key, event.shiftKey);
      return;
    }

    const target = adjacentCard(card.current, event.key);
    if (!target) return;
    event.preventDefault();
    target.querySelector<HTMLButtonElement>(".media-visual")?.focus();
    if (event.shiftKey) {
      const targetId = target.dataset.mediaItemId;
      if (targetId) onKeyboardRange(targetId);
    }
  }

  return (
    <article
      ref={card}
      data-media-item-id={item.id}
      className={`media-card ${item.kind === "folder" ? "folder-card" : "file-card"} ${photo ? "photo-card" : ""} ${selectionMode ? "selection-mode" : ""} ${selected ? "selected" : ""}`}
      onClick={handleCardClick}
      onContextMenu={onContextMenu}
    >
      <button
        type="button"
        className="selection"
        onClick={(event) => onSelect(event.shiftKey)}
        onKeyDown={(event) => {
          if (event.key === " " && event.shiftKey) {
            event.preventDefault();
            onSelect(true);
            return;
          }
          handleGridKeyDown(event);
        }}
        aria-label={`${selected ? t("media.deselect") : t("media.select")} ${item.name}`}
        aria-pressed={selected}
      >
        {selected && <Check />}
      </button>
      <button
        type="button"
        className="media-visual"
        onClick={handleVisualClick}
        onKeyDown={handleGridKeyDown}
        aria-label={
          selectionMode
            ? t("media.selectionInstructions", {
                state: selected ? t("media.selected") : t("media.notSelected"),
                name: item.name,
              })
            : t("media.openInstructions", { name: item.name })
        }
      >
        {item.thumbnailUrl ? (
          <img
            src={item.thumbnailUrl}
            crossOrigin="use-credentials"
            alt=""
            loading={priority ? "eager" : "lazy"}
            decoding="async"
            className={thumbnailLoaded ? "loaded" : ""}
            onLoad={() => {
              setThumbnailLoaded(true);
              logInfo("frontend.thumbnail", "loaded", { itemId: item.id });
            }}
            onError={() =>
              logError("frontend.thumbnail", "failed", { itemId: item.id })
            }
          />
        ) : (
          <span className={`file-art ${item.kind}`}>
            <KindIcon />
          </span>
        )}
        {item.kind === "video" && (
          <span className="video-badge">
            <Video /> {t("common.video")}
          </span>
        )}
      </button>
      <div className="media-meta">
        <span className="type-icon">
          <KindIcon />
        </span>
        <span>
          <strong title={item.name}>{item.name}</strong>
          <small title={item.catalogPath}>
            {item.catalogPath ??
              `${item.sizeBytes !== undefined ? `${formatBytes(item.sizeBytes)} · ` : ""}${formatDate(item.capturedAt ?? item.modifiedAt, locale === "es" ? "es-ES" : "en-US")}`}
          </small>
        </span>
        {item.systemRole && (
          <LockKeyhole
            className="system-lock"
            aria-label={t("media.protectedFolder")}
          />
        )}
        {onContextMenu && (
          <button
            type="button"
            className="icon-button media-more"
            aria-label={t("files.moreActions", { name: item.name })}
            onClick={onContextMenu}
          >
            <MoreVertical />
          </button>
        )}
      </div>
      {item.favorite && <Heart className="favorite" fill="currentColor" />}
      {item.kind !== "folder" && (
        <span
          className={`local-state ${item.localState}`}
          title={t("media.availability", { state: item.localState })}
        >
          {item.localState === "local" || item.localState === "cached" ? (
            <HardDrive />
          ) : (
            <Cloud />
          )}
        </span>
      )}
    </article>
  );
}

function adjacentCard(
  current: HTMLElement | null,
  key: string,
): HTMLElement | undefined {
  if (!current) return undefined;
  const cards = Array.from(
    document.querySelectorAll<HTMLElement>(".media-card[data-media-item-id]"),
  );
  const currentIndex = cards.indexOf(current);
  if (currentIndex < 0) return undefined;
  if (key === "Home") return cards[0];
  if (key === "End") return cards[cards.length - 1];
  if (key === "ArrowLeft") return cards[currentIndex - 1];
  if (key === "ArrowRight") return cards[currentIndex + 1];

  const currentRect = current.getBoundingClientRect();
  const currentX = currentRect.left + currentRect.width / 2;
  const currentY = currentRect.top + currentRect.height / 2;
  const candidates = cards.filter((candidate) => {
    if (candidate === current) return false;
    const rect = candidate.getBoundingClientRect();
    const candidateY = rect.top + rect.height / 2;
    return key === "ArrowUp" ? candidateY < currentY : candidateY > currentY;
  });

  return candidates.sort((left, right) => {
    const leftRect = left.getBoundingClientRect();
    const rightRect = right.getBoundingClientRect();
    const leftPrimary = Math.abs(leftRect.top + leftRect.height / 2 - currentY);
    const rightPrimary = Math.abs(
      rightRect.top + rightRect.height / 2 - currentY,
    );
    const leftSecondary = Math.abs(
      leftRect.left + leftRect.width / 2 - currentX,
    );
    const rightSecondary = Math.abs(
      rightRect.left + rightRect.width / 2 - currentX,
    );
    return leftPrimary - rightPrimary || leftSecondary - rightSecondary;
  })[0];
}
