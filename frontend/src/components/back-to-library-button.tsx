"use client";

import { ArrowLeft } from "lucide-react";
import { useRouter } from "next/navigation";

export function BackToLibraryButton({
  className,
  label,
  fallbackHref = "/photos",
}: {
  className?: string;
  label: string;
  fallbackHref?: string;
}) {
  const router = useRouter();

  return (
    <button
      className={className}
      onClick={() => {
        if (window.history.length > 1) router.back();
        else router.push(fallbackHref);
      }}
      type="button"
    >
      <ArrowLeft /> {label}
    </button>
  );
}
