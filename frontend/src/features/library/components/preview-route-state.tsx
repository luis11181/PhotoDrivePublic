"use client";

import { CircleAlert, Loader2, RefreshCw, X } from "lucide-react";
import { useEffect, useRef } from "react";
import { usePreferences } from "@/lib/preferences";

export function PreviewRouteState({
  pending,
  onClose,
  onRetry,
}: {
  pending: boolean;
  onClose: () => void;
  onRetry?: () => void;
}) {
  const closeButton = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  const { t } = usePreferences();

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    closeButton.current?.focus();

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onCloseRef.current();
    }

    window.addEventListener("keydown", handleKeyDown);
    return () => {
      document.body.style.overflow = previousOverflow;
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, []);

  return (
    <div
      className="photo-viewer preview-route-state"
      role="dialog"
      aria-modal="true"
      aria-labelledby="preview-route-title"
      aria-busy={pending}
    >
      <div className="photo-viewer-toolbar preview-route-toolbar">
        <button
          ref={closeButton}
          className="icon-button"
          type="button"
          onClick={onClose}
          aria-label={t("viewer.close")}
        >
          <X />
        </button>
      </div>
      <section className="preview-route-card" aria-live="polite">
        {pending ? <Loader2 className="spin" /> : <CircleAlert />}
        <h2 id="preview-route-title">
          {pending ? t("viewer.loading") : t("viewer.unavailableTitle")}
        </h2>
        <p>{pending ? t("viewer.loadingHelp") : t("viewer.unavailableHelp")}</p>
        {!pending && onRetry && (
          <button className="secondary" type="button" onClick={onRetry}>
            <RefreshCw />
            {t("viewer.retry")}
          </button>
        )}
      </section>
    </div>
  );
}
