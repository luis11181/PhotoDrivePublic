"use client";

import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo } from "react";
import {
  listPhotos,
  listSharedItems,
  listTrashItems,
  queryFiles,
} from "@/lib/api";
import { logInfo } from "@/lib/logger";
import type {
  ItemSort,
  FileTypeFilter,
  LibraryView,
  MediaTypeFilter,
  PhotoScope,
  PhotoSort,
} from "@/lib/types";

export function useLibraryQuery(options: {
  view: LibraryView;
  parentId?: string;
  photoScope: PhotoScope;
  mediaType: MediaTypeFilter;
  fileType: FileTypeFilter;
  enabled: boolean;
  photosFavorite: boolean;
  itemSort: ItemSort;
  photoSort: PhotoSort;
  photoYear?: number;
  photoCaptureDate?: string;
  photoSeekDate?: string;
  photoSeekCursor?: string;
  tagId?: string;
}) {
  const queryClient = useQueryClient();
  const queryKey = useMemo(
    () =>
      [
        "library",
        options.view,
        options.parentId ?? null,
        options.photoScope,
        options.mediaType,
        options.fileType,
        options.photosFavorite,
        options.itemSort,
        options.photoSort,
        options.photoYear ?? null,
        options.photoCaptureDate ?? null,
        options.photoSeekDate ?? null,
        options.photoSeekCursor ?? null,
        options.tagId ?? null,
      ] as const,
    [
      options.mediaType,
      options.fileType,
      options.parentId,
      options.photoScope,
      options.photosFavorite,
      options.itemSort,
      options.photoSort,
      options.photoYear,
      options.photoCaptureDate,
      options.photoSeekDate,
      options.photoSeekCursor,
      options.tagId,
      options.view,
    ],
  );
  const cacheKey = JSON.stringify(queryKey);
  const hadCachedData = useMemo(
    () => queryClient.getQueryData(queryKey) !== undefined,
    [queryClient, queryKey],
  );
  const result = useInfiniteQuery({
    queryKey,
    initialPageParam: options.photoSeekCursor ?? "",
    queryFn: ({ pageParam, signal }) => {
      if (options.view === "photos") {
        return listPhotos({
          cursor: pageParam || undefined,
          signal,
          scope: options.photoScope,
          type: options.mediaType,
          favorite: options.photosFavorite,
          sort: options.photoSort,
          year: options.photoYear,
          captureDate: options.photoCaptureDate,
          seekDate: pageParam ? undefined : options.photoSeekDate,
          tagId: options.tagId,
        });
      }
      if (options.view === "shared") {
        return listSharedItems({
          cursor: pageParam || undefined,
          signal,
          parentId: options.parentId,
          type: options.fileType,
          sort: options.itemSort,
        });
      }
      if (options.view === "trash") {
        return listTrashItems({
          cursor: pageParam || undefined,
          signal,
          type: "all",
          sort: options.itemSort,
        });
      }
      return queryFiles({
        cursor: pageParam || undefined,
        signal,
        location: "children",
        folderId: options.parentId,
        type: options.fileType,
        sort: options.itemSort,
        tagId: options.tagId,
      });
    },
    getNextPageParam: (page) => page.nextCursor,
    getPreviousPageParam: (page) => page.previousCursor,
    enabled: options.enabled,
    refetchInterval: (query) =>
      query.state.data?.pages.some((page) =>
        page.items.some(
          (item) =>
            item.metadata?.state === "pending" ||
            item.metadata?.state === "processing",
        ),
      )
        ? 2_000
        : false,
  });

  useEffect(() => {
    if (!options.enabled) return;
    logInfo("frontend.query", hadCachedData ? "cache hit" : "cache miss", {
      view: options.view,
      parentId: options.parentId ?? null,
      photoScope: options.view === "photos" ? options.photoScope : undefined,
      mediaType: options.view === "photos" ? options.mediaType : undefined,
      fileType: options.view === "files" ? options.fileType : undefined,
      sort: options.view === "photos" ? options.photoSort : options.itemSort,
      year: options.view === "photos" ? options.photoYear : undefined,
      captureDate:
        options.view === "photos" ? options.photoCaptureDate : undefined,
      seekDate: options.view === "photos" ? options.photoSeekDate : undefined,
    });
  }, [
    cacheKey,
    hadCachedData,
    options.enabled,
    options.mediaType,
    options.fileType,
    options.parentId,
    options.itemSort,
    options.photoSort,
    options.photoYear,
    options.photoCaptureDate,
    options.photoSeekDate,
    options.photoScope,
    options.view,
  ]);

  return result;
}
