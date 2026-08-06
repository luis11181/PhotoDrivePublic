"use client";

import {
  CalendarDays,
  MoreHorizontal,
} from "lucide-react";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type MouseEvent,
} from "react";
import { usePreferences } from "@/lib/preferences";
import type { PhotoGridDensity } from "@/lib/preferences";
import { translations } from "@/lib/translations";
import type { MediaItem, PhotoSort, TimelineEvent } from "@/lib/types";
import { prefetchAuthenticatedImage } from "../media-prefetch";
import { MediaCard } from "./media-card";

const PHOTO_CHUNK_SIZE = 24;
const afterNextPaint = () =>
  new Promise<void>((resolve) =>
    window.requestAnimationFrame(() => window.requestAnimationFrame(() => resolve())),
  );
const PHOTO_GRID_METRICS: Record<
  PhotoGridDensity,
  { minimumWidth: number; gap: number }
> = {
  comfortable: { minimumWidth: 240, gap: 8 },
  standard: { minimumWidth: 150, gap: 4 },
  compact: { minimumWidth: 105, gap: 3 },
};

type PhotoMonth = {
  key: string;
  label: string;
  year: string;
  items: MediaItem[];
  showHeading: boolean;
};

type PhotoChunk = {
  key: string;
  monthKey: string;
  label: string;
  year: string;
  showYear: boolean;
  showHeading: boolean;
  firstInMonth: boolean;
  items: MediaItem[];
  events: TimelineEvent[];
};

export function PhotoTimeline({
  items,
  selectedIds,
  selectionMode,
  onSelect,
  onKeyboardRange,
  onSelectAll,
  onOpen,
  onContextMenu,
  onLoadMore,
  loadingMore,
  hasMore,
  onLoadPrevious,
  loadingPrevious,
  hasPrevious,
  seekTargetKey,
  seekRequestId,
  onVisibleMonthChange,
  events = [],
  sort,
  selectedEventId,
  onSelectEvent,
  onEditEvent,
}: {
  items: MediaItem[];
  selectedIds: string[];
  selectionMode: boolean;
  onSelect: (item: MediaItem, range: boolean) => void;
  onKeyboardRange: (itemId: string) => void;
  onSelectAll: () => void;
  onOpen: (item: MediaItem) => void;
  onContextMenu?: (event: MouseEvent<HTMLElement>, item: MediaItem) => void;
  onLoadMore: () => Promise<unknown>;
  loadingMore: boolean;
  hasMore: boolean;
  onLoadPrevious: () => Promise<unknown>;
  loadingPrevious: boolean;
  hasPrevious: boolean;
  seekTargetKey?: string;
  seekRequestId?: number;
  onVisibleMonthChange?: (monthKey: string) => void;
  events?: TimelineEvent[];
  sort: PhotoSort;
  selectedEventId?: string;
  onSelectEvent?: (event: TimelineEvent) => void;
  onEditEvent?: (event: TimelineEvent) => void;
}) {
  const timeline = useRef<HTMLDivElement>(null);
  const previousSentinel = useRef<HTMLDivElement>(null);
  const sentinel = useRef<HTMLDivElement>(null);
  const [timelineWidth, setTimelineWidth] = useState(0);
  const appliedSeekRequest = useRef<number | undefined>(undefined);
  const paginationRequest = useRef<
    | {
        direction: "previous" | "next";
        seekRequestId?: number;
      }
    | undefined
  >(undefined);
  const seekSettlement = useRef<
    | {
        requestId: number;
        pendingDirections: Set<"previous" | "next">;
      }
    | undefined
  >(undefined);
  const { locale, photoGridDensity, t } = usePreferences();
  const gridMetrics = PHOTO_GRID_METRICS[photoGridDensity];
  const mobileGrid = timelineWidth > 0 && timelineWidth <= 600;
  const gridGap =
    mobileGrid && photoGridDensity === "comfortable" ? 6 : gridMetrics.gap;
  const months = useMemo(
    () => groupByMonth(items, events, selectedEventId, locale, sort),
    [events, items, locale, selectedEventId, sort],
  );
  const chunks = useMemo(
    () => chunkMonths(months, events, sort),
    [events, months, sort],
  );
  const columns = mobileGrid
    ? ({ comfortable: 1, standard: 2, compact: 3 } as const)[photoGridDensity]
    : Math.max(
        1,
        Math.floor(
          (timelineWidth + gridMetrics.gap) /
            (gridMetrics.minimumWidth + gridMetrics.gap),
        ),
      );
  const itemLocations = useMemo(() => {
    const locations = new Map<string, { index: number; chunkIndex: number }>();
    let index = 0;
    chunks.forEach((chunk, chunkIndex) => {
      chunk.items.forEach((item) => {
        locations.set(item.id, { index, chunkIndex });
        index++;
      });
    });
    return locations;
  }, [chunks]);
  const findSeekTarget = useCallback((key: string) => {
    return Array.from(
      timeline.current?.querySelectorAll<HTMLElement>(
        "[data-photo-month-key], [data-timeline-seek-key]",
      ) ?? [],
    ).find(
      (element) =>
        element.dataset.photoMonthKey === key ||
        element.dataset.timelineSeekKey === key,
    );
  }, []);
  const scrollToSeekTarget = useCallback(
    (key: string) => {
      const target = findSeekTarget(key);
      if (!target) return false;
      const topOffset = window.innerWidth <= 600 ? 64 : 72;
      const top = window.scrollY + target.getBoundingClientRect().top - topOffset;
      window.scrollTo({ top: Math.max(0, top), behavior: "auto" });
      return true;
    },
    [findSeekTarget],
  );
  // A short seek page can put both sentinels inside their root margins. Keep
  // opposite-direction infinite-query writes from racing for the same seek.
  const loadPage = useCallback(
    async (direction: "previous" | "next") => {
      if (
        paginationRequest.current &&
        paginationRequest.current.seekRequestId === seekRequestId
      )
        return;
      const request = { direction, seekRequestId };
      // A short seek result can clamp the target below its intended offset
      // until either side of the timeline has loaded. Re-align after both
      // prepends and appends so the final position does not depend on which
      // sentinel completed first.
      const targetBefore = seekTargetKey
        ? findSeekTarget(seekTargetKey)
        : undefined;
      const targetTopBefore = targetBefore?.getBoundingClientRect().top;
      const settlement =
        seekRequestId !== undefined &&
        seekSettlement.current?.requestId === seekRequestId
          ? seekSettlement.current
          : undefined;
      const settlingThisDirection = Boolean(
        settlement?.pendingDirections.has(direction),
      );
      paginationRequest.current = request;
      try {
        if (direction === "previous") await onLoadPrevious();
        else await onLoadMore();
        if (
          settlingThisDirection ||
          (targetTopBefore !== undefined &&
            targetTopBefore >= 0 &&
            targetTopBefore <= window.innerHeight * 0.75)
        ) {
          await afterNextPaint();
          if (paginationRequest.current === request && seekTargetKey)
            scrollToSeekTarget(seekTargetKey);
        }
        if (settlement && seekSettlement.current === settlement) {
          settlement.pendingDirections.delete(direction);
          if (settlement.pendingDirections.size === 0)
            seekSettlement.current = undefined;
        }
      } finally {
        if (paginationRequest.current === request)
          paginationRequest.current = undefined;
      }
    },
    [
      findSeekTarget,
      onLoadMore,
      onLoadPrevious,
      scrollToSeekTarget,
      seekRequestId,
      seekTargetKey,
    ],
  );

  useEffect(() => {
    const element = timeline.current;
    if (!element) return;
    const measure = () => setTimelineWidth(element.clientWidth);
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const element = previousSentinel.current;
    if (!element || !hasPrevious || loadingPrevious) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting))
          void loadPage("previous");
      },
      { rootMargin: "1000px 0px 0px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [hasPrevious, loadPage, loadingPrevious]);

  useEffect(() => {
    const element = sentinel.current;
    if (!element || !hasMore || loadingMore) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) void loadPage("next");
      },
      { rootMargin: "1200px 0px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [hasMore, loadPage, loadingMore]);

  useEffect(() => {
    const element = timeline.current;
    if (!element || !onVisibleMonthChange || chunks.length === 0) return;
    if (typeof IntersectionObserver === "undefined") return;
    const sections = Array.from(
      element.querySelectorAll<HTMLElement>("[data-photo-month-key]"),
    );
    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((entry) => entry.isIntersecting)
          .sort(
            (left, right) =>
              left.boundingClientRect.top - right.boundingClientRect.top,
          )[0];
        const monthKey = (visible?.target as HTMLElement | undefined)?.dataset
          .photoMonthKey;
        if (monthKey) onVisibleMonthChange(monthKey);
      },
      { rootMargin: "-112px 0px -72% 0px", threshold: 0 },
    );
    sections.forEach((section) => observer.observe(section));
    return () => observer.disconnect();
  }, [chunks, onVisibleMonthChange]);

  useEffect(() => {
    if (
      !seekTargetKey ||
      seekRequestId === undefined ||
      appliedSeekRequest.current === seekRequestId
    )
      return;
    if (!scrollToSeekTarget(seekTargetKey)) return;
    appliedSeekRequest.current = seekRequestId;
    seekSettlement.current = {
      requestId: seekRequestId,
      pendingDirections: new Set([
        ...(hasPrevious ? (["previous"] as const) : []),
        ...(hasMore ? (["next"] as const) : []),
      ]),
    };
  }, [
    chunks,
    hasMore,
    hasPrevious,
    scrollToSeekTarget,
    seekRequestId,
    seekTargetKey,
  ]);

  const navigate = useCallback(
    (itemId: string, key: string, range: boolean) => {
      const current = itemLocations.get(itemId);
      if (!current) return;
      let targetIndex = current.index;
      if (key === "Home") targetIndex = 0;
      if (key === "End") targetIndex = items.length - 1;
      if (key === "ArrowLeft") targetIndex--;
      if (key === "ArrowRight") targetIndex++;
      if (key === "ArrowUp") targetIndex -= columns;
      if (key === "ArrowDown") targetIndex += columns;
      targetIndex = Math.max(0, Math.min(items.length - 1, targetIndex));
      const target = items[targetIndex];
      const location = itemLocations.get(target.id);
      if (!location) return;
      if (range) onKeyboardRange(target.id);

      const focusTarget = () => {
        const card = Array.from(
          document.querySelectorAll<HTMLElement>(
            ".media-card[data-media-item-id]",
          ),
        ).find((candidate) => candidate.dataset.mediaItemId === target.id);
        card?.querySelector<HTMLButtonElement>(".media-visual")?.focus();
        return Boolean(card);
      };
      if (focusTarget()) return;

      document
        .querySelector<HTMLElement>(
          `[data-photo-chunk-index="${location.chunkIndex}"]`,
        )
        ?.scrollIntoView({ block: "nearest" });
      window.setTimeout(() => {
        if (!focusTarget()) window.setTimeout(focusTarget, 80);
      }, 30);
    },
    [columns, itemLocations, items, onKeyboardRange],
  );

  return (
    <div
      className={`photo-timeline density-${photoGridDensity}`}
      ref={timeline}
      aria-label={t("timeline.label")}
      aria-busy={loadingMore}
    >
      {(hasPrevious || loadingPrevious) && (
        <div
          className="photo-timeline-sentinel photo-timeline-previous"
          ref={previousSentinel}
          aria-live="polite"
        >
          {loadingPrevious
            ? t("timeline.loadingEarlier")
            : t("timeline.scrollForEarlier")}
        </div>
      )}
      {chunks.map((chunk, index) => (
        <VirtualPhotoChunk
          key={chunk.key}
          chunk={chunk}
          chunkIndex={index}
          nextChunk={chunks[index + 1]}
          timelineWidth={timelineWidth}
          columns={columns}
          density={photoGridDensity}
          gap={gridGap}
          selectedIds={selectedIds}
          selectionMode={selectionMode}
          onSelect={onSelect}
          onNavigate={navigate}
          onKeyboardRange={onKeyboardRange}
          onSelectAll={onSelectAll}
          onOpen={onOpen}
          onContextMenu={onContextMenu}
          events={chunk.events}
          selectedEventId={selectedEventId}
          onSelectEvent={onSelectEvent}
          onEditEvent={onEditEvent}
        />
      ))}
      <div
        className="photo-timeline-sentinel"
        ref={sentinel}
        aria-live="polite"
      >
        {loadingMore
          ? t("timeline.loadingMore")
          : hasMore
            ? t("timeline.scrollForMore")
            : t("timeline.end")}
      </div>
    </div>
  );
}

function VirtualPhotoChunk({
  chunk,
  chunkIndex,
  nextChunk,
  timelineWidth,
  columns,
  density,
  gap,
  selectedIds,
  selectionMode,
  onSelect,
  onNavigate,
  onKeyboardRange,
  onSelectAll,
  onOpen,
  onContextMenu,
  events,
  selectedEventId,
  onSelectEvent,
  onEditEvent,
}: {
  chunk: PhotoChunk;
  chunkIndex: number;
  nextChunk?: PhotoChunk;
  timelineWidth: number;
  columns: number;
  density: PhotoGridDensity;
  gap: number;
  selectedIds: string[];
  selectionMode: boolean;
  onSelect: (item: MediaItem, range: boolean) => void;
  onNavigate: (itemId: string, key: string, range: boolean) => void;
  onKeyboardRange: (itemId: string) => void;
  onSelectAll: () => void;
  onOpen: (item: MediaItem) => void;
  onContextMenu?: (event: MouseEvent<HTMLElement>, item: MediaItem) => void;
  events: TimelineEvent[];
  selectedEventId?: string;
  onSelectEvent?: (event: TimelineEvent) => void;
  onEditEvent?: (event: TimelineEvent) => void;
}) {
  const section = useRef<HTMLElement>(null);
  const { locale, t } = usePreferences();
  const [nearViewport, setNearViewport] = useState(chunkIndex < 2);
  const [measured, setMeasured] = useState({
    width: 0,
    height: 0,
    density,
  });
  const estimatedHeight = estimateChunkHeight(
    chunk,
    timelineWidth,
    columns,
    gap,
  );
  const placeholderHeight =
    measured.density === density &&
    Math.abs(measured.width - timelineWidth) < 2 &&
    measured.height > 0
      ? measured.height
      : estimatedHeight;

  useEffect(() => {
    const element = section.current;
    if (!element || typeof IntersectionObserver === "undefined") {
      setNearViewport(true);
      return;
    }
    const observer = new IntersectionObserver(
      ([entry]) => setNearViewport(entry.isIntersecting),
      { rootMargin: "1400px 0px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const element = section.current;
    if (!nearViewport || !element) return;
    const measure = () =>
      setMeasured({
        width: element.clientWidth,
        height: element.offsetHeight,
        density,
      });
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [density, nearViewport]);

  useEffect(() => {
    if (!nearViewport) return;
    [...chunk.items, ...(nextChunk?.items ?? [])].forEach((item) =>
      prefetchAuthenticatedImage(item.thumbnailUrl),
    );
  }, [chunk.items, nearViewport, nextChunk?.items]);

  return (
    <section
      ref={section}
      data-photo-chunk-index={chunkIndex}
      data-photo-month-key={chunk.monthKey}
      className={`photo-month ${chunk.firstInMonth ? "" : "photo-month-continuation"}`}
      style={nearViewport ? undefined : { height: placeholderHeight }}
      aria-labelledby={
        nearViewport && chunk.firstInMonth && chunk.showHeading
          ? `photo-month-${chunk.monthKey}`
          : undefined
      }
    >
      {nearViewport && (
        <>
          {chunk.showHeading && chunk.showYear && (
            <p className="photo-year">{chunk.year}</p>
          )}
          {chunk.showHeading && chunk.firstInMonth && (
            <div className="photo-month-heading">
              <h3 id={`photo-month-${chunk.monthKey}`}>{chunk.label}</h3>
            </div>
          )}
          {events.length > 0 && (
            <div className="timeline-event-stack">
              {events.map((timelineEvent) => (
                <article
                  className={`timeline-event-annotation ${timelineEvent.id === selectedEventId ? "selected" : ""}`}
                  data-timeline-seek-key={`event:${timelineEvent.id}`}
                  key={timelineEvent.id}
                >
                  <button className="timeline-event-open" type="button" onClick={() => onSelectEvent?.(timelineEvent)}>
                    <CalendarDays />
                    <span>
                      <small>{t("events.timelineEvent")}</small>
                      <strong>{timelineEvent.title}</strong>
                      <time>{formatEventRange(timelineEvent, locale)}</time>
                      {timelineEvent.description && <em>{timelineEvent.description}</em>}
                    </span>
                  </button>
                  {onEditEvent && (
                    <button className="icon-button timeline-event-menu" type="button" aria-label={t("events.edit")} onClick={() => onEditEvent(timelineEvent)}><MoreHorizontal /></button>
                  )}
                </article>
              ))}
            </div>
          )}
          <div className="media-grid photo-grid">
            {chunk.items.map((item, itemIndex) => (
              <MediaCard
                key={item.id}
                item={item}
                photo
                priority={chunkIndex === 0 && itemIndex < 8}
                selected={selectedIds.includes(item.id)}
                selectionMode={selectionMode}
                onSelect={(range) => onSelect(item, Boolean(range))}
                onNavigate={(key, range) => onNavigate(item.id, key, range)}
                onKeyboardRange={onKeyboardRange}
                onSelectAll={onSelectAll}
                onOpen={() => onOpen(item)}
                onContextMenu={
                  onContextMenu
                    ? (event) => onContextMenu(event, item)
                    : undefined
                }
              />
            ))}
          </div>
        </>
      )}
    </section>
  );
}

function formatEventRange(event: TimelineEvent, locale: "en" | "es") {
  const formatter = new Intl.DateTimeFormat(locale === "es" ? "es-ES" : "en-US", {
    day: "numeric",
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  });
  const start = formatter.format(new Date(`${event.startDate}T00:00:00Z`));
  if (!event.endDate) return start;
  return `${start} – ${formatter.format(new Date(`${event.endDate}T00:00:00Z`))}`;
}

function estimateChunkHeight(
  chunk: PhotoChunk,
  timelineWidth: number,
  columns: number,
  gap: number,
) {
  const safeWidth = timelineWidth || 900;
  const cellWidth = (safeWidth - Math.max(0, columns - 1) * gap) / columns;
  const rows = Math.ceil(chunk.items.length / columns);
  const headingHeight =
    chunk.firstInMonth && chunk.showHeading
      ? (chunk.showYear ? 66 : 38)
      : 0;
  const eventHeight = chunk.events.length > 0 ? chunk.events.length * 78 + 10 : 0;
  return headingHeight + eventHeight + 7 + rows * cellWidth + Math.max(0, rows - 1) * gap;
}

function chunkMonths(
  months: PhotoMonth[],
  events: TimelineEvent[],
  sort: PhotoSort,
): PhotoChunk[] {
  const chunks: PhotoChunk[] = [];
  months.forEach((month, monthIndex) => {
    const monthEvents = events
      .filter((event) => event.startDate.slice(0, 7) === month.key)
      .sort((left, right) =>
        sort === "captured_asc"
          ? left.startDate.localeCompare(right.startDate)
          : right.startDate.localeCompare(left.startDate),
      );
    const eventGroups = new Map<string, TimelineEvent[]>();
    monthEvents.forEach((event) =>
      eventGroups.set(event.startDate, [
        ...(eventGroups.get(event.startDate) ?? []),
        event,
      ]),
    );
    let firstInMonth = true;
    let itemStart = 0;
    let sequence = 0;
    const appendItems = (end: number) => {
      for (let start = itemStart; start < end; start += PHOTO_CHUNK_SIZE) {
        chunks.push(
          createPhotoChunk(
            month,
            monthIndex,
            firstInMonth,
            `${sequence++}`,
            month.items.slice(start, Math.min(end, start + PHOTO_CHUNK_SIZE)),
            [],
            months,
          ),
        );
        firstInMonth = false;
      }
      itemStart = end;
    };

    eventGroups.forEach((groupedEvents, eventDate) => {
      let boundary = itemStart;
      while (
        boundary < month.items.length &&
        itemComesBeforeEvent(month.items[boundary], eventDate, sort)
      )
        boundary++;
      appendItems(boundary);
      chunks.push({
        key: `${month.key}-event-${eventDate}`,
        monthKey: month.key,
        label: month.label,
        year: month.year,
        showHeading: month.showHeading,
        showYear:
          month.showHeading &&
          firstInMonth &&
          (monthIndex === 0 || months[monthIndex - 1].year !== month.year),
        firstInMonth,
        items: [],
        events: groupedEvents,
      });
      firstInMonth = false;
    });
    appendItems(month.items.length);
  });
  return chunks;
}

function createPhotoChunk(
  month: PhotoMonth,
  monthIndex: number,
  firstInMonth: boolean,
  suffix: string,
  items: MediaItem[],
  events: TimelineEvent[],
  months: PhotoMonth[],
): PhotoChunk {
  return {
    key: `${month.key}-${suffix}`,
    monthKey: month.key,
    label: month.label,
    year: month.year,
    showHeading: month.showHeading,
    showYear:
      month.showHeading &&
      firstInMonth &&
      (monthIndex === 0 || months[monthIndex - 1].year !== month.year),
    firstInMonth,
    items,
    events,
  };
}

function itemComesBeforeEvent(
  item: MediaItem,
  eventDate: string,
  sort: PhotoSort,
) {
  const date = timelineDate(item, sort);
  if (Number.isNaN(date.getTime())) return false;
  const itemDate = `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}-${String(date.getUTCDate()).padStart(2, "0")}`;
  return sort === "captured_asc"
    ? itemDate < eventDate
    : itemDate > eventDate;
}

function timelineDate(item: MediaItem, sort: PhotoSort) {
  return new Date(
    sort === "added_desc" ? item.createdAt : (item.capturedAt ?? item.createdAt),
  );
}

function groupByMonth(
  items: MediaItem[],
  events: TimelineEvent[],
  selectedEventId: string | undefined,
  locale: "en" | "es",
  sort: PhotoSort,
): PhotoMonth[] {
  if (sort === "added_desc")
    return [
      {
        key: "recently-added",
        label: "",
        year: "",
        items: [...items],
        showHeading: false,
      },
    ];

  const grouped = new Map<string, PhotoMonth>();
  items.forEach((item) => {
    const date = timelineDate(item, sort);
    const key = Number.isNaN(date.getTime())
      ? "unknown"
      : `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`;
    const current = grouped.get(key);
    if (current) {
      current.items.push(item);
      return;
    }
    grouped.set(key, {
      key,
      label: Number.isNaN(date.getTime())
        ? translations[locale]["timeline.dateUnavailable"]
        : new Intl.DateTimeFormat(locale === "es" ? "es-ES" : "en-US", {
            month: "long",
            timeZone: "UTC",
          }).format(date),
      year: Number.isNaN(date.getTime())
        ? translations[locale]["timeline.unknownDate"]
        : String(date.getUTCFullYear()),
      items: [item],
      showHeading: true,
    });
  });

  const loadedOrdinals = [...grouped.keys()]
    .filter((key) => key !== "unknown")
    .map(monthOrdinal);
  const firstLoaded = loadedOrdinals.length
    ? Math.min(...loadedOrdinals)
    : undefined;
  const lastLoaded = loadedOrdinals.length
    ? Math.max(...loadedOrdinals)
    : undefined;
  events.forEach((timelineEvent) => {
    const key = timelineEvent.startDate.slice(0, 7);
    const ordinal = monthOrdinal(key);
    const withinLoadedRange =
      firstLoaded !== undefined &&
      lastLoaded !== undefined &&
      ordinal >= firstLoaded &&
      ordinal <= lastLoaded;
    if (timelineEvent.id !== selectedEventId && !withinLoadedRange) return;
    if (grouped.has(key)) return;
    const date = new Date(`${key}-01T00:00:00Z`);
    grouped.set(key, {
      key,
      label: new Intl.DateTimeFormat(locale === "es" ? "es-ES" : "en-US", {
        month: "long",
        timeZone: "UTC",
      }).format(date),
      year: key.slice(0, 4),
      items: [],
      showHeading: true,
    });
  });

  const direction = sort === "captured_asc" ? 1 : -1;
  return [...grouped.values()].sort((left, right) => {
    if (left.key === "unknown") return 1;
    if (right.key === "unknown") return -1;
    return (monthOrdinal(left.key) - monthOrdinal(right.key)) * direction;
  });
}

function monthOrdinal(key: string) {
  const [year, month] = key.split("-").map(Number);
  return year * 12 + month;
}
