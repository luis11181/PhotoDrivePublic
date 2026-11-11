"use client";

/* eslint-disable @next/next/no-img-element -- the picker uses authenticated backend thumbnails. */

import {
  useInfiniteQuery,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import {
  Album as AlbumIcon,
  Check,
  ImagePlus,
  Share2,
  UserMinus,
  X,
} from "lucide-react";
import { FormEvent, useMemo, useState } from "react";
import {
  createAlbum,
  listAlbums,
  listAlbumShares,
  listPhotos,
  listShareRecipients,
  revokeAlbumShare,
  setAlbumItems,
  shareAlbum,
  thumbnailUrl,
  updateAlbum,
} from "@/lib/api";
import type { Album, AlbumShareRole, MediaItem } from "@/lib/types";
import { usePreferences } from "@/lib/preferences";

export function AlbumEditorDialog({
  album,
  onClose,
}: {
  album?: Album;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const [name, setName] = useState(album?.name ?? "");
  const [description, setDescription] = useState(album?.description ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;
    setBusy(true);
    setError("");
    try {
      if (album)
        await updateAlbum(album.id, {
          name,
          description: description || undefined,
        });
      else await createAlbum({ name, description: description || undefined });
      await queryClient.invalidateQueries({ queryKey: ["albums"] });
      onClose();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : t("albums.saveFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" onMouseDown={() => !busy && onClose()}>
      <form
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="album-editor-title"
        onSubmit={submit}
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <AlbumIcon />
        </div>
        <h2 id="album-editor-title">
          {album ? t("albums.editTitle") : t("albums.new")}
        </h2>
        <label className="field">
          {t("albums.name")}
          <input
            autoFocus
            maxLength={200}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </label>
        <label className="field album-description-field">
          {t("albums.description")} <span>{t("albums.optional")}</span>
          <textarea
            maxLength={2000}
            rows={4}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </label>
        {error && <p className="status-note compact metadata-error">{error}</p>}
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
            disabled={busy || !name.trim()}
          >
            {busy ? t("albums.saving") : t("albums.save")}
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

export function AlbumShareDialog({
  album,
  onClose,
}: {
  album: Album;
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const [recipientId, setRecipientId] = useState("");
  const [role, setRole] = useState<AlbumShareRole>("viewer");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const recipients = useQuery({
    queryKey: ["share-recipients"],
    queryFn: listShareRecipients,
    staleTime: 60_000,
  });
  const shares = useQuery({
    queryKey: ["album-shares", album.id],
    queryFn: () => listAlbumShares(album.id),
  });
  const sharedIds = useMemo(
    () => new Set(shares.data?.map((share) => share.recipientUserId) ?? []),
    [shares.data],
  );

  async function grant() {
    if (!recipientId) return;
    setBusy(true);
    setError("");
    try {
      await shareAlbum(album.id, recipientId, role);
      setRecipientId("");
      await Promise.all([
        shares.refetch(),
        queryClient.invalidateQueries({ queryKey: ["albums"] }),
      ]);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : t("albums.shareFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function changeRole(recipientUserId: string, nextRole: AlbumShareRole) {
    setBusy(true);
    setError("");
    try {
      await shareAlbum(album.id, recipientUserId, nextRole);
      await Promise.all([
        shares.refetch(),
        queryClient.invalidateQueries({ queryKey: ["albums"] }),
      ]);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : t("albums.shareFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function revoke(recipientUserId: string) {
    if (!window.confirm(t("albums.revokeConfirm"))) return;
    setBusy(true);
    setError("");
    try {
      await revokeAlbumShare(album.id, recipientUserId);
      await Promise.all([
        shares.refetch(),
        queryClient.invalidateQueries({ queryKey: ["albums"] }),
      ]);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : t("albums.revokeFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" onMouseDown={() => !busy && onClose()}>
      <section
        className="modal share-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="album-share-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <Share2 />
        </div>
        <h2 id="album-share-title">
          {t("albums.shareTitle", { name: album.name })}
        </h2>
        <p className="modal-copy">{t("albums.shareHelp")}</p>
        <div className="album-sharing-summary">
          <strong>{t("albums.privateSharing")}</strong>
          <span>{t("albums.privateSharingHelp")}</span>
        </div>
        <label className="share-recipient-control">
          {t("albums.familyMember")}
          <select
            value={recipientId}
            disabled={busy || recipients.isPending}
            onChange={(event) => setRecipientId(event.target.value)}
          >
            <option value="">{t("albums.choosePerson")}</option>
            {recipients.data
              ?.filter((recipient) => !sharedIds.has(recipient.userId))
              .map((recipient) => (
                <option key={recipient.userId} value={recipient.userId}>
                  {recipient.displayName} · {recipient.email}
                </option>
              ))}
          </select>
        </label>
        <label className="share-recipient-control">
          {t("albums.accessRole")}
          <select
            value={role}
            disabled={busy}
            onChange={(event) => setRole(event.target.value as AlbumShareRole)}
          >
            <option value="viewer">{t("albums.viewer")}</option>
            <option value="collaborator">{t("albums.collaborator")}</option>
          </select>
        </label>
        <button
          className="primary full"
          type="button"
          disabled={busy || !recipientId}
          onClick={() => void grant()}
        >
          <Share2 size={17} />{" "}
          {t(
            role === "collaborator"
              ? "albums.shareCollaborator"
              : "albums.shareViewer",
          )}
        </button>
        <div className="share-access-list">
          <strong>
            {t("albums.peopleAccessCount", { count: shares.data?.length ?? 0 })}
          </strong>
          <div className="album-owner-access">
            <span>
              <b>{t("albums.you")}</b>
              <small>{t("albums.ownerHelp")}</small>
            </span>
            <span className="share-role">{t("albums.owner")}</span>
          </div>
          {shares.isPending ? (
            <p>{t("albums.loadingAccess")}</p>
          ) : shares.data?.length ? (
            shares.data.map((share) => (
              <div key={share.recipientUserId}>
                <span>
                  <b>{share.recipientDisplayName}</b>
                  <small>{share.recipientEmail}</small>
                </span>
                <select
                  className="share-role"
                  value={share.role}
                  disabled={busy}
                  aria-label={t("albums.accessFor", {
                    name: share.recipientDisplayName,
                  })}
                  onChange={(event) =>
                    void changeRole(
                      share.recipientUserId,
                      event.target.value as AlbumShareRole,
                    )
                  }
                >
                  <option value="viewer">{t("albums.viewer")}</option>
                  <option value="collaborator">
                    {t("albums.collaborator")}
                  </option>
                </select>
                <button
                  className="icon-button"
                  type="button"
                  disabled={busy}
                  onClick={() => void revoke(share.recipientUserId)}
                  aria-label={t("albums.removeAccess", {
                    name: share.recipientDisplayName,
                  })}
                >
                  <UserMinus />
                </button>
              </div>
            ))
          ) : (
            <p>{t("albums.onlyYou")}</p>
          )}
        </div>
        {(error || recipients.error || shares.error) && (
          <p className="status-note compact metadata-error">
            {error || recipients.error?.message || shares.error?.message}
          </p>
        )}
        <button
          className="icon-button modal-close"
          disabled={busy}
          onClick={onClose}
          aria-label={t("common.close")}
        >
          <X />
        </button>
      </section>
    </div>
  );
}

export function AddPhotosToAlbumDialog({
  album,
  existingIds,
  onClose,
  onAdded,
  onBrowsePhotos,
}: {
  album: Album;
  existingIds: string[];
  onClose: () => void;
  onAdded: (count: number, unchanged: number) => void;
  onBrowsePhotos: () => void;
}) {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const existing = useMemo(() => new Set(existingIds), [existingIds]);
  const photos = useInfiniteQuery({
    queryKey: ["album-photo-picker"],
    queryFn: ({ pageParam, signal }) =>
      listPhotos({
        cursor: pageParam,
        signal,
        scope: "all",
        type: "all",
        sort: "captured_desc",
      }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor,
  });
  const items = useMemo(() => {
    const unique = new Map<string, MediaItem>();
    for (const item of photos.data?.pages.flatMap((page) => page.items) ?? [])
      unique.set(item.id, item);
    return [...unique.values()];
  }, [photos.data]);

  function toggle(itemId: string) {
    if (existing.has(itemId) || busy) return;
    setSelectedIds((current) =>
      current.includes(itemId)
        ? current.filter((id) => id !== itemId)
        : [...current, itemId],
    );
  }

  async function add() {
    if (!selectedIds.length) return;
    setBusy(true);
    setError("");
    try {
      const result = await setAlbumItems(album.id, selectedIds, true);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["albums"] }),
        queryClient.invalidateQueries({
          queryKey: ["albums", "detail", album.id],
        }),
      ]);
      if (result.failedCount) {
        setError(
          t("albums.pickerPartial", {
            added: result.succeededCount,
            failed: result.failedCount,
          }),
        );
        setSelectedIds(
          result.results
            .filter((item) => item.status === "failed")
            .map((item) => item.itemId),
        );
        return;
      }
      onAdded(result.succeededCount, result.unchangedCount);
      onClose();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : t("albums.addRemainingFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" onMouseDown={() => !busy && onClose()}>
      <section
        className="modal album-photo-picker-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="album-photo-picker-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <ImagePlus />
        </div>
        <h2 id="album-photo-picker-title">
          {t("albums.pickerTitle", { name: album.name })}
        </h2>
        <p className="modal-copy">{t("albums.pickerHelp")}</p>
        {photos.isPending ? (
          <p className="album-picker-state">{t("library.loading")}</p>
        ) : photos.isError ? (
          <p className="status-note compact metadata-error">
            {photos.error.message}
          </p>
        ) : items.length ? (
          <>
            <div className="album-photo-picker-grid">
              {items.map((item) => {
                const alreadyAdded = existing.has(item.id);
                const selected = selectedIds.includes(item.id);
                return (
                  <button
                    key={item.id}
                    type="button"
                    className={selected ? "selected" : ""}
                    disabled={alreadyAdded || busy}
                    aria-pressed={selected}
                    aria-label={
                      alreadyAdded
                        ? t("albums.alreadyAdded", { name: item.name })
                        : item.name
                    }
                    onClick={() => toggle(item.id)}
                  >
                    <img
                      src={thumbnailUrl(item.id, "sm")}
                      crossOrigin="use-credentials"
                      alt=""
                    />
                    {(selected || alreadyAdded) && (
                      <span className="album-picker-check">
                        <Check />
                      </span>
                    )}
                    {alreadyAdded && <small>{t("albums.inAlbum")}</small>}
                  </button>
                );
              })}
            </div>
            {photos.hasNextPage && (
              <button
                className="secondary full album-picker-load-more"
                type="button"
                disabled={photos.isFetchingNextPage}
                onClick={() => void photos.fetchNextPage()}
              >
                {photos.isFetchingNextPage
                  ? t("library.loadingMore")
                  : t("library.loadMore")}
              </button>
            )}
          </>
        ) : (
          <p className="album-picker-state">{t("albums.noPhotos")}</p>
        )}
        <button
          className="album-browse-photos"
          type="button"
          disabled={busy}
          onClick={onBrowsePhotos}
        >
          {t("albums.selectInPhotos")}
        </button>
        {error && <p className="status-note compact metadata-error">{error}</p>}
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
            type="button"
            disabled={busy || !selectedIds.length}
            onClick={() => void add()}
          >
            {busy
              ? t("albums.adding")
              : t("albums.addSelected", { count: selectedIds.length })}
          </button>
        </div>
        <button
          className="icon-button modal-close"
          disabled={busy}
          onClick={onClose}
          aria-label={t("common.close")}
        >
          <X />
        </button>
      </section>
    </div>
  );
}

export function AddToAlbumDialog({
  items,
  onClose,
}: {
  items: MediaItem[];
  onClose: () => void;
}) {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const [albumId, setAlbumId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const albums = useQuery({
    queryKey: ["albums", "editable"],
    queryFn: async () =>
      (await listAlbums("all")).filter(
        (album) =>
          album.accessRole === "owner" || album.accessRole === "collaborator",
      ),
  });

  async function add() {
    if (!albumId) return;
    setBusy(true);
    setError("");
    try {
      const result = await setAlbumItems(
        albumId,
        items.map((item) => item.id),
        true,
      );
      await queryClient.invalidateQueries({ queryKey: ["albums"] });
      if (result.failedCount)
        setError(
          t("albums.addPartial", {
            count: result.succeededCount,
            error: t("albums.addRemainingFailed"),
          }),
        );
      else onClose();
    } catch (reason) {
      setError(
        t("albums.addPartial", {
          count: 0,
          error:
            reason instanceof Error
              ? reason.message
              : t("albums.addRemainingFailed"),
        }),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" onMouseDown={() => !busy && onClose()}>
      <section
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="add-album-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon">
          <AlbumIcon />
        </div>
        <h2 id="add-album-title">{t("albums.addTitle")}</h2>
        <p className="modal-copy">
          {t("albums.addHelp", {
            count: items.length,
            items: t(items.length === 1 ? "albums.item" : "albums.items"),
          })}
        </p>
        <label className="share-recipient-control">
          {t("albums.album")}
          <select
            value={albumId}
            disabled={busy || albums.isPending}
            onChange={(event) => setAlbumId(event.target.value)}
          >
            <option value="">{t("albums.choose")}</option>
            {albums.data?.map((album) => (
              <option key={album.id} value={album.id}>
                {t("albums.optionCount", {
                  name: album.name,
                  count: album.memberCount,
                })}
              </option>
            ))}
          </select>
        </label>
        {albums.data?.length === 0 && (
          <p className="status-note compact">{t("albums.createFirst")}</p>
        )}
        <button
          className="primary full"
          type="button"
          disabled={busy || !albumId}
          onClick={() => void add()}
        >
          {busy ? t("albums.adding") : t("albums.add")}
        </button>
        {(error || albums.error) && (
          <p className="status-note compact metadata-error">
            {error || albums.error?.message}
          </p>
        )}
        <button
          className="icon-button modal-close"
          disabled={busy}
          onClick={onClose}
          aria-label={t("common.close")}
        >
          <X />
        </button>
      </section>
    </div>
  );
}
