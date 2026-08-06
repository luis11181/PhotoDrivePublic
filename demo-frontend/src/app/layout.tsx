import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "PhotoDrive | Your private family Photos + Drive",
  description:
    "A self-hosted family home for photo timelines, albums, folders, private sharing, and recovery-minded storage.",
  icons: { icon: "/icon.svg" },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body>{children}</body>
    </html>
  );
}
