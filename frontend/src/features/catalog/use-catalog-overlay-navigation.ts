"use client";

import { useCallback } from "react";
import { catalogDetailsHref, catalogPreviewHref } from "./catalog-item-links";

export function useCatalogOverlayNavigation(
  pathname: string,
  currentSearch: string,
) {
  const pushPreview = useCallback(
    (itemId: string) => {
      window.history.pushState(
        null,
        "",
        catalogPreviewHref(pathname, currentSearch, itemId),
      );
    },
    [currentSearch, pathname],
  );

  const replacePreview = useCallback(
    (itemId?: string) => {
      window.history.replaceState(
        null,
        "",
        catalogPreviewHref(pathname, currentSearch, itemId),
      );
    },
    [currentSearch, pathname],
  );

  const clearDetails = useCallback(() => {
    window.history.replaceState(
      null,
      "",
      catalogDetailsHref(pathname, currentSearch),
    );
  }, [currentSearch, pathname]);

  return { clearDetails, pushPreview, replacePreview };
}
