"use client";

import { useQueryClient } from "@tanstack/react-query";
import { createSHA256 } from "hash-wasm";
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  appendUploadChunk,
  cancelUploadSession,
  completeUploadSession,
  createUploadSession,
  getUploadSession,
  setAlbumItems,
  type UploadDestination,
} from "@/lib/api";
import { ApiRequestError } from "@/lib/auth";
import { logError, logInfo } from "@/lib/logger";
import {
  deleteStoredUpload,
  listStoredUploads,
  putStoredUpload,
  type StoredUpload,
} from "./upload-queue-storage";

export type UploadQueueItem = Omit<StoredUpload, "blob" | "checksum">;

type EnqueueResult = {
  accepted: number;
  failed: number;
  error?: string;
};

type UploadQueueContextValue = {
  activeUserId?: string;
  items: UploadQueueItem[];
  activateUser: (userId: string) => Promise<void>;
  deactivateUser: () => void;
  enqueueFiles: (
    files: File[],
    destination: UploadDestination,
  ) => Promise<EnqueueResult>;
  retry: (id: string) => Promise<void>;
  keepBoth: (id: string) => Promise<void>;
  keepAllConflicts: () => Promise<void>;
  skipAllConflicts: () => Promise<void>;
  cancelAllPending: () => Promise<void>;
  remove: (id: string) => Promise<void>;
  clearCompleted: () => Promise<void>;
};

const UploadQueueContext = createContext<UploadQueueContextValue | null>(null);
const MAX_CONCURRENT_UPLOADS = 3;

export function UploadQueueProvider({
  children,
}: {
  children: React.ReactNode;
}) {
  const queryClient = useQueryClient();
  const [activeUserId, setActiveUserId] = useState<string>();
  const [records, setRecords] = useState<StoredUpload[]>([]);
  const [wakeVersion, setWakeVersion] = useState(0);
  const activeUserRef = useRef<string | undefined>(undefined);
  const recordsRef = useRef<StoredUpload[]>([]);
  const processingRef = useRef(false);
  const wakeWorkerRef = useRef<(() => void) | undefined>(undefined);
  const abortControllersRef = useRef(new Map<string, AbortController>());
  const removedIdsRef = useRef(new Set<string>());
  const activationRef = useRef(0);

  const replaceRecords = useCallback((next: StoredUpload[]) => {
    recordsRef.current = next;
    setRecords(next);
    wakeWorkerRef.current?.();
  }, []);

  const updateRecord = useCallback(
    async (id: string, patch: Partial<StoredUpload>) => {
      if (removedIdsRef.current.has(id)) return undefined;
      const current = recordsRef.current.find((item) => item.id === id);
      if (!current) return undefined;
      const next: StoredUpload = {
        ...current,
        ...patch,
        updatedAt: new Date().toISOString(),
      };
      replaceRecords(
        recordsRef.current.map((item) => (item.id === id ? next : item)),
      );
      await putStoredUpload(next);
      return next;
    },
    [replaceRecords],
  );

  const activateUser = useCallback(
    async (userId: string) => {
      if (activeUserRef.current === userId) return;
      const activation = ++activationRef.current;
      abortControllersRef.current.forEach((controller) => controller.abort());
      activeUserRef.current = userId;
      setActiveUserId(userId);
      try {
        const stored = await listStoredUploads(userId);
        if (
          activation !== activationRef.current ||
          activeUserRef.current !== userId
        )
          return;
        replaceRecords(stored);
        setWakeVersion((current) => current + 1);
        if ("storage" in navigator && "persist" in navigator.storage)
          void navigator.storage.persist().catch(() => false);
      } catch (reason) {
        logError("frontend.upload", "durable queue restore failed", {
          reason:
            reason instanceof Error ? reason.message : "unknown_storage_error",
        });
      }
    },
    [replaceRecords],
  );

  const deactivateUser = useCallback(() => {
    ++activationRef.current;
    abortControllersRef.current.forEach((controller) => controller.abort());
    activeUserRef.current = undefined;
    setActiveUserId(undefined);
    replaceRecords([]);
  }, [replaceRecords]);

  const enqueueFiles = useCallback(
    async (files: File[], destination: UploadDestination) => {
      const userId = activeUserRef.current;
      if (!userId)
        return {
          accepted: 0,
          failed: files.length,
          error: "Sign in again before adding uploads.",
        };

      const batchId = createId();
      let accepted = 0;
      let failed = 0;
      let firstError: string | undefined;
      for (const file of files) {
        const now = new Date().toISOString();
        const upload: StoredUpload = {
          id: createId(),
          userId,
          batchId,
          blob: file,
          name: file.name,
          mimeType: file.type || "application/octet-stream",
          sizeBytes: file.size,
          lastModified: file.lastModified,
          destination,
          state: "queued",
          receivedBytes: 0,
          createdAt: now,
          updatedAt: now,
        };
        try {
          await putStoredUpload(upload);
          accepted++;
          replaceRecords([...recordsRef.current, upload]);
        } catch (reason) {
          failed++;
          firstError ??= storageErrorMessage(reason);
          logError("frontend.upload", "file could not enter durable queue", {
            sizeBytes: file.size,
            reason:
              reason instanceof Error ? reason.name : "unknown_storage_error",
          });
        }
      }
      setWakeVersion((current) => current + 1);
      return { accepted, failed, error: firstError };
    },
    [replaceRecords],
  );

  const retry = useCallback(
    async (id: string) => {
      const item = recordsRef.current.find((candidate) => candidate.id === id);
      if (!item || (!item.blob && !item.catalogItemId)) return;
      await updateRecord(id, {
        state: "queued",
        error: undefined,
        errorCode: undefined,
      });
      setWakeVersion((current) => current + 1);
    },
    [updateRecord],
  );

  const keepBoth = useCallback(
    async (id: string) => {
      const item = recordsRef.current.find((candidate) => candidate.id === id);
      if (!item || !item.blob) return;
      await updateRecord(id, {
        destination: { ...item.destination, allowNameConflict: true },
        state: "queued",
        error: undefined,
        errorCode: undefined,
      });
      setWakeVersion((current) => current + 1);
    },
    [updateRecord],
  );

  const remove = useCallback(
    async (id: string) => {
      const item = recordsRef.current.find((candidate) => candidate.id === id);
      if (!item) return;
      removedIdsRef.current.add(id);
      abortControllersRef.current.get(id)?.abort();
      try {
        const cancelling: StoredUpload = {
          ...item,
          state: "skipped",
          updatedAt: new Date().toISOString(),
        };
        replaceRecords(
          recordsRef.current.map((candidate) =>
            candidate.id === id ? cancelling : candidate,
          ),
        );
        await putStoredUpload(cancelling);
        if (item.serverUploadId && item.state !== "completed") {
          try {
            await cancelUploadSession(item.serverUploadId);
          } catch (reason) {
            if (
              !(reason instanceof ApiRequestError) ||
              ![404, 409].includes(reason.status)
            )
              throw reason;
          }
        }
        await deleteStoredUpload(id);
        replaceRecords(
          recordsRef.current.filter((candidate) => candidate.id !== id),
        );
      } catch (reason) {
        removedIdsRef.current.delete(id);
        await updateRecord(id, {
          state: "failed",
          errorCode: "upload",
          error:
            reason instanceof Error
              ? reason.message
              : "Could not remove this upload.",
        });
      } finally {
        setWakeVersion((current) => current + 1);
      }
    },
    [replaceRecords, updateRecord],
  );

  const keepAllConflicts = useCallback(async () => {
    const conflictIds = recordsRef.current
      .filter((item) => item.state === "conflict" && item.blob)
      .map((item) => item.id);
    for (const id of conflictIds) {
      const item = recordsRef.current.find((candidate) => candidate.id === id);
      if (!item || item.state !== "conflict" || !item.blob) continue;
      await updateRecord(id, {
        destination: { ...item.destination, allowNameConflict: true },
        state: "queued",
        error: undefined,
        errorCode: undefined,
      });
    }
    if (conflictIds.length) setWakeVersion((current) => current + 1);
  }, [updateRecord]);

  const skipAllConflicts = useCallback(async () => {
    const conflictIds = recordsRef.current
      .filter((item) => item.state === "conflict")
      .map((item) => item.id);
    conflictIds.forEach((id) => removedIdsRef.current.add(id));
    for (const id of conflictIds) await remove(id);
  }, [remove]);

  const cancelAllPending = useCallback(async () => {
    const pendingIds = recordsRef.current
      .filter((item) => item.state !== "completed" && item.state !== "skipped")
      .map((item) => item.id);
    pendingIds.forEach((id) => removedIdsRef.current.add(id));
    abortControllersRef.current.forEach((controller) => controller.abort());
    for (const id of pendingIds) await remove(id);
  }, [remove]);

  const clearCompleted = useCallback(async () => {
    const completed = recordsRef.current.filter(
      (item) => item.state === "completed" || item.state === "skipped",
    );
    await Promise.all(
      completed.map(async (item) => {
        removedIdsRef.current.add(item.id);
        await deleteStoredUpload(item.id);
      }),
    );
    replaceRecords(
      recordsRef.current.filter(
        (item) => item.state !== "completed" && item.state !== "skipped",
      ),
    );
  }, [replaceRecords]);

  useEffect(() => {
    const resumeOfflineUploads = () => {
      const resumable = recordsRef.current.filter(
        (item) => item.state === "paused" && item.errorCode === "offline",
      );
      if (!resumable.length) return;
      const next = recordsRef.current.map((item) =>
        resumable.some((candidate) => candidate.id === item.id)
          ? {
              ...item,
              state: "queued" as const,
              error: undefined,
              errorCode: undefined,
            }
          : item,
      );
      replaceRecords(next);
      void Promise.all(next.map((item) => putStoredUpload(item)));
      setWakeVersion((current) => current + 1);
    };
    window.addEventListener("online", resumeOfflineUploads);
    return () => window.removeEventListener("online", resumeOfflineUploads);
  }, [replaceRecords]);

  useEffect(() => {
    const userId = activeUserId;
    if (!userId || processingRef.current) return;
    const hasRunnable = records.some(
      (item) =>
        item.userId === userId &&
        !removedIdsRef.current.has(item.id) &&
        (item.state === "queued" || item.state === "uploading"),
    );
    if (!hasRunnable) return;
    processingRef.current = true;
    const activation = activationRef.current;
    const isCurrentUser = () =>
      activeUserRef.current === userId && activationRef.current === activation;

    // Refreshes can include every loaded album page. They must never hold up
    // the next file, and overlapping completions share one trailing refresh.
    let refreshPending = false;
    let refreshing = false;
    const refreshCatalog = async () => {
      refreshPending = true;
      if (refreshing) return;
      refreshing = true;
      try {
        while (refreshPending && isCurrentUser()) {
          refreshPending = false;
          await Promise.all([
            queryClient.invalidateQueries({ queryKey: ["library"] }),
            queryClient.invalidateQueries({
              queryKey: ["photo-timeline-anchors"],
            }),
            queryClient.invalidateQueries({ queryKey: ["photo-years"] }),
            queryClient.invalidateQueries({ queryKey: ["catalog-search"] }),
            queryClient.invalidateQueries({ queryKey: ["storage-summary"] }),
            queryClient.invalidateQueries({ queryKey: ["albums"] }),
            queryClient.invalidateQueries({ queryKey: ["album-photo-picker"] }),
          ]);
        }
      } catch (reason) {
        logError("frontend.upload", "catalog refresh failed", {
          reason: reason instanceof Error ? reason.message : "unknown_error",
        });
      } finally {
        refreshing = false;
      }
    };

    void withQueueLock(userId, async () => {
      if (!isCurrentUser()) return;
      // Read saved Blobs once when taking over the lock, rather than reloading
      // the entire batch from IndexedDB before every single file.
      const beforeLoad = new Map(
        recordsRef.current.map((item) => [item.id, item]),
      );
      const stored = await listStoredUploads(userId);
      if (!isCurrentUser()) return;
      const currentRecords = new Map(
        recordsRef.current.map((item) => [item.id, item]),
      );
      const normalized = stored.map((item) => {
        const current = currentRecords.get(item.id);
        if (current && current !== beforeLoad.get(item.id)) return current;
        return item.state === "uploading"
          ? { ...item, state: "queued" as const }
          : item;
      });
      const loadedIds = new Set(normalized.map((item) => item.id));
      replaceRecords([
        ...normalized.filter((item) => !removedIdsRef.current.has(item.id)),
        ...recordsRef.current.filter(
          (item) =>
            !beforeLoad.has(item.id) &&
            !loadedIds.has(item.id) &&
            !removedIdsRef.current.has(item.id),
        ),
      ]);

      const processNext = async (next: StoredUpload) => {
        if (!next.blob && !next.catalogItemId) {
          await updateRecord(next.id, {
            state: "failed",
            errorCode: "storage",
            error:
              "The saved file is unavailable. Remove it and choose it again.",
          });
          return;
        }

        const abortController = new AbortController();
        abortControllersRef.current.set(next.id, abortController);
        try {
          await updateRecord(next.id, {
            state: "uploading",
            error: undefined,
            errorCode: undefined,
          });
          if (!isCurrentUser() || abortController.signal.aborted) return;
          await processUpload(next, abortController.signal, updateRecord, true);
          if (!isCurrentUser()) return;
          const completed = await updateRecord(next.id, {
            state: "completed",
            receivedBytes: next.sizeBytes,
            blob: undefined,
            error: undefined,
            errorCode: undefined,
          });
          logInfo("frontend.upload", "durable queue item completed", {
            uploadId: completed?.serverUploadId,
            sizeBytes: next.sizeBytes,
          });
          void refreshCatalog();
        } catch (reason) {
          if (!isCurrentUser() || removedIdsRef.current.has(next.id)) return;
          const offline =
            typeof navigator !== "undefined" && navigator.onLine === false;
          const nameConflict =
            reason instanceof ApiRequestError &&
            reason.status === 409 &&
            reason.message === "An item with this name already exists.";
          const albumPending = Boolean(
            next.destination.albumId &&
            recordsRef.current.find((item) => item.id === next.id)
              ?.catalogItemId,
          );
          if (albumPending) {
            void refreshCatalog();
          }
          await updateRecord(next.id, {
            state: nameConflict ? "conflict" : offline ? "paused" : "failed",
            errorCode: nameConflict
              ? "name_conflict"
              : offline
                ? "offline"
                : albumPending
                  ? "album"
                  : "upload",
            error: nameConflict
              ? "A file with this name now exists in the destination."
              : offline
                ? "Waiting for an internet connection."
                : reason instanceof Error
                  ? reason.message
                  : "Upload failed.",
          });
          logError("frontend.upload", "durable queue item paused", {
            sizeBytes: next.sizeBytes,
            reason: nameConflict
              ? "name_conflict"
              : offline
                ? "offline"
                : reason instanceof Error
                  ? reason.message
                  : "unknown_error",
          });
        } finally {
          if (abortControllersRef.current.get(next.id) === abortController)
            abortControllersRef.current.delete(next.id);
        }
      };

      const running = new Set<Promise<void>>();
      const failures: unknown[] = [];
      let resume: (() => void) | undefined;
      let notified = false;
      const wake = () => {
        notified = true;
        resume?.();
      };
      wakeWorkerRef.current = wake;
      try {
        while (isCurrentUser()) {
          notified = false;
          while (running.size < MAX_CONCURRENT_UPLOADS) {
            const next = recordsRef.current.find(
              (item) =>
                item.userId === userId &&
                item.state === "queued" &&
                !removedIdsRef.current.has(item.id),
            );
            if (!next) break;
            // Claim synchronously before starting another file. Queue changes
            // also wake this scheduler, filling idle slots during slow enqueue.
            const task = processNext(next)
              .catch((reason) => {
                failures.push(reason);
              })
              .finally(() => {
                running.delete(task);
                wake();
              });
            running.add(task);
          }
          if (!running.size) break;
          await new Promise<void>((resolve) => {
            resume = resolve;
            if (notified) resolve();
          });
          resume = undefined;
        }
      } finally {
        // Keep the cross-tab lock until every transfer settles, including when
        // switching accounts or a browser-storage checkpoint fails.
        await Promise.allSettled(running);
        wakeWorkerRef.current = undefined;
      }
      if (failures.length) throw failures[0];
    })
      .catch((reason) => {
        logError("frontend.upload", "durable queue worker failed", {
          reason: reason instanceof Error ? reason.message : "unknown_error",
        });
      })
      .finally(() => {
        processingRef.current = false;
        setWakeVersion((current) => current + 1);
      });
  }, [
    activeUserId,
    queryClient,
    records,
    replaceRecords,
    updateRecord,
    wakeVersion,
  ]);

  const value = useMemo<UploadQueueContextValue>(
    () => ({
      activeUserId,
      items: records.map(toPublicUpload),
      activateUser,
      deactivateUser,
      enqueueFiles,
      retry,
      keepBoth,
      keepAllConflicts,
      skipAllConflicts,
      cancelAllPending,
      remove,
      clearCompleted,
    }),
    [
      activeUserId,
      activateUser,
      cancelAllPending,
      clearCompleted,
      deactivateUser,
      enqueueFiles,
      keepAllConflicts,
      keepBoth,
      records,
      remove,
      retry,
      skipAllConflicts,
    ],
  );

  return (
    <UploadQueueContext.Provider value={value}>
      {children}
    </UploadQueueContext.Provider>
  );
}

export function useUploadQueue() {
  const value = useContext(UploadQueueContext);
  if (!value)
    throw new Error("useUploadQueue must be used within UploadQueueProvider.");
  return value;
}

async function processUpload(
  initial: StoredUpload,
  signal: AbortSignal,
  update: (
    id: string,
    patch: Partial<StoredUpload>,
  ) => Promise<StoredUpload | undefined>,
  mayRestart: boolean,
): Promise<void> {
  if (initial.catalogItemId) {
    await finishAlbumMembership(initial, signal);
    return;
  }
  if (!initial.blob) throw new Error("The saved file is unavailable.");
  if (navigator.onLine === false) throw new TypeError("Offline");

  const blob = initial.blob;
  let item = initial;
  let session;
  try {
    session = item.serverUploadId
      ? await getUploadSession(item.serverUploadId, signal)
      : await createUploadSession(
          {
            name: item.name,
            type: item.mimeType,
            size: item.sizeBytes,
          },
          item.destination,
          signal,
          item.id,
        );
  } catch (reason) {
    if (
      item.serverUploadId &&
      reason instanceof ApiRequestError &&
      reason.status === 404
    ) {
      item =
        (await update(item.id, {
          serverUploadId: undefined,
          chunkSizeBytes: undefined,
          receivedBytes: 0,
          checksum: undefined,
        })) ?? item;
      session = await createUploadSession(
        { name: item.name, type: item.mimeType, size: item.sizeBytes },
        item.destination,
        signal,
        item.id,
      );
    } else {
      throw reason;
    }
  }

  if (session.sizeBytes !== undefined && session.sizeBytes !== item.sizeBytes)
    throw new Error("The saved file no longer matches its server session.");
  item =
    (await update(item.id, {
      serverUploadId: session.id,
      chunkSizeBytes: session.chunkSizeBytes,
      receivedBytes: session.receivedBytes,
    })) ?? item;

  try {
    let checksum = item.checksum;
    let remoteOffset = session.receivedBytes;
    if (!checksum) {
      const hasher = await createSHA256();
      hasher.init();
      let readOffset = 0;
      while (readOffset < item.sizeBytes) {
        if (signal.aborted) throw new DOMException("Aborted", "AbortError");
        const endExclusive = Math.min(
          readOffset + session.chunkSizeBytes,
          item.sizeBytes,
        );
        const chunk = blob.slice(readOffset, endExclusive);
        hasher.update(new Uint8Array(await chunk.arrayBuffer()));
        if (session.state !== "completed" && endExclusive > remoteOffset) {
          const pendingChunk = blob.slice(
            Math.max(readOffset, remoteOffset),
            endExclusive,
          );
          const progress = await appendUploadChunk(
            session.id,
            pendingChunk,
            remoteOffset,
            item.sizeBytes,
            signal,
          );
          remoteOffset = progress.receivedBytes;
          item =
            (await update(item.id, {
              receivedBytes: progress.receivedBytes,
            })) ?? item;
        }
        readOffset = endExclusive;
      }
      checksum = hasher.digest("hex");
      item = (await update(item.id, { checksum })) ?? item;
    }

    const completed = await completeUploadSession(
      session.id,
      checksum,
      Boolean(item.destination.allowNameConflict),
      signal,
    );
    item = (await update(item.id, {
      catalogItemId: completed.item.id,
      duplicateState: completed.duplicateState,
      blob: undefined,
    })) ?? { ...item, catalogItemId: completed.item.id };
  } catch (reason) {
    const recoverableSessionConflict =
      mayRestart &&
      reason instanceof ApiRequestError &&
      reason.status === 409 &&
      reason.message !== "An item with this name already exists.";
    if (!recoverableSessionConflict) throw reason;

    try {
      await cancelUploadSession(session.id, signal);
    } catch (cancelReason) {
      if (
        !(cancelReason instanceof ApiRequestError) ||
        cancelReason.status !== 409
      )
        throw cancelReason;
    }
    const restarted =
      (await update(item.id, {
        serverUploadId: undefined,
        chunkSizeBytes: undefined,
        receivedBytes: 0,
        checksum: undefined,
      })) ?? item;
    await processUpload(restarted, signal, update, false);
    return;
  }
  await finishAlbumMembership(item, signal);
}

async function finishAlbumMembership(item: StoredUpload, signal: AbortSignal) {
  if (!item.destination.albumId || !item.catalogItemId) return;
  const result = await setAlbumItems(
    item.destination.albumId,
    [item.catalogItemId],
    true,
    signal,
  );
  if (result.failedCount) {
    throw new Error(
      result.results.find((result) => result.status === "failed")?.message ??
        "Could not add the uploaded file to the album.",
    );
  }
}

async function withQueueLock<T>(userId: string, action: () => Promise<T>) {
  if ("locks" in navigator) {
    return navigator.locks.request(
      `media-explorer-upload-queue:${userId}`,
      action,
    );
  }
  return action();
}

function storageErrorMessage(reason: unknown) {
  return reason instanceof DOMException && reason.name === "QuotaExceededError"
    ? "Not enough browser storage is available to preserve this upload. Free device space and try again."
    : reason instanceof Error
      ? reason.message
      : "This browser could not preserve the selected file.";
}

function createId() {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (character) => {
        const random = Math.floor(Math.random() * 16);
        const value = character === "x" ? random : (random & 0x3) | 0x8;
        return value.toString(16);
      });
}

function toPublicUpload(record: StoredUpload): UploadQueueItem {
  const item = { ...record };
  delete item.blob;
  delete item.checksum;
  return item;
}
