import { useState } from "react";
import { api } from "../api/client";
import type { AppendSegmentInput, SessionDetail, StaticSegmentInput } from "../api/types";
import type { LiveState } from "../state/eventReducer";
import { DEFAULT_WATERMARKS, type KnownSession } from "../state/sessionStore";
import { loremTokens } from "../lib/format";

interface ConsoleEntry {
  id: number;
  title: string;
  ok: boolean;
  body: string;
  at: string;
}

interface Props {
  known: KnownSession | null;
  detail: SessionDetail | null;
  live: LiveState;
  onSessionCreated: (session: KnownSession) => void;
  onKnownUpdate: (id: string, patch: Partial<KnownSession>) => void;
  onPinned: (segmentId: string) => void;
  refresh: () => void;
  creating: boolean;
  onDoneCreating: () => void;
}

const KINDS = [
  "user_msg",
  "assistant_msg",
  "tool_call",
  "tool_result",
  "task",
  "decision",
  "skill_content",
  "mcp_resource",
];

let entryCounter = 0;

export function Playground(props: Props) {
  const { known, detail, live, refresh } = props;
  const [console_, setConsole] = useState<ConsoleEntry[]>([]);

  const log = (title: string, ok: boolean, body: unknown) => {
    setConsole((prev) =>
      [
        {
          id: ++entryCounter,
          title,
          ok,
          body: typeof body === "string" ? body : JSON.stringify(body, null, 2),
          at: new Date().toLocaleTimeString("de-DE"),
        },
        ...prev,
      ].slice(0, 40),
    );
  };

  const run = async <T,>(title: string, fn: () => Promise<T>): Promise<T | null> => {
    try {
      const result = await fn();
      log(title, true, result ?? "OK");
      refresh();
      return result;
    } catch (e) {
      log(title, false, e instanceof Error ? e.message : String(e));
      refresh();
      return null;
    }
  };

  return (
    <div className="pg-grid">
      <div>
        {(props.creating || !known) && (
          <CreateSessionPanel
            onCreated={(s) => {
              props.onSessionCreated(s);
              props.onDoneCreating();
              log("POST /v1/sessions", true, { session_id: s.id });
            }}
            onError={(msg) => log("POST /v1/sessions", false, msg)}
          />
        )}
        {known && detail && !props.creating && (
          <>
            <AppendPanel sessionId={known.id} run={run} onPinned={props.onPinned} />
            <RenderPanel sessionId={known.id} run={run} />
            <GcFramePanel sessionId={known.id} live={live} run={run} />
            <EpochBumpPanel
              known={known}
              detail={detail}
              run={run}
              onKnownUpdate={props.onKnownUpdate}
            />
          </>
        )}
      </div>
      <div className="panel" style={{ position: "sticky", top: 0 }}>
        <h3>Response-Konsole <span className="hint">— letzte API-Antworten</span></h3>
        <div className="console">
          {console_.length === 0 && <span className="muted">Noch keine Aktionen.</span>}
          {console_.map((e) => (
            <div key={e.id} className="entry">
              <div className={e.ok ? "head" : "err"}>
                {e.ok ? "✓" : "✗"} {e.title} <span style={{ color: "var(--text-faint)" }}>· {e.at}</span>
              </div>
              <div className="body">{e.body.length > 2200 ? e.body.slice(0, 2200) + " …(gekürzt)" : e.body}</div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

/* ---------- Session anlegen ---------- */

function CreateSessionPanel({
  onCreated,
  onError,
}: {
  onCreated: (s: KnownSession) => void;
  onError: (msg: string) => void;
}) {
  const [label, setLabel] = useState("Spielwiese");
  const [budget, setBudget] = useState(20_000);
  const [soft, setSoft] = useState(DEFAULT_WATERMARKS.soft);
  const [hard, setHard] = useState(DEFAULT_WATERMARKS.hard);
  const [emergency, setEmergency] = useState(DEFAULT_WATERMARKS.emergency);
  const [threshold, setThreshold] = useState(2000);
  const [systemPrompt, setSystemPrompt] = useState(
    "Du bist ein hilfreicher Agent. Beantworte präzise und nutze Tools, wenn nötig.",
  );
  const [toolLines, setToolLines] = useState(
    "core | get_weather | Liefert das Wetter für einen Ort\ncore | search_web | Websuche\nmcp:github | list_issues | Listet GitHub-Issues",
  );
  const [sinkUrl, setSinkUrl] = useState(
    () => localStorage.getItem("ctxman-ui.sink") ?? "http://localhost:5999/memory/ingest",
  );
  const [busy, setBusy] = useState(false);

  const create = async () => {
    setBusy(true);
    try {
      const staticSegments: StaticSegmentInput[] = [
        { kind: "system_prompt", role: "system", content: systemPrompt, source: "core" },
      ];
      for (const line of toolLines.split("\n")) {
        const [source, name, desc] = line.split("|").map((p) => p.trim());
        if (!source || !name) continue;
        staticSegments.push({
          kind: "tool_def",
          role: "system",
          source,
          content: JSON.stringify({ name, description: desc ?? "" }),
        });
      }
      const res = await api.createSession({
        policy_overrides: {
          budget_tokens: budget,
          watermarks: { soft, hard, emergency },
          externalize_threshold_tokens: threshold,
          // Frame-Pop/Archive promoten an diese URL — lokal der Mock (npm run mock).
          promotion: sinkUrl.trim() ? { sink: { type: "webhook", url: sinkUrl.trim() } } : undefined,
        },
        static_segments: staticSegments,
      });
      onCreated({
        id: res.session_id,
        label,
        addedAt: new Date().toISOString(),
        budgetTokens: budget,
        watermarks: { soft, hard, emergency },
        staticSegments,
      });
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="panel">
      <h3>Neue Session anlegen <span className="hint">— POST /v1/sessions, Policy + Static-Region</span></h3>
      <div className="form-row">
        <label>Label (nur UI)</label>
        <input value={label} onChange={(e) => setLabel(e.target.value)} />
      </div>
      <div className="form-row">
        <label>Budget (Tokens)</label>
        <input type="number" value={budget} onChange={(e) => setBudget(Number(e.target.value))} />
      </div>
      <div className="form-row">
        <label>Watermarks</label>
        <div className="row">
          <input type="number" step={0.05} min={0.05} max={1} style={{ width: 70 }} value={soft} onChange={(e) => setSoft(Number(e.target.value))} title="soft" />
          <input type="number" step={0.05} min={0.05} max={1} style={{ width: 70 }} value={hard} onChange={(e) => setHard(Number(e.target.value))} title="hard" />
          <input type="number" step={0.05} min={0.05} max={1} style={{ width: 70 }} value={emergency} onChange={(e) => setEmergency(Number(e.target.value))} title="emergency" />
          <span className="muted">soft / hard / emergency</span>
        </div>
      </div>
      <div className="form-row">
        <label>Externalize ab</label>
        <input type="number" value={threshold} onChange={(e) => setThreshold(Number(e.target.value))} />
      </div>
      <div className="form-row" style={{ alignItems: "start" }}>
        <label>System-Prompt</label>
        <textarea rows={3} value={systemPrompt} onChange={(e) => setSystemPrompt(e.target.value)} />
      </div>
      <div className="form-row" style={{ alignItems: "start" }}>
        <label>Tool-Defs<br /><span className="muted">source | name | desc</span></label>
        <textarea rows={4} value={toolLines} onChange={(e) => setToolLines(e.target.value)} />
      </div>
      <div className="form-row">
        <label>Promotion-Sink</label>
        <input
          value={sinkUrl}
          onChange={(e) => {
            setSinkUrl(e.target.value);
            localStorage.setItem("ctxman-ui.sink", e.target.value);
          }}
          title="Frame-Pop/Archive promoten Fakten an diese URL. Lokal: npm run mock (Port 5999)."
        />
      </div>
      <div className="row" style={{ justifyContent: "flex-end" }}>
        <button className="primary" disabled={busy} onClick={() => void create()}>
          Session erstellen
        </button>
      </div>
      <p className="muted" style={{ marginBottom: 0 }}>
        Tipp: Mit kleinem Budget (z.&nbsp;B. 20k) erreichst du die Watermarks schnell und siehst den GC arbeiten.
      </p>
    </div>
  );
}

/* ---------- Segmente anhängen ---------- */

function AppendPanel({
  sessionId,
  run,
  onPinned,
}: {
  sessionId: string;
  run: <T>(title: string, fn: () => Promise<T>) => Promise<T | null>;
  onPinned: (id: string) => void;
}) {
  const [kind, setKind] = useState("user_msg");
  const [role, setRole] = useState("user");
  const [content, setContent] = useState("Hallo Agent, analysiere bitte das Cluster.");
  const [pinned, setPinned] = useState(false);

  const append = async (segments: AppendSegmentInput[], title: string, pinIdx: number[] = []) => {
    const res = await run(title, () => api.appendSegments(sessionId, segments));
    if (res) for (const i of pinIdx) onPinned(res.segment_ids[i]);
  };

  const toolUnit = (tokens: number) => {
    const callId = `call_${Math.random().toString(36).slice(2, 10)}`;
    return [
      {
        kind: "tool_call",
        role: "assistant",
        content: JSON.stringify({ tool: "search_web", input: { query: "ctxman" }, id: callId }),
        tool_call_id: callId,
      },
      {
        kind: "tool_result",
        role: "tool",
        content: loremTokens(tokens, callId),
        tool_call_id: callId,
      },
    ];
  };

  return (
    <div className="panel">
      <h3>Segmente anhängen <span className="hint">— POST /segments (Idempotency-Key automatisch)</span></h3>
      <div className="quick-actions" style={{ marginBottom: 12 }}>
        <button onClick={() => void append([{ kind: "user_msg", role: "user", content: loremTokens(60, "user") }], "append user_msg")}>
          💬 user_msg
        </button>
        <button onClick={() => void append([{ kind: "assistant_msg", role: "assistant", content: loremTokens(90, "assistant") }], "append assistant_msg")}>
          🤖 assistant_msg
        </button>
        <button onClick={() => void append(toolUnit(300), "append Tool-Unit (klein)")}>
          🔧 Tool-Unit klein (~300 tok)
        </button>
        <button onClick={() => void append(toolUnit(4000), "append Tool-Unit (groß)")}>
          🐘 Tool-Unit groß (~4k tok → Externalisierung)
        </button>
        <button onClick={() => void append([{ kind: "decision", role: "assistant", content: "Entscheidung: Wir nutzen Postgres als primären Store.", pinned: true }], "append decision (pinned)", [0])}>
          📌 decision (pinned)
        </button>
        <button onClick={() => void append([{ kind: "task", role: "user", content: "Task: Memory-Leak im GC-Worker finden und fixen.", pinned: true }], "append task (pinned)", [0])}>
          📌 task (pinned)
        </button>
      </div>
      <div className="form-row">
        <label>Kind</label>
        <select value={kind} onChange={(e) => setKind(e.target.value)}>
          {KINDS.map((k) => (
            <option key={k}>{k}</option>
          ))}
        </select>
      </div>
      <div className="form-row">
        <label>Role</label>
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          {["user", "assistant", "tool", "system"].map((r) => (
            <option key={r}>{r}</option>
          ))}
        </select>
      </div>
      <div className="form-row" style={{ alignItems: "start" }}>
        <label>Content</label>
        <textarea rows={3} value={content} onChange={(e) => setContent(e.target.value)} />
      </div>
      <div className="row" style={{ justifyContent: "space-between" }}>
        <label className="muted" style={{ display: "flex", gap: 6, alignItems: "center" }}>
          <input type="checkbox" checked={pinned} onChange={(e) => setPinned(e.target.checked)} /> pinned
        </label>
        <button
          className="primary"
          onClick={() => void append([{ kind, role, content, pinned }], `append ${kind}`, pinned ? [0] : [])}
        >
          Anhängen
        </button>
      </div>
    </div>
  );
}

/* ---------- Render ---------- */

function RenderPanel({
  sessionId,
  run,
}: {
  sessionId: string;
  run: <T>(title: string, fn: () => Promise<T>) => Promise<T | null>;
}) {
  const [provider, setProvider] = useState("anthropic");
  const [scope, setScope] = useState<"path" | "frame">("path");
  const [turnAdvance, setTurnAdvance] = useState(true);

  return (
    <div className="panel">
      <h3>Render <span className="hint">— POST /render, liefert das Provider-Request-Fragment</span></h3>
      <div className="row">
        <select value={provider} onChange={(e) => setProvider(e.target.value)}>
          <option value="anthropic">anthropic</option>
          <option value="openai">openai</option>
        </select>
        <select value={scope} onChange={(e) => setScope(e.target.value as "path" | "frame")}>
          <option value="path">scope: path</option>
          <option value="frame">scope: frame</option>
        </select>
        <label className="muted" style={{ display: "flex", gap: 6, alignItems: "center" }}>
          <input type="checkbox" checked={turnAdvance} onChange={(e) => setTurnAdvance(e.target.checked)} />
          turn_advance
        </label>
        <button className="primary" onClick={() => void run(`render (${provider}, ${scope})`, () => api.render(sessionId, provider, scope, turnAdvance))}>
          ▶ Render
        </button>
      </div>
      <p className="muted" style={{ marginBottom: 0 }}>
        Ein Turn = ein Render mit turn_advance (Spec §3) — TTLs altern pro Model-Call, nicht pro User-Nachricht.
      </p>
    </div>
  );
}

/* ---------- GC + Frames ---------- */

function GcFramePanel({
  sessionId,
  live,
  run,
}: {
  sessionId: string;
  live: LiveState;
  run: <T>(title: string, fn: () => Promise<T>) => Promise<T | null>;
}) {
  const [frameLabel, setFrameLabel] = useState("research_subtask");
  const [returnContent, setReturnContent] = useState("Subagent-Ergebnis: Analyse abgeschlossen, 3 Findings.");

  const openFrames = Array.from(live.frames.values()).filter((f) => f.status === "open");
  const tip = openFrames[openFrames.length - 1];

  return (
    <div className="panel">
      <h3>GC &amp; Frames</h3>
      <div className="row" style={{ marginBottom: 12 }}>
        <button onClick={() => void run("gc minor", () => api.gc(sessionId, "minor"))}>🧹 Minor GC</button>
        <button onClick={() => void run("gc major", () => api.gc(sessionId, "major"))}>🗜 Major GC (Compaction)</button>
        <span className="muted">202 → Job läuft async, Events zeigen das Ergebnis</span>
      </div>
      <div className="row" style={{ marginBottom: 8 }}>
        <input value={frameLabel} onChange={(e) => setFrameLabel(e.target.value)} style={{ width: 180 }} />
        <button onClick={() => void run(`frame push "${frameLabel}"`, () => api.pushFrame(sessionId, frameLabel))}>
          ⤵ Frame push
        </button>
      </div>
      {tip && (
        <div className="col">
          <textarea rows={2} value={returnContent} onChange={(e) => setReturnContent(e.target.value)} />
          <div className="row">
            <button onClick={() => void run(`frame pop "${tip.label}"`, () => api.popFrame(sessionId, tip.id, returnContent))}>
              ⤴ Pop „{tip.label}" (oberster Frame)
            </button>
            <span className="muted">{openFrames.length} offen</span>
          </div>
        </div>
      )}
    </div>
  );
}

/* ---------- Epoch-Bump ---------- */

function EpochBumpPanel({
  known,
  detail,
  run,
  onKnownUpdate,
}: {
  known: KnownSession;
  detail: SessionDetail;
  run: <T>(title: string, fn: () => Promise<T>) => Promise<T | null>;
  onKnownUpdate: (id: string, patch: Partial<KnownSession>) => void;
}) {
  const [json, setJson] = useState(() =>
    JSON.stringify(
      known.staticSegments ?? [
        { kind: "system_prompt", role: "system", content: "…", source: "core" },
      ],
      null,
      2,
    ),
  );
  const [parseError, setParseError] = useState<string | null>(null);

  const bump = async () => {
    let segments: StaticSegmentInput[];
    try {
      segments = JSON.parse(json) as StaticSegmentInput[];
      setParseError(null);
    } catch (e) {
      setParseError(`JSON ungültig: ${e instanceof Error ? e.message : e}`);
      return;
    }
    const res = await run("PUT /static-segments (Epoch-Bump)", () =>
      api.replaceStaticSegments(known.id, segments, detail.context_version),
    );
    if (res) onKnownUpdate(known.id, { staticSegments: segments });
  };

  return (
    <div className="panel">
      <h3>Static-Epoch-Bump <span className="hint">— PUT /static-segments, ersetzt die Static-Region (Spec §4.2)</span></h3>
      <textarea rows={6} style={{ width: "100%" }} value={json} onChange={(e) => setJson(e.target.value)} />
      {parseError && <div className="error-banner">{parseError}</div>}
      <div className="row" style={{ justifyContent: "space-between", marginTop: 8 }}>
        <span className="muted">If-Match: {detail.context_version} (aktuelle context_version)</span>
        <button onClick={() => void bump()}>⚡ Epoch bumpen (e{detail.static_epoch} → e{detail.static_epoch + 1})</button>
      </div>
    </div>
  );
}

// Der frühere einfache Turn-Simulator ist ins Simulations-Labor (Tab „Simulation")
// umgezogen — dort gibt es Szenarien für Subagents, Marathon, Tool-Sturm usw.
