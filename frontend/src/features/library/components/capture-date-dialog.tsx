"use client";

import { type FormEvent, useState } from "react";
import { CalendarDays, RotateCcw, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function CaptureDateDialog({
  items,
  busy,
  onSave,
  onReset,
  onClose,
}: {
  items: MediaItem[];
  busy: boolean;
  onSave: (capturedOn: string) => Promise<void>;
  onReset: () => Promise<void>;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const [capturedOn, setCapturedOn] = useState(() =>
    items.length === 1 ? localDate(items[0].capturedAt) : "",
  );
  const [error, setError] = useState("");

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!capturedOn) {
      setError(t("captureDate.required"));
      return;
    }
    setError("");
    try {
      await onSave(capturedOn);
      onClose();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("captureDate.saveFailed"));
    }
  }

  async function reset() {
    setError("");
    try {
      await onReset();
      onClose();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("captureDate.saveFailed"));
    }
  }

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={() => !busy && onClose()}>
      <form className="modal capture-date-dialog" role="dialog" aria-modal="true" aria-labelledby="capture-date-title" onSubmit={(event) => void save(event)} onMouseDown={(event) => event.stopPropagation()}>
        <div className="modal-icon"><CalendarDays /></div>
        <p className="eyebrow">{t("captureDate.eyebrow")}</p>
        <h2 id="capture-date-title">{t(items.length === 1 ? "captureDate.title" : "captureDate.titleMany")}</h2>
        <p className="modal-copy">{t("captureDate.copy")}</p>
        <label className="field capture-date-field">
          {t("captureDate.label")}
          <input autoFocus type="date" value={capturedOn} disabled={busy} onChange={(event) => setCapturedOn(event.target.value)} />
        </label>
        {error && <p className="status-note compact metadata-error">{error}</p>}
        <div className="modal-actions capture-date-actions">
          <button className="secondary" type="button" disabled={busy} onClick={() => void reset()}><RotateCcw size={16} /> {t("captureDate.restore")}</button>
          <button className="secondary" type="button" disabled={busy} onClick={onClose}>{t("common.cancel")}</button>
          <button className="primary" type="submit" disabled={busy || !capturedOn}>{t("captureDate.save")}</button>
        </div>
        <button className="icon-button modal-close" type="button" disabled={busy} onClick={onClose} aria-label={t("common.close")}><X /></button>
      </form>
    </div>
  );
}

function localDate(value?: string) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}
