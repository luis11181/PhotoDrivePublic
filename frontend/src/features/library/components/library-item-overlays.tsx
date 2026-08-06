"use client";

import type { LibraryView, MediaItem } from "@/lib/types";
import type { TrashSource } from "../use-library-trash-actions";
import { ItemDetails } from "./item-details";
import { DocumentPreview } from "./document-preview";
import { PhotoViewer } from "./photo-viewer";
import { PreviewRouteState } from "./preview-route-state";
import { isPreviewable } from "../preview-support";

type ItemActions = {
  onRename: (item: MediaItem, name: string) => void;
  onFavorite: (item: MediaItem) => void;
  onViewerFavorite: (item: MediaItem) => void;
  onMove: (item: MediaItem, destination?: string) => void;
  onTrash: (item: MediaItem, source: TrashSource) => void;
  onRestore: (item: MediaItem) => void;
  onRetryMetadata: (item: MediaItem) => void;
  onCorrectCaptureDate: (items: MediaItem[]) => void;
  onShare: (items: MediaItem[]) => void;
  onRemoveTag: (item: MediaItem, tagId: string) => void;
  onCopy: (item: MediaItem) => void;
  onDownload: (item: MediaItem) => void;
};

export function LibraryItemOverlays({
  view,
  busy,
  details,
  folderOptions,
  onCloseDetails,
  onPreviewDetails,
  viewer,
  documentPreview,
  previewRoute,
  actions,
}: {
  view: LibraryView;
  busy: boolean;
  details: MediaItem | null;
  folderOptions: MediaItem[];
  onCloseDetails: () => void;
  onPreviewDetails: (item: MediaItem) => void;
  viewer: {
    item: MediaItem | null;
    previousItem?: MediaItem;
    nextItem?: MediaItem;
    hasMore: boolean;
    position: number;
    loadedCount: number;
    detailsOpen: boolean;
    onClose: () => void;
    onPrevious: () => void;
    onNext: () => void;
    onToggleDetails: () => void;
    onCloseDetails: () => void;
    onRename: (item: MediaItem) => void;
  };
  documentPreview: MediaItem | null;
  previewRoute: {
    pending: boolean;
    onRetry?: () => void;
  } | null;
  actions: ItemActions;
}) {
  return (
    <>
      {details && (
        <ItemDetails
          item={details}
          busy={busy}
          onClose={onCloseDetails}
          onRename={(name) => name && actions.onRename(details, name)}
          onFavorite={() => actions.onFavorite(details)}
          folderOptions={folderOptions}
          onMove={(destination) => actions.onMove(details, destination)}
          onTrash={() => actions.onTrash(details, "details")}
          onRestore={() => actions.onRestore(details)}
          onRetryMetadata={() => actions.onRetryMetadata(details)}
          onCorrectCaptureDate={() => actions.onCorrectCaptureDate([details])}
          onShare={() => actions.onShare([details])}
          onRemoveTag={removeTagHandler(details, actions.onRemoveTag)}
          showMoveAction={view === "files"}
          compactFileActions={view === "files"}
          onCopy={() => actions.onCopy(details)}
          onDownload={() => actions.onDownload(details)}
          onPreview={
            isPreviewable(details) ? () => onPreviewDetails(details) : undefined
          }
        />
      )}
      {documentPreview && (
        <DocumentPreview item={documentPreview} onClose={viewer.onClose} />
      )}
      {previewRoute && (
        <PreviewRouteState
          pending={previewRoute.pending}
          onClose={viewer.onClose}
          onRetry={previewRoute.onRetry}
        />
      )}
      {viewer.item && (
        <PhotoViewer
          key={viewer.item.id}
          item={viewer.item}
          previousItem={viewer.previousItem}
          nextItem={viewer.nextItem}
          hasMore={viewer.hasMore}
          position={viewer.position}
          loadedCount={viewer.loadedCount}
          onClose={viewer.onClose}
          onPrevious={viewer.onPrevious}
          onNext={viewer.onNext}
          detailsOpen={viewer.detailsOpen}
          onShowDetails={viewer.onToggleDetails}
          onFavorite={() => actions.onViewerFavorite(viewer.item!)}
          onShare={() => actions.onShare([viewer.item!])}
          onRename={() => viewer.onRename(viewer.item!)}
          onTrash={() => actions.onTrash(viewer.item!, "viewer")}
          detailsPanel={
            viewer.detailsOpen ? (
              <ItemDetails
                mode="viewer-info"
                item={viewer.item}
                busy={busy}
                onClose={viewer.onCloseDetails}
                onRename={() => undefined}
                onFavorite={() => undefined}
                onMove={() => undefined}
                folderOptions={[]}
                onTrash={() => undefined}
                onRestore={() => undefined}
                onRetryMetadata={() => actions.onRetryMetadata(viewer.item!)}
                onCorrectCaptureDate={() =>
                  actions.onCorrectCaptureDate([viewer.item!])
                }
                onShare={() => undefined}
                onRemoveTag={removeTagHandler(viewer.item, actions.onRemoveTag)}
                showMoveAction={false}
              />
            ) : undefined
          }
        />
      )}
    </>
  );
}

function removeTagHandler(
  item: MediaItem,
  onRemoveTag: (item: MediaItem, tagId: string) => void,
) {
  return item.accessRole !== "viewer" && !item.trashedAt && !item.systemRole
    ? (tagId: string) => onRemoveTag(item, tagId)
    : undefined;
}
