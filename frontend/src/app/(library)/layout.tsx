"use client";

import { Suspense, type ReactNode } from "react";
import { usePathname } from "next/navigation";
import { AuthGate } from "@/components/auth-gate";
import type { LibraryView } from "@/lib/types";

const viewByPath: Record<string, LibraryView> = {
  "/photos": "photos",
  "/albums": "albums",
  "/files": "files",
  "/shared": "shared",
  "/trash": "trash",
};

export default function LibraryLayout({ children }: { children: ReactNode }) {
  const pathname = usePathname();
  const normalizedPath =
    pathname.length > 1 ? pathname.replace(/\/$/, "") : pathname;
  const view = viewByPath[normalizedPath] ?? "photos";

  return (
    <>
      <Suspense fallback={null}>
        <AuthGate view={view} />
      </Suspense>
      {children}
    </>
  );
}
