"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import { UploadQueueProvider } from "@/features/uploads/upload-queue-provider";
import { PreferencesProvider } from "@/lib/preferences";

export function Providers({ children }: { children: React.ReactNode }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30_000,
            gcTime: 5 * 60_000,
            retry: 1,
            refetchOnWindowFocus: false,
          },
        },
      }),
  );

  return (
    <PreferencesProvider>
      <QueryClientProvider client={queryClient}>
        <UploadQueueProvider>{children}</UploadQueueProvider>
      </QueryClientProvider>
    </PreferencesProvider>
  );
}
