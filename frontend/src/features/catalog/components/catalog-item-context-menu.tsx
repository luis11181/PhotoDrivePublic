"use client";

import {
  Download,
  Copy,
  Eye,
  ExternalLink,
  Heart,
  Info,
  Pencil,
  RotateCcw,
  Share2,
  Trash2,
} from "lucide-react";
import { useEffect, useRef, type ReactNode } from "react";
import { usePreferences } from "@/lib/preferences";
import type { CatalogContextMenuState } from "../use-catalog-context-menu";

export function CatalogItemContextMenu({
  state,
  busy,
  onClose,
  onOpen,
  openInNewTabHref,
  onDownload,
  onFavorite,
  onCopy,
  onDetails,
  onRename,
  onShare,
  onTrash,
  onRestore,
  extraActions,
}: {
  state: CatalogContextMenuState;
  busy: boolean;
  onClose: () => void;
  onOpen: () => void;
  openInNewTabHref?: string;
  onDownload?: () => void;
  onFavorite?: () => void;
  onCopy?: () => void;
  onDetails: () => void;
  onRename?: () => void;
  onShare?: () => void;
  onTrash?: () => void;
  onRestore?: () => void;
  extraActions?: Array<{
    label: string;
    icon: ReactNode;
    onClick: () => void;
  }>;
}) {
  const { t } = usePreferences();
  const menuRef = useRef<HTMLDivElement>(null);
  const item = state.item;

  useEffect(() => {
    function close(event: PointerEvent) {
      if (
        event.target instanceof Node &&
        !menuRef.current?.contains(event.target)
      )
        onClose();
    }
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    window.addEventListener("pointerdown", close);
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      window.removeEventListener("pointerdown", close);
      window.removeEventListener("keydown", closeOnEscape);
    };
  }, [onClose]);

  function run(action: () => void) {
    onClose();
    action();
  }

  return (
    <div
      ref={menuRef}
      className="item-context-menu"
      role="menu"
      style={{ left: state.x, top: state.y }}
    >
      <MenuButton
        icon={<Eye />}
        label={t("selection.open")}
        onClick={() => run(onOpen)}
      />
      {openInNewTabHref && (
        <a
          className="item-context-link"
          role="menuitem"
          href={openInNewTabHref}
          target="_blank"
          rel="noopener noreferrer"
          onClick={onClose}
        >
          <ExternalLink />
          <span>{t("files.openNewTab")}</span>
        </a>
      )}
      {!item.trashedAt && onDownload && (
        <MenuButton
          icon={<Download />}
          label={t("common.download")}
          onClick={() => run(onDownload)}
        />
      )}
      {item.capabilities.canShare && !item.trashedAt && onShare && (
        <MenuButton
          icon={<Share2 />}
          label={t("details.share")}
          onClick={() => run(onShare)}
        />
      )}
      <MenuButton
        icon={<Info />}
        label={t("details.title")}
        onClick={() => run(onDetails)}
      />
      <div className="item-context-divider" />
      {item.capabilities.canFavorite && !item.trashedAt && onFavorite && (
        <MenuButton
          icon={<Heart fill={item.favorite ? "currentColor" : "none"} />}
          label={
            item.kind === "folder"
              ? item.favorite
                ? t("files.unpinQuickAccess")
                : t("files.pinQuickAccess")
              : item.favorite
                ? t("common.removeFavorite")
                : t("common.addFavorite")
          }
          onClick={() => run(onFavorite)}
          disabled={busy}
        />
      )}
      {item.capabilities.canRename && !item.trashedAt && onRename && (
        <MenuButton
          icon={<Pencil />}
          label={t("files.rename")}
          onClick={() => run(onRename)}
          disabled={busy}
        />
      )}
      {item.kind !== "folder" && item.accessRole !== "viewer" && !item.trashedAt && onCopy && (
        <MenuButton
          icon={<Copy />}
          label={t("files.makeCopy")}
          onClick={() => run(onCopy)}
          disabled={busy}
        />
      )}
      {extraActions?.map((action) => (
        <MenuButton
          key={action.label}
          icon={action.icon}
          label={action.label}
          onClick={() => run(action.onClick)}
          disabled={busy}
        />
      ))}
      {item.trashedAt && onRestore && (
        <MenuButton
          icon={<RotateCcw />}
          label={t("details.restore")}
          onClick={() => run(onRestore)}
          disabled={busy}
        />
      )}
      {item.capabilities.canTrash && !item.trashedAt && onTrash && (
        <MenuButton
          destructive
          icon={<Trash2 />}
          label={t("common.moveToTrash")}
          onClick={() => run(onTrash)}
          disabled={busy}
        />
      )}
    </div>
  );
}

function MenuButton({
  icon,
  label,
  onClick,
  disabled,
  destructive,
}: {
  icon: ReactNode;
  label: string;
  onClick: () => void;
  disabled?: boolean;
  destructive?: boolean;
}) {
  return (
    <button
      type="button"
      role="menuitem"
      className={destructive ? "destructive" : undefined}
      onClick={onClick}
      disabled={disabled}
    >
      {icon}
      <span>{label}</span>
    </button>
  );
}
