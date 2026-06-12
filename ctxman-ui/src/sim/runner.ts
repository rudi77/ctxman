import { api } from "../api/client";
import { ApiError, type RenderResponse, type StaticSegmentInput } from "../api/types";
import type { KnownSession } from "../state/sessionStore";
import { loremTokens } from "../lib/format";
import type { Scenario, SimContext, SimLine, SimState } from "./types";

// Modul-Singleton: genau ein aktiver Lauf. Der Zustand lebt außerhalb von React,
// damit Tab-Wechsel (Unmount der SimulationLab) den Lauf nicht verlieren —
// Komponenten docken per useSyncExternalStore wieder an.

class AbortError extends Error {
  constructor() {
    super("Simulation abgebrochen");
  }
}

let state: SimState = {
  status: "idle",
  scenarioId: null,
  scenarioName: null,
  sessionId: null,
  lines: [],
};

let abortRequested = false;
let speedFactor = 1;
const listeners = new Set<() => void>();

function emit(): void {
  state = { ...state };
  for (const fn of listeners) fn();
}

export function subscribeSim(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

export function getSimState(): SimState {
  return state;
}

export function stopSim(): void {
  abortRequested = true;
}

export function setSimSpeed(factor: number): void {
  speedFactor = factor;
}

const MAX_LINES = 400;

function pushLine(kind: SimLine["kind"], text: string): void {
  state.lines = [...state.lines, { at: new Date().toLocaleTimeString("de-DE"), kind, text }];
  if (state.lines.length > MAX_LINES) state.lines = state.lines.slice(-MAX_LINES);
  emit();
}

function throwIfAborted(): void {
  if (abortRequested) throw new AbortError();
}

export interface SimHooks {
  registerSession(session: KnownSession): void;
}

export interface SimOptions {
  /** Promotion-Sink-URL (leer ⇒ Default-Policy, vermutlich Platzhalter — Pops schlagen dann fehl). */
  promotionSinkUrl?: string;
}

export async function startSim(
  scenario: Scenario,
  params: Record<string, number>,
  hooks: SimHooks,
  options: SimOptions = {},
): Promise<void> {
  if (state.status === "running") return;

  abortRequested = false;
  state = {
    status: "running",
    scenarioId: scenario.id,
    scenarioName: scenario.name,
    sessionId: null,
    lines: [],
  };
  emit();
  pushLine("phase", `Szenario „${scenario.name}" startet`);

  try {
    // Eigene Session mit der Szenario-Policy — so sind Watermark-Effekte reproduzierbar.
    const created = await api.createSession({
      policy_overrides: {
        budget_tokens: scenario.policy.budgetTokens,
        watermarks: scenario.policy.watermarks,
        externalize_threshold_tokens: scenario.policy.externalizeThresholdTokens,
        promotion: options.promotionSinkUrl
          ? { sink: { type: "webhook", url: options.promotionSinkUrl } }
          : undefined,
      },
      static_segments: scenario.staticSegments,
    });
    state.sessionId = created.session_id;
    emit();

    hooks.registerSession({
      id: created.session_id,
      label: `${scenario.emoji} ${scenario.name}`,
      addedAt: new Date().toISOString(),
      budgetTokens: scenario.policy.budgetTokens,
      watermarks: scenario.policy.watermarks ?? { soft: 0.6, hard: 0.8, emergency: 0.95 },
      staticSegments: scenario.staticSegments,
    });
    pushLine("info", `Session ${created.session_id} angelegt (Budget ${scenario.policy.budgetTokens} tok)`);

    const ctx = buildContext(created.session_id);
    await scenario.run(ctx, params);

    pushLine("phase", "Szenario abgeschlossen ✅");
    state.status = "done";
    emit();
  } catch (e) {
    if (e instanceof AbortError) {
      pushLine("warn", "⏹ Simulation gestoppt");
      state.status = "aborted";
    } else {
      pushLine("warn", `Fehler: ${e instanceof Error ? e.message : String(e)}`);
      state.status = "error";
    }
    emit();
  }
}

function buildContext(sessionId: string): SimContext {
  const bigResultIds: string[] = [];
  let actionCounter = 0;

  const seed = () => `s${++actionCounter}`;

  const sleep = async (ms: number): Promise<void> => {
    const scaled = ms / speedFactor;
    const step = 100;
    let waited = 0;
    while (waited < scaled) {
      throwIfAborted();
      await new Promise((r) => setTimeout(r, Math.min(step, scaled - waited)));
      waited += step;
    }
    throwIfAborted();
  };

  const append = async (
    segments: Parameters<typeof api.appendSegments>[1],
  ): Promise<string[]> => {
    throwIfAborted();
    const res = await api.appendSegments(sessionId, segments);
    return res.segment_ids;
  };

  const render = async (
    turnAdvance = true,
    scope: "path" | "frame" = "path",
  ): Promise<RenderResponse> => {
    // Spec §3.1: 413 ist retrybar — der Client wartet, während die async Collection läuft.
    for (let attempt = 1; ; attempt++) {
      throwIfAborted();
      try {
        const res = await api.render(sessionId, "anthropic", scope, turnAdvance);
        ctx.log(`render → ${res.tokens_total} tok · ${res.watermark_state}`);
        return res;
      } catch (e) {
        if (e instanceof ApiError && e.status === 413 && attempt <= 4) {
          ctx.warn(`render → 413 Budget Exceeded (Versuch ${attempt}) — warte auf GC, Retry…`);
          await sleep(1500);
          continue;
        }
        throw e;
      }
    }
  };

  const ctx: SimContext = {
    sessionId,
    log: (text) => pushLine("info", text),
    warn: (text) => pushLine("warn", text),
    phase: (title) => pushLine("phase", title),
    sleep,
    rand: (min, max) => min + Math.floor(Math.random() * (max - min + 1)),
    chance: (p) => Math.random() < p,

    appendUser: async (tokens = 80) => {
      await append([{ kind: "user_msg", role: "user", content: loremTokens(tokens, seed()) }]);
      ctx.log(`💬 user_msg (~${tokens} tok)`);
    },
    appendAssistant: async (tokens = 110) => {
      await append([{ kind: "assistant_msg", role: "assistant", content: loremTokens(tokens, seed()) }]);
      ctx.log(`🤖 assistant_msg (~${tokens} tok)`);
    },
    toolUnit: async (tokens, tool = "search_web") => {
      const callId = `call_${seed()}_${Math.random().toString(36).slice(2, 6)}`;
      const ids = await append([
        { kind: "tool_call", role: "assistant", content: JSON.stringify({ tool, id: callId }), tool_call_id: callId },
        { kind: "tool_result", role: "tool", content: loremTokens(tokens, callId), tool_call_id: callId },
      ]);
      if (tokens >= 2000) bigResultIds.push(ids[1]);
      ctx.log(`🔧 Tool-Unit ${tool} (~${tokens} tok)${tokens >= 2000 ? " — Externalisierungs-Kandidat" : ""}`);
      return ids[1];
    },
    decision: async (text) => {
      await append([{ kind: "decision", role: "assistant", content: text, pinned: true }]);
      ctx.log(`📌 decision gepinnt: ${text.slice(0, 60)}…`);
    },
    task: async (text) => {
      await append([{ kind: "task", role: "user", content: text, pinned: true }]);
      ctx.log(`📌 task gepinnt: ${text.slice(0, 60)}…`);
    },
    render,

    pushFrame: async (label) => {
      throwIfAborted();
      const res = await api.pushFrame(sessionId, label);
      ctx.log(`⤵ Frame „${label}" gepusht`);
      return res.frame_id;
    },
    popFrame: async (frameId, returnContent) => {
      throwIfAborted();
      await api.popFrame(sessionId, frameId, returnContent);
      ctx.log(`⤴ Frame gepoppt — subagent_return im Parent`);
    },
    gc: async (level) => {
      throwIfAborted();
      await api.gc(sessionId, level);
      ctx.log(`🧹 GC ${level} angestoßen (202, läuft async)`);
    },
    epochBump: async (segments: StaticSegmentInput[]) => {
      throwIfAborted();
      const detail = await api.getSession(sessionId);
      const res = await api.replaceStaticSegments(sessionId, segments, detail.context_version);
      const d = res.diff;
      ctx.log(
        `⚡ Epoch-Bump → e${res.static_epoch} · +[${d.added_sources.join(", ")}] −[${d.removed_sources.join(", ")}]`,
      );
    },
    tryPageFault: async () => {
      throwIfAborted();
      // GET /refs liefert 200 nur für externalisierte Segmente mit vorhandenem Blob;
      // live wie evicted ⇒ 410. Also Kandidaten der Reihe nach probieren (älteste zuerst).
      for (const id of bigResultIds) {
        try {
          await api.expandRef(sessionId, id);
          await append([{ kind: "ref_expansion", role: "tool", content: loremTokens(300, `pf_${id}`) }]);
          ctx.log(`🔍 Page Fault auf ${id.slice(0, 8)}… → 200, ref_expansion angehängt`);
          return;
        } catch (e) {
          if (e instanceof ApiError && e.status === 410) continue; // live oder schon weg — nächster.
          throw e;
        }
      }
      ctx.log("🔍 Kein expandierbares Segment (410 überall — noch nichts externalisiert oder schon evicted)");
    },
  };

  return ctx;
}
