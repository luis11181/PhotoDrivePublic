"use client";

import { useQuery } from "@tanstack/react-query";
import { Download, ExternalLink, FileText, X } from "lucide-react";
import { contentUrl, downloadItemsUrl, getTextContent } from "@/lib/api";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";
import { previewKind } from "../preview-support";

const maxInlineTextBytes = 2 * 1024 * 1024;

export function DocumentPreview({
  item,
  onClose,
}: {
  item: MediaItem;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const kind = previewKind(item);
  const source = contentUrl(item.id);
  const textTooLarge =
    kind === "text" &&
    item.sizeBytes !== undefined &&
    item.sizeBytes > maxInlineTextBytes;
  const textQuery = useQuery({
    queryKey: ["document-text-preview", item.id],
    queryFn: ({ signal }) => getTextContent(item.id, signal),
    enabled: kind === "text" && !textTooLarge,
    staleTime: 5 * 60_000,
  });
  return (
    <div
      className="document-viewer-backdrop"
      role="presentation"
      onMouseDown={onClose}
    >
      <section
        className="document-viewer"
        role="dialog"
        aria-modal="true"
        aria-labelledby="document-preview-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header>
          <div>
            <FileText />
            <h2 id="document-preview-title">{item.name}</h2>
          </div>
          <nav aria-label={t("details.actions")}>
            <a
              href={source}
              target="_blank"
              rel="noopener noreferrer"
              title={t("files.openNewTab")}
            >
              <ExternalLink />
            </a>
            <a
              href={downloadItemsUrl([item.id])}
              download={item.name}
              title={t("common.download")}
            >
              <Download />
            </a>
            <button
              type="button"
              onClick={onClose}
              aria-label={t("common.close")}
            >
              <X />
            </button>
          </nav>
        </header>
        <div className="document-viewer-content">
          {kind === "audio" ? (
            <audio controls autoPlay={false} src={source}>
              {t("details.previewUnavailable")}
            </audio>
          ) : textQuery.isPending && !textTooLarge ? (
            <p className="document-preview-status">{t("library.loading")}</p>
          ) : textQuery.isError || textTooLarge ? (
            <p className="document-preview-status">
              {t("details.previewUnavailable")}
            </p>
          ) : (
            <pre className="document-text-preview">
              {formatTextPreview(textQuery.data ?? "", item.mimeType)}
            </pre>
          )}
        </div>
      </section>
    </div>
  );
}

function formatTextPreview(value: string, mimeType?: string) {
  const normalizedMimeType = mimeType?.toLowerCase() ?? "";
  if (
    normalizedMimeType === "application/json" ||
    normalizedMimeType.endsWith("+json")
  ) {
    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }
  return value;
}
