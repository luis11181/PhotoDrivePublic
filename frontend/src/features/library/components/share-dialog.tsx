"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Share2, UserMinus, X } from "lucide-react";
import { useMemo, useState } from "react";
import {
  listItemShares,
  listShareRecipients,
  revokeItemShare,
  shareItem,
} from "@/lib/api";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";

export function ShareDialog({ items, onClose }: { items: MediaItem[]; onClose: () => void }) {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const singleItem = items.length === 1 ? items[0] : undefined;
  const [recipientId, setRecipientId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [result, setResult] = useState("");
  const recipients = useQuery({
    queryKey: ["share-recipients"],
    queryFn: listShareRecipients,
    staleTime: 60_000,
  });
  const shares = useQuery({
    queryKey: ["item-shares", singleItem?.id ?? "multiple"],
    queryFn: () => listItemShares(singleItem!.id),
    enabled: Boolean(singleItem),
  });
  const sharedRecipientIds = useMemo(
    () => new Set(shares.data?.map((share) => share.recipientUserId) ?? []),
    [shares.data],
  );
  const availableRecipients =
    recipients.data?.filter(
      (recipient) => !singleItem || !sharedRecipientIds.has(recipient.userId),
    ) ?? [];

  async function grant() {
    if (!recipientId) return;
    setBusy(true);
    setError("");
    setResult("");
    let succeeded = 0;
    const failures: string[] = [];
    for (const item of items) {
      try {
        await shareItem(item.id, recipientId);
        succeeded += 1;
      } catch (reason) {
        failures.push(
          `${item.name}: ${reason instanceof Error ? reason.message : t("share.failed")}`,
        );
      }
    }
    try {
      setRecipientId("");
      if (singleItem && succeeded) await shares.refetch();
      await queryClient.invalidateQueries({ queryKey: ["library", "shared"] });
      if (failures.length) {
        setError(
          t("share.partial", {
            shared: succeeded,
            failed: failures.length,
            error: failures[0],
          }),
        );
      } else {
        setResult(
          items.length === 1
            ? t("share.success")
            : t("share.multiSuccess", { count: succeeded }),
        );
      }
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : t("share.refreshFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function revoke(recipientUserId: string) {
    setBusy(true);
    setError("");
    try {
      if (!singleItem) return;
      await revokeItemShare(singleItem.id, recipientUserId);
      await shares.refetch();
      await queryClient.invalidateQueries({ queryKey: ["library", "shared"] });
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("albums.revokeFailed"));
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
        aria-labelledby="share-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="modal-icon"><Share2 /></div>
        <h2 id="share-title">
          {singleItem
            ? t("share.singleTitle", { name: singleItem.name })
            : t("share.multipleTitle", { count: items.length })}
        </h2>
        <p className="modal-copy">
          {t("share.help")}
        </p>
        <label className="share-recipient-control">
          {t("albums.familyMember")}
          <select
            value={recipientId}
            disabled={busy || recipients.isPending}
            onChange={(event) => setRecipientId(event.target.value)}
          >
            <option value="">{t("albums.choosePerson")}</option>
            {availableRecipients.map((recipient) => (
              <option key={recipient.userId} value={recipient.userId}>
                {recipient.displayName} · {recipient.email}
              </option>
            ))}
          </select>
        </label>
        <button className="primary full" type="button" disabled={busy || !recipientId} onClick={() => void grant()}>
          <Share2 size={17} />{" "}
          {items.length === 1
            ? t("albums.shareViewer")
            : t("share.shareCount", { count: items.length })}
        </button>
        {singleItem ? (
          <div className="share-access-list">
            <strong>{t("albums.peopleAccess")}</strong>
            {shares.isPending ? (
              <p>{t("albums.loadingAccess")}</p>
            ) : shares.data?.length ? (
              shares.data.map((share) => (
                <div key={share.recipientUserId}>
                  <span><b>{share.recipientDisplayName}</b><small>{share.recipientEmail}</small></span>
                  <span className="share-role">{t("albums.viewer")}</span>
                  <button className="icon-button" type="button" disabled={busy} onClick={() => void revoke(share.recipientUserId)} aria-label={t("albums.removeAccess", { name: share.recipientDisplayName })}>
                    <UserMinus />
                  </button>
                </div>
              ))
            ) : (
              <p>{t("albums.onlyYou")}</p>
            )}
          </div>
        ) : (
          <p className="status-note compact">
            {t("share.multiHelp")}
          </p>
        )}
        {result && <p className="status-note compact">{result}</p>}
        {(error || recipients.error || shares.error) && (
          <p className="status-note compact metadata-error">
            {error || recipients.error?.message || shares.error?.message}
          </p>
        )}
        <button className="icon-button modal-close" disabled={busy} onClick={onClose} aria-label={t("common.close")}><X /></button>
      </section>
    </div>
  );
}
