"use client";

import { Camera, FolderOpen, FolderPlus, Upload, X } from "lucide-react";
import { useRef } from "react";
import { usePreferences } from "@/lib/preferences";
import type { LibraryView } from "@/lib/types";

export function UploadDialog({
  view,
  uploading,
  progress,
  onFiles,
  onClose,
  albumName,
}: {
  view: LibraryView;
  uploading: boolean;
  progress: string;
  onFiles: (files: FileList) => void;
  onClose: () => void;
  albumName?: string;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const cameraInputRef = useRef<HTMLInputElement>(null);
  const { t } = usePreferences();
  const photos = view === "photos";
  return (
    <div className="modal-backdrop" onMouseDown={() => !uploading && onClose()}>
      <section
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="upload-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <Upload />
        </div>
        <h2 id="upload-title">
          {albumName
            ? t("upload.albumTitle", { name: albumName })
            : photos
              ? t("upload.uploadPhotos")
              : t("upload.uploadFiles")}
        </h2>
        <p className="modal-copy">
          {albumName
            ? t("upload.albumHelp")
            : photos
              ? t("upload.photosExplanation")
              : t("upload.filesExplanation")}
        </p>
        <input
          ref={inputRef}
          className="visually-hidden"
          type="file"
          multiple
          accept={photos ? "image/*,video/*" : undefined}
          onChange={(event) => {
            const files = event.target.files;
            if (files?.length) onFiles(files);
            event.currentTarget.value = "";
          }}
        />
        {!photos && (
          <input
            ref={folderInputRef}
            className="visually-hidden"
            type="file"
            multiple
            {...({
              webkitdirectory: "",
              directory: "",
            } as React.InputHTMLAttributes<HTMLInputElement>)}
            onChange={(event) => {
              const files = event.target.files;
              if (files?.length) onFiles(files);
              event.currentTarget.value = "";
            }}
          />
        )}
        {photos && (
          <input
            ref={cameraInputRef}
            className="visually-hidden"
            type="file"
            accept="image/*,video/*"
            capture="environment"
            onChange={(event) => {
              const files = event.target.files;
              if (files?.length) onFiles(files);
              event.currentTarget.value = "";
            }}
          />
        )}
        <button
          className="drop-zone"
          disabled={uploading}
          onClick={() => inputRef.current?.click()}
        >
          <Upload />
          <strong>
            {uploading
              ? t("upload.uploading")
              : photos
                ? t("upload.chooseMedia")
                : t("upload.chooseFiles")}
          </strong>
          <span>{progress || t("upload.multipleSupported")}</span>
        </button>
        {!photos && (
          <button
            className="secondary upload-camera-action"
            type="button"
            disabled={uploading}
            onClick={() => folderInputRef.current?.click()}
          >
            <FolderOpen /> {t("upload.chooseFolder")}
          </button>
        )}
        {photos && (
          <button
            className="secondary upload-camera-action"
            type="button"
            disabled={uploading}
            onClick={() => cameraInputRef.current?.click()}
          >
            <Camera /> {t("upload.takePhoto")}
          </button>
        )}
        <p className="upload-durable-copy">{t("upload.durableSelection")}</p>
        <button
          className="icon-button modal-close"
          disabled={uploading}
          onClick={onClose}
          aria-label={t("common.close")}
        >
          <X />
        </button>
      </section>
    </div>
  );
}

export function UploadNameConflictDialog({
  names,
  fileCount,
  nonConflictingCount,
  onCancel,
  onUploadNonConflicting,
  onKeepBoth,
}: {
  names: string[];
  fileCount: number;
  nonConflictingCount: number;
  onCancel: () => void;
  onUploadNonConflicting: () => void;
  onKeepBoth: () => void;
}) {
  const { t } = usePreferences();
  const displayedNames = names.slice(0, 3);
  const remaining = names.length - displayedNames.length;
  return (
    <div className="modal-backdrop" onMouseDown={onCancel}>
      <section
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="upload-name-conflict-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <Upload />
        </div>
        <h2 id="upload-name-conflict-title">{t("upload.nameConflict")}</h2>
        <p className="modal-copy">
          {fileCount === 1
            ? t("upload.singleConflict")
            : t("upload.multipleConflict")}
        </p>
        <ul className="status-note compact">
          {displayedNames.map((name) => (
            <li key={name}>{name}</li>
          ))}
          {remaining > 0 && (
            <li>{t("upload.andMore", { count: remaining })}</li>
          )}
        </ul>
        <p className="modal-copy">{t("upload.keepBothExplanation")}</p>
        <div className="modal-actions upload-conflict-actions">
          <button className="secondary" type="button" onClick={onCancel}>
            {t("common.cancel")}
          </button>
          {fileCount > 1 && nonConflictingCount > 0 && (
            <button
              className="secondary upload-non-conflicting-action"
              type="button"
              onClick={onUploadNonConflicting}
            >
              {t("upload.nonConflicting", {
                count: nonConflictingCount,
                items:
                  nonConflictingCount === 1
                    ? t("upload.item")
                    : t("upload.items"),
              })}
            </button>
          )}
          <button className="primary" type="button" onClick={onKeepBoth}>
            {t("upload.keepBoth")}
          </button>
        </div>
      </section>
    </div>
  );
}

export function FolderDialog({
  busy,
  onCreate,
  onClose,
}: {
  busy: boolean;
  onCreate: (name: string) => void;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  return (
    <div className="modal-backdrop" onMouseDown={onClose}>
      <form
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="folder-title"
        onMouseDown={(event) => event.stopPropagation()}
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          onCreate(String(data.get("name") ?? "").trim());
        }}
      >
        <div className="modal-icon">
          <FolderPlus />
        </div>
        <h2 id="folder-title">{t("upload.createFolder")}</h2>
        <label className="field">
          {t("upload.folderName")}
          <input
            autoFocus
            name="name"
            maxLength={255}
            required
            placeholder={t("upload.folderPlaceholder")}
          />
        </label>
        <div className="modal-actions">
          <button className="secondary" type="button" onClick={onClose}>
            {t("common.cancel")}
          </button>
          <button className="primary" disabled={busy} type="submit">
            {busy ? t("upload.creating") : t("upload.createFolder")}
          </button>
        </div>
      </form>
    </div>
  );
}
