"use client";

import { Trash2 } from "lucide-react";
import { useState } from "react";
import { formatBytes } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";

export function ClearTrashDialog({
  busy,
  fileCount,
  folderCount,
  originalCount,
  originalBytes,
  onClear,
  onClose,
}: {
  busy: boolean;
  fileCount: number;
  folderCount: number;
  originalCount: number;
  originalBytes: number;
  onClear: (confirmationPhrase: string) => void;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const [confirmed, setConfirmed] = useState(false);
  const [confirmationPhrase, setConfirmationPhrase] = useState("");
  const deletionCount = fileCount + folderCount + originalCount;
  return (
    <div className="modal-backdrop" onMouseDown={() => !busy && onClose()}>
      <section
        className="modal"
        aria-modal="true"
        aria-labelledby="clear-trash-title"
        role="dialog"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon destructive-icon">
          <Trash2 />
        </div>
        <h2 id="clear-trash-title">{t("clearTrash.title")}</h2>
        <p className="modal-copy">
          {t("clearTrash.catalog", {
            files: fileCount,
            fileNoun: t(fileCount === 1 ? "clearTrash.file" : "clearTrash.files"),
            folders: folderCount,
            folderNoun: t(
              folderCount === 1
                ? "clearTrash.folder"
                : "clearTrash.folders",
            ),
          })}
        </p>
        <p className="modal-copy">
          {t("clearTrash.originals", {
            count: originalCount,
            noun: t(
              originalCount === 1
                ? "clearTrash.original"
                : "clearTrash.originalPlural",
            ),
            size: formatBytes(originalBytes),
          })}
        </p>
        <p className="modal-copy">{t("clearTrash.note")}</p>
        <label className="confirm-checkbox">
          <input
            checked={confirmed}
            disabled={busy}
            onChange={(event) => setConfirmed(event.target.checked)}
            type="checkbox"
          />
          {t("clearTrash.understand")}
        </label>
        <label className="field confirmation-field">
          {t("clearTrash.type")} <strong>DELETE</strong> {t("clearTrash.confirm")}
          <input
            autoComplete="off"
            disabled={busy}
            onChange={(event) => setConfirmationPhrase(event.target.value)}
            value={confirmationPhrase}
          />
        </label>
        <div className="modal-actions">
          <button className="secondary" disabled={busy} onClick={onClose} type="button">
            {t("common.cancel")}
          </button>
          <button
            className="secondary destructive"
            disabled={busy || !confirmed || confirmationPhrase !== "DELETE" || deletionCount === 0}
            onClick={() => onClear(confirmationPhrase)}
            type="button"
          >
            {busy ? t("clearTrash.clearing") : t("clearTrash.clear")}
          </button>
        </div>
      </section>
    </div>
  );
}
