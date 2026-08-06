const prefetchedImages = new Set<string>();

export function prefetchAuthenticatedImage(url?: string) {
  if (!url || typeof window === "undefined" || prefetchedImages.has(url))
    return;
  prefetchedImages.add(url);
  const image = new Image();
  image.crossOrigin = "use-credentials";
  image.decoding = "async";
  image.src = url;
  image.onerror = () => prefetchedImages.delete(url);
}
