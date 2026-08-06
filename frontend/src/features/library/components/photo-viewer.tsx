"use client";

/* eslint-disable @next/next/no-img-element -- originals and thumbnails are authenticated backend responses. */

import {
  ChevronLeft,
  ChevronRight,
  Download,
  Heart,
  Info,
  Maximize2,
  Minus,
  MoreVertical,
  Pause,
  Pencil,
  Play,
  Plus,
  RotateCcw,
  RotateCw,
  Share2,
  Trash2,
  Volume2,
  VolumeX,
  X,
} from "lucide-react";
import {
  type ReactNode,
  type PointerEvent as ReactPointerEvent,
  type WheelEvent as ReactWheelEvent,
  useEffect,
  useRef,
  useState,
} from "react";
import { contentUrl, downloadItemsUrl, thumbnailUrl } from "@/lib/api";
import { formatBytes, formatDate } from "@/lib/formatting";
import { usePreferences } from "@/lib/preferences";
import type { MediaItem } from "@/lib/types";
import { prefetchAuthenticatedImage } from "../media-prefetch";

type Point = { x: number; y: number };

export function PhotoViewer({
  item,
  previousItem,
  nextItem,
  hasMore,
  position,
  loadedCount,
  onClose,
  onPrevious,
  onNext,
  onShowDetails,
  detailsOpen,
  onFavorite,
  onShare,
  onRename,
  onTrash,
  detailsPanel,
}: {
  item: MediaItem;
  previousItem?: MediaItem;
  nextItem?: MediaItem;
  hasMore?: boolean;
  position: number;
  loadedCount: number;
  onClose: () => void;
  onPrevious: () => void;
  onNext: () => void;
  onShowDetails: () => void;
  detailsOpen?: boolean;
  onFavorite: () => void;
  onShare?: () => void;
  onRename?: () => void;
  onTrash: () => void;
  detailsPanel?: ReactNode;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [loadedImageId, setLoadedImageId] = useState<string | null>(null);
  const [scale, setScale] = useState(1);
  const [offset, setOffset] = useState<Point>({ x: 0, y: 0 });
  const [swipeOffset, setSwipeOffset] = useState(0);
  const [dragging, setDragging] = useState(false);
  const [videoPaused, setVideoPaused] = useState(true);
  const [videoMuted, setVideoMuted] = useState(false);
  const closeButton = useRef<HTMLButtonElement>(null);
  const mediaStage = useRef<HTMLDivElement>(null);
  const video = useRef<HTMLVideoElement>(null);
  const scaleRef = useRef(1);
  const gesture = useRef({
    points: new Map<number, Point>(),
    start: { x: 0, y: 0 },
    startOffset: { x: 0, y: 0 },
    startScale: 1,
    pinchDistance: 0,
    pinching: false,
  });
  const { locale, t } = usePreferences();
  const isImage = item.kind === "image";
  const currentThumbnail = thumbnailUrl(item.id, "lg");

  useEffect(() => {
    scaleRef.current = scale;
  }, [scale]);

  useEffect(() => {
    [
      currentThumbnail,
      previousItem && thumbnailUrl(previousItem.id, "lg"),
      nextItem && thumbnailUrl(nextItem.id, "lg"),
    ].forEach((url) => prefetchAuthenticatedImage(url || undefined));
  }, [currentThumbnail, nextItem, previousItem]);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    closeButton.current?.focus();
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      const textInput = Boolean(target?.closest("input, select, textarea"));
      const interactiveControl = Boolean(
        target?.closest("button, a, input, select, textarea"),
      );
      const nativeMediaControl = Boolean(target?.closest("video"));
      if (event.key === "Escape") {
        if (menuOpen) {
          setMenuOpen(false);
        } else if (detailsOpen) {
          onShowDetails();
        } else if (scaleRef.current > 1) {
          changeScale(1);
        } else {
          onClose();
        }
        return;
      }
      if (menuOpen || textInput || nativeMediaControl) return;
      if (event.key === "ArrowLeft" && previousItem) onPrevious();
      if (event.key === "ArrowRight" && nextItem) onNext();
      if (isImage && (event.key === "+" || event.key === "=")) {
        event.preventDefault();
        zoomBy(0.5);
      }
      if (isImage && event.key === "-") {
        event.preventDefault();
        zoomBy(-0.5);
      }
      if (isImage && event.key === "0") {
        event.preventDefault();
        changeScale(1);
      }
      if (item.kind === "video" && event.key === " " && !interactiveControl) {
        event.preventDefault();
        void toggleVideoPlayback();
      }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  });

  function changeScale(nextScale: number) {
    const constrained = Math.min(5, Math.max(1, nextScale));
    scaleRef.current = constrained;
    setScale(constrained);
    if (constrained === 1) setOffset({ x: 0, y: 0 });
  }

  function zoomBy(delta: number) {
    changeScale(scaleRef.current + delta);
  }

  function constrainOffset(point: Point, currentScale = scaleRef.current) {
    const stage = mediaStage.current;
    if (!stage || currentScale <= 1) return { x: 0, y: 0 };
    const maxX = (stage.clientWidth * (currentScale - 1)) / 2;
    const maxY = (stage.clientHeight * (currentScale - 1)) / 2;
    return {
      x: Math.max(-maxX, Math.min(maxX, point.x)),
      y: Math.max(-maxY, Math.min(maxY, point.y)),
    };
  }

  function handlePointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    event.currentTarget.setPointerCapture(event.pointerId);
    const point = { x: event.clientX, y: event.clientY };
    const state = gesture.current;
    state.points.set(event.pointerId, point);
    setDragging(true);
    if (state.points.size === 1) {
      state.start = point;
      state.startOffset = offset;
      state.startScale = scaleRef.current;
      state.pinching = false;
      state.pinchDistance = 0;
    } else if (state.points.size === 2) {
      state.pinching = true;
      state.startScale = scaleRef.current;
      state.pinchDistance = distance([...state.points.values()]);
    }
  }

  function handlePointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const state = gesture.current;
    if (!state.points.has(event.pointerId)) return;
    const point = { x: event.clientX, y: event.clientY };
    state.points.set(event.pointerId, point);
    if (state.points.size >= 2) {
      const nextDistance = distance([...state.points.values()]);
      if (state.pinchDistance > 0)
        changeScale(state.startScale * (nextDistance / state.pinchDistance));
      return;
    }

    const deltaX = point.x - state.start.x;
    const deltaY = point.y - state.start.y;
    if (scaleRef.current > 1 || state.startScale > 1) {
      setOffset(
        constrainOffset({
          x: state.startOffset.x + deltaX,
          y: state.startOffset.y + deltaY,
        }),
      );
    } else {
      setSwipeOffset(deltaX * 0.72);
    }
  }

  function finishPointer(
    event: ReactPointerEvent<HTMLDivElement>,
    cancelled = false,
  ) {
    const state = gesture.current;
    const storedPoint = state.points.get(event.pointerId);
    const point = storedPoint
      ? { x: event.clientX, y: event.clientY }
      : undefined;
    const wasLastPointer = state.points.size === 1;
    state.points.delete(event.pointerId);
    if (
      !cancelled &&
      point &&
      wasLastPointer &&
      !state.pinching &&
      scaleRef.current === 1
    ) {
      const deltaX = point.x - state.start.x;
      const deltaY = point.y - state.start.y;
      const threshold = Math.max(
        55,
        Math.min(110, (mediaStage.current?.clientWidth ?? 500) * 0.14),
      );
      if (
        Math.abs(deltaX) >= threshold &&
        Math.abs(deltaX) > Math.abs(deltaY)
      ) {
        if (deltaX > 0 && previousItem) onPrevious();
        if (deltaX < 0 && nextItem) onNext();
      }
    }
    setSwipeOffset(0);
    if (state.points.size === 0) {
      setDragging(false);
      state.pinching = false;
      state.pinchDistance = 0;
    } else if (state.points.size === 1) {
      const remaining = [...state.points.values()][0];
      state.start = remaining;
      state.startOffset = offset;
      state.startScale = scaleRef.current;
      state.pinching = false;
      state.pinchDistance = 0;
    }
  }

  function handleWheel(event: ReactWheelEvent<HTMLDivElement>) {
    event.preventDefault();
    zoomBy(event.deltaY < 0 ? 0.35 : -0.35);
  }

  async function toggleVideoPlayback() {
    const element = video.current;
    if (!element) return;
    if (element.paused) {
      try {
        await element.play();
      } catch {
        setVideoPaused(true);
      }
    } else {
      element.pause();
    }
  }

  function seekVideo(seconds: number) {
    const element = video.current;
    if (!element) return;
    element.currentTime = Math.max(
      0,
      Math.min(element.duration || Infinity, element.currentTime + seconds),
    );
  }

  function toggleVideoMuted() {
    const element = video.current;
    if (!element) return;
    element.muted = !element.muted;
    setVideoMuted(element.muted);
  }

  function enterFullscreen() {
    const element = video.current ?? mediaStage.current;
    void element?.requestFullscreen?.().catch(() => undefined);
  }

  const dimensions =
    item.metadata?.width && item.metadata.height
      ? `${item.metadata.width} × ${item.metadata.height}`
      : undefined;
  const imageLoaded = loadedImageId === item.id;
  const transform = `translate3d(${offset.x + swipeOffset}px, ${offset.y}px, 0) scale(${scale})`;

  return (
    <div
      className={`photo-viewer ${isImage ? "image-viewer" : "video-viewer"} ${scale > 1 ? "zoomed" : ""} ${detailsOpen ? "details-open" : ""}`}
      role="dialog"
      aria-modal="true"
      aria-label={t("viewer.label", { name: item.name })}
    >
      <div className="photo-viewer-toolbar">
        <div className="photo-viewer-heading">
          <strong title={item.name}>{item.name}</strong>
          <span>
            {formatDate(
              item.capturedAt ?? item.createdAt,
              locale === "es" ? "es-ES" : "en-US",
            )}{" "}
            · {t("viewer.position", { position, count: loadedCount })}
          </span>
        </div>
        <div className="photo-viewer-tools">
          {isImage && (
            <div
              className="photo-viewer-zoom-tools"
              aria-label={t("viewer.zoomControls")}
            >
              <button
                className="icon-button"
                disabled={scale <= 1}
                onClick={() => zoomBy(-0.5)}
                aria-label={t("viewer.zoomOut")}
              >
                <Minus />
              </button>
              <button
                className="photo-viewer-zoom-level"
                disabled={scale === 1}
                onClick={() => changeScale(1)}
                aria-label={t("viewer.resetZoom")}
              >
                {Math.round(scale * 100)}%
              </button>
              <button
                className="icon-button"
                disabled={scale >= 5}
                onClick={() => zoomBy(0.5)}
                aria-label={t("viewer.zoomIn")}
              >
                <Plus />
              </button>
            </div>
          )}
          {item.capabilities.canFavorite && !item.trashedAt && (
            <button
              className="icon-button"
              onClick={onFavorite}
              aria-label={
                item.favorite
                  ? t("common.removeFavorite")
                  : t("common.addFavorite")
              }
              title={
                item.favorite
                  ? t("common.removeFavorite")
                  : t("common.addFavorite")
              }
            >
              <Heart fill={item.favorite ? "currentColor" : "none"} />
            </button>
          )}
          {onShare && item.capabilities.canShare && !item.trashedAt && (
            <button
              className="icon-button"
              onClick={onShare}
              aria-label={t("details.share")}
              title={t("details.share")}
            >
              <Share2 />
            </button>
          )}
          <button
            className={`icon-button ${detailsOpen ? "active" : ""}`}
            onClick={onShowDetails}
            aria-label={
              detailsOpen ? t("viewer.hideDetails") : t("viewer.showDetails")
            }
            aria-pressed={detailsOpen}
            aria-expanded={detailsOpen}
            aria-controls="viewer-info-panel"
            title={
              detailsOpen ? t("viewer.hideDetails") : t("viewer.showDetails")
            }
          >
            <Info />
          </button>
          <div className="photo-viewer-menu-wrap">
            <button
              className="icon-button"
              onClick={() => setMenuOpen((open) => !open)}
              aria-label={t("viewer.photoActions")}
              aria-expanded={menuOpen}
              aria-haspopup="menu"
              aria-controls="photo-viewer-actions"
            >
              <MoreVertical />
            </button>
            {menuOpen && (
              <div
                className="photo-viewer-menu"
                id="photo-viewer-actions"
                role="menu"
              >
                {onRename && item.capabilities.canRename && !item.trashedAt && (
                  <button
                    type="button"
                    role="menuitem"
                    onClick={() => {
                      onRename();
                      setMenuOpen(false);
                    }}
                  >
                    <Pencil /> {t("details.rename")}
                  </button>
                )}
                <a
                  href={downloadItemsUrl([item.id])}
                  download={item.name}
                  role="menuitem"
                  onClick={() => setMenuOpen(false)}
                >
                  <Download /> {t("common.download")}
                </a>
                {item.capabilities.canTrash && !item.trashedAt && (
                  <button
                    type="button"
                    role="menuitem"
                    className="destructive"
                    onClick={() => {
                      onTrash();
                      onClose();
                    }}
                  >
                    <Trash2 /> {t("common.moveToTrash")}
                  </button>
                )}
              </div>
            )}
          </div>
          <button
            ref={closeButton}
            className="icon-button"
            onClick={onClose}
            aria-label={t("viewer.close")}
          >
            <X />
          </button>
        </div>
      </div>
      <button
        className="photo-viewer-close-area"
        aria-label={t("viewer.close")}
        onClick={onClose}
      />
      <div
        ref={mediaStage}
        className={`photo-viewer-media ${isImage ? "image-gestures" : ""} ${dragging ? "dragging" : ""}`}
        onPointerDown={isImage ? handlePointerDown : undefined}
        onPointerMove={isImage ? handlePointerMove : undefined}
        onPointerUp={isImage ? (event) => finishPointer(event) : undefined}
        onPointerCancel={
          isImage ? (event) => finishPointer(event, true) : undefined
        }
        onWheel={isImage ? handleWheel : undefined}
        onDoubleClick={
          isImage
            ? () => changeScale(scaleRef.current === 1 ? 2.5 : 1)
            : undefined
        }
      >
        {item.kind === "video" ? (
          <video
            key={item.id}
            ref={video}
            controls
            autoPlay
            playsInline
            preload="metadata"
            crossOrigin="use-credentials"
            poster={currentThumbnail}
            src={contentUrl(item.id)}
            onPlay={() => setVideoPaused(false)}
            onPause={() => setVideoPaused(true)}
            onVolumeChange={(event) => setVideoMuted(event.currentTarget.muted)}
          />
        ) : (
          <div className="photo-viewer-image-stack" style={{ transform }}>
            <img
              className="photo-viewer-image-placeholder"
              src={currentThumbnail}
              crossOrigin="use-credentials"
              decoding="async"
              fetchPriority="high"
              alt=""
              aria-hidden="true"
            />
            <img
              key={item.id}
              className={`photo-viewer-image-original ${imageLoaded ? "loaded" : ""}`}
              src={contentUrl(item.id)}
              crossOrigin="use-credentials"
              alt={item.name}
              draggable={false}
              onLoad={() => setLoadedImageId(item.id)}
            />
          </div>
        )}
      </div>
      <button
        className="photo-viewer-nav previous"
        disabled={!previousItem}
        onClick={onPrevious}
        aria-label={t("viewer.previous")}
      >
        <ChevronLeft />
      </button>
      <button
        className="photo-viewer-nav next"
        disabled={!nextItem && !hasMore}
        onClick={onNext}
        aria-label={t("viewer.next")}
      >
        <ChevronRight />
      </button>
      {item.kind === "video" && (
        <div
          className="photo-viewer-video-actions"
          aria-label={t("viewer.videoControls")}
        >
          <button
            type="button"
            onClick={() => seekVideo(-10)}
            aria-label={t("viewer.rewind")}
          >
            <RotateCcw />
            <span>10</span>
          </button>
          <button
            type="button"
            onClick={() => void toggleVideoPlayback()}
            aria-label={videoPaused ? t("viewer.play") : t("viewer.pause")}
          >
            {videoPaused ? <Play /> : <Pause />}
          </button>
          <button
            type="button"
            onClick={() => seekVideo(10)}
            aria-label={t("viewer.forward")}
          >
            <RotateCw />
            <span>10</span>
          </button>
          <button
            type="button"
            onClick={toggleVideoMuted}
            aria-label={videoMuted ? t("viewer.unmute") : t("viewer.mute")}
          >
            {videoMuted ? <VolumeX /> : <Volume2 />}
          </button>
          <button
            type="button"
            onClick={enterFullscreen}
            aria-label={t("viewer.fullscreen")}
          >
            <Maximize2 />
          </button>
        </div>
      )}
      <div className="photo-viewer-caption">
        <strong>
          {item.kind === "video" ? t("common.video") : t("common.photo")}
        </strong>
        {item.sizeBytes !== undefined && (
          <span>{formatBytes(item.sizeBytes)}</span>
        )}
        {dimensions && <span>{dimensions}</span>}
      </div>
      {detailsPanel}
    </div>
  );
}

function distance(points: Point[]) {
  if (points.length < 2) return 0;
  return Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y);
}
