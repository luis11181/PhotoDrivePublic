"use client";

/* eslint-disable @next/next/no-img-element -- thumbnails are authenticated backend responses. */

import {
  Cloud,
  Copy,
  CalendarDays,
  Download,
  ExternalLink,
  FileText,
  FolderInput,
  Heart,
  LockKeyhole,
  Maximize2,
  MoreVertical,
  Pin,
  Pencil,
  RefreshCw,
  Share2,
  Trash2,
  X,
} from "lucide-react";
import { useRef, useState } from "react";
import { downloadItemsUrl } from "@/lib/api";
import { formatBytes, formatDate } from "@/lib/formatting";
import { logError, logInfo } from "@/lib/logger";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function ItemDetails({
  item,
  busy,
  onClose,
  onRename,
  onFavorite,
  onMove,
  folderOptions,
  onTrash,
  onRestore,
  onRetryMetadata,
  onCorrectCaptureDate,
  onShare,
  onRemoveTag,
  showMoveAction = true,
  mode = "manage",
  onPreview,
  onCopy,
  onDownload,
  compactFileActions = false,
}: {
  item: MediaItem;
  busy: boolean;
  onClose: () => void;
  onRename: (name: string) => void;
  onFavorite: () => void;
  onMove: (parentId?: string) => void;
  folderOptions: MediaItem[];
  onTrash: () => void;
  onRestore: () => void;
  onRetryMetadata: () => void;
  onCorrectCaptureDate?: () => void;
  onShare: () => void;
  onRemoveTag?: (tagId: string) => void;
  showMoveAction?: boolean;
  mode?: "manage" | "viewer-info";
  onPreview?: () => void;
  onCopy?: () => void;
  onDownload?: () => void;
  compactFileActions?: boolean;
}) {
  const { locale, t } = usePreferences();
  const protectedFolder = Boolean(item.systemRole);
  const isMedia = item.kind === "image" || item.kind === "video";
  const opensInNewTab = item.mimeType?.toLowerCase() === "application/pdf";
  const [fileAction, setFileAction] = useState<{
    itemId: string;
    value: "rename" | "move";
  } | null>(null);
  const moreActions = useRef<HTMLDetailsElement>(null);
  const activeFileAction =
    fileAction?.itemId === item.id ? fileAction.value : null;
  const hasFileMoreActions =
    (item.capabilities.canRename ||
      item.capabilities.canMove ||
      item.capabilities.canTrash ||
      (item.kind !== "folder" && item.accessRole !== "viewer" && onCopy)) &&
    !item.trashedAt;
  const dateLocale = locale === "es" ? "es-ES" : "en-US";
  return (
    <aside
      id={mode === "viewer-info" ? "viewer-info-panel" : undefined}
      className={`details-panel ${mode === "viewer-info" ? "viewer-info-panel" : ""}`}
      aria-label={t("details.aria", { name: item.name })}
    >
      <div className="details-header">
        <h2>
          {mode === "viewer-info" ? t("details.infoTitle") : t("details.title")}
        </h2>
        <button
          className="icon-button"
          onClick={onClose}
          aria-label={t("details.close")}
        >
          <X />
        </button>
      </div>
      {mode !== "viewer-info" && item.thumbnailUrl ? (
        <img
          className="details-image"
          src={item.thumbnailUrl}
          crossOrigin="use-credentials"
          alt=""
          onLoad={() =>
            logInfo("frontend.thumbnail", "details loaded", { itemId: item.id })
          }
          onError={() =>
            logError("frontend.thumbnail", "details failed", {
              itemId: item.id,
            })
          }
        />
      ) : mode !== "viewer-info" ? (
        <div className="document-preview">
          {protectedFolder ? <LockKeyhole /> : <FileText />}
        </div>
      ) : null}
      <h3>{item.name}</h3>
      <p className="details-kind">
        {itemKindLabel(item.kind, t)}
        {item.sizeBytes !== undefined
          ? ` · ${formatBytes(item.sizeBytes)}`
          : ""}
      </p>
      {mode === "manage" && compactFileActions && !item.trashedAt && (
        <div className="details-file-actions" aria-label={t("details.actions")}>
          {onPreview && (
            <button
              className="secondary details-preview-action"
              disabled={busy}
              onClick={onPreview}
            >
              {opensInNewTab ? (
                <ExternalLink size={17} />
              ) : (
                <Maximize2 size={17} />
              )}{" "}
              {t(opensInNewTab ? "files.openNewTab" : "details.openPreview")}
            </button>
          )}
          {item.capabilities.canFavorite && (
            <button
              className="icon-button details-quick-action"
              disabled={busy}
              onClick={onFavorite}
              aria-label={favoriteActionLabel(item, t)}
              title={favoriteActionLabel(item, t)}
            >
              {item.kind === "folder" ? (
                <Pin fill={item.favorite ? "currentColor" : "none"} />
              ) : (
                <Heart fill={item.favorite ? "currentColor" : "none"} />
              )}
            </button>
          )}
          {item.capabilities.canShare && (
            <button
              className="icon-button details-quick-action"
              disabled={busy}
              onClick={onShare}
              aria-label={t("details.share")}
              title={t("details.share")}
            >
              <Share2 />
            </button>
          )}
          {!item.trashedAt &&
            (item.kind === "folder" ? (
              onDownload ? (
                <button
                  className="icon-button details-quick-action"
                  type="button"
                  aria-label={t("common.download")}
                  title={t("common.download")}
                  onClick={onDownload}
                >
                  <Download />
                </button>
              ) : null
            ) : (
              <a
                className="icon-button details-quick-action"
                href={downloadItemsUrl([item.id])}
                download={item.name}
                aria-label={t("common.download")}
                title={t("common.download")}
                onClick={() =>
                  logInfo("frontend.download", "download requested", {
                    itemId: item.id,
                  })
                }
              >
                <Download />
              </a>
            ))}
          {hasFileMoreActions && (
            <details
              key={item.id}
              ref={moreActions}
              className="details-action-menu-wrap"
              onBlur={(event) => {
                if (!event.currentTarget.contains(event.relatedTarget))
                  event.currentTarget.removeAttribute("open");
              }}
              onKeyDown={(event) => {
                if (event.key !== "Escape") return;
                event.currentTarget.removeAttribute("open");
                event.currentTarget.querySelector("summary")?.focus();
              }}
            >
              <summary
                className="icon-button details-quick-action"
                aria-label={t("details.moreActions")}
                title={t("details.moreActions")}
              >
                <MoreVertical />
              </summary>
              <div className="details-action-menu" role="menu">
                {item.capabilities.canRename && (
                  <button
                    type="button"
                    role="menuitem"
                    onClick={() => {
                      setFileAction({ itemId: item.id, value: "rename" });
                      moreActions.current?.removeAttribute("open");
                    }}
                  >
                    <Pencil /> {t("details.rename")}
                  </button>
                )}
                {item.capabilities.canMove && (
                  <button
                    type="button"
                    role="menuitem"
                    onClick={() => {
                      setFileAction({ itemId: item.id, value: "move" });
                      moreActions.current?.removeAttribute("open");
                    }}
                  >
                    <FolderInput /> {t("details.move")}
                  </button>
                )}
                {item.kind !== "folder" &&
                  item.accessRole !== "viewer" &&
                  onCopy && (
                    <button
                      type="button"
                      role="menuitem"
                      onClick={() => {
                        onCopy();
                        moreActions.current?.removeAttribute("open");
                      }}
                    >
                      <Copy /> {t("files.makeCopy")}
                    </button>
                  )}
                {item.capabilities.canTrash && (
                  <button
                    type="button"
                    role="menuitem"
                    className="destructive"
                    onClick={() => {
                      onTrash();
                      moreActions.current?.removeAttribute("open");
                    }}
                  >
                    <Trash2 /> {t("common.moveToTrash")}
                  </button>
                )}
              </div>
            </details>
          )}
        </div>
      )}
      {mode === "manage" && compactFileActions && activeFileAction && (
        <div className="details-inline-editor">
          <button
            className="icon-button details-inline-editor-close"
            type="button"
            onClick={() => setFileAction(null)}
            aria-label={t("common.close")}
          >
            <X />
          </button>
          {activeFileAction === "rename" ? (
            <RenameControl
              name={item.name}
              busy={busy}
              onRename={(name) => {
                onRename(name);
                setFileAction(null);
              }}
            />
          ) : (
            <MoveControl
              item={item}
              folders={folderOptions}
              busy={busy}
              onMove={(parentId) => {
                onMove(parentId);
                setFileAction(null);
              }}
            />
          )}
        </div>
      )}
      {protectedFolder && (
        <p className="status-note compact">{t("details.protected")}</p>
      )}
      {item.accessRole === "viewer" && (
        <p className="status-note compact">
          {item.sharedByDisplayName
            ? t("details.sharedNamed", { name: item.sharedByDisplayName })
            : t("details.shared")}
        </p>
      )}
      <div className="detail-list">
        {item.catalogPath && (
          <span>
            {t("details.storedIn")}
            <strong>{catalogLocation(item.catalogPath, t)}</strong>
          </span>
        )}
        {item.tags && item.tags.length > 0 && (
          <span>
            {t("details.tags")}
            <strong className="detail-tag-list">
              {item.tags.map((tag) => (
                <span className={`detail-tag tag-${tag.color}`} key={tag.id}>
                  {tag.name}
                  {onRemoveTag && (
                    <button
                      type="button"
                      disabled={busy}
                      aria-label={t("tags.removeNamed", { name: tag.name })}
                      title={t("tags.removeNamed", { name: tag.name })}
                      onClick={() => onRemoveTag(tag.id)}
                    >
                      <X size={12} />
                    </button>
                  )}
                </span>
              ))}
            </strong>
          </span>
        )}
        <span>
          {t("details.modified")}
          <strong>{formatDate(item.modifiedAt, dateLocale)}</strong>
        </span>
        {item.capturedAt && (
          <span>
            {t("details.captured")}
            <strong className="detail-editable-value">
              <span>
                {formatDate(item.capturedAt, dateLocale)}
                {item.captureDateSource
                  ? ` · ${captureSourceLabel(item.captureDateSource, t)}`
                  : ""}
              </span>
              {compactFileActions &&
                onCorrectCaptureDate &&
                isMedia &&
                item.accessRole !== "viewer" &&
                !item.trashedAt && (
                  <button
                    type="button"
                    className="detail-inline-edit"
                    disabled={busy}
                    onClick={onCorrectCaptureDate}
                    aria-label={t("details.correctCaptureDate")}
                    title={t("details.correctCaptureDate")}
                  >
                    <Pencil />
                  </button>
                )}
            </strong>
          </span>
        )}
        {item.metadata && item.kind !== "folder" && (
          <>
            <span>
              {t("details.metadata")}
              <strong>{stateLabel(item.metadata.state, t)}</strong>
            </span>
            {item.metadata.detectedMimeType && (
              <span>
                {t("details.detectedType")}
                <strong>
                  {item.metadata.detectedMimeType}
                  {item.metadata.mediaFormat
                    ? ` · ${item.metadata.mediaFormat}`
                    : ""}
                </strong>
              </span>
            )}
            {item.metadata.width && item.metadata.height && (
              <span>
                {t("details.dimensions")}
                <strong>
                  {item.metadata.width} × {item.metadata.height}
                </strong>
              </span>
            )}
            {item.metadata.orientation && (
              <span>
                {t("details.orientation")}
                <strong>EXIF {item.metadata.orientation}</strong>
              </span>
            )}
            {item.metadata.description && (
              <span>
                {t("details.description")}
                <strong>{item.metadata.description}</strong>
              </span>
            )}
            {item.metadata.latitude != null &&
              item.metadata.longitude != null && (
                <span>
                  {t("details.location")}
                  <strong>
                    {item.metadata.latitude.toFixed(6)},{" "}
                    {item.metadata.longitude.toFixed(6)}
                    {item.metadata.altitudeMeters != null
                      ? ` · ${Math.round(item.metadata.altitudeMeters)} m`
                      : ""}
                  </strong>
                </span>
              )}
            <span>
              {t("details.recoveryManifest")}
              <strong>
                {stateLabel(item.metadata.recoveryManifestState, t)}
              </strong>
            </span>
          </>
        )}
        <span>
          {t("details.availability")}
          <strong>
            <Cloud size={14} /> {localStateLabel(item.localState, t)}
          </strong>
        </span>
      </div>
      {item.metadata?.state === "failed" && (
        <p className="status-note compact metadata-error">
          {t("details.metadataFailed")}
          {item.metadata.errorMessage ? ` ${item.metadata.errorMessage}` : ""}
        </p>
      )}
      <div className="details-actions">
        {mode === "manage" &&
          !compactFileActions &&
          onPreview &&
          isMedia &&
          !item.trashedAt && (
            <button
              className="primary full"
              disabled={busy}
              onClick={onPreview}
            >
              <Maximize2 size={17} /> {t("details.openPreview")}
            </button>
          )}
        {!compactFileActions &&
          onCorrectCaptureDate &&
          isMedia &&
          item.accessRole !== "viewer" &&
          !item.trashedAt && (
            <button
              className="secondary full"
              disabled={busy}
              onClick={onCorrectCaptureDate}
            >
              <CalendarDays size={17} /> {t("details.correctCaptureDate")}
            </button>
          )}
        {item.kind !== "folder" &&
          item.capabilities.canRetryMetadata &&
          (item.metadata?.state === "failed" ||
            item.metadata?.recoveryManifestState === "failed") && (
            <button
              className="secondary full"
              disabled={busy}
              onClick={onRetryMetadata}
            >
              <RefreshCw size={17} /> {t("details.retryMetadata")}
            </button>
          )}
        {mode === "manage" &&
          !compactFileActions &&
          item.capabilities.canRename &&
          !item.trashedAt && (
            <RenameControl name={item.name} busy={busy} onRename={onRename} />
          )}
        {mode === "manage" &&
          !compactFileActions &&
          showMoveAction &&
          item.capabilities.canMove &&
          !item.trashedAt && (
            <MoveControl
              item={item}
              folders={folderOptions}
              busy={busy}
              onMove={onMove}
            />
          )}
        {mode === "manage" &&
          !compactFileActions &&
          item.capabilities.canFavorite &&
          !item.trashedAt && (
            <button
              className="secondary full"
              disabled={busy}
              onClick={onFavorite}
            >
              <Heart size={17} fill={item.favorite ? "currentColor" : "none"} />
              {item.favorite
                ? t("common.removeFavorite")
                : t("common.addFavorite")}
            </button>
          )}
        {mode === "manage" &&
          !compactFileActions &&
          item.capabilities.canShare &&
          !item.trashedAt && (
            <button
              className="secondary full"
              disabled={busy}
              onClick={onShare}
            >
              <Share2 size={17} /> {t("details.share")}
            </button>
          )}
        {mode === "manage" &&
          !compactFileActions &&
          (item.trashedAt ? (
            <button
              className="primary full"
              disabled={busy}
              onClick={onRestore}
            >
              {t("details.restore")}
            </button>
          ) : (
            item.capabilities.canTrash && (
              <button
                className="secondary full destructive"
                disabled={busy}
                onClick={onTrash}
              >
                <Trash2 size={17} /> {t("common.moveToTrash")}
              </button>
            )
          ))}
        {mode === "manage" &&
          !compactFileActions &&
          !item.trashedAt &&
          item.kind === "folder" &&
          onDownload && (
            <button
              className="primary full"
              type="button"
              onClick={onDownload}
            >
              <Download size={17} /> {t("common.download")}
            </button>
          )}
        {mode === "manage" &&
          !compactFileActions &&
          !item.trashedAt &&
          item.kind !== "folder" && (
            <a
              className="primary full"
              href={downloadItemsUrl([item.id])}
              download={item.name}
              onClick={() =>
                logInfo("frontend.download", "download requested", {
                  itemId: item.id,
                  archive: false,
                })
              }
            >
              <Download size={17} /> {t("common.download")}
            </a>
          )}
      </div>
    </aside>
  );
}

function favoriteActionLabel(
  item: MediaItem,
  t: (key: import("@/lib/translations").TranslationKey) => string,
) {
  if (item.kind === "folder")
    return item.favorite
      ? t("files.unpinQuickAccess")
      : t("files.pinQuickAccess");
  return item.favorite ? t("common.removeFavorite") : t("common.addFavorite");
}

type Translate = ReturnType<typeof usePreferences>["t"];

function itemKindLabel(kind: MediaItem["kind"], t: Translate) {
  return t(
    (
      {
        folder: "details.folder",
        image: "details.image",
        video: "details.video",
        document: "details.document",
      } as const
    )[kind],
  );
}

function stateLabel(state: string, t: Translate) {
  return (
    {
      pending: t("details.statePending"),
      processing: t("details.stateProcessing"),
      succeeded: t("details.stateSucceeded"),
      failed: t("details.stateFailed"),
    }[state] ?? state.replaceAll("_", " ")
  );
}

function localStateLabel(state: string, t: Translate) {
  return (
    {
      local: t("details.stateLocal"),
      cached: t("details.stateCached"),
      remote: t("details.stateRemote"),
      pinned: t("details.statePinned"),
    }[state] ?? state.replaceAll("_", " ")
  );
}

function captureSourceLabel(source: string, t: Translate) {
  return (
    {
      user: t("details.captureCorrected"),
      exif_datetime_original: t("details.captureExifOriginal"),
      exif_datetime_digitized: t("details.captureExifDigitized"),
      embedded_creation_time: t("details.captureEmbedded"),
      imported_metadata: t("details.captureImported"),
      filename: t("details.captureFilename"),
      uploaded_at: t("details.captureUpload"),
    }[source] ?? source
  );
}

function catalogLocation(path: string, t: Translate) {
  const displayPath = path.replaceAll(" / ", " › ");
  if (displayPath === "Documents") return t("library.files");
  if (displayPath.startsWith("Documents › "))
    return `${t("library.files")} › ${displayPath.slice("Documents › ".length)}`;
  if (displayPath === "Media") return t("library.photos");
  if (displayPath.startsWith("Media › "))
    return `${t("library.photos")} › ${displayPath.slice("Media › ".length)}`;
  return displayPath;
}

function MoveControl({
  item,
  folders,
  busy,
  onMove,
}: {
  item: MediaItem;
  folders: MediaItem[];
  busy: boolean;
  onMove: (parentId?: string) => void;
}) {
  const { t } = usePreferences();
  return (
    <form
      className="rename-control"
      onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        onMove(String(data.get("parentId") ?? "") || undefined);
      }}
    >
      <label>
        {t("details.moveWithinFiles")}
        <select
          name="parentId"
          defaultValue={item.parentId ?? ""}
          disabled={busy}
        >
          <option value="">{t("details.filesHome")}</option>
          {folders
            .filter((folder) => folder.id !== item.id)
            .map((folder) => (
              <option key={folder.id} value={folder.id}>
                {folder.name}
              </option>
            ))}
        </select>
      </label>
      <button className="secondary full" disabled={busy} type="submit">
        {t("details.move")}
      </button>
    </form>
  );
}

function RenameControl({
  name,
  busy,
  onRename,
}: {
  name: string;
  busy: boolean;
  onRename: (name: string) => void;
}) {
  const { t } = usePreferences();
  return (
    <form
      className="rename-control"
      onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        onRename(String(data.get("name") ?? "").trim());
      }}
    >
      <label>
        {t("details.rename")}
        <input
          name="name"
          defaultValue={name}
          disabled={busy}
          maxLength={255}
        />
      </label>
      <button className="secondary full" disabled={busy} type="submit">
        {t("details.saveName")}
      </button>
    </form>
  );
}
