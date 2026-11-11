"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Crown, Loader2, Server } from "lucide-react";
import { getClusterHosts, setPrimaryHost, type ClusterHost } from "@/lib/api";

const copy = {
  en: {
    title: "Servers",
    help: "Every computer running PhotoDrive shares the same cloud database. The primary server also extracts photo metadata and runs the weekly database backup. Imports and cache preparation always run on the server where you started them. Before turning the primary off, make another server primary.",
    primary: "Primary",
    thisServer: "This server",
    online: "Online",
    offline: "Offline",
    lastSeen: "Last seen",
    makePrimary: "Make primary",
    failed: "Could not load the servers.",
  },
  es: {
    title: "Servidores",
    help: "Cada computador que ejecuta PhotoDrive comparte la misma base de datos en la nube. El servidor principal además extrae los metadatos de las fotos y hace el respaldo semanal de la base de datos. Las importaciones y la preparación de caché siempre se ejecutan en el servidor donde las iniciaste. Antes de apagar el principal, convierte otro servidor en principal.",
    primary: "Principal",
    thisServer: "Este servidor",
    online: "En línea",
    offline: "Sin conexión",
    lastSeen: "Visto por última vez",
    makePrimary: "Hacer principal",
    failed: "No se pudieron cargar los servidores.",
  },
};

export function ServerHostsPanel({ locale }: { locale: "en" | "es" }) {
  const text = copy[locale];
  const queryClient = useQueryClient();
  const hosts = useQuery({ queryKey: ["cluster-hosts"], queryFn: getClusterHosts, refetchInterval: 60_000 });
  const makePrimary = useMutation({
    mutationFn: setPrimaryHost,
    onSuccess: (data) => {
      queryClient.setQueryData(["cluster-hosts"], data);
      void queryClient.invalidateQueries({ queryKey: ["developer-operational-health"] });
    },
  });

  return (
    <section className="imports-panel" id="servers">
      <div className="imports-section-heading">
        <div>
          <h2>{text.title}</h2>
          <p>{text.help}</p>
        </div>
      </div>
      {hosts.isPending && <Loader2 className="spin" aria-hidden="true" />}
      {hosts.isError && <p className="auth-error">{text.failed}</p>}
      {(makePrimary.error as Error | null) && <p className="auth-error">{(makePrimary.error as Error).message}</p>}
      {hosts.data && (
        <ul className="developer-hosts">
          {hosts.data.hosts.map((host) => (
            <HostRow
              busy={makePrimary.isPending}
              host={host}
              key={host.id}
              locale={locale}
              onMakePrimary={() => makePrimary.mutate(host.id)}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

function HostRow({ busy, host, locale, onMakePrimary }: {
  busy: boolean;
  host: ClusterHost;
  locale: "en" | "es";
  onMakePrimary: () => void;
}) {
  const text = copy[locale];
  const lastSeen = new Date(host.lastSeenAt).toLocaleString(locale === "es" ? "es-CO" : "en-US");
  return (
    <li className="developer-host">
      <Server aria-hidden="true" />
      <div>
        <strong>{host.name}</strong>
        <small>
          {host.isCurrent ? `${text.thisServer} · ` : ""}
          {host.online ? text.online : `${text.offline} · ${text.lastSeen} ${lastSeen}`}
        </small>
      </div>
      {host.isPrimary ? (
        <span className="developer-host-primary"><Crown aria-hidden="true" /> {text.primary}</span>
      ) : (
        <button className="secondary" disabled={busy} onClick={onMakePrimary} type="button">{text.makePrimary}</button>
      )}
    </li>
  );
}
