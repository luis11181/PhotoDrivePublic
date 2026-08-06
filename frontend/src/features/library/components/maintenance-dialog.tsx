"use client";

import type { DeveloperBackupReport, DeveloperMaintenanceReport } from "@/lib/api";
import { formatBytes } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";
import { useState } from "react";

export function MaintenanceDialog({
  auditing,
  backingUp,
  report,
  backupReport,
  onAudit,
  onBackup,
  cleaning,
  cleanupRevision,
  onCleanup,
  onClose,
  embedded = false,
}: {
  auditing: boolean;
  backingUp: boolean;
  report: DeveloperMaintenanceReport | null;
  backupReport: DeveloperBackupReport | null;
  onAudit: () => void;
  onBackup: () => void;
  cleaning: boolean;
  cleanupRevision: number;
  onCleanup: (candidateIds: string[], confirmationPhrase: string, expectedBytes: number) => void;
  onClose?: () => void;
  embedded?: boolean;
}) {
  const { t } = usePreferences();
  const busy = auditing || backingUp || cleaning;
  return (
    <div className={embedded ? "maintenance-embedded" : "account-backdrop"} onMouseDown={embedded ? undefined : onClose}>
      <section
        className={embedded ? "maintenance-surface" : "account-dialog maintenance-dialog"}
        aria-modal={embedded ? undefined : "true"}
        aria-labelledby="maintenance-title"
        role={embedded ? undefined : "dialog"}
        onMouseDown={(event) => event.stopPropagation()}
      >
        {!embedded && <button
          className="account-close"
          aria-label={t("maintenance.close")}
          onClick={onClose}
          type="button"
        >
          ×
        </button>}
        <p className="auth-eyebrow">{t("developer.eyebrow")}</p>
        <h2 id="maintenance-title">{t("maintenance.title")}</h2>
        <p className="auth-copy">{t("maintenance.intro")}</p>
        <div className="maintenance-actions">
          <section className="maintenance-action">
            <h3>{t("maintenance.auditTitle")}</h3>
            <p className="auth-copy">
              <strong>{t("maintenance.useWhen")}</strong>{" "}
              {t("maintenance.auditWhen")}
            </p>
            <p className="auth-copy">
              <strong>{t("maintenance.whatItDoes")}</strong>{" "}
              {t("maintenance.auditDoes")}
            </p>
            <p className="auth-copy">
              <strong>{t("maintenance.safeBoundary")}</strong>{" "}
              {t("maintenance.auditBoundary")}
            </p>
            <button className="auth-primary" disabled={busy} onClick={onAudit} type="button">
              {auditing ? t("maintenance.checking") : t("maintenance.check")}
            </button>
          </section>
          <section className="maintenance-action">
            <h3>{t("maintenance.backupTitle")}</h3>
            <p className="auth-copy">
              <strong>{t("maintenance.useWhen")}</strong>{" "}
              {t("maintenance.backupWhen")}
            </p>
            <p className="auth-copy">
              <strong>{t("maintenance.whatItSaves")}</strong>{" "}
              {t("maintenance.backupSaves")}
            </p>
            <p className="auth-copy">
              <strong>{t("maintenance.verification")}</strong>{" "}
              {t("maintenance.backupVerification")}
            </p>
            <button className="secondary" disabled={busy} onClick={onBackup} type="button">
              {backingUp ? t("maintenance.creating") : t("maintenance.create")}
            </button>
          </section>
        </div>
        {report && <StorageAuditReport report={report} />}
        {backupReport && <BackupReport report={backupReport} />}
        {report && report.cleanupCandidates.length > 0 && (
          <StorageCleanupControls
            busy={busy}
            candidates={report.cleanupCandidates}
            cleaning={cleaning}
            key={`${report.ranAt}:${cleanupRevision}:${report.cleanupCandidates.map((candidate) => candidate.id).join(",")}`}
            onCleanup={onCleanup}
          />
        )}
      </section>
    </div>
  );
}

function StorageAuditReport({ report }: { report: DeveloperMaintenanceReport }) {
  const { locale, t } = usePreferences();
  return (
    <section className="maintenance-report" aria-label={t("maintenance.auditReport")}>
      <h3>{t("maintenance.auditReport")}</h3>
      <p className="auth-copy">{t("maintenance.auditReportHelp", { date: formatRunAt(report.ranAt, locale) })}</p>
      <ReportSection title={t("maintenance.integrityCleanup")}>
        <Metric label={t("maintenance.missingOriginals")} value={report.missingOriginalCount} />
        <Metric label={t("maintenance.orphanedBlobs")} value={`${report.orphanedCatalogBlobCount} · ${formatBytes(report.orphanedCatalogBlobBytes)}`} />
        <Metric label={t("maintenance.untrackedObjects")} value={report.untrackedS3OriginalCount} />
        <Metric label={t("maintenance.pendingDeletion")} value={`${report.pendingPermanentDeletionBlobCount} · ${formatBytes(report.pendingPermanentDeletionBytes)}`} />
        <Metric label={t("maintenance.selectedCandidates")} value={report.pendingCleanupCandidateCount} />
      </ReportSection>
      <p className="auth-copy"><strong>{t("maintenance.readingResults")}</strong>{" "}{t("maintenance.readingResultsHelp")}</p>
      <ReportSection title={t("maintenance.catalogStorage")}>
        <Metric label={t("maintenance.files")} value={report.catalogFileCount} />
        <Metric label={t("maintenance.folders")} value={report.catalogFolderCount} />
        <Metric label={t("maintenance.referencedOriginals")} value={`${report.referencedBlobCount} · ${formatBytes(report.referencedBlobBytes)}`} />
      </ReportSection>
      <ReportSection title={t("maintenance.localStorage")}>
        <Metric label={t("maintenance.originalCache")} value={`${formatBytes(report.originalCacheBytes)} / ${formatBytes(report.originalCacheLimitBytes)}`} />
        <Metric label={t("maintenance.originalEntries")} value={report.originalCacheEntryCount} />
        <Metric label={t("maintenance.thumbnailCache")} value={`${report.thumbnailCacheEntryCount} · ${formatBytes(report.thumbnailCacheBytes)}`} />
        <Metric label={t("maintenance.stagingRemoved")} value={`${report.removedCompletedStagingFiles} · ${formatBytes(report.removedCompletedStagingBytes)}`} />
        <Metric label={t("maintenance.cacheEvicted")} value={`${report.evictedCacheEntries} · ${formatBytes(report.evictedCacheBytes)}`} />
      </ReportSection>
      <ReportSection title={t("maintenance.versionedInventory")}>
        <Metric label={t("maintenance.originalVersions")} value={`${report.s3OriginalKeyCount} / ${report.s3OriginalVersionCount}`} />
        <Metric label={t("maintenance.originalBytes")} value={formatBytes(report.s3OriginalVersionBytes)} />
        <Metric label={t("maintenance.manifestVersions")} value={`${report.s3RecoveryManifestKeyCount} / ${report.s3RecoveryManifestVersionCount}`} />
        <Metric label={t("maintenance.manifestBytes")} value={formatBytes(report.s3RecoveryManifestVersionBytes)} />
        <Metric label={t("maintenance.deleteMarkers")} value={report.s3DeleteMarkerCount} />
      </ReportSection>
      <IssueList issues={report.issues} />
    </section>
  );
}

function BackupReport({ report }: { report: DeveloperBackupReport }) {
  const { locale, t } = usePreferences();
  return (
    <section className="maintenance-report" aria-label={t("maintenance.backupReport")}>
      <h3>{t("maintenance.backupReport")}</h3>
      <p className="auth-copy">{t("maintenance.backupReportHelp", { date: formatRunAt(report.ranAt, locale) })}</p>
      <ReportSection title={t("maintenance.backupContents")}>
        <Metric label={t("maintenance.libraries")} value={report.libraryCount} />
        <Metric label={t("maintenance.folders")} value={report.folderCount} />
        <Metric label={t("maintenance.files")} value={report.fileCount} />
        <Metric label={t("maintenance.blobVersions")} value={`${report.blobCount} / ${report.fileVersionCount}`} />
        <Metric label={t("maintenance.metadataRecords")} value={report.fileVersionMetadataCount} />
      </ReportSection>
      <ReportSection title={t("maintenance.remoteVerification")}>
        <Metric label={t("maintenance.remoteBackup")} value={report.remoteBackupVerified ? t("maintenance.verified") : t("maintenance.needsAttention")} />
        <Metric label={t("maintenance.verifiedBytes")} value={formatBytes(report.remoteBackupBytes)} />
        <Metric label={t("maintenance.artifacts")} value={report.remoteBackupArtifactCount} />
        <Metric label={t("maintenance.backupSet")} value={report.remoteBackupSetId ?? t("maintenance.notVerifiedRemote")} />
      </ReportSection>
      <IssueList issues={report.issues} />
    </section>
  );
}

function ReportSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="maintenance-report-section">
      <h4>{title}</h4>
      <div className="detail-list">{children}</div>
    </section>
  );
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return <span>{label}<strong>{value}</strong></span>;
}

function IssueList({ issues }: { issues: DeveloperMaintenanceReport["issues"] | DeveloperBackupReport["issues"] }) {
  const { locale, t } = usePreferences();
  if (issues.length === 0) return <p className="maintenance-no-issues">{t("maintenance.noIssues")}</p>;
  return (
    <section className="maintenance-issues" aria-label={t("maintenance.reportedIssues")}>
      <h4>{t("maintenance.reportedIssues")}</h4>
      {issues.map((issue) => (
        <p className={`maintenance-issue maintenance-issue-${issue.severity}`} key={`${issue.code}:${issue.message}`}>
          <strong>{issueSeverity(issue.severity, locale)}</strong> · {issue.message}
        </p>
      ))}
    </section>
  );
}

function StorageCleanupControls({
  busy,
  candidates,
  cleaning,
  onCleanup,
}: {
  busy: boolean;
  candidates: DeveloperMaintenanceReport["cleanupCandidates"];
  cleaning: boolean;
  onCleanup: (candidateIds: string[], confirmationPhrase: string, expectedBytes: number) => void;
}) {
  const { t } = usePreferences();
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [confirmed, setConfirmed] = useState(false);
  const [confirmationPhrase, setConfirmationPhrase] = useState("");
  const selected = candidates.filter((candidate) => selectedIds.includes(candidate.id));
  const selectedCount = selected.length;
  const selectedBytes = selected.reduce((total, candidate) => total + candidate.sizeBytes, 0);
  return (
    <div className="maintenance-cleanup">
      <h3>{t("maintenance.cleanupTitle")}</h3>
      <p className="auth-copy">
        <strong>{t("maintenance.cleanupReview")}</strong>{" "}
        {t("maintenance.cleanupReviewHelp")}
      </p>
      <p className="auth-copy">
        <strong>{t("maintenance.beforeDeletion")}</strong>{" "}
        {t("maintenance.beforeDeletionHelp")}
      </p>
      <p className="auth-copy">
        <strong>{t("maintenance.permanentEffect")}</strong>{" "}
        {t("maintenance.permanentEffectHelp")}
      </p>
      <div className="maintenance-candidate-list">
        {candidates.map((candidate) => (
          <label className="confirm-checkbox" key={candidate.id}>
            <input
              checked={selectedIds.includes(candidate.id)}
              disabled={busy}
              onChange={(event) =>
                setSelectedIds((current) =>
                  event.target.checked
                    ? [...current, candidate.id]
                    : current.filter((id) => id !== candidate.id),
                )
              }
              type="checkbox"
            />
            <span>
              <strong>{candidate.kind === "orphaned_catalog_blob" ? t("maintenance.orphanedBlob") : t("maintenance.untrackedOriginal")}</strong>
              {" · "}{formatBytes(candidate.sizeBytes)}
              {candidate.attemptCount > 0 ? ` · ${t(candidate.attemptCount === 1 ? "maintenance.priorAttempt" : "maintenance.priorAttempts", { count: candidate.attemptCount })}` : ""}
              {candidate.error ? ` · ${candidate.error}` : ""}
            </span>
          </label>
        ))}
      </div>
      <p className="maintenance-selection-summary">{t("maintenance.selected", { count: selectedCount, size: formatBytes(selectedBytes) })}</p>
      <label className="confirm-checkbox">
        <input
          checked={confirmed}
          disabled={busy || selectedCount === 0}
          onChange={(event) => setConfirmed(event.target.checked)}
          type="checkbox"
        />
        {t("maintenance.understand")}
      </label>
      <label className="field confirmation-field">
        {t("maintenance.typeConfirm")} <strong>DELETE INCONSISTENCIES</strong>{" "}
        {t("maintenance.toConfirm")}
        <input
          autoComplete="off"
          disabled={busy || selectedCount === 0}
          onChange={(event) => setConfirmationPhrase(event.target.value)}
          value={confirmationPhrase}
        />
      </label>
      <button
        className="secondary destructive"
        disabled={busy || selectedCount === 0 || !confirmed || confirmationPhrase !== "DELETE INCONSISTENCIES"}
        onClick={() => onCleanup(selectedIds, confirmationPhrase, selectedBytes)}
        type="button"
      >
        {cleaning
          ? t("maintenance.deleting")
          : t("maintenance.deleteReviewed", { count: selectedCount })}
      </button>
    </div>
  );
}

function formatRunAt(value: string, locale: "en" | "es") {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleString(locale === "es" ? "es-ES" : "en-US");
}

function issueSeverity(value: string, locale: "en" | "es") {
  if (locale !== "es") return value;
  return (
    {
      info: "información",
      warning: "aviso",
      error: "error",
      critical: "crítico",
    }[value] ?? value
  );
}
