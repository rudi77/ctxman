import { useState } from "react";
import { api } from "../api/client";
import { ApiError } from "../api/types";
import type { SegmentView } from "../state/eventReducer";
import { getRecorded } from "../state/contentStore";
import { kindColor } from "../lib/colors";
import { formatTokens } from "../lib/format";

interface Props {
  sessionId: string;
  segment: SegmentView | null;
  pinnedIds: ReadonlySet<string>;
  onPinChange: (segmentId: string, pinned: boolean) => void;
  onAction: () => void;
}

/**
 * Detail-Panel zum in der Memory-Map ausgewählten Segment: Pin/Unpin (Spec §4.3)
 * und Page-Fault per GET /refs/{segment_id} (Spec §3.4 — setzt last_referenced_turn).
 */
export function SegmentInspector({ sessionId, segment, pinnedIds, onPinChange, onAction }: Props) {
  const [expanded, setExpanded] = useState<{ id: string; content: string; gone?: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!segment) {
    return <span className="muted">Segment in der Memory-Map anklicken, um Details und Aktionen zu sehen.</span>;
  }

  const isPinned = pinnedIds.has(segment.id);
  const recorded = getRecorded(sessionId, segment.id);

  const doPin = async () => {
    setBusy(true);
    setError(null);
    try {
      if (isPinned) await api.unpin(sessionId, segment.id);
      else await api.pin(sessionId, segment.id);
      onPinChange(segment.id, !isPinned);
      onAction();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const doExpand = async () => {
    setBusy(true);
    setError(null);
    try {
      const res = await api.expandRef(sessionId, segment.id);
      setExpanded({ id: segment.id, content: res.content });
      onAction();
    } catch (e) {
      if (e instanceof ApiError && e.status === 410) {
        // Spec §7.1: 410 Gone liefert summary/origin als bestmögliche Restinformation.
        setExpanded({ id: segment.id, content: JSON.stringify(e.body, null, 2), gone: true });
      } else {
        setError(e instanceof Error ? e.message : String(e));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="col">
      <div className="row">
        <span className="swatch" style={{ width: 14, height: 14, borderRadius: 4, background: kindColor(segment.kind), display: "inline-block" }} />
        <b>{segment.kind}</b>
        <span className="badge ok" style={{ textTransform: "none" }}>{segment.state}</span>
        {isPinned && <span title="gepinnt">📌</span>}
      </div>
      <div className="muted" style={{ fontFamily: "var(--mono)" }}>
        id {segment.id}
        <br />
        seq {segment.seq} · {formatTokens(segment.tokens)} Tokens · Region {segment.region}
        {segment.frameId && <> · Frame {segment.frameId.slice(0, 8)}…</>}
        {recorded?.role && <> · role {recorded.role}</>}
        {recorded?.toolCallId && <> · unit {recorded.toolCallId}</>}
        {segment.expansions > 0 && <> · {segment.expansions}× expandiert</>}
        {segment.synthetic && <> · (aus Events abgeleitet)</>}
      </div>
      {recorded?.content ? (
        <div>
          <div className="muted" style={{ marginBottom: 4 }}>Inhalt (beim Append lokal aufgezeichnet):</div>
          <div className="json-view" style={{ maxHeight: 220 }}>{recorded.content}</div>
        </div>
      ) : (
        <div className="muted">
          Kein lokal aufgezeichneter Inhalt — Segment stammt nicht aus dieser UI.
          {segment.state === "externalized" && " Inhalt per expand_context_ref aus dem Blob Store laden ↓"}
        </div>
      )}
      <div className="row">
        <button onClick={doPin} disabled={busy || segment.region === "static"}>
          {isPinned ? "📌 Unpin" : "📌 Pin"}
        </button>
        <button
          onClick={doExpand}
          disabled={busy || segment.state !== "externalized"}
          title={
            segment.state === "externalized"
              ? "Page Fault: Inhalt aus dem Blob Store zurückholen (Spec §3.4)"
              : "Nur externalisierte Segmente sind expandierbar (Spec §3.4) — live-Inhalte stehen ohnehin im Context"
          }
        >
          🔍 expand_context_ref
        </button>
      </div>
      {error && <div className="error-banner">{error}</div>}
      {expanded?.id === segment.id && (
        <div>
          {expanded.gone && <div className="muted">410 Gone — Inhalt nicht mehr verfügbar, Restinformation:</div>}
          <div className="json-view">{expanded.content}</div>
        </div>
      )}
    </div>
  );
}
