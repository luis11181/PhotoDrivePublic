"use client";

import { useState } from "react";
import { Check, Pencil, Tag, Trash2, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { CatalogTag } from "@/lib/types";

const COLORS: CatalogTag["color"][] = [
  "gray", "red", "orange", "yellow", "green", "blue", "purple", "pink",
];

export function TagManagementDialog({
  tags,
  busy,
  onUpdate,
  onDelete,
  onClose,
}: {
  tags: CatalogTag[];
  busy: boolean;
  onUpdate: (tag: CatalogTag, name: string, color: CatalogTag["color"]) => Promise<void>;
  onDelete: (tag: CatalogTag) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [color, setColor] = useState<CatalogTag["color"]>("gray");
  const [error, setError] = useState("");

  function startEditing(tag: CatalogTag) {
    setEditingId(tag.id);
    setName(tag.name);
    setColor(tag.color);
    setError("");
  }

  async function save(tag: CatalogTag) {
    if (!name.trim()) return;
    setError("");
    try {
      await onUpdate(tag, name, color);
      setEditingId(null);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("tags.updateFailed"));
    }
  }

  async function remove(tag: CatalogTag) {
    if (!window.confirm(t("tags.deleteConfirm", { name: tag.name }))) return;
    setError("");
    try {
      await onDelete(tag);
      if (editingId === tag.id) setEditingId(null);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("tags.deleteFailed"));
    }
  }

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={() => !busy && onClose()}>
      <section className="modal tag-dialog tag-management-dialog" role="dialog" aria-modal="true" aria-labelledby="tag-management-title" onMouseDown={(event) => event.stopPropagation()}>
        <header className="tag-dialog-header">
          <div>
            <p className="eyebrow">{t("tags.eyebrow")}</p>
            <h2 id="tag-management-title">{t("tags.manageTitle")}</h2>
          </div>
          <button className="icon-button modal-close" type="button" disabled={busy} onClick={onClose} aria-label={t("tags.close")}><X /></button>
        </header>
        <p className="modal-copy">{t("tags.manageHelp")}</p>
        <div className="tag-management-list">
          {tags.length === 0 ? (
            <p className="status-note">{t("tags.noneToManage")}</p>
          ) : tags.map((tag) => editingId === tag.id ? (
            <div className="tag-management-edit" key={tag.id}>
              <label className="field">
                {t("tags.name")}
                <input autoFocus maxLength={64} value={name} disabled={busy} onChange={(event) => setName(event.target.value)} />
              </label>
              <label className="field">
                {t("tags.color")}
                <select value={color} disabled={busy} onChange={(event) => setColor(event.target.value as CatalogTag["color"])}>
                  {COLORS.map((candidate) => <option key={candidate} value={candidate}>{candidate}</option>)}
                </select>
              </label>
              <div className="tag-management-actions">
                <button type="button" className="secondary" disabled={busy} onClick={() => setEditingId(null)}>{t("common.cancel")}</button>
                <button type="button" className="primary" disabled={busy || !name.trim()} onClick={() => void save(tag)}><Check size={16} /> {t("tags.save")}</button>
              </div>
            </div>
          ) : (
            <div className="tag-management-row" key={tag.id}>
              <span className={`tag-dot tag-${tag.color}`}><Tag size={14} /></span>
              <span><strong>{tag.name}</strong><small>{t("tags.assignmentCount", { count: tag.fileCount + tag.folderCount })}</small></span>
              <button className="icon-button" type="button" disabled={busy} onClick={() => startEditing(tag)} aria-label={t("tags.editNamed", { name: tag.name })} title={t("tags.editNamed", { name: tag.name })}><Pencil size={16} /></button>
              <button className="icon-button destructive" type="button" disabled={busy} onClick={() => void remove(tag)} aria-label={t("tags.deleteNamed", { name: tag.name })} title={t("tags.deleteNamed", { name: tag.name })}><Trash2 size={16} /></button>
            </div>
          ))}
        </div>
        {error && <p className="status-note compact metadata-error">{error}</p>}
      </section>
    </div>
  );
}
