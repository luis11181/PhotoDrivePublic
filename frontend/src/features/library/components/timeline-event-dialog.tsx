"use client";

import { type FormEvent, useState } from "react";
import { CalendarPlus, Trash2, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { TimelineEvent } from "@/lib/types";

export type TimelineEventValues = {
  title: string;
  startDate: string;
  endDate?: string;
  description?: string;
};

export function TimelineEventDialog({
  event,
  busy,
  onSave,
  onDelete,
  onClose,
}: {
  event: TimelineEvent | null;
  busy: boolean;
  onSave: (values: TimelineEventValues) => Promise<void>;
  onDelete?: () => Promise<void>;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const [title, setTitle] = useState(event?.title ?? "");
  const [startDate, setStartDate] = useState(event?.startDate ?? "");
  const [endDate, setEndDate] = useState(event?.endDate ?? "");
  const [description, setDescription] = useState(event?.description ?? "");
  const [error, setError] = useState("");
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  async function submit(formEvent: FormEvent<HTMLFormElement>) {
    formEvent.preventDefault();
    if (!title.trim() || !startDate) {
      setError(t("events.required"));
      return;
    }
    if (endDate && endDate < startDate) {
      setError(t("events.endBeforeStart"));
      return;
    }
    setError("");
    try {
      await onSave({
        title: title.trim(),
        startDate,
        endDate: endDate || undefined,
        description: description.trim() || undefined,
      });
      onClose();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("events.saveFailed"));
    }
  }

  async function remove() {
    if (!onDelete) return;
    setError("");
    try {
      await onDelete();
      onClose();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("events.deleteFailed"));
    }
  }

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={() => !busy && onClose()}>
      <form className="modal timeline-event-dialog" role="dialog" aria-modal="true" aria-labelledby="timeline-event-dialog-title" onSubmit={(formEvent) => void submit(formEvent)} onMouseDown={(pointerEvent) => pointerEvent.stopPropagation()}>
        <div className="modal-icon"><CalendarPlus /></div>
        <p className="eyebrow">{t("events.timelineEvent")}</p>
        <h2 id="timeline-event-dialog-title">{t(event ? "events.edit" : "events.new")}</h2>
        <label className="field">
          {t("events.title")}
          <input autoFocus maxLength={200} value={title} disabled={busy} onChange={(inputEvent) => setTitle(inputEvent.target.value)} />
        </label>
        <div className="timeline-event-date-fields">
          <label className="field">
            {t("events.startDate")}
            <input type="date" value={startDate} disabled={busy} onChange={(inputEvent) => setStartDate(inputEvent.target.value)} />
          </label>
          <label className="field">
            {t("events.endDate")}
            <input type="date" min={startDate || undefined} value={endDate} disabled={busy} onChange={(inputEvent) => setEndDate(inputEvent.target.value)} />
          </label>
        </div>
        <label className="field timeline-event-description-field">
          {t("events.description")}
          <textarea rows={4} maxLength={2000} value={description} disabled={busy} onChange={(inputEvent) => setDescription(inputEvent.target.value)} />
        </label>
        {error && <p className="status-note compact metadata-error">{error}</p>}
        {confirmingDelete && <p className="timeline-event-delete-warning">{t("events.deleteWarning")}</p>}
        <div className="modal-actions timeline-event-dialog-actions">
          {event && onDelete && (
            confirmingDelete ? (
              <button className="secondary destructive" type="button" disabled={busy} onClick={() => void remove()}><Trash2 size={16} /> {t("events.confirmDelete")}</button>
            ) : (
              <button className="secondary destructive" type="button" disabled={busy} onClick={() => setConfirmingDelete(true)}><Trash2 size={16} /> {t("events.delete")}</button>
            )
          )}
          <button className="secondary" type="button" disabled={busy} onClick={onClose}>{t("common.cancel")}</button>
          <button className="primary" type="submit" disabled={busy || !title.trim() || !startDate}>{t("events.save")}</button>
        </div>
        <button className="icon-button modal-close" type="button" disabled={busy} onClick={onClose} aria-label={t("common.close")}><X /></button>
      </form>
    </div>
  );
}
