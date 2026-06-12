// Wire-Typen der ctxman-API (Spec §4.3, snake_case wie vom Service serialisiert).

export type WatermarkState = "ok" | "soft" | "hard" | "emergency";
export type SegmentRegion = "static" | "working";
export type SegmentLifecycle = "live" | "externalized" | "compacted" | "evicted";

export interface SessionDetail {
  session_id: string;
  agent_template_id: string | null;
  status: "active" | "archived";
  context_version: number;
  static_epoch: number;
  current_turn: number;
  tokens_used: number;
  watermark_state: WatermarkState;
  created_at: string;
  updated_at: string;
}

export interface CreateSessionResponse {
  session_id: string;
  context_version: number;
}

export interface StaticSegmentInput {
  kind: string;
  role?: string | null;
  content: string;
  source?: string | null;
}

export interface PolicyOverrides {
  budget_tokens?: number;
  watermarks?: { soft?: number; hard?: number; emergency?: number };
  externalize_threshold_tokens?: number;
  tokenizer?: string;
  on_tool_removed?: "keep" | "externalize" | "evict";
}

export interface CreateSessionRequest {
  agent_template_id?: string | null;
  policy_overrides?: PolicyOverrides;
  static_segments?: StaticSegmentInput[];
}

export interface AppendSegmentInput {
  kind: string;
  role?: string | null;
  content?: string | null;
  tool_call_id?: string | null;
  pinned?: boolean;
  source?: string | null;
}

export interface AppendSegmentsResponse {
  segment_ids: string[];
  context_version: number;
}

export interface RenderResponse {
  request_fragment: unknown;
  cache_breakpoints: unknown[];
  builtin_tools: unknown[];
  context_version: number;
  tokens_total: number;
  watermark_state: WatermarkState;
}

export interface ReplaceStaticSegmentsResponse {
  static_epoch: number;
  context_version: number;
  diff: {
    added_tools: string[];
    removed_tools: string[];
    added_sources: string[];
    removed_sources: string[];
  };
}

export interface PushFrameResponse {
  frame_id: string;
}

export interface PopFrameResponse {
  return_segment_id: string;
  context_version: number;
}

export interface GcResponse {
  job_id: string;
}

export interface ExpandRefResponse {
  content: string;
  content_type: string;
}

export interface EventItem {
  id: string;
  seq: number;
  type: string;
  payload: Record<string, unknown>;
  created_at: string;
}

export interface EventsResponse {
  events: EventItem[];
}

export interface Healthz {
  status: string;
  auth_mode: string;
}

/** Fehler mit HTTP-Status + geparstem Body (sofern JSON), für die Response-Konsole. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: unknown,
    message?: string,
  ) {
    super(message ?? `HTTP ${status}`);
  }
}
