import { AuthGate } from "@/components/auth-gate";

export default function DeveloperPage() {
  return <AuthGate view="developer" />;
}
