"use client";

import { ChevronRight, FolderRoot } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function LibraryBreadcrumbs({
  view,
  folders,
  onRoot,
  onFolder,
}: {
  view: "files" | "shared";
  folders: MediaItem[];
  onRoot: () => void;
  onFolder: (folder: MediaItem) => void;
}) {
  const { t } = usePreferences();
  const visibleFolders = folders.filter(
    (folder) => view !== "files" || folder.systemRole !== "documents_root",
  );
  return (
    <nav className="folder-breadcrumbs" aria-label={t("files.breadcrumbs")}>
      <button
        type="button"
        onClick={onRoot}
        title={view === "files" ? t("library.files") : t("library.shared")}
      >
        <FolderRoot />
        <span>
          {view === "files" ? t("library.files") : t("library.shared")}
        </span>
      </button>
      {visibleFolders.map((folder, index) => (
        <span className="folder-breadcrumb-segment" key={folder.id}>
          <ChevronRight aria-hidden="true" />
          <button
            type="button"
            className={
              index === visibleFolders.length - 1 ? "current" : undefined
            }
            aria-current={
              index === visibleFolders.length - 1 ? "page" : undefined
            }
            onClick={() => onFolder(folder)}
            title={folder.name}
          >
            {folder.name}
          </button>
        </span>
      ))}
    </nav>
  );
}
