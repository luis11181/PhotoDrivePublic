"use client";

import {
  useInfiniteQuery,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Folder,
  ArrowUp,
  FolderInput,
  Info,
  Loader2,
  RefreshCw,
  Trash2,
} from "lucide-react";
import { FormEvent, useState } from "react";
import { BackToLibraryButton } from "@/components/back-to-library-button";
import { usePreferences } from "@/lib/preferences";
import type { CurrentUser } from "@/lib/auth";
import {
  getImportOptions,
  listImportSourceFolders,
  listImportItems,
  listImportJobs,
  retryImportJob,
  deleteImportJob,
  startGoogleTakeoutImport,
  type ImportDestination,
  type ImportItemCategory,
  type ImportItemResult,
  type ImportJob,
} from "@/lib/api";

export function ImportManagement({
  currentUser,
}: {
  currentUser: CurrentUser;
}) {
  const { t } = usePreferences();
  if (!currentUser.isOwner)
    return (
      <main className="users-page">
        <section className="users-panel users-denied">
          <h1>{t("users.ownerRequired")}</h1>
          <p>{t("imports.ownerHelp")}</p>
          <BackToLibraryButton
            className="auth-secondary"
            label={t("imports.backPhotos")}
          />
        </section>
      </main>
    );
  return <OwnerImportManagement />;
}

function OwnerImportManagement() {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const options = useQuery({
    queryKey: ["import-options"],
    queryFn: getImportOptions,
  });
  const jobs = useQuery({
    queryKey: ["import-jobs"],
    queryFn: listImportJobs,
    refetchInterval: (query) =>
      query.state.data?.some((job) => job.state === "running") ? 2000 : false,
  });
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await action();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : t("imports.actionFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="imports-page">
      <header className="imports-header">
        <div>
          <p className="auth-eyebrow">{t("imports.eyebrow")}</p>
          <h1>{t("imports.title")}</h1>
          <p>{t("imports.intro")}</p>
        </div>
        <BackToLibraryButton
          className="auth-secondary imports-back"
          label={t("imports.backPhotos")}
        />
      </header>

      {(error || message) && (
        <p className={error ? "auth-error" : "auth-success"} role="status">
          {error || message}
        </p>
      )}

      <TakeoutForm
        destinations={options.data?.destinations ?? []}
        loadingOptions={options.isPending}
        optionsError={options.isError}
        busy={busy}
        onSubmit={(
          sourcePath,
          destinationFolderId,
          skipSameName,
          skipIdenticalFiles,
        ) =>
          run(async () => {
            const job = await startGoogleTakeoutImport({
              sourcePath,
              destinationFolderId,
              nameConflictPolicy: skipSameName ? "skip" : "keep_both",
              skipIdenticalFiles,
            });
            setMessage(
              t("imports.started", {
                count: job.discoveredCount,
                destination: job.destinationPath,
              }),
            );
            await queryClient.invalidateQueries({ queryKey: ["import-jobs"] });
          })
        }
      />

      <section className="imports-panel">
        <div className="imports-section-heading">
          <div>
            <h2>{t("imports.recent")}</h2>
            <p>{t("imports.recentHelp")}</p>
          </div>
          <button
            className="imports-refresh"
            aria-label={t("imports.refresh")}
            disabled={jobs.isFetching}
            onClick={() => void jobs.refetch()}
            type="button"
          >
            <RefreshCw className={jobs.isFetching ? "spin" : ""} />
          </button>
        </div>
        <p className="imports-results-help">
          <Info />
          {t("imports.resultsHelp")}
        </p>
        {jobs.isPending ? (
          <Loader2 className="auth-spinner" />
        ) : jobs.error ? (
          <p className="auth-error">{t("imports.loadFailed")}</p>
        ) : jobs.data?.length ? (
          <div className="import-job-list">
            {jobs.data.map((job) => (
              <ImportJobCard
                key={job.id}
                job={job}
                busy={busy}
                onRetry={() =>
                  run(async () => {
                    await retryImportJob(job.id);
                    setMessage(t("imports.retryQueued"));
                    await queryClient.invalidateQueries({
                      queryKey: ["import-jobs"],
                    });
                  })
                }
                onRemove={() => {
                  if (
                    !window.confirm(
                      t(
                        job.failedCount > 0
                          ? "imports.removeConfirmFailed"
                          : "imports.removeConfirm",
                        { count: job.failedCount },
                      ),
                    )
                  )
                    return;
                  void run(async () => {
                    await deleteImportJob(job.id);
                    setMessage(t("imports.removed"));
                    await queryClient.invalidateQueries({
                      queryKey: ["import-jobs"],
                    });
                  });
                }}
              />
            ))}
          </div>
        ) : (
          <div className="imports-empty">
            <FolderInput />
            <p>{t("imports.empty")}</p>
          </div>
        )}
      </section>
    </main>
  );
}

function TakeoutForm({
  destinations,
  loadingOptions,
  optionsError,
  busy,
  onSubmit,
}: {
  destinations: ImportDestination[];
  loadingOptions: boolean;
  optionsError: boolean;
  busy: boolean;
  onSubmit: (
    sourcePath: string,
    destinationFolderId: string,
    skipSameName: boolean,
    skipIdenticalFiles: boolean,
  ) => void;
}) {
  const { t } = usePreferences();
  const [sourcePath, setSourcePath] = useState<string | null>(null);
  const [destinationFolderId, setDestinationFolderId] = useState("");
  const [skipSameName, setSkipSameName] = useState(true);
  const [skipIdenticalFiles, setSkipIdenticalFiles] = useState(true);
  const effectiveDestinationFolderId =
    destinationFolderId ||
    destinations.find((destination) => destination.systemRole === "media_root")
      ?.id ||
    destinations[0]?.id ||
    "";
  const mediaRoot = destinations.find(
    (destination) => destination.systemRole === "media_root",
  );
  const documentsRoot = destinations.find(
    (destination) => destination.systemRole === "documents_root",
  );
  const isInRoot = (
    destination?: ImportDestination,
    root?: ImportDestination,
  ) =>
    Boolean(
      destination &&
      root &&
      (destination.id === root.id ||
        destination.path.startsWith(`${root.path} / `)),
    );
  const mediaDestinations = destinations.filter((destination) =>
    isInRoot(destination, mediaRoot),
  );
  const fileDestinations = destinations.filter((destination) =>
    isInRoot(destination, documentsRoot),
  );
  const otherDestinations = destinations.filter(
    (destination) =>
      !isInRoot(destination, mediaRoot) &&
      !isInRoot(destination, documentsRoot),
  );
  const selectedDestination = destinations.find(
    (destination) => destination.id === effectiveDestinationFolderId,
  );
  const selectedDestinationIsMedia = isInRoot(selectedDestination, mediaRoot);

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (sourcePath === null) return;
    onSubmit(
      sourcePath.trim(),
      effectiveDestinationFolderId,
      skipSameName,
      skipIdenticalFiles,
    );
  }

  return (
    <section className="imports-panel imports-start-card">
      <div className="imports-section-heading">
        <div>
          <h2>{t("imports.takeout")}</h2>
          <p>{t("imports.takeoutHelp")}</p>
        </div>
      </div>
      <form className="imports-form" onSubmit={submit}>
        <SourceFolderPicker
          value={sourcePath}
          onChange={setSourcePath}
          disabled={busy}
        />
        <div className="imports-settings">
          <h3 className="imports-settings-title">{t("imports.settings")}</h3>
          <label className="import-destination-field">
            {t("imports.destination")}
            <select
              required
              value={effectiveDestinationFolderId}
              onChange={(event) => setDestinationFolderId(event.target.value)}
              disabled={busy || loadingOptions}
            >
              {loadingOptions && (
                <option value="">{t("imports.loadingFolders")}</option>
              )}
              {!loadingOptions && destinations.length === 0 && (
                <option value="">{t("imports.noFolders")}</option>
              )}
              {mediaDestinations.length > 0 && (
                <optgroup label={t("imports.photosDestinations")}>
                  {mediaDestinations.map((destination) => (
                    <option key={destination.id} value={destination.id}>
                      {destination.path}
                    </option>
                  ))}
                </optgroup>
              )}
              {fileDestinations.length > 0 && (
                <optgroup label={t("imports.filesDestinations")}>
                  {fileDestinations.map((destination) => (
                    <option key={destination.id} value={destination.id}>
                      {destination.path}
                    </option>
                  ))}
                </optgroup>
              )}
              {otherDestinations.length > 0 && (
                <optgroup label={t("imports.otherDestinations")}>
                  {otherDestinations.map((destination) => (
                    <option key={destination.id} value={destination.id}>
                      {destination.path}
                    </option>
                  ))}
                </optgroup>
              )}
            </select>
            <small>{t("imports.destinationHelp")}</small>
          </label>
          {selectedDestination && (
            <p className="import-destination-note">
              <Info />
              {selectedDestinationIsMedia
                ? t("imports.mediaDestinationNote")
                : t("imports.filesDestinationNote")}
            </p>
          )}
          {optionsError && (
            <p className="auth-error import-options-error">
              {t("imports.destinationFailed")}
            </p>
          )}
          <p className="import-conflict-heading">{t("imports.skipOptions")}</p>
          <label className="import-checkbox-option">
            <input
              type="checkbox"
              disabled={busy}
              checked={skipSameName}
              onChange={(event) => setSkipSameName(event.target.checked)}
            />
            <span>
              <strong>{t("imports.skipSameName")}</strong>
              <small>{t("imports.skipSameNameHelp")}</small>
            </span>
          </label>
          <label className="import-checkbox-option">
            <input
              type="checkbox"
              disabled={busy}
              checked={skipIdenticalFiles}
              onChange={(event) => setSkipIdenticalFiles(event.target.checked)}
            />
            <span>
              <strong>{t("imports.skipIdentical")}</strong>
              <small>{t("imports.skipIdenticalHelp")}</small>
            </span>
          </label>
        </div>
        <footer className="imports-form-footer">
          <p className="imports-privacy-note">
            <Info aria-hidden="true" />
            {t("imports.privacy")}
          </p>
          <button
            className="auth-primary"
            disabled={
              busy ||
              loadingOptions ||
              optionsError ||
              sourcePath === null ||
              !effectiveDestinationFolderId
            }
            type="submit"
          >
            {busy ? <Loader2 className="spin" /> : <FolderInput />}
            {t("imports.start")}
          </button>
        </footer>
      </form>
    </section>
  );
}

function SourceFolderPicker({
  value,
  onChange,
  disabled,
}: {
  value: string | null;
  onChange: (value: string) => void;
  disabled: boolean;
}) {
  const { t } = usePreferences();
  const [path, setPath] = useState("");
  const folders = useInfiniteQuery({
    queryKey: ["import-source-folders", path],
    retry: false,
    queryFn: ({ pageParam, signal }) =>
      listImportSourceFolders(path, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
  const current = folders.data?.pages[0];
  return (
    <section
      className="import-source-browser"
      aria-label={t("imports.sourceDirectory")}
    >
      <h3>{t("imports.sourceDirectory")}</h3>
      <p>{t("imports.sourceHelp")}</p>
      <div className="import-source-toolbar">
        <button
          type="button"
          className="auth-secondary"
          disabled={disabled || !current || current.parentPath === null}
          onClick={() => setPath(current?.parentPath ?? "")}
        >
          <ArrowUp />
          {t("imports.parentFolder")}
        </button>
        <strong title={path || t("imports.sourceRoot")}>
          {path || t("imports.sourceRoot")}
        </strong>
      </div>
      {folders.isPending ? (
        <div className="import-source-loading" role="status">
          <Loader2 className="spin" aria-hidden="true" />
          {t("imports.loadingFolders")}
        </div>
      ) : folders.isError ? (
        <div role="alert">
          <p className="auth-error">{t("imports.sourceUnavailable")}</p>
          <button
            type="button"
            className="auth-secondary"
            onClick={() => void folders.refetch()}
          >
            {t("imports.refresh")}
          </button>
        </div>
      ) : (
        <>
          <div className="import-source-folders">
            {folders.data?.pages
              .flatMap((page) => page.folders)
              .map((folder) => (
                <button
                  type="button"
                  key={folder.path}
                  disabled={disabled}
                  onClick={() => setPath(folder.path)}
                >
                  <Folder aria-hidden="true" />
                  <span>{folder.name}</span>
                  <ChevronRight
                    className="import-folder-chevron"
                    aria-hidden="true"
                  />
                </button>
              ))}
            {!folders.data?.pages.some((page) => page.folders.length > 0) && (
              <p>{t("imports.noSourceFolders")}</p>
            )}
          </div>
          {folders.hasNextPage && (
            <button
              type="button"
              className="auth-secondary import-source-more"
              disabled={disabled || folders.isFetchingNextPage}
              onClick={() => void folders.fetchNextPage()}
            >
              {t("imports.moreFolders")}
            </button>
          )}
        </>
      )}
      <div className="import-source-selection">
        <button
          type="button"
          className="auth-secondary import-source-choose"
          disabled={
            disabled || folders.isPending || folders.isError || !current
          }
          onClick={() => onChange(path)}
        >
          {value === path && <CheckCircle2 aria-hidden="true" />}
          {t("imports.chooseFolder")}
        </button>
        {value !== null && (
          <p className="import-source-selected" role="status">
            <span>{t("imports.selectedSource")}</span>
            <strong>{value || t("imports.sourceRoot")}</strong>
          </p>
        )}
      </div>
    </section>
  );
}

function ImportJobCard({
  job,
  busy,
  onRetry,
  onRemove,
}: {
  job: ImportJob;
  busy: boolean;
  onRetry: () => void;
  onRemove: () => void;
}) {
  const { locale, t } = usePreferences();
  const [category, setCategory] = useState<ImportItemCategory | null>(null);
  const finished =
    job.importedCount +
    job.duplicateCount +
    job.nameConflictCount +
    job.failedCount;
  const progress = job.discoveredCount
    ? Math.min(100, Math.round((finished / job.discoveredCount) * 100))
    : 0;
  const complete = job.state === "completed";
  const hasErrors = job.state === "completed_with_errors";
  const skippedCount = job.duplicateCount + job.nameConflictCount;

  function toggle(next: ImportItemCategory, count: number) {
    if (count === 0) return;
    setCategory((current) => (current === next ? null : next));
  }

  return (
    <article className="import-job-card">
      <div className="import-job-title">
        <span className={`import-state import-state-${job.state}`}>
          {job.state === "running" ? (
            <Loader2 className="spin" />
          ) : complete ? (
            <CheckCircle2 />
          ) : (
            <AlertTriangle />
          )}
          {importStateLabel(job.state, locale)}
        </span>
        <time dateTime={job.createdAt}>
          {new Intl.DateTimeFormat(locale === "es" ? "es-ES" : "en-US", {
            dateStyle: "medium",
            timeStyle: "short",
          }).format(new Date(job.createdAt))}
        </time>
      </div>
      <p className="import-destination">
        {t("imports.destinationLabel")} <strong>{job.destinationPath}</strong>
      </p>
      <div
        className="import-progress"
        aria-label={t("imports.progress", { progress })}
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={progress}
      >
        <span style={{ width: `${progress}%` }} />
      </div>
      <div className="import-count-grid">
        <ImportCount
          label={t("imports.discovered")}
          value={job.discoveredCount}
          active={category === "all"}
          onClick={() => toggle("all", job.discoveredCount)}
        />
        <ImportCount
          label={t("imports.imported")}
          value={job.importedCount}
          active={category === "imported"}
          onClick={() => toggle("imported", job.importedCount)}
        />
        <ImportCount
          label={t("imports.skipped")}
          value={skippedCount}
          active={category === "skipped"}
          onClick={() => toggle("skipped", skippedCount)}
        />
        <ImportCount
          label={t("imports.warnings")}
          value={job.warningCount}
          active={category === "warnings"}
          onClick={() => toggle("warnings", job.warningCount)}
        />
        <ImportCount
          label={t("imports.failed")}
          value={job.failedCount}
          tone="danger"
          active={category === "failed"}
          onClick={() => toggle("failed", job.failedCount)}
        />
      </div>
      {skippedCount > 0 && (
        <p className="import-skipped-breakdown">
          {t("imports.skippedBreakdown", {
            identical: job.duplicateCount,
            names: job.nameConflictCount,
          })}
        </p>
      )}
      {(job.orphanSidecarCount > 0 || job.ignoredJsonCount > 0) && (
        <p className="import-job-note">
          {t("imports.sidecarSummary", {
            sidecars: job.orphanSidecarCount,
            json: job.ignoredJsonCount,
          })}
        </p>
      )}
      {category && <ImportItemDetails jobId={job.id} category={category} />}
      {job.state !== "running" && (
        <div className="import-job-actions">
          {hasErrors && job.failedCount > 0 && (
            <button
              className="auth-secondary import-retry"
              disabled={busy}
              onClick={onRetry}
              type="button"
            >
              <RefreshCw /> {t("imports.retryFailed")}
            </button>
          )}
          <button
            className="auth-secondary import-remove"
            disabled={busy}
            onClick={onRemove}
            type="button"
          >
            <Trash2 /> {t("imports.remove")}
          </button>
        </div>
      )}
    </article>
  );
}

function ImportCount({
  label,
  value,
  tone,
  active,
  onClick,
}: {
  label: string;
  value: number;
  tone?: "danger";
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      className={[
        tone === "danger" && value ? "import-count-danger" : "",
        active ? "import-count-active" : "",
      ]
        .filter(Boolean)
        .join(" ")}
      disabled={value === 0}
      aria-expanded={active}
      onClick={onClick}
      type="button"
    >
      <span>
        <strong>{value}</strong>
        {label}
      </span>
      {value > 0 && <ChevronDown />}
    </button>
  );
}

function ImportItemDetails({
  jobId,
  category,
}: {
  jobId: string;
  category: ImportItemCategory;
}) {
  const { locale, t } = usePreferences();
  const results = useInfiniteQuery({
    queryKey: ["import-items", jobId, category],
    initialPageParam: "",
    queryFn: ({ pageParam, signal }) =>
      listImportItems(jobId, category, pageParam || undefined, signal),
    getNextPageParam: (page) => page.nextCursor,
  });
  const items = results.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <section className="import-item-results" aria-live="polite">
      <h3>{categoryLabel(category, locale)}</h3>
      {results.isPending ? (
        <Loader2 className="auth-spinner" />
      ) : results.error ? (
        <p className="auth-error">{t("imports.detailsFailed")}</p>
      ) : items.length ? (
        <>
          <ul>
            {items.map((item) => (
              <ImportItemRow key={item.id} item={item} />
            ))}
          </ul>
          {results.hasNextPage && (
            <button
              className="auth-secondary import-results-more"
              disabled={results.isFetchingNextPage}
              onClick={() => void results.fetchNextPage()}
              type="button"
            >
              {results.isFetchingNextPage ? (
                <Loader2 className="spin" />
              ) : (
                <ChevronDown />
              )}
              {t("imports.loadMore")}
            </button>
          )}
        </>
      ) : (
        <p className="import-job-note">{t("imports.noMatches")}</p>
      )}
    </section>
  );
}

function ImportItemRow({ item }: { item: ImportItemResult }) {
  const { locale } = usePreferences();
  const outcome = item.outcomeCode ?? item.state;
  return (
    <li>
      <div>
        <strong>{item.name}</strong>
        <code>{item.relativePath}</code>
      </div>
      <p>{outcomeLabel(outcome, locale)}</p>
      {item.warningCodes.length > 0 && (
        <ul className="import-warning-list">
          {item.warningCodes.map((warning) => (
            <li key={warning}>{warningLabel(warning, locale)}</li>
          ))}
        </ul>
      )}
      {item.error && <p className="import-item-error">{item.error}</p>}
    </li>
  );
}

const categoryLabels: Record<
  "en" | "es",
  Record<ImportItemCategory, string>
> = {
  en: {
    all: "All discovered items",
    imported: "Imported items",
    skipped: "Skipped files",
    duplicates: "Skipped duplicates",
    name_conflicts: "Skipped name conflicts",
    warnings: "Metadata warnings",
    failed: "Failed items",
  },
  es: {
    all: "Todos los elementos descubiertos",
    imported: "Elementos importados",
    skipped: "Archivos omitidos",
    duplicates: "Duplicados omitidos",
    name_conflicts: "Conflictos de nombre omitidos",
    warnings: "Avisos de metadatos",
    failed: "Elementos fallidos",
  },
};

const outcomeLabels: Record<"en" | "es", Record<string, string>> = {
  en: {
    imported_new_original: "Imported; a new original was stored.",
    imported_reused_original:
      "Imported; the existing stored original was reused.",
    duplicate_active:
      "Skipped because identical bytes already exist in the active library.",
    duplicate_trashed:
      "Skipped because identical bytes already exist in Trash.",
    name_conflict:
      "Skipped because a different active file has this name in the destination folder.",
    source_changed:
      "Failed because the source changed after the import was scanned.",
    destination_unavailable:
      "Failed because the selected destination is no longer active.",
    read_failed: "Failed while reading the source file.",
    storage_failed: "Failed while storing the original.",
    catalog_failed: "Failed while committing the catalog item.",
    pending: "Waiting to be processed.",
    processing: "Currently being processed.",
  },
  es: {
    imported_new_original: "Importado; se almacenó un original nuevo.",
    imported_reused_original:
      "Importado; se reutilizó el original que ya estaba almacenado.",
    duplicate_active:
      "Omitido porque ya existen bytes idénticos en la biblioteca activa.",
    duplicate_trashed:
      "Omitido porque ya existen bytes idénticos en la Papelera.",
    name_conflict:
      "Omitido porque otro archivo activo tiene este nombre en la carpeta de destino.",
    source_changed:
      "Falló porque el origen cambió después de analizar la importación.",
    destination_unavailable:
      "Falló porque el destino seleccionado ya no está activo.",
    read_failed: "Falló al leer el archivo de origen.",
    storage_failed: "Falló al almacenar el original.",
    catalog_failed: "Falló al guardar el elemento en el catálogo.",
    pending: "En espera de procesamiento.",
    processing: "Se está procesando.",
  },
};

const warningLabels: Record<"en" | "es", Record<string, string>> = {
  en: {
    sidecar_missing:
      "No matching Google metadata sidecar was found; the original was still imported.",
    sidecar_invalid:
      "The matching Google metadata sidecar was malformed and was ignored.",
    sidecar_title_mismatch:
      "The sidecar title did not match the media filename.",
    sidecar_ambiguous:
      "More than one sidecar could match this media file, so none was used.",
    taken_time_ignored:
      "The Google taken-time value was invalid and was ignored.",
    description_truncated:
      "The imported description exceeded the catalog limit and was shortened.",
    location_ignored:
      "The Google location was incomplete or invalid and was ignored.",
    altitude_ignored: "The Google altitude was invalid and was ignored.",
    warning_details_unavailable:
      "The stored warning details could not be read.",
  },
  es: {
    sidecar_missing:
      "No se encontró un archivo complementario de metadatos de Google; el original sí se importó.",
    sidecar_invalid:
      "El archivo complementario de Google estaba dañado y se ignoró.",
    sidecar_title_mismatch:
      "El título del archivo complementario no coincidía con el nombre del archivo multimedia.",
    sidecar_ambiguous:
      "Más de un archivo complementario podía corresponder a este archivo, así que no se usó ninguno.",
    taken_time_ignored:
      "La fecha de captura de Google no era válida y se ignoró.",
    description_truncated:
      "La descripción importada superaba el límite del catálogo y se acortó.",
    location_ignored:
      "La ubicación de Google estaba incompleta o no era válida y se ignoró.",
    altitude_ignored: "La altitud de Google no era válida y se ignoró.",
    warning_details_unavailable:
      "No se pudieron leer los detalles almacenados del aviso.",
  },
};

function humanize(value: string) {
  return value.replaceAll("_", " ");
}

function importStateLabel(state: string, locale: "en" | "es") {
  const labels: Record<string, string> =
    locale === "es"
      ? {
          pending: "pendiente",
          running: "en curso",
          completed: "completada",
          completed_with_errors: "completada con errores",
          failed: "fallida",
        }
      : {};
  return labels[state] ?? humanize(state);
}

function categoryLabel(category: ImportItemCategory, locale: "en" | "es") {
  return categoryLabels[locale][category];
}

function outcomeLabel(outcome: string, locale: "en" | "es") {
  return outcomeLabels[locale][outcome] ?? humanize(outcome);
}

function warningLabel(warning: string, locale: "en" | "es") {
  return warningLabels[locale][warning] ?? humanize(warning);
}
