"use client";

import { useInfiniteQuery } from "@tanstack/react-query";
import { queryFiles, searchCatalog } from "@/lib/api";
import type {
  FileDiscoveryScope,
  FileTypeFilter,
  ItemSort,
  PhotoScope,
} from "@/lib/types";

export function useCatalogSearch(options: {
  query: string;
  scope: FileDiscoveryScope | "photos" | "trash";
  photoScope: PhotoScope;
  parentId?: string;
  includePhotos: boolean;
  type?: FileTypeFilter;
  favorite?: boolean;
  sort?: ItemSort;
  enabled: boolean;
  tagId?: string;
}) {
  const normalizedQuery = options.query.trim();
  return useInfiniteQuery({
    queryKey: [
      "catalog-search",
      normalizedQuery,
      options.scope,
      options.photoScope,
      options.parentId ?? null,
      options.includePhotos,
      options.type ?? "all",
      options.favorite ?? false,
      options.sort ?? "name_asc",
      options.tagId ?? null,
    ],
    initialPageParam: "",
    queryFn: ({ pageParam, signal }) =>
      options.scope === "all" || options.scope === "subtree"
        ? queryFiles({
            query: normalizedQuery.length >= 2 ? normalizedQuery : undefined,
            location: options.scope,
            folderId: options.parentId,
            includePhotos: options.includePhotos,
            favorite: options.favorite,
            type: options.type ?? "all",
            sort: options.sort ?? "name_asc",
            cursor: pageParam || undefined,
            signal,
            tagId: options.tagId,
          })
        : searchCatalog({
            query: normalizedQuery,
            scope: options.scope,
            photoScope: options.photoScope,
            type: options.type,
            cursor: pageParam || undefined,
            signal,
            tagId: options.tagId,
          }),
    getNextPageParam: (page) => page.nextCursor,
    enabled:
      options.enabled &&
      ((options.scope !== "shared" &&
        options.scope !== "photos" &&
        options.scope !== "trash") ||
        normalizedQuery.length >= 2),
  });
}
