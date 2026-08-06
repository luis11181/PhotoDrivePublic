"use client";

import { useQuery } from "@tanstack/react-query";
import { Folder, Library, Trash2, X } from "lucide-react";
import { useEffect } from "react";
import { previewTrash } from "@/lib/api";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function TrashPreviewDialog({
  items,
  busy,
  onConfirm,
  onClose,
}: {
  items: MediaItem[];
  busy: boolean;
  onConfirm: () => Promise<void>;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const preview = useQuery({
    queryKey: ["trash-preview", ...items.map((item) => item.id).sort()],
    queryFn: () => previewTrash(items.map((item) => item.id)),
    staleTime: 0,
    retry: false,
  });
  const data = preview.data;

  useEffect(() => {
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape" && !busy) onClose();
    }
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [busy, onClose]);

  return (
    <div className="modal-backdrop" onMouseDown={() => !busy && onClose()}>
      <section
        className="modal trash-preview-modal"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="trash-preview-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon destructive-icon"><Trash2 /></div>
        <h2 id="trash-preview-title">
          {items.length === 1
            ? t("trashPreview.singleTitle", { name: items[0].name })
            : t("trashPreview.multiTitle", { count: items.length })}
        </h2>
        {preview.isPending ? (
          <p className="modal-copy">{t("trashPreview.checking")}</p>
        ) : preview.isError ? (
          <p className="status-note compact metadata-error">{preview.error.message}</p>
        ) : data ? (
          <>
            <p className="modal-copy">
              {t("trashPreview.help", {
                items: t(
                  data.fileCount + data.folderCount === 1
                    ? "albums.item"
                    : "albums.items",
                ),
              })}
            </p>
            <div className="trash-impact-list">
              <span><Folder /><b>{t("trashPreview.filesFolders", { files: data.fileCount, folders: data.folderCount })}</b><small>{t("trashPreview.movedFolders")}</small></span>
              {data.albumCount > 0 && <span><Library /><b>{data.albumCount} {t(data.albumCount === 1 ? "trashPreview.album" : "trashPreview.albums")}</b><small>{t("trashPreview.membership")}</small></span>}
            </div>
            {(data.photoItemCount > 0 || data.albumCount > 0) && (
              <p className="status-note compact">{t("trashPreview.restoreHelp")}</p>
            )}
          </>
        ) : null}
        <div className="modal-actions">
          <button className="secondary" type="button" disabled={busy} onClick={onClose}>{t("common.cancel")}</button>
          <button className="primary destructive" type="button" disabled={busy || !data || preview.isError} onClick={() => void onConfirm()}>
            {busy ? t("trashPreview.moving") : t("trashPreview.move")}
          </button>
        </div>
        <button className="icon-button modal-close" disabled={busy} onClick={onClose} aria-label={t("common.close")}><X /></button>
      </section>
    </div>
  );
}
