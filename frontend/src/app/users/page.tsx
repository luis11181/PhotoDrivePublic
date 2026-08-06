import { AuthGate } from "@/components/auth-gate";

export default function UsersPage() {
  return <AuthGate view="users" />;
}
