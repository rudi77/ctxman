// Farbzuordnung pro Segment-Kind (Spec §2.3 Startvokabular); unbekannte Kinds
// bekommen eine stabile Hash-Farbe, damit die Memory-Map konsistent bleibt.

// Kohärente, leicht entsättigte Palette: nach Funktion gruppiert (Static/Tools =
// Indigo-Familie, Konversation = Blau/Teal, Tool-I/O = Bernstein, Meta = Pink/Teal),
// damit die Memory-Map ruhig wirkt statt wie ein Regenbogen.
const KIND_COLORS: Record<string, string> = {
  system_prompt: "#7c74e0",
  tool_def: "#6366cf",
  skill_index: "#8b8fe6",
  skill_content: "#4f9d6b",
  mcp_resource: "#3f8a5c",
  task: "#b266c4",
  decision: "#c76896",
  user_msg: "#4f86d6",
  assistant_msg: "#3aa88a",
  tool_call: "#d39341",
  tool_result: "#c9793f",
  subagent_return: "#4aa3bd",
  ref_expansion: "#7fa84c",
  compaction_summary: "#3a9d96",
};

export function kindColor(kind: string): string {
  const known = KIND_COLORS[kind];
  if (known) return known;
  // Unbekannte Kinds bekommen eine stabile, gedämpfte Hash-Farbe im selben Register.
  let hash = 0;
  for (let i = 0; i < kind.length; i++) hash = (hash * 31 + kind.charCodeAt(i)) | 0;
  return `hsl(${Math.abs(hash) % 360} 42% 56%)`;
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
