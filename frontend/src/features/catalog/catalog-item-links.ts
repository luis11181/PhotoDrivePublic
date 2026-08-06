export function catalogPreviewHref(
  pathname: string,
  currentSearch: string,
  itemId?: string,
) {
  const params = new URLSearchParams(currentSearch);
  if (itemId) {
    params.delete("details");
    params.set("preview", itemId);
  } else params.delete("preview");
  const query = params.toString();
  return query ? `${pathname}?${query}` : pathname;
}

export function catalogDetailsHref(
  pathname: string,
  currentSearch: string,
  itemId?: string,
) {
  const params = new URLSearchParams(currentSearch);
  params.delete("preview");
  if (itemId) params.set("details", itemId);
  else params.delete("details");
  const query = params.toString();
  return query ? `${pathname}?${query}` : pathname;
}
