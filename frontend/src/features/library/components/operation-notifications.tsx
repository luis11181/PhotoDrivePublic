"use client";

import {
  AlertTriangle,
  CheckCircle2,
  LoaderCircle,
  X,
  XCircle,
} from "lucide-react";
import { usePreferences } from "@/lib/preferences";
import type { OperationNotification } from "../use-operation-notifications";

export function OperationNotifications({
  open,
  notifications,
  onClose,
  onDismiss,
  onClearCompleted,
}: {
  open: boolean;
  notifications: OperationNotification[];
  onClose: () => void;
  onDismiss: (id: string) => void;
  onClearCompleted: () => void;
}) {
  const { t } = usePreferences();
  if (!open) return null;
  const hasCompleted = notifications.some(
    (notification) => notification.status !== "running",
  );

  return (
    <aside className="notifications-panel" aria-label={t("topbar.notifications")}>
      <div className="notifications-header">
        <div>
          <p className="eyebrow">{t("notifications.activity")}</p>
          <h2>{t("notifications.title")}</h2>
        </div>
        <div>
          {hasCompleted && (
            <button className="text-button" onClick={onClearCompleted}>
              {t("notifications.clear")}
            </button>
          )}
          <button
            className="icon-button"
            onClick={onClose}
            aria-label={t("notifications.close")}
          >
            <X />
          </button>
        </div>
      </div>
      <div className="notifications-list" aria-live="polite">
        {notifications.length ? (
          notifications.map((notification) => (
            <NotificationRow
              key={notification.id}
              notification={notification}
              onDismiss={() => onDismiss(notification.id)}
            />
          ))
        ) : (
          <div className="notifications-empty">
            <CheckCircle2 />
            <strong>{t("notifications.empty")}</strong>
            <span>{t("notifications.emptyHelp")}</span>
          </div>
        )}
      </div>
    </aside>
  );
}

function NotificationRow({
  notification,
  onDismiss,
}: {
  notification: OperationNotification;
  onDismiss: () => void;
}) {
  const { t } = usePreferences();
  const running = notification.status === "running";
  const percentage = notification.total
    ? Math.round((notification.completed / notification.total) * 100)
    : 0;

  return (
    <article className={`notification-row ${notification.status}`}>
      <div className="notification-icon" aria-hidden="true">
        {notification.status === "running" ? (
          <LoaderCircle className="catalog-spinner" />
        ) : notification.status === "success" ? (
          <CheckCircle2 />
        ) : notification.status === "warning" ? (
          <AlertTriangle />
        ) : (
          <XCircle />
        )}
      </div>
      <div className="notification-copy">
        <div className="notification-title">
          <strong>{notification.title}</strong>
          {!running && (
            <button onClick={onDismiss} aria-label={t("notifications.dismiss")}>
              <X />
            </button>
          )}
        </div>
        <span>
          {notification.succeeded} {notification.successLabel}
          {notification.failed > 0
            ? ` · ${t("notifications.failed", {
                count: notification.failed,
              })}`
            : ""}
          {running
            ? ` · ${t("notifications.progress", {
                done: notification.completed,
                total: notification.total,
              })}`
            : ""}
        </span>
        {notification.detail && <small>{notification.detail}</small>}
        {running && (
          <div
            className="notification-progress"
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={notification.total}
            aria-valuenow={notification.completed}
          >
            <span style={{ width: `${percentage}%` }} />
          </div>
        )}
      </div>
    </article>
  );
}
