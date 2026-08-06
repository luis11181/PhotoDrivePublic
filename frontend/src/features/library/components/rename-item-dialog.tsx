"use client";

import { type FormEvent, useState } from "react";
import { Pencil, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function RenameItemDialog({
  item,
  busy,
  onSave,
  onClose,
}: {
  item: MediaItem;
  busy: boolean;
  onSave: (name: string) => Promise<boolean>;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const [name, setName] = useState(item.name);

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const normalized = name.trim();
    if (!normalized || normalized === item.name) return;
    if (await onSave(normalized)) onClose();
  }

  return (
    <div
      className="modal-backdrop"
      role="presentation"
      onMouseDown={() => !busy && onClose()}
    >
      <form
        className="modal rename-item-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="rename-item-title"
        onSubmit={(event) => void save(event)}
        onMouseDown={(event) => event.stopPropagation()}
        onKeyDown={(event) => {
          if (event.key !== "Escape" || busy) return;
          event.stopPropagation();
          onClose();
        }}
      >
        <div className="modal-icon">
          <Pencil />
        </div>
        <h2 id="rename-item-title">{t("details.rename")}</h2>
        <label className="field">
          {t("details.rename")}
          <input
            autoFocus
            value={name}
            disabled={busy}
            maxLength={255}
            onChange={(event) => setName(event.target.value)}
          />
        </label>
        <div className="modal-actions">
          <button
            className="secondary"
            type="button"
            disabled={busy}
            onClick={onClose}
          >
            {t("common.cancel")}
          </button>
          <button
            className="primary"
            type="submit"
            disabled={busy || !name.trim() || name.trim() === item.name}
          >
            {t("details.saveName")}
          </button>
        </div>
        <button
          className="icon-button modal-close"
          type="button"
          disabled={busy}
          onClick={onClose}
          aria-label={t("common.close")}
        >
          <X />
        </button>
      </form>
    </div>
  );
}
