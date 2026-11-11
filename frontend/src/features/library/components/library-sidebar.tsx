"use client";

import Link from "next/link";
import {
  Album,
  HardDrive,
  Folder,
  FolderInput,
  Github,
  Home,
  Image as ImageIcon,
  Pin,
  RefreshCw,
  Share2,
  SlidersHorizontal,
  Trash2,
  Users,
  X,
} from "lucide-react";
import type { StorageSummary } from "@/lib/api";
import { PreferenceControls } from "@/components/preference-controls";
import { formatBytes } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";
import type { TranslationKey } from "@/lib/translations";
import type { LibraryView, MediaItem } from "@/lib/types";

const navigation = [
  {
    key: "photos",
    view: "photos" as const,
    href: "/photos",
    label: "sidebar.photos" as TranslationKey,
    icon: ImageIcon,
  },
  {
    key: "albums",
    view: "albums" as const,
    href: "/albums",
    label: "sidebar.albums" as TranslationKey,
    icon: Album,
  },
  {
    key: "files",
    view: "files" as const,
    href: "/files",
    label: "sidebar.files" as TranslationKey,
    icon: Folder,
  },
  {
    key: "shared",
    view: "shared" as const,
    href: "/shared",
    label: "sidebar.shared" as TranslationKey,
    icon: Share2,
  },
];

export function LibrarySidebar({
  view,
  open,
  onClose,
  storageSummary,
  storageLoading,
  storageRefreshing,
  onRefreshStorage,
  isOwner,
  quickAccessFolders = [],
  quickAccessLoading = false,
}: {
  view: LibraryView | "imports" | "developer" | "users" | "storage";
  open: boolean;
  onClose: () => void;
  storageSummary?: StorageSummary;
  storageLoading: boolean;
  storageRefreshing: boolean;
  onRefreshStorage: () => void;
  isOwner: boolean;
  quickAccessFolders?: MediaItem[];
  quickAccessLoading?: boolean;
}) {
  const { t } = usePreferences();

  return (
    <aside className={`sidebar ${open ? "sidebar-open" : ""}`}>
      <div className="brand">
        <Link className="brand-home" href="/" onClick={onClose}>
          <span className="brand-mark">
            <Home size={18} />
          </span>
          <strong>PhotoDrive</strong>
        </Link>
        <button
          className="icon-button sidebar-close"
          onClick={onClose}
          aria-label={t("sidebar.closeNavigation")}
        >
          <X />
        </button>
      </div>
      <PreferenceControls className="sidebar-preferences" />

      <p className="nav-label nav-label-first">{t("sidebar.library")}</p>
      <nav aria-label={t("sidebar.libraryNavigation")}>
        {navigation.map(({ key, view: itemView, href, label, icon: Icon }) => {
          const active = view === itemView;
          return (
            <Link
              key={key}
              className={`nav-item ${key === "albums" ? "nav-subitem" : ""} ${active ? "active" : ""}`}
              href={href}
              onClick={onClose}
              aria-current={active ? "page" : undefined}
              aria-label={
                itemView === "albums" ? t("sidebar.photoAlbums") : undefined
              }
              title={
                itemView === "albums" ? t("sidebar.photoAlbums") : undefined
              }
            >
              <Icon />
              <span>{t(label)}</span>
            </Link>
          );
        })}
      </nav>

      {(quickAccessLoading || quickAccessFolders.length > 0) && (
        <section className="quick-access" aria-label={t("sidebar.quickAccess")}>
          <p className="nav-label">{t("sidebar.quickAccess")}</p>
          {quickAccessLoading && quickAccessFolders.length === 0 ? (
            <p className="quick-access-loading">{t("library.loading")}</p>
          ) : (
            quickAccessFolders.slice(0, 6).map((folder) => (
              <Link
                key={folder.id}
                className="nav-item quick-access-item"
                href={quickAccessHref(folder)}
                onClick={onClose}
                title={folder.catalogPath ?? folder.name}
              >
                <Pin />
                <span>{folder.name}</span>
              </Link>
            ))
          )}
        </section>
      )}

      <nav
        className="sidebar-utility-nav"
        aria-label={t("sidebar.toolsAndSettings")}
      >
        <Link
          className={`nav-item ${view === "trash" ? "active" : ""}`}
          href="/trash"
          onClick={onClose}
          aria-current={view === "trash" ? "page" : undefined}
        >
          <Trash2 />
          <span>{t("sidebar.trash")}</span>
        </Link>
        {isOwner && <p className="nav-label">{t("sidebar.administration")}</p>}
        {isOwner && (
          <Link
            className={`nav-item ${view === "imports" ? "active" : ""}`}
            href="/imports"
            onClick={onClose}
            aria-current={view === "imports" ? "page" : undefined}
          >
            <FolderInput />
            <span>{t("sidebar.imports")}</span>
          </Link>
        )}
        {isOwner && (
          <Link
            className={`nav-item ${view === "users" ? "active" : ""}`}
            href="/users"
            onClick={onClose}
            aria-current={view === "users" ? "page" : undefined}
          >
            <Users />
            <span>{t("sidebar.familyAccounts")}</span>
          </Link>
        )}
        {isOwner && (
          <Link
            className={`nav-item ${view === "developer" ? "active" : ""}`}
            href="/developer"
            onClick={onClose}
            aria-current={view === "developer" ? "page" : undefined}
          >
            <SlidersHorizontal />
            <span>{t("sidebar.developerTools")}</span>
          </Link>
        )}
        <a
          className="nav-item"
          href="https://github.com/luis11181/PhotoDrivePublic"
          rel="noreferrer"
          target="_blank"
        >
          <Github />
          <span>{t("sidebar.sourceLicense")}</span>
        </a>
      </nav>

      <div className="sidebar-spacer" />
      <div className="storage-compact">
        <Link className={view === "storage" ? "active" : ""} href="/storage" onClick={onClose} aria-current={view === "storage" ? "page" : undefined}>
          <HardDrive size={17} /><span>{t("sidebar.storage")}</span>
          <strong>{storageSummary ? formatBytes(storageSummary.storedOriginalBytes) : storageLoading ? "…" : "—"}</strong>
        </Link>
        <button className="storage-refresh" aria-label={t("sidebar.refreshStorage")} disabled={storageRefreshing} onClick={onRefreshStorage} type="button"><RefreshCw className={storageRefreshing ? "spin" : ""} /></button>
      </div>
    </aside>
  );
}

function quickAccessHref(folder: MediaItem) {
  const route = folder.accessRole === "viewer" ? "/shared" : "/files";
  const query = new URLSearchParams({ folder: folder.id, name: folder.name });
  if (folder.systemRole) query.set("role", folder.systemRole);
  return `${route}?${query.toString()}`;
}
