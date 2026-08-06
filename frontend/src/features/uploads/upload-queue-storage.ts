import type { UploadDestination } from "@/lib/api";

export type UploadQueueState =
  | "queued"
  | "uploading"
  | "paused"
  | "conflict"
  | "failed"
  | "completed"
  | "skipped";

export type StoredUpload = {
  id: string;
  userId: string;
  batchId: string;
  blob?: Blob;
  name: string;
  mimeType: string;
  sizeBytes: number;
  lastModified: number;
  destination: UploadDestination;
  state: UploadQueueState;
  receivedBytes: number;
  serverUploadId?: string;
  chunkSizeBytes?: number;
  checksum?: string;
  catalogItemId?: string;
  duplicateState?: "none" | "active" | "trashed";
  errorCode?: "offline" | "name_conflict" | "storage" | "upload";
  error?: string;
  createdAt: string;
  updatedAt: string;
};

const databaseName = "media-explorer-upload-queue";
const databaseVersion = 1;
const storeName = "uploads";

let databasePromise: Promise<IDBDatabase> | undefined;

export async function listStoredUploads(userId: string) {
  const database = await openDatabase();
  return new Promise<StoredUpload[]>((resolve, reject) => {
    const transaction = database.transaction(storeName, "readonly");
    const request = transaction
      .objectStore(storeName)
      .index("userId")
      .getAll(IDBKeyRange.only(userId));
    request.onsuccess = () => {
      resolve(
        (request.result as StoredUpload[]).sort((left, right) =>
          left.createdAt.localeCompare(right.createdAt),
        ),
      );
    };
    request.onerror = () => reject(request.error);
  });
}

export async function putStoredUpload(upload: StoredUpload) {
  const database = await openDatabase();
  return runWrite(database, (store) => store.put(upload));
}

export async function deleteStoredUpload(id: string) {
  const database = await openDatabase();
  return runWrite(database, (store) => store.delete(id));
}

function openDatabase() {
  if (typeof indexedDB === "undefined")
    return Promise.reject(new Error("This browser cannot preserve uploads."));
  databasePromise ??= new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open(databaseName, databaseVersion);
    request.onupgradeneeded = () => {
      const database = request.result;
      const store = database.createObjectStore(storeName, { keyPath: "id" });
      store.createIndex("userId", "userId", { unique: false });
    };
    request.onsuccess = () => {
      request.result.onversionchange = () => request.result.close();
      resolve(request.result);
    };
    request.onerror = () => reject(request.error);
    request.onblocked = () =>
      reject(new Error("Upload storage is open in an older browser tab."));
  });
  return databasePromise;
}

function runWrite(
  database: IDBDatabase,
  action: (store: IDBObjectStore) => IDBRequest,
) {
  return new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(storeName, "readwrite");
    action(transaction.objectStore(storeName));
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}
