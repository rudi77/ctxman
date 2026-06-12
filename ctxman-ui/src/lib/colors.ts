// Farbzuordnung pro Segment-Kind (Spec §2.3 Startvokabular); unbekannte Kinds
// bekommen eine stabile Hash-Farbe, damit die Memory-Map konsistent bleibt.

const KIND_COLORS: Record<string, string> = {
  system_prompt: "#8b5cf6",
  tool_def: "#6366f1",
  skill_index: "#818cf8",
  skill_content: "#84cc16",
  mcp_resource: "#65a30d",
  task: "#d946ef",
  decision: "#ec4899",
  user_msg: "#3b82f6",
  assistant_msg: "#10b981",
  tool_call: "#f59e0b",
  tool_result: "#fb923c",
  subagent_return: "#22d3ee",
  ref_expansion: "#a3e635",
  compaction_summary: "#14b8a6",
};

export function kindColor(kind: string): string {
  const known = KIND_COLORS[kind];
  if (known) return known;
  let hash = 0;
  for (let i = 0; i < kind.length; i++) hash = (hash * 31 + kind.charCodeAt(i)) | 0;
  return `hsl(${Math.abs(hash) % 360} 60% 55%)`;
}

export const WATERMARK_COLORS: Record<string, string> = {
  ok: "#22c55e",
  soft: "#eab308",
  hard: "#f97316",
  emergency: "#ef4444",
};

export const EVENT_COLORS: Record<string, string> = {
  segment_appended: "#3b82f6",
  segment_externalized: "#f59e0b",
  segment_evicted: "#ef4444",
  unit_evicted: "#dc2626",
  compaction_started: "#14b8a6",
  compaction_completed: "#0d9488",
  fact_promoted: "#ec4899",
  frame_pushed: "#22d3ee",
  frame_popped: "#06b6d4",
  ref_expanded: "#a3e635",
  static_epoch_bumped: "#8b5cf6",
  blob_swept: "#64748b",
  watermark_crossed: "#f97316",
  render_served: "#10b981",
};

export function eventColor(type: string): string {
  return EVENT_COLORS[type] ?? "#94a3b8";
}
