"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";

export default function Home() {
  const router = useRouter();
  useEffect(() => router.replace("/photos"), [router]);
  return <main className="auth-page" aria-label="Opening Photos" />;
}
