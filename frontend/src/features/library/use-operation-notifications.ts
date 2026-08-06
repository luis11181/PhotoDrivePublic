"use client";

import { useCallback, useState } from "react";

export type OperationStatus = "running" | "success" | "warning" | "error";

export interface OperationNotification {
  id: string;
  title: string;
  status: OperationStatus;
  total: number;
  completed: number;
  succeeded: number;
  failed: number;
  successLabel: string;
  detail?: string;
  createdAt: string;
}

type OperationUpdate = Partial<
  Pick<
    OperationNotification,
    "title" | "status" | "completed" | "succeeded" | "failed" | "detail"
  >
>;

export function useOperationNotifications() {
  const [notifications, setNotifications] = useState<OperationNotification[]>(
    [],
  );

  const startOperation = useCallback(
    (options: { title: string; total: number; successLabel: string }) => {
      const id = createId();
      setNotifications((current) => [
        {
          id,
          title: options.title,
          status: "running",
          total: options.total,
          completed: 0,
          succeeded: 0,
          failed: 0,
          successLabel: options.successLabel,
          createdAt: new Date().toISOString(),
        },
        ...current,
      ]);
      return id;
    },
    [],
  );

  const updateOperation = useCallback((id: string, update: OperationUpdate) => {
    setNotifications((current) =>
      current.map((notification) =>
        notification.id === id ? { ...notification, ...update } : notification,
      ),
    );
  }, []);

  const addResult = useCallback(
    (options: {
      title: string;
      successLabel: string;
      succeeded: number;
      failed?: number;
      detail?: string;
    }) => {
      const failed = options.failed ?? 0;
      const total = options.succeeded + failed;
      const id = createId();
      setNotifications((current) => [
        {
          id,
          title: options.title,
          status:
            failed === 0
              ? "success"
              : options.succeeded > 0
                ? "warning"
                : "error",
          total,
          completed: total,
          succeeded: options.succeeded,
          failed,
          successLabel: options.successLabel,
          detail: options.detail,
          createdAt: new Date().toISOString(),
        },
        ...current,
      ]);
      return id;
    },
    [],
  );

  const dismiss = useCallback((id: string) => {
    setNotifications((current) =>
      current.filter((notification) => notification.id !== id),
    );
  }, []);

  const clearCompleted = useCallback(() => {
    setNotifications((current) =>
      current.filter((notification) => notification.status === "running"),
    );
  }, []);

  return {
    notifications,
    startOperation,
    updateOperation,
    addResult,
    dismiss,
    clearCompleted,
  };
}

function createId() {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
