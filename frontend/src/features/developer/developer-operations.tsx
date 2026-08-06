"use client";

import { useQuery } from "@tanstack/react-query";
import {
  Activity,
  CircleCheck,
  Database,
  HardDrive,
  Images,
  Loader2,
  RefreshCw,
  ShieldCheck,
  ShieldAlert,
  TriangleAlert,
} from "lucide-react";
import { useState } from "react";
import { BackToLibraryButton } from "@/components/back-to-library-button";
import { MaintenanceDialog } from "@/features/library/components/maintenance-dialog";
import type { CurrentUser } from "@/lib/auth";
import {
  cancelCacheBuild,
  cleanupStorageInconsistencies,
  getCacheBuildOptions,
  getDeveloperBackupStatus,
  getDeveloperOperationalHealth,
  listCacheBuilds,
  previewCacheBuild,
  retryCacheBuild,
  runDeveloperBackup,
  runDeveloperStorageAudit,
  startCacheBuild,
  type CacheBuildJob,
  type CacheBuildOptions,
  type DeveloperBackupReport,
  type DeveloperBackupStatus,
  type DeveloperMaintenanceReport,
  type DeveloperOperationalHealth,
} from "@/lib/api";
import { formatBytes, formatNumber } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";

const activeStates = ["pending", "running", "cancelling"];

export function DeveloperOperations({ currentUser }: { currentUser: CurrentUser }) {
  const { locale, t } = usePreferences();
  const jobs = useQuery({
    queryKey: ["cache-build-jobs"],
    queryFn: listCacheBuilds,
    enabled: currentUser.isOwner,
    refetchInterval: (query) =>
      query.state.data?.some((job) => activeStates.includes(job.state))
        ? 2_000
        : false,
  });
  const currentBuild =
    jobs.data?.find((job) => activeStates.includes(job.state)) ?? jobs.data?.[0];
  const buildActive = Boolean(currentBuild && activeStates.includes(currentBuild.state));
  const options = useQuery({
    queryKey: ["cache-build-options"],
    queryFn: getCacheBuildOptions,
    enabled: currentUser.isOwner,
    refetchInterval: buildActive ? 5_000 : false,
  });
  const backupStatus = useQuery({
    queryKey: ["developer-backup-status"],
    queryFn: getDeveloperBackupStatus,
    enabled: currentUser.isOwner,
    refetchInterval: 60_000,
  });
  const operationalHealth = useQuery({
    queryKey: ["developer-operational-health"],
    queryFn: getDeveloperOperationalHealth,
    enabled: currentUser.isOwner,
    refetchInterval: 60_000,
  });
  const [keepOriginals, setKeepOriginals] = useState(true);
  const [cacheBusy, setCacheBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [auditing, setAuditing] = useState(false);
  const [backingUp, setBackingUp] = useState(false);
  const [cleaning, setCleaning] = useState(false);
  const [cleanupRevision, setCleanupRevision] = useState(0);
  const [maintenanceReport, setMaintenanceReport] = useState<DeveloperMaintenanceReport | null>(null);
  const [backupReport, setBackupReport] = useState<DeveloperBackupReport | null>(null);

  if (!currentUser.isOwner) {
    return (
      <main className="auth-page">
        <section className="auth-card">
          <ShieldAlert className="auth-spinner" />
          <h1>{t("users.ownerRequired")}</h1>
          <p className="auth-copy">{t("developer.ownerHelp")}</p>
          <BackToLibraryButton className="auth-secondary" label={t("developer.backPhotos")} />
        </section>
      </main>
    );
  }

  const completedCount = currentBuild ? finishedCount(currentBuild) : 0;
  const cacheState = buildActive && currentBuild
    ? cacheStateLabel(currentBuild, completedCount, t)
    : catalogReadinessLabel(
        options.data?.distinctOriginalCount ?? 0,
        options.data?.gridThumbnailCount ?? 0,
        options.data?.cachedOriginalCount ?? 0,
        t,
      );

  async function refreshOperations() {
    await Promise.all([options.refetch(), jobs.refetch(), backupStatus.refetch(), operationalHealth.refetch()]);
  }

  async function buildCatalogCache() {
    setCacheBusy(true);
    setError("");
    setMessage("");
    try {
      const request = { keepOriginals };
      const preview = await previewCacheBuild(request);
      if (preview.queuedItemCount === 0) {
        setMessage(
          keepOriginals
            ? t("developer.noDownloadRetained")
            : t("developer.noDownload"),
        );
        await options.refetch();
        return;
      }
      const started = await startCacheBuild(request, preview);
      setMessage(
        t("developer.started", {
          count: started.queuedItemCount,
          size: formatBytes(started.plannedDownloadBytes),
        }),
      );
      await Promise.all([jobs.refetch(), options.refetch()]);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("developer.cacheFailed"));
    } finally {
      setCacheBusy(false);
    }
  }

  async function runCurrentAction(action: () => Promise<unknown>, successMessage: string) {
    setCacheBusy(true);
    setError("");
    try {
      await action();
      setMessage(successMessage);
      await Promise.all([jobs.refetch(), options.refetch()]);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("developer.cacheFailed"));
    } finally {
      setCacheBusy(false);
    }
  }

  return (
    <main className="imports-page developer-page">
      <header className="imports-header developer-header">
        <div>
          <p className="auth-eyebrow">{t("developer.eyebrow")}</p>
          <h1>{t("developer.title")}</h1>
          <p>{t("developer.intro")}</p>
        </div>
        <div className="developer-header-actions">
          <button className="imports-refresh" aria-label={t("developer.refresh")} onClick={() => void refreshOperations()} type="button">
            <RefreshCw className={options.isFetching || jobs.isFetching || backupStatus.isFetching || operationalHealth.isFetching ? "spin" : ""} />
          </button>
          <BackToLibraryButton className="auth-secondary imports-back" label={t("developer.backPhotos")} />
        </div>
      </header>

      {(error || message) && <p className={error ? "auth-error" : "auth-success"} role="status">{error || message}</p>}

      <OwnerHealthCenter
        backup={backupStatus.data}
        backupFailed={backupStatus.isError}
        build={currentBuild}
        jobsFailed={jobs.isError}
        jobsLoading={jobs.isPending}
        loading={options.isPending || backupStatus.isPending || jobs.isPending}
        maintenanceReport={maintenanceReport}
        operational={operationalHealth.data}
        operationalFailed={operationalHealth.isError}
        options={options.data}
        optionsFailed={options.isError}
      />

      <section className="imports-panel developer-overview">
        <div className="imports-section-heading">
          <div>
            <h2>{t("developer.overview")}</h2>
            <p>{t("developer.overviewHelp")}</p>
          </div>
          <Database />
        </div>
        {options.isPending ? <Loader2 className="auth-spinner" /> : options.data ? (
          <div className="developer-metrics">
            <Metric label={t("developer.catalogCache")} value={cacheState} />
            <Metric label={t("developer.thumbnailsReady")} value={`${options.data.gridThumbnailCount} / ${options.data.distinctOriginalCount}`} />
            <Metric label={t("developer.originalsLocal")} value={`${options.data.cachedOriginalCount} / ${options.data.distinctOriginalCount}`} />
            <Metric label={t("developer.activeMedia")} value={String(options.data.catalogMediaItemCount)} />
            <Metric label={t("developer.allAccountFiles")} value={formatNumber(options.data.catalogFileCount, locale)} />
            <Metric label={t("developer.allAccountStorage")} value={formatBytes(options.data.storedOriginalBytes)} />
            <Metric label={t("developer.cacheSpace")} value={t("developer.usedOf", { used: formatBytes(options.data.originalCacheBytes), total: formatBytes(options.data.originalCacheMaxBytes) })} />
            <Metric label={t("developer.freeDisk")} value={formatBytes(options.data.availableDiskBytes)} />
            <BackupMetric status={backupStatus.data} loading={backupStatus.isPending} failed={backupStatus.isError} locale={locale} />
          </div>
        ) : <p className="auth-error">{t("developer.configurationFailed")}</p>}
        <p className="auth-copy developer-overview-note">{t("developer.overviewNote")}</p>
      </section>

      <section className="imports-panel" id="cache-preparation">
        <div className="imports-section-heading">
          <div>
            <h2>{t("developer.prepare")}</h2>
            <p>{t("developer.prepareHelp")}</p>
          </div>
          <HardDrive />
        </div>
        <div className="imports-form developer-cache-form">
          <label className="imports-confirm developer-cache-choice">
            <input checked={keepOriginals} disabled={buildActive} onChange={(event) => setKeepOriginals(event.target.checked)} type="checkbox" />
            <span><strong>{t("developer.keepOriginals")}</strong><small>{t("developer.keepOriginalsHelp", { limit: formatBytes(options.data?.originalCacheMaxBytes ?? 0) })}</small></span>
          </label>
          {!keepOriginals && <p className="developer-callout"><strong>{t("developer.thumbnailOnlyTitle")}</strong> {t("developer.thumbnailOnlyHelp")}</p>}
          <p className="auth-copy"><strong>{t("developer.safeEffect")}</strong> {t("developer.safeEffectHelp")}</p>
          <button className="auth-primary" disabled={cacheBusy || buildActive || options.isPending} onClick={() => void buildCatalogCache()} type="button">
            {cacheBusy ? t("developer.starting") : buildActive ? t("developer.preparing") : t("developer.prepareNow")}
          </button>
        </div>

        {currentBuild && (
          <article className="developer-job developer-current-build">
            <div className="developer-job-heading">
              <strong>{t("developer.currentPreparation")}</strong>
              <span className={`developer-state developer-state-${currentBuild.state}`}>{cacheStateLabel(currentBuild, completedCount, t)}</span>
            </div>
            <p>{currentBuild.keepOriginals ? t("developer.creatingRetained") : t("developer.creatingTemporary")} · {t("developer.upToS3", { size: formatBytes(currentBuild.plannedDownloadBytes) })}</p>
            <progress max={Math.max(1, currentBuild.queuedItemCount)} value={completedCount} />
            <small>{t("developer.finished", { done: completedCount, total: currentBuild.queuedItemCount })}{currentBuild.failedCount ? ` · ${t("developer.failedCount", { count: currentBuild.failedCount })}` : ""}</small>
            <div className="developer-job-actions">
              {currentBuild && ["pending", "running"].includes(currentBuild.state) && (
                <button className="secondary" disabled={cacheBusy} onClick={() => void runCurrentAction(() => cancelCacheBuild(currentBuild.id), t("developer.cancelRequested"))} type="button">{t("developer.stop")}</button>
              )}
              {currentBuild.failedCount > 0 && !activeStates.includes(currentBuild.state) && (
                <button className="secondary" disabled={cacheBusy} onClick={() => void runCurrentAction(() => retryCacheBuild(currentBuild.id), t("developer.retryStarted"))} type="button">{t("developer.retry")}</button>
              )}
            </div>
          </article>
        )}
      </section>

      <section className="imports-panel developer-maintenance-panel" id="storage-safety">
        <MaintenanceDialog
          auditing={auditing}
          backingUp={backingUp}
          report={maintenanceReport}
          backupReport={backupReport}
          cleaning={cleaning}
          cleanupRevision={cleanupRevision}
          embedded
          onAudit={() => void runAudit()}
          onBackup={() => void runBackup()}
          onCleanup={(candidateIds, phrase, expectedBytes) => void runCleanup(candidateIds, phrase, expectedBytes)}
        />
      </section>
    </main>
  );

  async function runAudit() {
    setAuditing(true); setError("");
    try {
      const report = await runDeveloperStorageAudit();
      setMaintenanceReport(report);
      setMessage(report.issues.length || report.cleanupCandidates.length
        ? t("developer.auditIssues")
        : t("developer.auditClean"));
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : t("developer.auditFailed")); }
    finally { setAuditing(false); }
  }

  async function runBackup() {
    setBackingUp(true); setError("");
    try {
      const report = await runDeveloperBackup();
      setBackupReport(report);
      if (report.remoteBackupVerified) await backupStatus.refetch();
      setMessage(report.remoteBackupVerified
        ? t("developer.backupSuccess")
        : t("developer.backupAttention"));
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : t("developer.backupFailed")); }
    finally { setBackingUp(false); }
  }

  async function runCleanup(candidateIds: string[], confirmationPhrase: string, expectedCandidateBytes: number) {
    setCleaning(true); setError("");
    try {
      const result = await cleanupStorageInconsistencies({ candidateIds, expectedCandidateCount: candidateIds.length, expectedCandidateBytes, confirmationPhrase });
      await backupStatus.refetch();
      setMaintenanceReport((current) => current ? { ...current, pendingCleanupCandidateCount: result.remainingCandidates.length, cleanupCandidates: result.remainingCandidates } : current);
      setMessage(t("developer.cleanupSuccess", {
        backup: result.safetyBackupSetId ?? t("maintenance.verified"),
        count: result.deletedCandidateCount,
      }));
    } catch (reason) { setError(reason instanceof Error ? reason.message : t("developer.cleanupFailed")); }
    finally { setCleaning(false); setCleanupRevision((value) => value + 1); }
  }
}

type HealthTone = "healthy" | "attention" | "checking" | "neutral";

function OwnerHealthCenter({
  backup,
  backupFailed,
  build,
  jobsFailed,
  jobsLoading,
  loading,
  maintenanceReport,
  operational,
  operationalFailed,
  options,
  optionsFailed,
}: {
  backup?: DeveloperBackupStatus;
  backupFailed: boolean;
  build?: CacheBuildJob;
  jobsFailed: boolean;
  jobsLoading: boolean;
  loading: boolean;
  maintenanceReport: DeveloperMaintenanceReport | null;
  operational?: DeveloperOperationalHealth;
  operationalFailed: boolean;
  options?: CacheBuildOptions;
  optionsFailed: boolean;
}) {
  const { t } = usePreferences();
  const backupTone: HealthTone = backupFailed || operationalFailed
    ? "attention"
    : !backup || !operational
      ? "checking"
      : backup.state === "current" && operational.consecutiveBackupFailureCount === 0
        ? "healthy"
        : "attention";
  const missingThumbnails = options
    ? Math.max(0, options.distinctOriginalCount - options.gridThumbnailCount)
    : 0;
  const previewTone: HealthTone = optionsFailed
    ? "attention"
    : !options
      ? "checking"
      : missingThumbnails > 0
        ? "attention"
        : "healthy";
  const operationTone: HealthTone = jobsFailed
    ? "attention"
    : jobsLoading
      ? "checking"
      : !build
        ? "healthy"
        : activeStates.includes(build.state)
          ? "checking"
          : build.failedCount > 0 || build.state === "completed_with_errors"
            ? "attention"
            : "healthy";
  const auditHasIssues = Boolean(
    maintenanceReport &&
      (maintenanceReport.issues.length > 0 ||
        maintenanceReport.cleanupCandidates.length > 0),
  );
  const auditTone: HealthTone = !maintenanceReport
    ? "neutral"
    : auditHasIssues
      ? "attention"
      : "healthy";
  const queueTone: HealthTone = operationalFailed
    ? "attention"
    : !operational
      ? "checking"
      : operational.staleUploadCount > 0 || operational.staleMetadataCount > 0 || operational.failedMetadataCount > 0 || operational.repeatedWorkerFailureCount > 0
        ? "attention"
        : "healthy";
  const capacityTone: HealthTone = operationalFailed
    ? "attention"
    : !operational
      ? "checking"
      : operational.lowDiskSpace || operational.cachePressure
        ? "attention"
        : "healthy";
  const tones = [backupTone, previewTone, operationTone, auditTone, queueTone, capacityTone];
  const overallTone: HealthTone = tones.includes("attention")
    ? "attention"
    : loading || tones.includes("checking")
      ? "checking"
      : "healthy";

  return (
    <section className={`imports-panel developer-health developer-health-${overallTone}`} aria-labelledby="owner-health-title">
      <div className="developer-health-heading">
        <span className={`developer-health-mark developer-health-mark-${overallTone}`} aria-hidden="true">
          {overallTone === "healthy" ? <CircleCheck /> : overallTone === "attention" ? <TriangleAlert /> : <Activity />}
        </span>
        <div>
          <p className="auth-eyebrow">{t("developer.healthEyebrow")}</p>
          <h2 id="owner-health-title">{t("developer.healthTitle")}</h2>
          <p>{overallHealthLabel(overallTone, t)}</p>
        </div>
      </div>
      <div className="developer-health-grid">
        <HealthCard
          action="#storage-safety"
          actionLabel={t("developer.reviewRecovery")}
          detail={backupTone === "healthy"
            ? t("developer.healthBackupCurrent")
            : backupTone === "checking"
              ? t("developer.healthChecking")
              : t("developer.healthBackupAttention")}
          icon={<ShieldCheck />}
          label={t("developer.healthBackup")}
          tone={backupTone}
        />
        <HealthCard
          action={previewTone === "attention" ? "#cache-preparation" : undefined}
          actionLabel={t("developer.prepareNow")}
          detail={previewTone === "healthy"
            ? t("developer.healthPreviewsReady")
            : previewTone === "checking"
              ? t("developer.healthChecking")
              : optionsFailed
                ? t("developer.statusUnavailable")
                : t("developer.healthPreviewsMissing", { count: missingThumbnails })}
          icon={<Images />}
          label={t("developer.healthPreviews")}
          tone={previewTone}
        />
        <HealthCard
          action={operationTone === "attention" ? "#cache-preparation" : undefined}
          actionLabel={t("developer.reviewOperation")}
          detail={jobsFailed
            ? t("developer.statusUnavailable")
            : jobsLoading
              ? t("developer.healthChecking")
              : !build
                ? t("developer.healthNoOperations")
                : activeStates.includes(build.state)
                  ? t("developer.healthOperationRunning")
                  : build.failedCount > 0
                    ? t("developer.healthOperationFailed", { count: build.failedCount })
                    : t("developer.healthOperationsGood")}
          icon={<Activity />}
          label={t("developer.healthOperations")}
          tone={operationTone}
        />
        <HealthCard
          action="#storage-safety"
          actionLabel={t("developer.reviewRecovery")}
          detail={queueTone === "checking"
            ? t("developer.healthChecking")
            : queueTone === "healthy"
              ? t("developer.healthQueuesGood")
              : t("developer.healthQueuesAttention", {
                  uploads: operational?.staleUploadCount ?? 0,
                  metadata: (operational?.staleMetadataCount ?? 0) + (operational?.failedMetadataCount ?? 0),
                  repeated: operational?.repeatedWorkerFailureCount ?? 0,
                })}
          icon={<Activity />}
          label={t("developer.healthQueues")}
          tone={queueTone}
        />
        <HealthCard
          action={capacityTone === "attention" ? "#cache-preparation" : undefined}
          actionLabel={t("developer.reviewOperation")}
          detail={capacityTone === "checking"
            ? t("developer.healthChecking")
            : capacityTone === "healthy"
              ? t("developer.healthCapacityGood", { free: formatBytes(operational?.availableDiskBytes ?? 0) })
              : operational?.lowDiskSpace
                ? t("developer.healthDiskLow", { free: formatBytes(operational.availableDiskBytes) })
                : t("developer.healthCachePressure")}
          icon={<HardDrive />}
          label={t("developer.healthCapacity")}
          tone={capacityTone}
        />
        <HealthCard
          action="#storage-safety"
          actionLabel={maintenanceReport ? t("developer.reviewAudit") : t("developer.runAudit")}
          detail={!maintenanceReport
            ? t("developer.healthAuditNotRun")
            : auditHasIssues
              ? t("developer.healthAuditAttention", {
                  count: maintenanceReport.issues.length + maintenanceReport.cleanupCandidates.length,
                })
              : t("developer.healthAuditClean")}
          icon={<Database />}
          label={t("developer.healthStorage")}
          tone={auditTone}
        />
      </div>
    </section>
  );
}

function HealthCard({ action, actionLabel, detail, icon, label, tone }: {
  action?: string;
  actionLabel: string;
  detail: string;
  icon: React.ReactNode;
  label: string;
  tone: HealthTone;
}) {
  const { t } = usePreferences();
  return (
    <article className={`developer-health-card developer-health-card-${tone}`}>
      <div className="developer-health-card-heading">
        {icon}
        <span className={`developer-health-badge developer-health-badge-${tone}`}>
          {healthStateLabel(tone, t)}
        </span>
      </div>
      <h3>{label}</h3>
      <p>{detail}</p>
      {action && <a href={action}>{actionLabel}</a>}
    </article>
  );
}

function overallHealthLabel(tone: HealthTone, t: Translate) {
  if (tone === "healthy") return t("developer.healthOverallHealthy");
  if (tone === "attention") return t("developer.healthOverallAttention");
  if (tone === "checking") return t("developer.healthOverallChecking");
  return t("developer.healthOverallNeutral");
}

function healthStateLabel(tone: HealthTone, t: Translate) {
  if (tone === "healthy") return t("developer.healthStateHealthy");
  if (tone === "attention") return t("developer.healthStateAttention");
  if (tone === "checking") return t("developer.healthStateChecking");
  return t("developer.healthStateNeutral");
}

function finishedCount(job: CacheBuildJob) {
  return job.completedCount + job.skippedCount + job.failedCount + job.cancelledCount;
}

type Translate = ReturnType<typeof usePreferences>["t"];

function cacheStateLabel(job: CacheBuildJob, completedCount: number, t: Translate) {
  if (job.state === "pending") return t("developer.statePending");
  if (job.state === "running") return t("developer.stateRunning", { done: completedCount, total: job.queuedItemCount });
  if (job.state === "cancelling") return t("developer.stateCancelling");
  if (job.state === "completed") return t("developer.stateCompleted");
  if (job.state === "completed_with_errors") return t("developer.stateErrors", { count: job.failedCount });
  if (job.state === "cancelled") return t("developer.stateCancelled");
  return job.state;
}

function catalogReadinessLabel(
  distinctOriginalCount: number,
  gridThumbnailCount: number,
  cachedOriginalCount: number,
  t: Translate,
) {
  if (distinctOriginalCount === 0) return t("developer.noActiveMedia");
  const missingThumbnails = Math.max(0, distinctOriginalCount - gridThumbnailCount);
  const originalsNotRetained = Math.max(0, distinctOriginalCount - cachedOriginalCount);
  if (missingThumbnails === 0 && originalsNotRetained === 0) return t("developer.allReady");
  if (missingThumbnails === 0) return t("developer.allThumbnails", { count: originalsNotRetained });
  return t(missingThumbnails === 1 ? "developer.thumbnailNeeds" : "developer.thumbnailsNeed", { count: missingThumbnails });
}

function Metric({ label, value }: { label: string; value: string }) {
  return <span><small>{label}</small><strong>{value}</strong></span>;
}

function BackupMetric({ status, loading, failed, locale }: { status?: DeveloperBackupStatus; loading: boolean; failed: boolean; locale: "en" | "es" }) {
  const { t } = usePreferences();
  if (loading) return <Metric label={t("developer.lastBackup")} value={t("developer.loading")} />;
  if (failed) {
    return (
      <span>
        <small>{t("developer.lastBackup")}</small>
        <strong>{t("developer.statusUnavailable")}</strong>
        <small className="developer-backup-status developer-backup-status-stale">{t("developer.refreshAgain")}</small>
      </span>
    );
  }
  if (!status || status.state === "missing" || !status.lastRemoteVerifiedAt) {
    return (
      <span>
        <small>{t("developer.lastBackup")}</small>
        <strong>{t("developer.notRecorded")}</strong>
        <small className="developer-backup-status developer-backup-status-missing">{t("developer.missingBackup")}</small>
      </span>
    );
  }

  return (
    <span>
      <small>{t("developer.lastBackup")}</small>
      <strong>{new Date(status.lastRemoteVerifiedAt).toLocaleString(locale === "es" ? "es-ES" : "en-US")}</strong>
      <small className={`developer-backup-status developer-backup-status-${status.state}`}>
        {status.state === "current" ? t("developer.current") : t("developer.backupDue")} · {formatBackupAge(status.ageHours, t)}
      </small>
    </span>
  );
}

function formatBackupAge(ageHours: number | undefined, t: Translate) {
  if (ageHours === undefined) return t("developer.ageUnavailable");
  if (ageHours < 1) return t("developer.lessHour");
  const hours = Math.floor(ageHours);
  return t(hours === 1 ? "developer.hourAgo" : "developer.hoursAgo", { count: hours });
}
