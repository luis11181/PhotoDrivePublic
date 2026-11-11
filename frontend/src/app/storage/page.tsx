import { AuthGate } from "@/components/auth-gate";
export default function StoragePage() {
  return <AuthGate view="storage" />;
}
