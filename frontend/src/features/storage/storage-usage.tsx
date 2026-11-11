"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import Link from "next/link";
import {
  HardDrive,
  Film,
  Trash2,
  Layers,
  Folder,
  File,
  ArrowUpRight,
  RefreshCw,
} from "lucide-react";
import { authRequest } from "@/lib/auth";
import { formatBytes } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";
import styles from "@/styles/storage-usage.module.css";

type Usage = {
  physicalBytes: number;
  logicalBytes: number;
  trashBytes: number;
  retainedVersionBytes: number;
  videoBytes: number;
  calculatedAt: string;
  growth: { key: string; bytes: number; count: number }[];
  folders: { id: string; name: string; bytes: number }[];
};
type Files = {
  files: {
    id: string;
    name: string;
    bytes: number;
    trashed: boolean;
    versionId: string;
    current: boolean;
  }[];
  hasMore: boolean;
};

export function StorageUsage({ userId }: { userId: string }) {
  const queryClient = useQueryClient();
  const { locale } = usePreferences();
  const es = locale === "es";
  const text = (en: string, spanish: string) => (es ? spanish : en);
  const [filter, setFilter] = useState({
    category: "all",
    month: "",
    folderId: "",
    label: "",
  });
  const [offset, setOffset] = useState(0);
  const summaryKey = ["storage-usage", userId];
  const filesKey = ["storage-files", userId];
  const summary = useQuery({
    queryKey: summaryKey,
    queryFn: ({ signal }) =>
      authRequest<Usage>("/api/v1/storage-usage", { signal }),
    staleTime: 60_000,
  });
  const refreshSummary = useMutation({
    mutationFn: () => authRequest<Usage>("/api/v1/storage-usage?refresh=true"),
    onMutate: () => queryClient.cancelQueries({ queryKey: summaryKey }),
    onSuccess: (data) => {
      queryClient.setQueryData(summaryKey, data);
    },
  });
  const files = useQuery({
    queryKey: [
      ...filesKey,
      filter.category,
      filter.month,
      filter.folderId,
      offset,
    ],
    queryFn: ({ signal }) => {
      const query = new URLSearchParams({
        category: filter.category,
        offset: String(offset),
      });
      if (filter.month) query.set("month", filter.month);
      if (filter.folderId) query.set("folderId", filter.folderId);
      return authRequest<Files>(`/api/v1/storage-usage/files?${query}`, {
        signal,
      });
    },
    staleTime: 60_000,
  });
  const summaryRefreshing = summary.isFetching || refreshSummary.isPending;
  const select = (
    category: string,
    label: string,
    month = "",
    folderId = "",
  ) => {
    setFilter({ category, label, month, folderId });
    setOffset(0);
  };
  const data = summary.data;
  const categories = data
    ? [
        {
          key: "video",
          label: text("Videos", "Videos"),
          bytes: data.videoBytes,
          icon: Film,
        },
        {
          key: "trash",
          label: text("Only in trash", "Solo en la papelera"),
          bytes: data.trashBytes,
          icon: Trash2,
        },
        {
          key: "versions",
          label: text("Only in older versions", "Solo en versiones anteriores"),
          bytes: data.retainedVersionBytes,
          icon: Layers,
        },
      ]
    : [];
  return (
    <main className={styles.page}>
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>{text("YOUR ARCHIVE", "TU ARCHIVO")}</p>
          <h1>{text("Storage", "Almacenamiento")}</h1>
          <p>
            {text(
              "See what takes up space and find your largest files.",
              "Consulta qué ocupa espacio y encuentra tus archivos más grandes.",
            )}
          </p>
        </div>
        <button
          className={styles.button}
          disabled={summaryRefreshing || files.isFetching}
          onClick={() => {
            refreshSummary.mutate();
            void queryClient.invalidateQueries({
              queryKey: filesKey,
              refetchType: offset === 0 ? "active" : "none",
            });
            setOffset(0);
          }}
        >
          <RefreshCw size={16} className={summaryRefreshing ? "spin" : ""} />
          {text("Refresh", "Actualizar")}
        </button>
      </header>
      {summary.isPending && (
        <p role="status">
          {text("Calculating storage…", "Calculando almacenamiento…")}
        </p>
      )}
      {(summary.isError || refreshSummary.isError) && (
        <p role="alert">
          {text(
            "Storage could not be loaded. Try Refresh.",
            "No se pudo cargar el almacenamiento. Pulsa Actualizar.",
          )}
        </p>
      )}
      {data && (
        <>
          <section
            className={styles.overview}
            aria-label={text("Storage overview", "Resumen de almacenamiento")}
          >
            <div className={styles.total}>
              <span className={styles.icon}>
                <HardDrive size={23} />
              </span>
              <div>
                <span>
                  {text("Originals stored", "Originales almacenados")}
                </span>
                <strong>{formatBytes(data.physicalBytes)}</strong>
                <small>
                  {text(
                    "Each original counted once, including trash.",
                    "Cada original se cuenta una vez, incluida la papelera.",
                  )}
                </small>
              </div>
            </div>
            <div className={styles.savings}>
              <span>
                {text(
                  "Saved by reusing identical files",
                  "Ahorrado al reutilizar archivos idénticos",
                )}
              </span>
              <strong>
                {formatBytes(
                  Math.max(0, data.logicalBytes - data.physicalBytes),
                )}
              </strong>
              <small>
                {text(
                  "Copies share an original without taking up extra space.",
                  "Las copias comparten un original sin ocupar espacio adicional.",
                )}
              </small>
            </div>
          </section>
          <div className={styles.cards}>
            {categories.map(({ key, label, bytes, icon: Icon }) => (
              <button
                key={key}
                aria-pressed={filter.category === key}
                onClick={() => select(key, label)}
              >
                <Icon size={18} />
                <span>
                  {label}
                  <strong>{formatBytes(bytes)}</strong>
                </span>
                <ArrowUpRight size={16} />
              </button>
            ))}
          </div>
        </>
      )}
      <div className={styles.breakdowns}>
        <section className={styles.panel} aria-live="polite">
          <div className={styles.panelHeading}>
            <div>
              <h2>
                {filter.label || text("Largest files", "Archivos más grandes")}
              </h2>
              <p>
                {text(
                  "Sorted from largest to smallest",
                  "Ordenados de mayor a menor",
                )}
              </p>
            </div>
            {filter.category !== "all" && (
              <button
                className={styles.button}
                onClick={() => select("all", "")}
              >
                {text("All files", "Todos los archivos")}
              </button>
            )}
          </div>
          {files.isPending && (
            <p role="status">{text("Loading files…", "Cargando archivos…")}</p>
          )}
          {files.isError && (
            <p role="alert">
              {text(
                "Files could not be loaded. Try Refresh.",
                "No se pudieron cargar los archivos. Pulsa Actualizar.",
              )}
            </p>
          )}
          {files.data?.files.map((file) => (
            <Link
              className={styles.row}
              key={file.versionId}
              href={
                (file.trashed ? "/trash" : "/files") +
                "?details=" +
                encodeURIComponent(file.id)
              }
            >
              <File size={18} />
              <span>
                {file.name}
                <small>
                  {file.trashed
                    ? text("Trash", "Papelera")
                    : !file.current
                      ? text(
                          "Older version · opens current file",
                          "Versión anterior · abre el archivo actual",
                        )
                      : text("Original file", "Archivo original")}
                </small>
              </span>
              <strong>{formatBytes(file.bytes)}</strong>
              <ArrowUpRight size={15} />
            </Link>
          ))}
          {files.data?.files.length === 0 && (
            <p className={styles.empty}>
              {text(
                "No files in this breakdown.",
                "No hay archivos en esta categoría.",
              )}
            </p>
          )}
          {(offset > 0 || files.data?.hasMore) && (
            <nav
              className={styles.pagination}
              aria-label={text("Storage pages", "Páginas de almacenamiento")}
            >
              <button
                className={styles.button}
                disabled={offset === 0 || files.isFetching}
                onClick={() => setOffset((v) => Math.max(0, v - 50))}
              >
                {text("Previous", "Anterior")}
              </button>
              <span>{Math.floor(offset / 50) + 1}</span>
              <button
                className={styles.button}
                disabled={!files.data?.hasMore || files.isFetching}
                onClick={() => setOffset((v) => v + 50)}
              >
                {text("Next", "Siguiente")}
              </button>
            </nav>
          )}
        </section>
        {data && (
          <aside className={styles.panel}>
            <div className={styles.panelHeading}>
              <div>
                <h2>{text("Largest folders", "Carpetas más grandes")}</h2>
                <p>
                  {text(
                    "Includes files in subfolders and trash",
                    "Incluye archivos en subcarpetas y papelera",
                  )}
                </p>
              </div>
            </div>
            {data.folders.map((folder) => (
              <button
                className={styles.row}
                key={folder.id}
                onClick={() => select("folder", folder.name, "", folder.id)}
              >
                <Folder size={18} />
                <span>{folder.name}</span>
                <strong>{formatBytes(folder.bytes)}</strong>
              </button>
            ))}
            {!data.folders.length && (
              <p className={styles.empty}>
                {text(
                  "No stored folders yet.",
                  "Aún no hay carpetas almacenadas.",
                )}
              </p>
            )}
            <p className={styles.footnote}>
              {text(
                "A parent folder includes its subfolders, so these totals overlap.",
                "Una carpeta incluye sus subcarpetas, por lo que estos totales se superponen.",
              )}
            </p>
          </aside>
        )}
      </div>
      {data && (
        <footer className={styles.footer}>
          <details>
            <summary>
              {text(
                "How storage is counted",
                "Cómo se calcula el almacenamiento",
              )}
            </summary>
            <p>
              {text(
                "Totals cover originals referenced by your library. Categories can overlap, and another family library may still use an original in your trash. Backups, caches and storage-provider versions are excluded.",
                "Los totales incluyen originales de tu biblioteca. Las categorías pueden superponerse y otra biblioteca familiar puede usar originales en tu papelera. Se excluyen respaldos, cachés y versiones del proveedor.",
              )}
            </p>
          </details>
          <span>
            {text("Updated ", "Actualizado ")}
            {new Date(data.calculatedAt).toLocaleTimeString(locale, {
              hour: "2-digit",
              minute: "2-digit",
            })}
          </span>
        </footer>
      )}
    </main>
  );
}
