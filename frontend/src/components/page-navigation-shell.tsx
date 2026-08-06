"use client";

import { useQuery } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import type { CurrentUser } from "@/lib/auth";
import { getStorageSummary } from "@/lib/api";
import { LibrarySidebar } from "@/features/library/components/library-sidebar";
import { LibraryTopbar } from "@/features/library/components/library-topbar";

type PageView = "imports" | "developer" | "users";

export function PageNavigationShell({
  view,
  user,
  onOpenAccount,
  onSignOut,
  children,
}: {
  view: PageView;
  user: CurrentUser;
  onOpenAccount: () => void;
  onSignOut: () => Promise<void>;
  children: ReactNode;
}) {
  const [navigationOpen, setNavigationOpen] = useState(false);
  const storage = useQuery({
    queryKey: ["storage-summary"],
    queryFn: () => getStorageSummary(),
    staleTime: Infinity,
    refetchOnMount: false,
    refetchOnWindowFocus: false,
  });

  return (
    <div className="app-shell">
      <LibrarySidebar
        view={view}
        open={navigationOpen}
        onClose={() => setNavigationOpen(false)}
        storageSummary={storage.data}
        storageLoading={storage.isLoading}
        storageRefreshing={storage.isFetching}
        onRefreshStorage={() => void storage.refetch()}
        isOwner={user.isOwner}
      />
      <section className="workspace">
        <LibraryTopbar
          query=""
          onQueryChange={() => undefined}
          showSearch={false}
          alwaysShowMenu
          onOpenNavigation={() => setNavigationOpen(true)}
          onOpenAccount={onOpenAccount}
          onSignOut={onSignOut}
          onToggleNotifications={() => undefined}
          notificationCount={0}
          notificationsOpen={false}
          operationRunning={false}
          user={user}
        />
        {children}
      </section>
    </div>
  );
}
