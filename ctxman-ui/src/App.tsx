import { useEffect, useMemo, useState } from "react";
import { api, getTenant, setTenant } from "./api/client";
import type { Healthz } from "./api/types";
import { useKnownSessions } from "./state/sessionStore";
import { useLiveSession } from "./state/useLiveSession";
import { SessionSidebar } from "./components/SessionSidebar";
import { Dashboard } from "./components/Dashboard";
import { Playground } from "./components/Playground";

type Tab = "dashboard" | "playground";

export default function App() {
  const { sessions, add, remove, update } = useKnownSessions();
  const [selectedId, setSelectedId] = useState<string | null>(sessions[0]?.id ?? null);
  const [tab, setTab] = useState<Tab>("dashboard");
  const [creating, setCreating] = useState(false);
  const [health, setHealth] = useState<Healthz | null>(null);
  const [tenant, setTenantState] = useState(getTenant());
  // Pinned-Status pro Session: die Events tragen kein pinned-Flag, deshalb merkt sich die UI,
  // was sie selbst gepinnt/angehängt hat (Best-Effort-Anzeige).
  const [pinned, setPinned] = useState<Record<string, Set<string>>>({});

  const known = useMemo(
    () => sessions.find((s) => s.id === selectedId) ?? null,
    [sessions, selectedId],
  );

  const { detail, live, error, refresh } = useLiveSession(known?.id ?? null);

  useEffect(() => {
    const tick = () => api.healthz().then(setHealth).catch(() => setHealth(null));
    tick();
    const handle = setInterval(tick, 10_000);
    return () => clearInterval(handle);
  }, [tenant]);

  const pinnedIds = pinned[selectedId ?? ""] ?? new Set<string>();

  const setPin = (segmentId: string, isPinned: boolean) => {
    if (!selectedId) return;
    setPinned((prev) => {
      const next = new Set(prev[selectedId] ?? []);
      if (isPinned) next.add(segmentId);
      else next.delete(segmentId);
      return { ...prev, [selectedId]: next };
    });
  };

  const applyTenant = (value: string) => {
    setTenant(value);
    setTenantState(value);
  };

  return (
    <div className="app">
      <header className="header">
        <div className="logo">
          ctx<span>man</span> <span style={{ color: "var(--text-faint)", fontSize: 12 }}>UI</span>
        </div>
        <div className="spacer" />
        <label className="muted">Tenant:</label>
        <input
          className="tenant-input"
          placeholder="default"
          value={tenant}
          onChange={(e) => applyTenant(e.target.value)}
          title="X-Tenant-Id-Header (Auth-Modus none); leer = default_tenant"
        />
        <div className="health" title={health ? `auth_mode: ${health.auth_mode}` : "API nicht erreichbar"}>
          <span className={`dot ${health ? "up" : ""}`} />
          {health ? `API ok · auth ${health.auth_mode}` : "API offline"}
        </div>
      </header>

      <SessionSidebar
        sessions={sessions}
        selectedId={selectedId}
        onSelect={(id) => {
          setSelectedId(id);
          setCreating(false);
        }}
        onAdd={(s) => {
          add(s);
          setSelectedId(s.id);
        }}
        onRemove={(id) => {
          remove(id);
          if (selectedId === id) setSelectedId(null);
        }}
        onCreateNew={() => {
          setCreating(true);
          setTab("playground");
        }}
      />

      <main className="main">
        {error && <div className="error-banner">Live-Update-Fehler: {error}</div>}
        <div className="tabs">
          <button className={tab === "dashboard" ? "active" : ""} onClick={() => setTab("dashboard")}>
            📊 Dashboard
          </button>
          <button className={tab === "playground" ? "active" : ""} onClick={() => setTab("playground")}>
            🎮 Spielwiese
          </button>
        </div>

        {tab === "dashboard" &&
          (known ? (
            <Dashboard
              known={known}
              detail={detail}
              live={live}
              pinnedIds={pinnedIds}
              onPinChange={setPin}
              refresh={refresh}
            />
          ) : (
            <div className="empty-state">
              <h2>Keine Session ausgewählt</h2>
              <p>
                Links eine Session wählen, eine ID hinzufügen — oder auf der Spielwiese eine neue Session
                anlegen und den Context-GC live beobachten.
              </p>
              <button
                className="primary"
                onClick={() => {
                  setCreating(true);
                  setTab("playground");
                }}
              >
                🎮 Zur Spielwiese
              </button>
            </div>
          ))}

        {tab === "playground" && (
          <Playground
            known={known}
            detail={detail}
            live={live}
            creating={creating}
            onDoneCreating={() => setCreating(false)}
            onSessionCreated={(s) => {
              add(s);
              setSelectedId(s.id);
            }}
            onKnownUpdate={update}
            onPinned={(segId) => setPin(segId, true)}
            refresh={refresh}
          />
        )}
      </main>
    </div>
  );
}
