export type OperationLogLevel = "info" | "warn" | "error";
export type OperationLogEntry = {
  timestamp: string;
  level: OperationLogLevel;
  source: string;
  event: string;
  details: Record<string, unknown>;
};

declare global {
  interface Window {
    __MEDIA_EXPLORER_LOGS__?: OperationLogEntry[];
  }
}

const maximumRetainedEntries = 250;

export function logInfo(
  source: string,
  event: string,
  details: Record<string, unknown> = {},
) {
  write("info", source, event, details);
}

export function logWarning(
  source: string,
  event: string,
  details: Record<string, unknown> = {},
) {
  write("warn", source, event, details);
}

export function logError(
  source: string,
  event: string,
  details: Record<string, unknown> = {},
) {
  write("error", source, event, details);
}

function write(
  level: OperationLogLevel,
  source: string,
  event: string,
  details: Record<string, unknown>,
) {
  const entry: OperationLogEntry = {
    timestamp: new Date().toISOString(),
    level,
    source,
    event,
    details,
  };

  if (typeof window !== "undefined") {
    const entries = window.__MEDIA_EXPLORER_LOGS__ ?? [];
    entries.push(entry);
    if (entries.length > maximumRetainedEntries)
      entries.splice(0, entries.length - maximumRetainedEntries);
    window.__MEDIA_EXPLORER_LOGS__ = entries;
  }

  const message = `[${source}] ${event}`;
  // Next's development overlay treats every browser console.error as an
  // unhandled application failure. Keep the original severity in the retained
  // structured entry, but use a warning transport in development so handled
  // API and thumbnail failures remain inspectable without covering the UI.
  if (
    level === "error" &&
    typeof window !== "undefined" &&
    process.env.NODE_ENV === "development"
  )
    console.warn(`${message} [error]`, details);
  else if (level === "error") console.error(message, details);
  else if (level === "warn") console.warn(message, details);
  else console.info(message, details);
}
