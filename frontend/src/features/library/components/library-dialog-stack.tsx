"use client";

import type { Dispatch, SetStateAction } from "react";
import { AddToAlbumDialog } from "@/features/albums/album-dialogs";
import { CatalogItemContextMenu } from "@/features/catalog/components/catalog-item-context-menu";
import { CatalogKeyboardShortcutsDialog } from "@/features/catalog/components/catalog-keyboard-shortcuts-dialog";
import type { CatalogContextMenuState } from "@/features/catalog/use-catalog-context-menu";
import type { getTrashSummary } from "@/lib/api";
import { usePreferences } from "@/lib/preferences";
import type { CatalogTag, LibraryView, MediaItem, TimelineEvent } from "@/lib/types";
import type { OperationNotification } from "../use-operation-notifications";
import type { PendingTrash } from "../use-library-trash-actions";
import { CaptureDateDialog } from "./capture-date-dialog";
import { ClearTrashDialog } from "./clear-trash-dialog";
import { OperationNotifications } from "./operation-notifications";
import { RenameItemDialog } from "./rename-item-dialog";
import { ShareDialog } from "./share-dialog";
import { TagDialog } from "./tag-dialog";
import { TagManagementDialog } from "./tag-management-dialog";
import { TrashPreviewDialog } from "./trash-preview-dialog";
import { FolderDialog, UploadDialog } from "./upload-dialog";
import { TimelineEventDialog, type TimelineEventValues } from "./timeline-event-dialog";

export type LibraryDialogName = "upload" | "folder" | "clear-trash" | "event" | null;

export type LibraryToastMessage = {
  message: string;
  actionLabel?: string;
  action?: () => Promise<void> | void;
};

type TrashSummary = Awaited<ReturnType<typeof getTrashSummary>>;

export function LibraryDialogStack({
  view,
  busy,
  uploadProgress,
  tags,
  trashSummary,
  sharingItems,
  setSharingItems,
  albumItems,
  setAlbumItems,
  tagItems,
  setTagItems,
  tagsManaging,
  setTagsManaging,
  captureDateItems,
  setCaptureDateItems,
  renamingItem,
  setRenamingItem,
  pendingTrash,
  setPendingTrash,
  dialog,
  setDialog,
  contextMenu,
  setContextMenu,
  shortcutsOpen,
  setShortcutsOpen,
  notificationsOpen,
  setNotificationsOpen,
  notifications,
  onDismissNotification,
  onClearNotifications,
  toast,
  onToastAction,
  onCreateTag,
  onSetTag,
  onUpdateTag,
  onDeleteTag,
  onSaveCaptureDate,
  onRename,
  onConfirmTrash,
  onChooseFiles,
  onCreateFolder,
  onClearTrash,
  onOpenContextItem,
  contextItemHref,
  onDownload,
  onFavorite,
  onCopy,
  onDetails,
  onShare,
  onRequestTrash,
  onRestore,
  editingTimelineEvent,
  setEditingTimelineEvent,
  onSaveTimelineEvent,
  onDeleteTimelineEvent,
}: {
  view: LibraryView;
  busy: boolean;
  uploadProgress: string;
  tags: CatalogTag[];
  trashSummary?: TrashSummary;
  sharingItems: MediaItem[];
  setSharingItems: Dispatch<SetStateAction<MediaItem[]>>;
  albumItems: MediaItem[];
  setAlbumItems: Dispatch<SetStateAction<MediaItem[]>>;
  tagItems: MediaItem[];
  setTagItems: Dispatch<SetStateAction<MediaItem[]>>;
  tagsManaging: boolean;
  setTagsManaging: Dispatch<SetStateAction<boolean>>;
  captureDateItems: MediaItem[];
  setCaptureDateItems: Dispatch<SetStateAction<MediaItem[]>>;
  renamingItem: MediaItem | null;
  setRenamingItem: Dispatch<SetStateAction<MediaItem | null>>;
  pendingTrash: PendingTrash | null;
  setPendingTrash: Dispatch<SetStateAction<PendingTrash | null>>;
  dialog: LibraryDialogName;
  setDialog: Dispatch<SetStateAction<LibraryDialogName>>;
  contextMenu: CatalogContextMenuState | null;
  setContextMenu: Dispatch<SetStateAction<CatalogContextMenuState | null>>;
  shortcutsOpen: boolean;
  setShortcutsOpen: Dispatch<SetStateAction<boolean>>;
  notificationsOpen: boolean;
  setNotificationsOpen: Dispatch<SetStateAction<boolean>>;
  notifications: OperationNotification[];
  onDismissNotification: (id: string) => void;
  onClearNotifications: () => void;
  toast: LibraryToastMessage | null;
  onToastAction: () => void;
  onCreateTag: (name: string, color: CatalogTag["color"]) => Promise<void>;
  onSetTag: (tagId: string, included: boolean) => Promise<void>;
  onUpdateTag: (
    tag: CatalogTag,
    name: string,
    color: CatalogTag["color"],
  ) => Promise<void>;
  onDeleteTag: (tag: CatalogTag) => Promise<void>;
  onSaveCaptureDate: (capturedOn?: string, reset?: boolean) => Promise<void>;
  onRename: (item: MediaItem, name: string) => Promise<boolean>;
  onConfirmTrash: () => Promise<void>;
  onChooseFiles: (files: FileList) => Promise<void>;
  onCreateFolder: (name: string) => Promise<void>;
  onClearTrash: (confirmationPhrase: string) => Promise<void>;
  onOpenContextItem: (item: MediaItem) => void;
  contextItemHref: (item: MediaItem) => string;
  onDownload: (items: MediaItem[]) => void;
  onFavorite: (item: MediaItem) => void;
  onCopy: (item: MediaItem) => void;
  onDetails: (item: MediaItem) => void;
  onShare: (items: MediaItem[]) => void;
  onRequestTrash: (item: MediaItem) => void;
  onRestore: (item: MediaItem) => void;
  editingTimelineEvent: TimelineEvent | null;
  setEditingTimelineEvent: Dispatch<SetStateAction<TimelineEvent | null>>;
  onSaveTimelineEvent: (values: TimelineEventValues) => Promise<void>;
  onDeleteTimelineEvent: () => Promise<void>;
}) {
  const { t } = usePreferences();

  return (
    <>
      {sharingItems.length > 0 && (
        <ShareDialog items={sharingItems} onClose={() => setSharingItems([])} />
      )}
      {albumItems.length > 0 && (
        <AddToAlbumDialog
          items={albumItems}
          onClose={() => setAlbumItems([])}
        />
      )}
      {tagItems.length > 0 && (
        <TagDialog
          items={tagItems}
          tags={tags}
          busy={busy}
          onCreate={onCreateTag}
          onSet={onSetTag}
          onClose={() => setTagItems([])}
        />
      )}
      {tagsManaging && (
        <TagManagementDialog
          tags={tags}
          busy={busy}
          onUpdate={onUpdateTag}
          onDelete={onDeleteTag}
          onClose={() => setTagsManaging(false)}
        />
      )}
      {captureDateItems.length > 0 && (
        <CaptureDateDialog
          items={captureDateItems}
          busy={busy}
          onSave={(capturedOn) => onSaveCaptureDate(capturedOn)}
          onReset={() => onSaveCaptureDate(undefined, true)}
          onClose={() => setCaptureDateItems([])}
        />
      )}
      {renamingItem && (
        <RenameItemDialog
          item={renamingItem}
          busy={busy}
          onSave={(name) => onRename(renamingItem, name)}
          onClose={() => setRenamingItem(null)}
        />
      )}
      {pendingTrash && (
        <TrashPreviewDialog
          items={pendingTrash.items}
          busy={busy}
          onConfirm={onConfirmTrash}
          onClose={() => setPendingTrash(null)}
        />
      )}
      {dialog === "upload" && (
        <UploadDialog
          view={view}
          uploading={busy}
          progress={uploadProgress}
          onFiles={(files) => void onChooseFiles(files)}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === "folder" && (
        <FolderDialog
          busy={busy}
          onCreate={(name) => void onCreateFolder(name)}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === "clear-trash" && (
        <ClearTrashDialog
          busy={busy}
          fileCount={trashSummary?.fileCount ?? 0}
          folderCount={trashSummary?.folderCount ?? 0}
          originalCount={trashSummary?.originalCount ?? 0}
          originalBytes={trashSummary?.originalBytes ?? 0}
          onClear={(phrase) => void onClearTrash(phrase)}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === "event" && (
        <TimelineEventDialog
          event={editingTimelineEvent}
          busy={busy}
          onSave={onSaveTimelineEvent}
          onDelete={editingTimelineEvent ? onDeleteTimelineEvent : undefined}
          onClose={() => {
            setEditingTimelineEvent(null);
            setDialog(null);
          }}
        />
      )}
      {contextMenu && (
        <CatalogItemContextMenu
          state={contextMenu}
          busy={busy}
          onClose={() => setContextMenu(null)}
          onOpen={() => onOpenContextItem(contextMenu.item)}
          openInNewTabHref={contextItemHref(contextMenu.item)}
          onDownload={() => onDownload([contextMenu.item])}
          onFavorite={() => onFavorite(contextMenu.item)}
          onCopy={view === "files" ? () => onCopy(contextMenu.item) : undefined}
          onDetails={() => onDetails(contextMenu.item)}
          onRename={() => setRenamingItem(contextMenu.item)}
          onShare={() => onShare([contextMenu.item])}
          onTrash={() => onRequestTrash(contextMenu.item)}
          onRestore={
            contextMenu.item.trashedAt
              ? () => onRestore(contextMenu.item)
              : undefined
          }
        />
      )}
      {shortcutsOpen && (
        <CatalogKeyboardShortcutsDialog
          onClose={() => setShortcutsOpen(false)}
        />
      )}
      <OperationNotifications
        open={notificationsOpen}
        notifications={notifications}
        onClose={() => setNotificationsOpen(false)}
        onDismiss={onDismissNotification}
        onClearCompleted={onClearNotifications}
      />
      {toast && (
        <div className="toast" role="status">
          <span>{toast.message}</span>
          {toast.action && (
            <button type="button" onClick={onToastAction}>
              {toast.actionLabel ?? t("files.undo")}
            </button>
          )}
        </div>
      )}
    </>
  );
}
