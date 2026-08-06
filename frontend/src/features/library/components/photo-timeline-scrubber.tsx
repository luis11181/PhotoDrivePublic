"use client";

import { CalendarDays } from "lucide-react";
import { useMemo, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { usePreferences } from "@/lib/preferences";
import type { PhotoTimelineAnchor, TimelineEvent } from "@/lib/types";

export function PhotoTimelineScrubber({
  anchors,
  activeKey,
  activeEvent,
  onSeek,
  events = [],
  onSelectEvent,
}: {
  anchors: PhotoTimelineAnchor[];
  activeKey?: string;
  activeEvent?: TimelineEvent;
  onSeek: (anchor: PhotoTimelineAnchor) => void;
  events?: TimelineEvent[];
  onSelectEvent?: (event: TimelineEvent) => void;
}) {
  const { locale, t } = usePreferences();
  const track = useRef<HTMLDivElement>(null);
  const dragging = useRef(false);
  const activeIndex = nearestAnchorIndex(anchors, activeKey);
  const [previewIndex, setPreviewIndex] = useState(activeIndex);
  const [engaged, setEngaged] = useState(false);
  const [openEventGroup, setOpenEventGroup] = useState<string>();

  const labels = useMemo(
    () =>
      anchors.map((anchor) => ({
        full: formatAnchor(anchor, locale, true),
        short: formatAnchor(anchor, locale, false),
      })),
    [anchors, locale],
  );
  const eventGroups = useMemo(() => groupEvents(events, anchors), [anchors, events]);
  const activeEventGroup = activeEvent
    ? eventGroups.find((group) =>
        group.events.some((timelineEvent) => timelineEvent.id === activeEvent.id),
      )
    : undefined;
  const currentIndex = engaged
    ? previewIndex
    : (activeEventGroup?.anchorIndex ?? activeIndex);
  const preview = anchors[currentIndex] ?? anchors[0];
  if (!preview) return null;
  const eventPreview = !engaged && activeEvent;

  function indexFromPointer(clientY: number) {
    const bounds = track.current?.getBoundingClientRect();
    if (!bounds || bounds.height <= 0) return 0;
    const ratio = Math.max(0, Math.min(1, (clientY - bounds.top) / bounds.height));
    return Math.round(ratio * Math.max(0, anchors.length - 1));
  }

  function moveTo(index: number, commit: boolean) {
    const nextIndex = Math.max(0, Math.min(anchors.length - 1, index));
    setPreviewIndex(nextIndex);
    if (commit) onSeek(anchors[nextIndex]);
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    let nextIndex = currentIndex;
    if (event.key === "ArrowDown" || event.key === "ArrowRight") nextIndex++;
    else if (event.key === "ArrowUp" || event.key === "ArrowLeft") nextIndex--;
    else if (event.key === "PageDown") nextIndex += Math.max(1, Math.round(anchors.length / 10));
    else if (event.key === "PageUp") nextIndex -= Math.max(1, Math.round(anchors.length / 10));
    else if (event.key === "Home") nextIndex = 0;
    else if (event.key === "End") nextIndex = anchors.length - 1;
    else return;
    event.preventDefault();
    moveTo(nextIndex, true);
  }

  return (
    <aside className="photo-timeline-scrubber" aria-label={t("timeline.scrubberRegion")}>
      <div
        ref={track}
        className={`photo-scrubber-track ${engaged ? "engaged" : ""}`}
        role="slider"
        tabIndex={0}
        aria-label={t("timeline.scrubber")}
        aria-orientation="vertical"
        aria-valuemin={0}
        aria-valuemax={anchors.length - 1}
        aria-valuenow={currentIndex}
        aria-valuetext={
          eventPreview
            ? `${t("events.timelineEvent")}: ${eventPreview.title}, ${formatEventMonth(eventPreview, locale)}`
            : `${labels[currentIndex]?.full}, ${t("timeline.itemCount", { count: preview.count })}`
        }
        onFocus={() => {
          setPreviewIndex(activeIndex);
          setEngaged(true);
        }}
        onBlur={() => setEngaged(false)}
        onKeyDown={onKeyDown}
        onPointerDown={(event) => {
          dragging.current = true;
          setEngaged(true);
          event.currentTarget.setPointerCapture(event.pointerId);
          moveTo(indexFromPointer(event.clientY), false);
        }}
        onPointerMove={(event) => {
          if (dragging.current) moveTo(indexFromPointer(event.clientY), false);
        }}
        onPointerUp={(event) => {
          if (!dragging.current) return;
          dragging.current = false;
          const index = indexFromPointer(event.clientY);
          moveTo(index, true);
          event.currentTarget.releasePointerCapture(event.pointerId);
        }}
        onPointerCancel={() => {
          dragging.current = false;
          setPreviewIndex(activeIndex);
        }}
      >
        <div className="photo-scrubber-line" aria-hidden="true" />
        {anchors.map((anchor, index) => {
          const key = anchorKey(anchor);
          const showYear = index === 0 || anchors[index - 1].year !== anchor.year;
          return (
            <span
              key={key}
              className={`photo-scrubber-mark ${key === activeKey ? "active" : ""} ${index === currentIndex ? "preview" : ""}`}
              style={{ top: `${positionPercent(index, anchors.length)}%` }}
              aria-hidden="true"
            >
              <span className="photo-scrubber-dot" />
              {showYear && <span className="photo-scrubber-year">{anchor.year}</span>}
            </span>
          );
        })}
        {onSelectEvent && eventGroups.map((group) => (
          <span
            className="photo-scrubber-event-wrap"
            key={group.key}
            style={{ top: `${positionPercent(group.anchorIndex, anchors.length)}%` }}
          >
            <button
              className={`photo-scrubber-event ${group.events.some((timelineEvent) => timelineEvent.id === activeEvent?.id) ? "active" : ""}`}
              type="button"
              aria-expanded={openEventGroup === group.key}
              aria-label={group.events.length === 1 ? `${t("events.timelineEvent")}: ${group.events[0].title}` : `${group.events.length} ${t("events.searchGroup")}`}
              title={group.events.map((event) => event.title).join(" · ")}
              onPointerDown={(event) => event.stopPropagation()}
              onClick={() => group.events.length === 1 ? onSelectEvent(group.events[0]) : setOpenEventGroup((current) => current === group.key ? undefined : group.key)}
            >
              <CalendarDays />
              {group.events.length > 1 && <b>{group.events.length}</b>}
            </button>
            {openEventGroup === group.key && group.events.length > 1 && (
              <div className="photo-scrubber-event-list">
                {group.events.map((timelineEvent) => (
                  <button key={timelineEvent.id} type="button" onPointerDown={(event) => event.stopPropagation()} onClick={() => { setOpenEventGroup(undefined); onSelectEvent(timelineEvent); }}>
                    <strong>{timelineEvent.title}</strong>
                    <small>{timelineEvent.startDate}{timelineEvent.endDate ? ` – ${timelineEvent.endDate}` : ""}</small>
                  </button>
                ))}
              </div>
            )}
          </span>
        ))}
        <span
          className="photo-scrubber-preview"
          style={{ top: `${positionPercent(currentIndex, anchors.length)}%` }}
          aria-hidden="true"
        >
          <b>
            {eventPreview
              ? formatEventMonth(eventPreview, locale)
              : labels[currentIndex]?.short}
          </b>
          <small>
            {eventPreview
              ? eventPreview.title
              : t("timeline.itemCount", { count: preview.count })}
          </small>
        </span>
      </div>
    </aside>
  );
}

function groupEvents(events: TimelineEvent[], anchors: PhotoTimelineAnchor[]) {
  const groups = new Map<string, TimelineEvent[]>();
  events.forEach((event) => {
    const key = event.startDate.slice(0, 7);
    groups.set(key, [...(groups.get(key) ?? []), event]);
  });
  return [...groups.entries()].map(([key, groupedEvents]) => {
    const [year, month] = key.split("-").map(Number);
    let anchorIndex = 0;
    let smallestDistance = Number.POSITIVE_INFINITY;
    anchors.forEach((anchor, index) => {
      const distance = Math.abs(anchor.year * 12 + anchor.month - (year * 12 + month));
      if (distance < smallestDistance) {
        smallestDistance = distance;
        anchorIndex = index;
      }
    });
    return { key, events: groupedEvents, anchorIndex };
  });
}

function nearestAnchorIndex(
  anchors: PhotoTimelineAnchor[],
  activeKey: string | undefined,
) {
  if (!activeKey || anchors.length === 0) return 0;
  const exactIndex = anchors.findIndex(
    (anchor) => anchorKey(anchor) === activeKey,
  );
  if (exactIndex >= 0) return exactIndex;
  const activeOrdinal = monthOrdinal(activeKey);
  if (activeOrdinal === undefined) return 0;
  let nearestIndex = 0;
  let nearestDistance = Number.POSITIVE_INFINITY;
  anchors.forEach((anchor, index) => {
    const distance = Math.abs(
      anchor.year * 12 + anchor.month - activeOrdinal,
    );
    if (distance < nearestDistance) {
      nearestDistance = distance;
      nearestIndex = index;
    }
  });
  return nearestIndex;
}

function monthOrdinal(key: string) {
  const match = /^(\d{4})-(\d{2})$/.exec(key);
  if (!match) return undefined;
  return Number(match[1]) * 12 + Number(match[2]);
}

export function anchorKey(anchor: Pick<PhotoTimelineAnchor, "year" | "month">) {
  return `${anchor.year}-${String(anchor.month).padStart(2, "0")}`;
}

function positionPercent(index: number, count: number) {
  return count <= 1 ? 0 : (index / (count - 1)) * 100;
}

function formatAnchor(
  anchor: Pick<PhotoTimelineAnchor, "year" | "month">,
  locale: "en" | "es",
  full: boolean,
) {
  return new Intl.DateTimeFormat(locale === "es" ? "es-ES" : "en-US", {
    month: full ? "long" : "short",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(anchor.year, anchor.month - 1, 1)));
}

function formatEventMonth(event: TimelineEvent, locale: "en" | "es") {
  return new Intl.DateTimeFormat(locale === "es" ? "es-ES" : "en-US", {
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(`${event.startDate}T00:00:00Z`));
}
