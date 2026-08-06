"use client";

import {
  Album,
  CalendarDays,
  CheckCheck,
  Download,
  Eye,
  Heart,
  MoreHorizontal,
  MoveRight,
  RefreshCw,
  RotateCcw,
  Share2,
  Tags as TagsIcon,
  Trash2,
  X,
} from "lucide-react";
import { type FormEvent, type ReactNode, useEffect, useState } from "react";
import { usePreferences } from "@/lib/preferences";
import type { LibraryView, MediaItem } from "@/lib/types";

const MAX_DOWNLOAD_ITEMS = 100;

export function SelectionActions({
  selectedItems,
  folderOptions,
  view,
  busy,
  onOpen,
  onDownload,
  onFavorite,
  onMove,
  onTrash,
  onRestore,
  onRetryMetadata,
  onCorrectCaptureDate,
  onShare,
  onAddToAlbum,
  onTags,
  onRemoveFromAlbum,
  allVisibleSelected,
  onSelectAll,
  onClear,
}: {
  selectedItems: MediaItem[];
  folderOptions: MediaItem[];
  view: LibraryView;
  busy: boolean;
  onOpen: (item: MediaItem) => void;
  onDownload: (items: MediaItem[]) => void;
  onFavorite: (favorite: boolean) => void;
  onMove: (parentId?: string) => void;
  onTrash: () => void;
  onRestore: () => void;
  onRetryMetadata: () => void;
  onCorrectCaptureDate?: (items: MediaItem[]) => void;
  onShare: (items: MediaItem[]) => void;
  onAddToAlbum: (items: MediaItem[]) => void;
  onTags?: (items: MediaItem[]) => void;
  onRemoveFromAlbum?: () => void;
  allVisibleSelected: boolean;
  onSelectAll: () => void;
  onClear: () => void;
}) {
  const { t } = usePreferences();
  const [menuOpen, setMenuOpen] = useState(false);
  const [moveOpen, setMoveOpen] = useState(false);
  const [destination, setDestination] = useState("");
  const selectedIds = new Set(selectedItems.map((item) => item.id));
  const downloadableItems = selectedItems.filter((item) => !item.trashedAt);
  const movableItems = selectedItems.filter(
    (item) => item.capabilities.canMove && !item.trashedAt,
  );
  const trashableItems = selectedItems.filter(
    (item) => item.capabilities.canTrash && !item.trashedAt,
  );
  const restorableItems = selectedItems.filter((item) => item.trashedAt);
  const shareableItems = selectedItems.filter(
    (item) => item.capabilities.canShare && !item.trashedAt,
  );
  const mediaItems = selectedItems.filter(
    (item) =>
      (item.kind === "image" || item.kind === "video") && !item.trashedAt,
  );
  const albumItems = mediaItems;
  const favoriteItems = selectedItems.filter(
    (item) => item.capabilities.canFavorite && !item.trashedAt,
  );
  const taggableItems = selectedItems.filter(
    (item) =>
      item.accessRole !== "viewer" && !item.trashedAt && !item.systemRole,
  );
  const retryableItems = selectedItems.filter(
    (item) =>
      item.capabilities.canRetryMetadata &&
      item.metadata?.state === "failed" &&
      !item.trashedAt,
  );
  const captureDateItems = selectedItems.filter(
    (item) =>
      item.accessRole !== "viewer" &&
      !item.trashedAt &&
      (item.kind === "image" || item.kind === "video"),
  );
  const shouldFavorite = favoriteItems.some((item) => !item.favorite);
  const downloadOverLimit = downloadableItems.length > MAX_DOWNLOAD_ITEMS;
  const downloadDisabled = busy || downloadableItems.length === 0;

  useEffect(() => {
    if (!menuOpen) return;
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.preventDefault();
      setMenuOpen(false);
      setMoveOpen(false);
    }
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [menuOpen]);

  function run(action: () => void) {
    setMenuOpen(false);
    setMoveOpen(false);
    action();
  }

  function submitMove(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    run(() => onMove(destination || undefined));
  }

  const selectedCount = selectedItems.length;
  const availability = (count: number, singular: string, plural: string) =>
    count === selectedCount
      ? `${count} ${count === 1 ? singular : plural}`
      : t("selection.supported", { count, total: selectedCount });

  return (
    <>
      <div
        className="selection-bar"
        role="region"
        aria-label={t("selection.region")}
      >
        <div className="selection-summary">
          <strong>
            <span>{selectedCount}</span>
            <span className="selection-summary-label">
              {t("selection.selected")}
            </span>
          </strong>
          <small>
            {downloadOverLimit
              ? t("selection.chooseLimit", { count: MAX_DOWNLOAD_ITEMS })
              : t("selection.supportedHelp")}
          </small>
        </div>
        <div className="selection-primary-actions">
          {view === "trash" ? (
            <button
              type="button"
              className="selection-primary-action"
              disabled={busy || restorableItems.length === 0}
              aria-label={t("selection.restoreSelected")}
              onClick={() => run(onRestore)}
            >
              <RotateCcw />
              <span>{t("selection.restore")}</span>
            </button>
          ) : (
            <>
              <button
                type="button"
                className="selection-primary-action"
                disabled={downloadDisabled}
                aria-label={t("selection.downloadSelected")}
                onClick={() => run(() => onDownload(downloadableItems))}
              >
                <Download />
                <span>{t("common.download")}</span>
              </button>
              {favoriteItems.length > 0 && (
                <button
                  type="button"
                  className="selection-primary-action"
                  disabled={busy}
                  aria-label={
                    shouldFavorite
                      ? t("selection.favoriteSelected")
                      : t("selection.unfavoriteSelected")
                  }
                  onClick={() => run(() => onFavorite(shouldFavorite))}
                >
                  <Heart fill={shouldFavorite ? "none" : "currentColor"} />
                  <span>
                    {shouldFavorite
                      ? t("selection.favorite")
                      : t("selection.unfavorite")}
                  </span>
                </button>
              )}
            </>
          )}
          <button
            type="button"
            className="selection-primary-action"
            disabled={busy}
            aria-haspopup="dialog"
            aria-expanded={menuOpen}
            aria-label={t("selection.moreActions")}
            onClick={() => setMenuOpen(true)}
          >
            <MoreHorizontal />
            <span>{t("selection.more")}</span>
          </button>
          <button
            type="button"
            className="selection-clear"
            disabled={busy}
            onClick={onClear}
            aria-label={t("selection.clear")}
          >
            <X />
          </button>
        </div>
      </div>

      {menuOpen && (
        <div
          className="selection-menu-backdrop"
          onMouseDown={() => setMenuOpen(false)}
        >
          <section
            className="selection-menu"
            role="dialog"
            aria-modal="true"
            aria-labelledby="selection-menu-title"
            data-selection-menu="open"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <div className="selection-menu-handle" aria-hidden="true" />
            <header className="selection-menu-header">
              <div>
                <p className="eyebrow">{t("selection.bulk")}</p>
                <h2 id="selection-menu-title">
                  {t("selection.count", { count: selectedCount })}
                </h2>
              </div>
              <button
                type="button"
                className="icon-button"
                onClick={() => setMenuOpen(false)}
                aria-label={t("selection.close")}
                autoFocus
              >
                <X />
              </button>
            </header>

            {view !== "trash" ? (
              <div className="selection-menu-sections">
                <SelectionSection title={t("selection.getShare")}>
                  {selectedCount === 1 && (
                    <ActionButton
                      icon={<Eye />}
                      title={t("selection.open")}
                      detail={
                        view === "photos" || view === "albums"
                          ? t("selection.viewMedia")
                          : t("selection.openItem")
                      }
                      disabled={busy}
                      onClick={() => run(() => onOpen(selectedItems[0]))}
                    />
                  )}
                  <ActionButton
                    icon={<Download />}
                    title={
                      downloadOverLimit
                        ? t("selection.tooMany")
                        : t("common.download")
                    }
                    detail={
                      downloadOverLimit
                        ? t("selection.chooseLimit", {
                            count: MAX_DOWNLOAD_ITEMS,
                          })
                        : downloadableItems.length > 1
                          ? `${availability(downloadableItems.length, t("selection.nounItem"), t("albums.items"))} · ZIP`
                          : availability(
                              downloadableItems.length,
                              t("selection.nounItem"),
                              t("albums.items"),
                            )
                    }
                    disabled={downloadDisabled}
                    onClick={() => run(() => onDownload(downloadableItems))}
                  />
                  <ActionButton
                    icon={<Share2 />}
                    title={t("albums.share")}
                    detail={availability(
                      shareableItems.length,
                      t("selection.nounItem"),
                      t("albums.items"),
                    )}
                    disabled={busy || shareableItems.length === 0}
                    onClick={() => run(() => onShare(shareableItems))}
                  />
                </SelectionSection>

                <SelectionSection title={t("selection.organize")}>
                  <ActionButton
                    icon={
                      <Heart fill={shouldFavorite ? "none" : "currentColor"} />
                    }
                    title={
                      shouldFavorite
                        ? t("selection.favorite")
                        : t("selection.unfavorite")
                    }
                    detail={availability(
                      favoriteItems.length,
                      t("selection.nounItem"),
                      t("albums.items"),
                    )}
                    disabled={busy || favoriteItems.length === 0}
                    onClick={() => run(() => onFavorite(shouldFavorite))}
                  />
                  {onTags && (
                    <ActionButton
                      icon={<TagsIcon />}
                      title={t("selection.tags")}
                      detail={availability(
                        taggableItems.length,
                        t("selection.nounItem"),
                        t("albums.items"),
                      )}
                      disabled={busy || taggableItems.length === 0}
                      onClick={() => run(() => onTags(taggableItems))}
                    />
                  )}
                  {onCorrectCaptureDate && captureDateItems.length > 0 && (
                    <ActionButton
                      icon={<CalendarDays />}
                      title={t("selection.correctCaptureDate")}
                      detail={availability(
                        captureDateItems.length,
                        t("selection.nounMedia"),
                        t("selection.nounMediaPlural"),
                      )}
                      disabled={busy}
                      onClick={() =>
                        run(() => onCorrectCaptureDate(captureDateItems))
                      }
                    />
                  )}
                  <ActionButton
                    icon={<Album />}
                    title={t("selection.addAlbum")}
                    detail={availability(
                      albumItems.length,
                      t("selection.nounMedia"),
                      t("selection.nounMediaPlural"),
                    )}
                    disabled={busy || albumItems.length === 0}
                    onClick={() => run(() => onAddToAlbum(albumItems))}
                  />
                  {onRemoveFromAlbum && (
                    <ActionButton
                      icon={<Album />}
                      title={t("selection.removeAlbum")}
                      detail={t("selection.filesKept", {
                        count: selectedCount,
                        items: t(
                          selectedCount === 1 ? "albums.item" : "albums.items",
                        ),
                      })}
                      disabled={busy}
                      onClick={() => run(onRemoveFromAlbum)}
                    />
                  )}
                  {view === "files" && (
                    <>
                      <ActionButton
                        icon={<MoveRight />}
                        title={t("selection.moveFiles")}
                        detail={availability(
                          movableItems.length,
                          t("selection.nounItem"),
                          t("albums.items"),
                        )}
                        disabled={busy || movableItems.length === 0}
                        expanded={moveOpen}
                        onClick={() => setMoveOpen((current) => !current)}
                      />
                      {moveOpen && (
                        <form
                          className="selection-move-panel"
                          onSubmit={submitMove}
                        >
                          <label htmlFor="selection-destination">
                            {t("selection.chooseFolder")}
                          </label>
                          <div>
                            <select
                              id="selection-destination"
                              value={destination}
                              disabled={busy}
                              onChange={(event) =>
                                setDestination(event.target.value)
                              }
                            >
                              <option value="">
                                {t("selection.filesHome")}
                              </option>
                              {folderOptions
                                .filter((folder) => !selectedIds.has(folder.id))
                                .map((folder) => (
                                  <option key={folder.id} value={folder.id}>
                                    {folder.name}
                                  </option>
                                ))}
                            </select>
                            <button
                              type="submit"
                              className="primary"
                              disabled={busy}
                            >
                              {t("selection.moveCount", {
                                count: movableItems.length,
                              })}
                            </button>
                          </div>
                        </form>
                      )}
                    </>
                  )}
                </SelectionSection>

                {(retryableItems.length > 0 || trashableItems.length > 0) && (
                  <SelectionSection title={t("selection.manage")}>
                    {retryableItems.length > 0 && (
                      <ActionButton
                        icon={<RefreshCw />}
                        title={t("selection.retry")}
                        detail={availability(
                          retryableItems.length,
                          t("selection.nounFailed"),
                          t("selection.nounFailedPlural"),
                        )}
                        disabled={busy}
                        onClick={() => run(onRetryMetadata)}
                      />
                    )}
                    <ActionButton
                      icon={<Trash2 />}
                      title={t("selection.trash")}
                      detail={availability(
                        trashableItems.length,
                        t("selection.nounItem"),
                        t("albums.items"),
                      )}
                      disabled={busy || trashableItems.length === 0}
                      destructive
                      onClick={() => run(onTrash)}
                    />
                  </SelectionSection>
                )}
              </div>
            ) : (
              <SelectionSection title={t("selection.trashSection")}>
                <ActionButton
                  icon={<RotateCcw />}
                  title={t("selection.restore")}
                  detail={availability(
                    restorableItems.length,
                    t("selection.nounItem"),
                    t("albums.items"),
                  )}
                  disabled={busy || restorableItems.length === 0}
                  onClick={() => run(onRestore)}
                />
              </SelectionSection>
            )}

            <div className="selection-menu-footer">
              <button
                type="button"
                disabled={busy}
                onClick={() => run(onSelectAll)}
              >
                <CheckCheck />
                {allVisibleSelected
                  ? t("selection.deselectAll")
                  : t("selection.selectAll")}
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() => run(onClear)}
              >
                <X />
                {t("selection.clear")}
              </button>
            </div>
          </section>
        </div>
      )}
    </>
  );
}

function SelectionSection({
  title,
  children,
}: {
  title: string;
  children: ReactNode;
}) {
  return (
    <section className="selection-menu-section">
      <h3>{title}</h3>
      <div className="selection-action-grid">{children}</div>
    </section>
  );
}

function ActionButton({
  icon,
  title,
  detail,
  disabled,
  destructive = false,
  expanded,
  onClick,
}: {
  icon: ReactNode;
  title: string;
  detail: string;
  disabled: boolean;
  destructive?: boolean;
  expanded?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      className={`selection-action ${destructive ? "destructive" : ""}`}
      disabled={disabled}
      aria-expanded={expanded}
      onClick={onClick}
    >
      <span className="selection-action-icon">{icon}</span>
      <span>
        <strong>{title}</strong>
        <small>{detail}</small>
      </span>
    </button>
  );
}
