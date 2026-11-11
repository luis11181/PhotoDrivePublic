import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "export",
  // Add the LAN or Tailscale hostnames you open the development server from.
  allowedDevOrigins: ["photodrive", "photodrive.local"],
  images: {
    unoptimized: true,
    remotePatterns: [{ protocol: "https", hostname: "images.unsplash.com" }],
  },
  turbopack: { root: process.cwd() },
};

export default nextConfig;
