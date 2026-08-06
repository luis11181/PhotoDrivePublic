"use client";

import { useQuery } from "@tanstack/react-query";
import { Download, X } from "lucide-react";
import { useEffect } from "react";
import {
  previewAlbumDownload,
  previewFolderDownload,
  previewItemsDownload,
} from "@/lib/api";
import { formatBytes } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";

export type DownloadConfirmationRequest =
  | { kind: "items"; ids: string[] }
  | { kind: "folder"; id: string; name: string }
  | { kind: "album"; id: string; name: string };

export function DownloadConfirmationDialog({
  request,
  onConfirm,
  onClose,
}: {
  request: DownloadConfirmationRequest;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const preview = useQuery({
    queryKey: [
      "download-preview",
      request.kind,
      ...(request.kind === "items" ? [...request.ids].sort() : [request.id]),
    ],
    queryFn: ({ signal }) => {
      if (request.kind === "items")
        return previewItemsDownload(request.ids, signal);
      if (request.kind === "folder")
        return previewFolderDownload(request.id, signal);
      return previewAlbumDownload(request.id, signal);
    },
    staleTime: 0,
    retry: false,
  });

  useEffect(() => {
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [onClose]);

  const title =
    request.kind === "folder"
      ? t("downloadConfirm.folderTitle", { name: request.name })
      : request.kind === "album"
        ? t("downloadConfirm.albumTitle", { name: request.name })
        : t("downloadConfirm.selectionTitle");

  return (
    <div className="modal-backdrop" onMouseDown={onClose}>
      <section
        className="modal download-confirmation-modal"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="download-confirmation-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon"><Download /></div>
        <h2 id="download-confirmation-title">{title}</h2>
        {preview.isPending ? (
          <p className="modal-copy">{t("downloadConfirm.checking")}</p>
        ) : preview.isError ? (
          <p className="status-note compact metadata-error">
            {preview.error.message}
          </p>
        ) : preview.data ? (
          <>
            <p className="download-confirmation-summary">
              {preview.data.fileCount === 0
                ? t("downloadConfirm.empty")
                : t(
                    preview.data.fileCount === 1
                      ? "downloadConfirm.summaryOne"
                      : "downloadConfirm.summary",
                    {
                      count: preview.data.fileCount,
                      size: formatBytes(preview.data.totalBytes),
                    },
                  )}
            </p>
            <p className="modal-copy">{t("downloadConfirm.help")}</p>
          </>
        ) : null}
        <div className="modal-actions">
          <button className="secondary" type="button" onClick={onClose}>
            {t("common.cancel")}
          </button>
          <button
            className="primary"
            type="button"
            disabled={preview.isPending}
            onClick={onConfirm}
          >
            <Download size={17} /> {t("downloadConfirm.confirm")}
          </button>
        </div>
        <button
          className="icon-button modal-close"
          onClick={onClose}
          aria-label={t("common.close")}
        >
          <X />
        </button>
      </section>
    </div>
  );
}
