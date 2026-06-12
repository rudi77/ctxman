import { useState, useSyncExternalStore } from "react";
import { SCENARIOS } from "../sim/scenarios";
import { getSimState, setSimSpeed, startSim, stopSim, subscribeSim, type SimHooks } from "../sim/runner";
import type { Scenario } from "../sim/types";

interface Props {
  hooks: SimHooks;
  /** Zur laufenden Simulation aufs Dashboard springen. */
  onShowDashboard: () => void;
}

/**
 * Simulations-Labor: Galerie vordefinierter Szenarien (Subagents, Marathon,
 * Tool-Sturm, …). Der Lauf lebt in einem Modul-Singleton — Tab-Wechsel
 * unterbricht ihn nicht, das Dashboard zeigt parallel den Live-Effekt.
 */
export function SimulationLab({ hooks, onShowDashboard }: Props) {
  const sim = useSyncExternalStore(subscribeSim, getSimState);
  const [params, setParams] = useState<Record<string, Record<string, number>>>({});
  const [speed, setSpeed] = useState(1);
  const [sinkUrl, setSinkUrl] = useState(
    () => localStorage.getItem("ctxman-ui.sink") ?? "http://localhost:5999/memory/ingest",
  );
  const running = sim.status === "running";

  const paramsFor = (s: Scenario): Record<string, number> => {
    const stored = params[s.id] ?? {};
    return Object.fromEntries(s.params.map((p) => [p.key, stored[p.key] ?? p.default]));
  };

  const start = (s: Scenario) => {
    setSimSpeed(speed);
    void startSim(s, paramsFor(s), hooks, { promotionSinkUrl: sinkUrl.trim() || undefined });
    onShowDashboard();
  };

  return (
    <div>
      <div className="panel">
        <h3>
          Simulations-Labor{" "}
          <span className="hint">
            — jedes Szenario erstellt eine eigene Session mit passender Policy; das Dashboard zeigt live den Effekt
          </span>
        </h3>
        <div className="row" style={{ justifyContent: "space-between" }}>
          <label className="muted" style={{ display: "flex", gap: 8, alignItems: "center" }}>
            Geschwindigkeit:
            <select
              value={speed}
              onChange={(e) => {
                const f = Number(e.target.value);
                setSpeed(f);
                setSimSpeed(f); // wirkt auch auf einen laufenden Lauf
              }}
            >
              <option value={0.5}>🐢 gemütlich (0.5×)</option>
              <option value={1}>▶ normal (1×)</option>
              <option value={2.5}>⏩ schnell (2.5×)</option>
            </select>
          </label>
          <label className="muted" style={{ display: "flex", gap: 8, alignItems: "center" }}>
            Promotion-Sink:
            <input
              style={{ width: 280 }}
              value={sinkUrl}
              onChange={(e) => {
                setSinkUrl(e.target.value);
                localStorage.setItem("ctxman-ui.sink", e.target.value);
              }}
              title="Frame-Pop/Archive promoten Fakten an diese URL (Spec §2.5/§3.3). Lokal: npm run mock starten."
            />
          </label>
          {running && (
            <div className="row">
              <span className="badge soft">läuft: {sim.scenarioName}</span>
              <button onClick={onShowDashboard}>📊 Live im Dashboard</button>
              <button className="danger" onClick={stopSim}>⏹ Stopp</button>
            </div>
          )}
        </div>
      </div>

      <div className="scenario-grid">
        {SCENARIOS.map((s) => (
          <div key={s.id} className={`panel scenario-card ${sim.scenarioId === s.id && running ? "running" : ""}`}>
            <div className="scenario-head">
              <span className="scenario-emoji">{s.emoji}</span>
              <div>
                <div className="scenario-name">{s.name}</div>
                <div className="scenario-tagline">{s.tagline}</div>
              </div>
            </div>
            <p className="scenario-desc">{s.description}</p>
            <div className="scenario-watch">
              <span className="muted">Beobachten:</span>
              <ul>
                {s.watchFor.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            </div>
            <div className="row" style={{ justifyContent: "space-between", marginTop: "auto" }}>
              <div className="row">
                {s.params.map((p) => (
                  <label key={p.key} className="muted" style={{ display: "flex", gap: 5, alignItems: "center" }}>
                    {p.label}
                    <input
                      type="number"
                      min={p.min}
                      max={p.max}
                      style={{ width: 62 }}
                      value={paramsFor(s)[p.key]}
                      onChange={(e) =>
                        setParams((prev) => ({
                          ...prev,
                          [s.id]: { ...prev[s.id], [p.key]: Number(e.target.value) },
                        }))
                      }
                    />
                  </label>
                ))}
                <span className="chip" title="Policy dieses Szenarios">
                  Budget <b>{(s.policy.budgetTokens / 1000).toFixed(0)}k</b>
                </span>
              </div>
              <button className="primary" disabled={running} onClick={() => start(s)}>
                ▶ Starten
              </button>
            </div>
          </div>
        ))}
      </div>

      {(sim.lines.length > 0 || running) && (
        <div className="panel">
          <h3>
            Simulations-Log
            {sim.status === "done" && <span className="badge ok">fertig</span>}
            {sim.status === "aborted" && <span className="badge archived">gestoppt</span>}
            {sim.status === "error" && <span className="badge emergency">Fehler</span>}
            {running && <span className="badge soft">läuft</span>}
          </h3>
          <div className="sim-narration">
            {[...sim.lines].reverse().map((l, i) => (
              <div key={sim.lines.length - i} className={`sim-line ${l.kind}`}>
                <span className="t">{l.at}</span>
                <span>{l.text}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
