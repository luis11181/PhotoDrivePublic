"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import type { CurrentUser } from "@/lib/auth";
import { getStorageSummary } from "@/lib/api";
import { LibrarySidebar } from "@/features/library/components/library-sidebar";
import { LibraryTopbar } from "@/features/library/components/library-topbar";
import { usePreferences } from "@/lib/preferences";

type PageView = "imports" | "developer" | "users" | "storage";

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
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const storageKey = ["storage-summary"];
  const storage = useQuery({
    queryKey: storageKey,
    queryFn: () => getStorageSummary(),
    staleTime: Infinity,
    refetchOnMount: false,
    refetchOnWindowFocus: false,
  });
  const refreshStorage = useMutation({
    mutationFn: () => getStorageSummary({ refresh: true }),
    onMutate: () => queryClient.cancelQueries({ queryKey: storageKey }),
    onSuccess: (data) => {
      queryClient.setQueryData(storageKey, data);
    },
  });

  return (
    <div className="app-shell">
      <LibrarySidebar
        view={view}
        open={navigationOpen}
        onClose={() => setNavigationOpen(false)}
        storageSummary={storage.data}
        storageLoading={storage.isLoading}
        storageRefreshing={storage.isFetching || refreshStorage.isPending}
        onRefreshStorage={() => refreshStorage.mutate()}
        isOwner={user.isOwner}
      />
      <section className="workspace">
        <LibraryTopbar
          query=""
          onQueryChange={() => undefined}
          showSearch={false}
          sectionLabel={t(
            view === "storage" ? "sidebar.storage" : view === "imports"
              ? "sidebar.imports"
              : view === "users"
                ? "sidebar.familyAccounts"
                : "sidebar.developerTools",
          )}
          onOpenNavigation={() => setNavigationOpen(true)}
          onOpenAccount={onOpenAccount}
          onSignOut={onSignOut}
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
