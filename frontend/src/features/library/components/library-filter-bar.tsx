"use client";

import { Heart, RotateCcw, X } from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type {
  CatalogTag,
  FileDiscoveryScope,
  FileTypeFilter,
  ItemSort,
  LibraryView,
  PhotoScope,
  PhotoSort,
} from "@/lib/types";
import { TagFilterControl } from "./library-view-primitives";

export function LibraryFilterBar({
  view,
  normalizedQuery,
  parentId,
  photoScope,
  onPhotoScopeChange,
  mediaType,
  onMediaTypeChange,
  photoSort,
  onPhotoSortChange,
  photoYears,
  photoCaptureDate,
  photoYear,
  onPhotoYearChange,
  tags,
  selectedTagId,
  onSelectedTagChange,
  onManageTags,
  photosFavorite,
  onTogglePhotosFavorite,
  filesFavorite,
  onToggleFilesFavorite,
  fileDiscoveryActive,
  hasActivePhotoFilters,
  onResetPhotoFilters,
  fileType,
  onFileTypeChange,
  itemSort,
  onItemSortChange,
  effectiveFileSearchScope,
  onFileSearchScopeChange,
  searchIncludesPhotos,
  onSearchIncludesPhotosChange,
}: {
  view: LibraryView;
  normalizedQuery: string;
  parentId?: string;
  photoScope: PhotoScope;
  onPhotoScopeChange: (value: PhotoScope) => void;
  mediaType: string;
  onMediaTypeChange: (value: string) => void;
  photoSort: PhotoSort;
  onPhotoSortChange: (value: PhotoSort) => void;
  photoYears?: number[];
  photoCaptureDate?: string;
  photoYear?: number;
  onPhotoYearChange: (value?: number) => void;
  tags: CatalogTag[];
  selectedTagId: string;
  onSelectedTagChange: (value: string) => void;
  onManageTags: () => void;
  photosFavorite: boolean;
  onTogglePhotosFavorite: () => void;
  filesFavorite: boolean;
  onToggleFilesFavorite: () => void;
  fileDiscoveryActive: boolean;
  hasActivePhotoFilters: boolean;
  onResetPhotoFilters: () => void;
  fileType: FileTypeFilter;
  onFileTypeChange: (value: FileTypeFilter) => void;
  itemSort: ItemSort;
  onItemSortChange: (value: ItemSort) => void;
  effectiveFileSearchScope: FileDiscoveryScope;
  onFileSearchScopeChange: (value: FileDiscoveryScope) => void;
  searchIncludesPhotos: boolean;
  onSearchIncludesPhotosChange: (value: boolean) => void;
}) {
  const { t } = usePreferences();

  return (
    <>
      {view === "photos" && (
        <>
          {/^\d/.test(normalizedQuery) && (
            <p className="photo-search-hint">
              {t("library.captureDate")} <b>YYYY</b> · <b>YYYY-MM</b> ·{" "}
              <b>YYYY-MM-DD</b>
            </p>
          )}
          <div
            className="photo-filter-row"
            aria-label={t("library.photoFilters")}
          >
            <label className="filter-select">
              <span>{t("library.show")}</span>
              <select
                value={photoScope}
                onChange={(event) =>
                  onPhotoScopeChange(event.target.value as PhotoScope)
                }
              >
                <option value="folder">{t("library.photosFolder")}</option>
                <option value="all">{t("library.allMedia")}</option>
              </select>
            </label>
            <label className="filter-select">
              <span>{t("library.type")}</span>
              <select
                value={mediaType}
                onChange={(event) => onMediaTypeChange(event.target.value)}
              >
                <option value="all">{t("library.all")}</option>
                <option value="image">{t("library.photos")}</option>
                <option value="video">{t("library.videos")}</option>
              </select>
            </label>
            <label className="filter-select">
              <span>{t("library.sort")}</span>
              <select
                value={photoSort}
                onChange={(event) =>
                  onPhotoSortChange(event.target.value as PhotoSort)
                }
              >
                <option value="captured_desc">
                  {t("library.newestFirst")}
                </option>
                <option value="captured_asc">{t("library.oldestFirst")}</option>
                <option value="added_desc">{t("library.recentlyAdded")}</option>
              </select>
            </label>
            {photoYears && photoYears.length > 0 && (
              <label className="filter-select">
                <span>{t("library.year")}</span>
                <select
                  value={photoCaptureDate ? "" : (photoYear ?? "")}
                  disabled={Boolean(photoCaptureDate)}
                  onChange={(event) =>
                    onPhotoYearChange(
                      event.target.value
                        ? Number(event.target.value)
                        : undefined,
                    )
                  }
                >
                  <option value="">{t("library.allYears")}</option>
                  {photoYears.map((year) => (
                    <option key={year} value={year}>
                      {year}
                    </option>
                  ))}
                </select>
              </label>
            )}
            <TagFilterControl
              tags={tags}
              value={selectedTagId}
              onChange={onSelectedTagChange}
              onManage={onManageTags}
            />
            <button
              className={`favorite-filter ${photosFavorite ? "active" : ""}`}
              type="button"
              aria-pressed={photosFavorite}
              aria-label={t("library.showFavoritesOnly")}
              title={t("library.showFavoritesOnly")}
              onClick={onTogglePhotosFavorite}
            >
              <Heart fill={photosFavorite ? "currentColor" : "none"} />
              <span>{t("library.favorites")}</span>
            </button>
            {hasActivePhotoFilters && (
              <button
                className="filter-reset"
                type="button"
                onClick={onResetPhotoFilters}
              >
                <RotateCcw size={14} /> {t("library.reset")}
              </button>
            )}
          </div>
          {hasActivePhotoFilters && (
            <div
              className="active-filter-chips"
              aria-label={t("library.activeFilters")}
            >
              {photoScope !== "folder" && (
                <button
                  type="button"
                  onClick={() => onPhotoScopeChange("folder")}
                >
                  {t("library.show")}: {t("library.allMedia")} <X />
                </button>
              )}
              {mediaType !== "all" && (
                <button type="button" onClick={() => onMediaTypeChange("all")}>
                  {t("library.type")}: {photoTypeLabel(mediaType, t)} <X />
                </button>
              )}
              {photoSort !== "captured_desc" && (
                <button
                  type="button"
                  onClick={() => onPhotoSortChange("captured_desc")}
                >
                  {t("library.sort")}: {photoSortLabel(photoSort, t)} <X />
                </button>
              )}
              {photoYear !== undefined && !photoCaptureDate && (
                <button type="button" onClick={() => onPhotoYearChange()}>
                  {t("library.year")}: {photoYear} <X />
                </button>
              )}
              {photosFavorite && (
                <button type="button" onClick={onTogglePhotosFavorite}>
                  {t("library.favorites")} <X />
                </button>
              )}
              {selectedTagId && (
                <button type="button" onClick={() => onSelectedTagChange("")}>
                  {t("library.tag")}:{" "}
                  {tags.find((tag) => tag.id === selectedTagId)?.name ??
                    selectedTagId}{" "}
                  <X />
                </button>
              )}
            </div>
          )}
        </>
      )}
      {(view === "files" || view === "shared") && (
        <>
          <div className="file-sort-row">
            <label className="filter-select">
              <span>{t("library.type")}</span>
              <select
                value={fileType}
                onChange={(event) =>
                  onFileTypeChange(event.target.value as FileTypeFilter)
                }
              >
                <option value="all">{t("library.allItems")}</option>
                <option value="folder">{t("library.folders")}</option>
                <option value="document">{t("library.documents")}</option>
                <option value="pdf">PDFs</option>
                <option value="spreadsheet">{t("library.spreadsheets")}</option>
                <option value="image">{t("library.images")}</option>
                <option value="video">{t("library.videos")}</option>
              </select>
            </label>
            <label className="filter-select">
              <span>{t("library.sortBy")}</span>
              <select
                value={itemSort}
                onChange={(event) =>
                  onItemSortChange(event.target.value as ItemSort)
                }
              >
                <option value="name_asc">{t("library.nameAsc")}</option>
                <option value="name_desc">{t("library.nameDesc")}</option>
                <option value="modified_desc">
                  {t(
                    view === "files"
                      ? "sidebar.recent"
                      : "library.modifiedNewest",
                  )}
                </option>
                <option value="modified_asc">
                  {t("library.modifiedOldest")}
                </option>
                <option value="size_desc">{t("library.sizeLargest")}</option>
                <option value="size_asc">{t("library.sizeSmallest")}</option>
              </select>
            </label>
            {view === "files" && effectiveFileSearchScope !== "shared" && (
              <TagFilterControl
                tags={tags}
                value={selectedTagId}
                onChange={onSelectedTagChange}
                onManage={onManageTags}
              />
            )}
            {view === "files" && fileDiscoveryActive && (
              <div className="discovery-scope-control">
                <label className="filter-select">
                  <span>{t("library.lookIn")}</span>
                  <select
                    value={effectiveFileSearchScope}
                    onChange={(event) =>
                      onFileSearchScopeChange(
                        event.target.value as FileDiscoveryScope,
                      )
                    }
                  >
                    {parentId && (
                      <option value="subtree">{t("library.thisFolder")}</option>
                    )}
                    <option value="all">{t("library.allFiles")}</option>
                    {normalizedQuery.length >= 2 && (
                      <option value="shared">
                        {t("library.sharedWithMe")}
                      </option>
                    )}
                  </select>
                </label>
                {effectiveFileSearchScope === "subtree" && (
                  <small>{t("library.includesSubfolders")}</small>
                )}
              </div>
            )}
            {view === "files" && effectiveFileSearchScope !== "shared" && (
              <button
                className={`favorite-filter ${filesFavorite ? "active" : ""}`}
                type="button"
                aria-pressed={filesFavorite}
                aria-label={t("library.showFavoritesOnly")}
                title={t("library.showFavoritesOnly")}
                onClick={onToggleFilesFavorite}
              >
                <Heart fill={filesFavorite ? "currentColor" : "none"} />
                <span>{t("library.favorites")}</span>
              </button>
            )}
            {normalizedQuery.length < 2 && <p>{t("library.foldersFirst")}</p>}
          </div>
          {view === "files" &&
            fileDiscoveryActive &&
            effectiveFileSearchScope === "all" && (
              <details className="search-options">
                <summary>{t("library.advancedOptions")}</summary>
                <div>
                  <label className="search-checkbox">
                    <input
                      type="checkbox"
                      checked={searchIncludesPhotos}
                      onChange={(event) =>
                        onSearchIncludesPhotosChange(event.target.checked)
                      }
                    />
                    {t("library.includePhotos")}
                  </label>
                </div>
              </details>
            )}
          {view === "files" && effectiveFileSearchScope === "shared" && (
            <p className="search-scope-help">{t("library.sharedSearchHelp")}</p>
          )}
          {view === "files" &&
            (filesFavorite ||
              fileType !== "all" ||
              selectedTagId ||
              (fileDiscoveryActive &&
                (effectiveFileSearchScope !== "all" ||
                  searchIncludesPhotos))) && (
              <div
                className="active-filter-chips"
                aria-label={t("library.activeFilters")}
              >
                {filesFavorite && (
                  <button type="button" onClick={onToggleFilesFavorite}>
                    {t("library.favorites")} <X />
                  </button>
                )}
                {fileType !== "all" && (
                  <button type="button" onClick={() => onFileTypeChange("all")}>
                    {t("library.type")}: {fileTypeLabel(fileType, t)} <X />
                  </button>
                )}
                {selectedTagId && (
                  <button type="button" onClick={() => onSelectedTagChange("")}>
                    {t("library.tag")}:{" "}
                    {tags.find((tag) => tag.id === selectedTagId)?.name ??
                      selectedTagId}{" "}
                    <X />
                  </button>
                )}
                {view === "files" &&
                  fileDiscoveryActive &&
                  effectiveFileSearchScope !== "all" && (
                    <button
                      type="button"
                      onClick={() => onFileSearchScopeChange("all")}
                    >
                      {t("library.lookIn")}:{" "}
                      {effectiveFileSearchScope === "subtree"
                        ? t("library.thisFolder")
                        : t("library.sharedWithMe")}{" "}
                      <X />
                    </button>
                  )}
                {view === "files" &&
                  fileDiscoveryActive &&
                  searchIncludesPhotos && (
                    <button
                      type="button"
                      onClick={() => onSearchIncludesPhotosChange(false)}
                    >
                      {t("library.includePhotos")} <X />
                    </button>
                  )}
              </div>
            )}
        </>
      )}
      {view === "shared" && !parentId && (
        <p className="status-note">{t("library.sharedExplanation")}</p>
      )}
      {view === "trash" && (
        <p className="status-note">{t("library.trashExplanation")}</p>
      )}
    </>
  );
}

function fileTypeLabel(
  type: FileTypeFilter,
  t: (key: import("@/lib/translations").TranslationKey) => string,
) {
  if (type === "folder") return t("library.folders");
  if (type === "document") return t("library.documents");
  if (type === "spreadsheet") return t("library.spreadsheets");
  if (type === "image") return t("library.images");
  if (type === "video") return t("library.videos");
  return type === "pdf" ? "PDF" : t("library.allItems");
}

function photoTypeLabel(
  type: string,
  t: (key: import("@/lib/translations").TranslationKey) => string,
) {
  if (type === "image") return t("library.photos");
  if (type === "video") return t("library.videos");
  return t("library.all");
}

function photoSortLabel(
  sort: PhotoSort,
  t: (key: import("@/lib/translations").TranslationKey) => string,
) {
  if (sort === "captured_asc") return t("library.oldestFirst");
  if (sort === "added_desc") return t("library.recentlyAdded");
  return t("library.newestFirst");
}
