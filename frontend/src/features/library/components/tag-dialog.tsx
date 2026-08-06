"use client";

import { type FormEvent, useState } from "react";
import { Plus, Tag, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { CatalogTag, MediaItem } from "@/lib/types";

const COLORS: CatalogTag["color"][] = [
  "gray", "red", "orange", "yellow", "green", "blue", "purple", "pink",
];

export function TagDialog({
  items,
  tags,
  busy,
  onCreate,
  onSet,
  onClose,
}: {
  items: MediaItem[];
  tags: CatalogTag[];
  busy: boolean;
  onCreate: (name: string, color: CatalogTag["color"]) => Promise<void>;
  onSet: (tagId: string, included: boolean) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const [name, setName] = useState("");
  const [color, setColor] = useState<CatalogTag["color"]>("gray");

  async function create(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;
    await onCreate(name, color);
    setName("");
  }

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={onClose}>
      <section className="modal tag-dialog" role="dialog" aria-modal="true" aria-labelledby="tag-dialog-title" onMouseDown={(event) => event.stopPropagation()}>
        <header className="tag-dialog-header">
          <div><p className="eyebrow">{t("tags.eyebrow")}</p><h2 id="tag-dialog-title">{t("tags.title", { count: items.length })}</h2></div>
          <button className="icon-button modal-close" type="button" onClick={onClose} aria-label={t("tags.close")}><X /></button>
        </header>
        <div className="tag-dialog-list">
          {tags.length === 0 && <p className="status-note">{t("tags.createFirst")}</p>}
          {tags.map((tag) => {
            const assigned = items.filter((item) => item.tags?.some((candidate) => candidate.id === tag.id)).length;
            const included = assigned === items.length;
            return (
              <label className="tag-dialog-row" key={tag.id}>
                <input type="checkbox" checked={included} disabled={busy} onChange={() => void onSet(tag.id, !included)} />
                <span className={`tag-dot tag-${tag.color}`}><Tag size={14} /></span>
                <span><strong>{tag.name}</strong><small>{assigned === 0 ? t("tags.notAssigned") : assigned === items.length ? t("tags.assignedAll") : t("tags.assignedCount", { count: assigned, total: items.length })}</small></span>
              </label>
            );
          })}
        </div>
        <form className="tag-create-row" onSubmit={(event) => void create(event)}>
          <input value={name} maxLength={64} placeholder={t("tags.newName")} onChange={(event) => setName(event.target.value)} />
          <select value={color} onChange={(event) => setColor(event.target.value as CatalogTag["color"])} aria-label={t("tags.color")}>
            {COLORS.map((candidate) => <option key={candidate} value={candidate}>{candidate}</option>)}
          </select>
          <button className="secondary" type="submit" disabled={busy || !name.trim()}><Plus size={16} /> {t("tags.create")}</button>
        </form>
      </section>
    </div>
  );
}
