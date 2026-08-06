"use client";

import { Keyboard, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";

export function CatalogKeyboardShortcutsDialog({
  onClose,
}: {
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const shortcuts = [
    ["Arrow keys", t("files.shortcutNavigate")],
    ["Space", t("files.shortcutToggle")],
    ["Ctrl / ⌘ + A", t("files.shortcutSelectAll")],
    ["Enter", t("files.shortcutOpen")],
    ["F2", t("files.shortcutRename")],
    ["Delete", t("files.shortcutTrash")],
    ["R", t("files.shortcutRestore")],
    ["I", t("files.shortcutDetails")],
    ["Esc", t("files.shortcutClear")],
    ["Ctrl / ⌘ + /", t("files.shortcutHelp")],
  ];
  return (
    <div className="modal-backdrop" onMouseDown={onClose}>
      <section
        className="modal shortcuts-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="shortcuts-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <Keyboard />
        </div>
        <h2 id="shortcuts-title">{t("files.keyboardShortcuts")}</h2>
        <div className="shortcut-list">
          {shortcuts.map(([keys, action]) => (
            <span key={keys}>
              <kbd>{keys}</kbd>
              <strong>{action}</strong>
            </span>
          ))}
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
