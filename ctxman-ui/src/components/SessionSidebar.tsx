import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { SessionDetail } from "../api/types";
import { WATERMARK_COLORS } from "../lib/colors";
import { formatTokens, shortId } from "../lib/format";
import { DEFAULT_BUDGET, type KnownSession } from "../state/sessionStore";

interface Props {
  sessions: KnownSession[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onAdd: (session: KnownSession) => void;
  onRemove: (id: string) => void;
  onCreateNew: () => void;
}

/** Sidebar mit allen der UI bekannten Sessions (localStorage) + „Session per ID hinzufügen“. */
export function SessionSidebar({ sessions, selectedId, onSelect, onAdd, onRemove, onCreateNew }: Props) {
  const [details, setDetails] = useState<Record<string, SessionDetail>>({});
  const [addId, setAddId] = useState("");
  const [addError, setAddError] = useState<string | null>(null);

  // Karten-Refresh: alle bekannten Sessions alle 5 s aktualisieren (kleine N, billige Reads).
  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      const entries = await Promise.all(
        sessions.map(async (s) => {
          try {
            return [s.id, await api.getSession(s.id)] as const;
          } catch {
            return null;
          }
        }),
      );
      if (cancelled) return;
      setDetails((prev) => {
        const next = { ...prev };
        for (const e of entries) if (e) next[e[0]] = e[1];
        return next;
      });
    };
    void tick();
    const handle = setInterval(tick, 5000);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [sessions]);

  const handleAdd = async () => {
    const id = addId.trim();
    if (!id) return;
    setAddError(null);
    try {
      await api.getSession(id); // existiert + gehört zum Tenant?
      onAdd({ id, label: `Session ${shortId(id)}`, addedAt: new Date().toISOString() });
      setAddId("");
    } catch {
      setAddError("Session nicht gefunden (ID/Tenant prüfen)");
    }
  };

  return (
    <aside className="sidebar">
      <div className="sidebar-head">
        <h2>Sessions</h2>
        <button className="primary small" onClick={onCreateNew}>+ Neu</button>
      </div>
      <div className="session-list">
        {sessions.length === 0 && (
          <span className="muted" style={{ padding: 8 }}>
            Noch keine Sessions bekannt. Auf der Spielwiese eine anlegen oder unten eine ID eintragen.
          </span>
        )}
        {sessions.map((s) => {
          const d = details[s.id];
          const budget = s.budgetTokens ?? DEFAULT_BUDGET;
          const frac = d ? Math.min(d.tokens_used / budget, 1) : 0;
          const color = d ? WATERMARK_COLORS[d.watermark_state] : "#475569";
          return (
            <div
              key={s.id}
              className={`session-card ${selectedId === s.id ? "selected" : ""}`}
              onClick={() => onSelect(s.id)}
            >
              <div className="card-top">
                <span className="label">{s.label}</span>
                <button
                  className="small danger"
                  title="Aus der Liste entfernen (Session bleibt im Service)"
                  onClick={(e) => {
                    e.stopPropagation();
                    onRemove(s.id);
                  }}
                >
                  ✕
                </button>
              </div>
              <div className="sid">{s.id}</div>
              <div className="card-meta">
                {d ? (
                  <>
                    <span>{formatTokens(d.tokens_used)} tok</span>
                    <span>Turn {d.current_turn}</span>
                    <span className={`badge ${d.status === "active" ? d.watermark_state : "archived"}`}>
                      {d.status === "active" ? d.watermark_state : "archiviert"}
                    </span>
                  </>
                ) : (
                  <span>lädt…</span>
                )}
              </div>
              <div className="mini-bar">
                <div className="fill" style={{ width: `${frac * 100}%`, background: color }} />
              </div>
            </div>
          );
        })}
      </div>
      <div className="add-by-id">
        <input
          placeholder="Session-ID (ULID) hinzufügen…"
          value={addId}
          onChange={(e) => setAddId(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && void handleAdd()}
        />
        <button onClick={() => void handleAdd()}>＋</button>
      </div>
      {addError && <div className="error-banner" style={{ margin: "0 10px 10px" }}>{addError}</div>}
    </aside>
  );
}
