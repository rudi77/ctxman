import type { RenderResponse, StaticSegmentInput } from "../api/types";

/** Parameter, die der Benutzer pro Szenario einstellen kann (z. B. Turn-Anzahl). */
export interface ScenarioParam {
  key: string;
  label: string;
  min: number;
  max: number;
  default: number;
}

/**
 * Ein Simulations-Szenario: deklarative Beschreibung + run()-Skript gegen den
 * SimContext. Jedes Szenario erstellt sich eine eigene Session mit passender
 * Policy, damit der Effekt (Watermarks, GC) zuverlässig sichtbar wird.
 */
export interface Scenario {
  id: string;
  emoji: string;
  name: string;
  tagline: string;
  description: string;
  /** Worauf man auf dem Dashboard achten sollte. */
  watchFor: string[];
  policy: {
    budgetTokens: number;
    watermarks?: { soft: number; hard: number; emergency: number };
    externalizeThresholdTokens?: number;
  };
  staticSegments: StaticSegmentInput[];
  params: ScenarioParam[];
  run(ctx: SimContext, params: Record<string, number>): Promise<void>;
}

/** Vom Runner bereitgestellte Skript-API — alle Aktionen loggen sich selbst. */
export interface SimContext {
  sessionId: string;
  log(text: string): void;
  warn(text: string): void;
  phase(title: string): void;
  /** Abbruch-sicher und über den Speed-Faktor skaliert. */
  sleep(ms: number): Promise<void>;
  rand(min: number, max: number): number;
  chance(p: number): boolean;

  appendUser(tokens?: number): Promise<void>;
  appendAssistant(tokens?: number): Promise<void>;
  /** Vollständige Unit: tool_call + tool_result (I5). Liefert die tool_result-Segment-ID. */
  toolUnit(tokens: number, tool?: string): Promise<string>;
  decision(text: string): Promise<void>;
  task(text: string): Promise<void>;
  /** Render mit 413-Retry (Budget Exceeded → warten, GC arbeiten lassen, erneut). */
  render(turnAdvance?: boolean, scope?: "path" | "frame"): Promise<RenderResponse>;

  pushFrame(label: string): Promise<string>;
  popFrame(frameId: string, returnContent: string): Promise<void>;
  gc(level: "minor" | "major"): Promise<void>;
  /** Ersetzt die Static-Region (Epoch-Bump, holt If-Match selbst). */
  epochBump(segments: StaticSegmentInput[]): Promise<void>;
  /**
   * Versucht einen Page Fault auf ein früheres großes tool_result:
   * 200 ⇒ ref_expansion-Segment anhängen (wie das SDK es täte), 410 ⇒ loggen.
   */
  tryPageFault(): Promise<void>;
}

export type SimStatus = "idle" | "running" | "done" | "aborted" | "error";

export interface SimLine {
  at: string;
  kind: "info" | "warn" | "phase";
  text: string;
}

export interface SimState {
  status: SimStatus;
  scenarioId: string | null;
  scenarioName: string | null;
  sessionId: string | null;
  lines: SimLine[];
}
