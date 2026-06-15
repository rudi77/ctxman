import { useMemo, useState } from "react";
import type { FrameView, SegmentView } from "../state/eventReducer";
import type { StaticSegmentInput } from "../api/types";
import { getRecorded } from "../state/contentStore";
import { kindColor } from "../lib/colors";
import { formatTokens, shortId } from "../lib/format";

interface Props {
  sessionId: string;
  segments: SegmentView[];
  frames: FrameView[];
  staticEpoch: number;
  /** Bekannte Static-Segmente (nur bei UI-erstellten Sessions) — sonst Aggregat-Block. */
  knownStatic?: StaticSegmentInput[];
  staticTokensEstimate: number;
  selectedId: string | null;
  onSelect: (segment: SegmentView | null) => void;
  pinnedIds: ReadonlySet<string>;
}

interface HoverPreview {
  title: string;
  meta: string;
  text: string | null;
  color: string;
}

/**
 * Die Memory-Map: Static-Region (Stack) und Working Set (Heap), das Working Set
 * sektioniert nach Frames (Root + Subagent-Frames, Spec §2.5). Blockbreite ∝ Tokens,
 * Farbe = Kind. Hover zeigt den Inhalt (lokal beim Append aufgezeichnet), Klick
 * öffnet den Inspektor.
 */
export function MemoryMap({
  sessionId,
  segments,
  frames,
  staticEpoch,
  knownStatic,
  staticTokensEstimate,
  selectedId,
  onSelect,
  pinnedIds,
}: Props) {
  const [showDead, setShowDead] = useState(true);
  const [hover, setHover] = useState<HoverPreview | null>(null);

  const working = useMemo(
    () => segments.filter((s) => s.region === "working").sort((a, b) => a.seq - b.seq),
    [segments],
  );

  const liveTokens = working
    .filter((s) => s.state === "live" || s.state === "externalized")
    .reduce((sum, s) => sum + s.tokens, 0);

  const maxTokens = Math.max(staticTokensEstimate, ...working.map((s) => s.tokens), 1);
  // Logarithmische Skala: 9k-Results sprengen die Map nicht, Kleinsegmente bleiben klickbar.
  const widthFor = (tokens: number) => {
    const min = 26;
    const max = 280;
    const f = Math.log(1 + tokens) / Math.log(1 + maxTokens);
    return Math.round(min + f * (max - min));
  };

  // Working Set in Sektionen: Root zuerst, dann Frames in Push-Reihenfolge (openedTurn, id).
  const sections = useMemo(() => {
    const byFrame = new Map<string | null, SegmentView[]>();
    for (const s of working) {
      const key = s.frameId;
      const list = byFrame.get(key);
      if (list) list.push(s);
      else byFrame.set(key, [s]);
    }
    const frameOrder = [...frames].sort(
      (a, b) => a.openedTurn - b.openedTurn || a.id.localeCompare(b.id),
    );
    const result: { frame: FrameView | null; segments: SegmentView[] }[] = [
      { frame: null, segments: byFrame.get(null) ?? [] },
    ];
    for (const f of frameOrder) {
      const segs = byFrame.get(f.id);
      if (segs && segs.length > 0) result.push({ frame: f, segments: segs });
    }
    return result;
  }, [working, frames]);

  const previewFor = (s: SegmentView): HoverPreview => {
    const recorded = getRecorded(sessionId, s.id);
    const metaParts = [
      `seq ${s.seq}`,
      `${formatTokens(s.tokens)} tok`,
      s.state,
      recorded?.role ? `role ${recorded.role}` : null,
      recorded?.toolCallId ? `unit ${recorded.toolCallId}` : null,
      s.expansions ? `${s.expansions}× expandiert` : null,
      shortId(s.id),
    ].filter(Boolean);
    return {
      title: s.kind,
      meta: metaParts.join(" · "),
      text: recorded?.content
        ? recorded.content
        : s.synthetic && s.kind === "compaction_summary"
          ? "(Summary-Inhalt liegt im Service — Quellsegmente wurden kompaktiert)"
          : null,
      color: kindColor(s.kind),
    };
  };

  const staticPreview = (entry: StaticSegmentInput): HoverPreview => ({
    title: entry.kind,
    meta: [entry.source ? `source ${entry.source}` : null, `~${formatTokens(Math.round(entry.content.length / 4))} tok`, "immutable (I1)"]
      .filter(Boolean)
      .join(" · "),
    text: entry.content,
    color: kindColor(entry.kind),
  });

  const renderBlock = (s: SegmentView) => {
    const dead = s.state === "evicted" || s.state === "compacted";
    const classes = [
      "memblock",
      s.state === "externalized" ? "externalized" : "",
      dead ? "dead" : "",
      s.compacting ? "compacting" : "",
      pinnedIds.has(s.id) ? "pinned" : "",
      selectedId === s.id ? "selected" : "",
    ]
      .filter(Boolean)
      .join(" ");
    return (
      <div
        key={s.id}
        className={classes}
        style={{ width: widthFor(s.tokens), background: kindColor(s.kind) }}
        onMouseEnter={() => setHover(previewFor(s))}
        onMouseLeave={() => setHover(null)}
        onClick={() => onSelect(selectedId === s.id ? null : s)}
        title={pinnedIds.has(s.id) ? "gepinnt" : undefined}
      >
        <span className="blk-label">{s.kind.replace(/_/g, " ")}</span>
      </div>
    );
  };

  const kindsInUse = useMemo(() => {
    const set = new Map<string, string>();
    if (knownStatic) for (const s of knownStatic) set.set(s.kind, kindColor(s.kind));
    for (const s of working) set.set(s.kind, kindColor(s.kind));
    return Array.from(set.entries());
  }, [working, knownStatic]);

  return (
    <div>
      {/* ---------- Static-Region, gruppiert nach source ---------- */}
      <div className="memmap-region-label">
        <span>
          Static Region · Epoche {staticEpoch} · {formatTokens(staticTokensEstimate)} tok · immutable (I1)
        </span>
        <span className="line" />
      </div>
      {knownStatic && knownStatic.length > 0 ? (
        Array.from(
          knownStatic.reduce((acc, entry) => {
            const key = entry.source ?? "(ohne source)";
            acc.set(key, [...(acc.get(key) ?? []), entry]);
            return acc;
          }, new Map<string, StaticSegmentInput[]>()),
        ).map(([source, entries]) => (
          <div key={source} className="memmap-section static">
            <div className="section-head">
              <span className="section-tag">static</span>
              <span className="section-name">{source}</span>
              <span className="section-meta">{entries.length} Segment(e)</span>
            </div>
            <div className="memmap">
              {entries.map((entry, i) => (
                <div
                  key={i}
                  className="memblock"
                  style={{
                    width: widthFor(Math.max(40, Math.round(entry.content.length / 4))),
                    background: kindColor(entry.kind),
                  }}
                  onMouseEnter={() => setHover(staticPreview(entry))}
                  onMouseLeave={() => setHover(null)}
                >
                  <span className="blk-label">{entry.kind.replace(/_/g, " ")}</span>
                </div>
              ))}
            </div>
          </div>
        ))
      ) : (
        <div className="memmap">
          <div
            className="memblock"
            style={{ width: widthFor(staticTokensEstimate), background: kindColor("system_prompt") }}
            onMouseEnter={() =>
              setHover({
                title: "Static-Region (Aggregat)",
                meta: `~${formatTokens(staticTokensEstimate)} tok · Epoche ${staticEpoch}`,
                text: "Die einzelnen Static-Segmente sind nur für Sessions bekannt, die diese UI selbst angelegt hat (die Static-Region erzeugt keine Events).",
                color: kindColor("system_prompt"),
              })
            }
            onMouseLeave={() => setHover(null)}
          >
            <span className="blk-label">static (aggregiert)</span>
          </div>
        </div>
      )}

      {/* ---------- Working Set, sektioniert nach Frames ---------- */}
      <div className="memmap-region-label" style={{ marginTop: 16 }}>
        <span>
          Working Set · {working.filter((s) => s.state === "live" || s.state === "externalized").length} render-eligible ·{" "}
          {formatTokens(liveTokens)} tok
        </span>
        <span className="line" />
        <button className="small" onClick={() => setShowDead((v) => !v)}>
          {showDead ? "evicted/compacted ausblenden" : "evicted/compacted einblenden"}
        </button>
      </div>

      {working.length === 0 && (
        <span className="muted">Noch keine Working-Segmente — auf der Spielwiese anhängen oder ein Szenario starten.</span>
      )}

      {sections.map(({ frame, segments: segs }) => {
        const visible = showDead ? segs : segs.filter((s) => s.state === "live" || s.state === "externalized");
        if (visible.length === 0 && frame === null && sections.length > 1) return null;
        if (visible.length === 0 && frame !== null) return null;
        const tokens = segs
          .filter((s) => s.state === "live" || s.state === "externalized")
          .reduce((sum, s) => sum + s.tokens, 0);
        return (
          <div key={frame?.id ?? "root"} className={`memmap-section ${frame === null ? "root" : "frame"} ${frame?.status === "popped" ? "popped" : ""}`}>
            <div className="section-head">
              <span className="section-tag">{frame === null ? "root" : "frame"}</span>
              <span className="section-name">{frame === null ? "Root-Context" : frame.label || "(ohne Label)"}</span>
              <span className="section-meta">
                {frame !== null && (frame.status === "open" ? "offen · " : "gepoppt · ")}
                {segs.length} Segmente · {formatTokens(tokens)} tok live
              </span>
            </div>
            <div className="memmap">{visible.map(renderBlock)}</div>
          </div>
        );
      })}

      {/* ---------- Hover-Preview: was steht drin? ---------- */}
      <div className={`memmap-preview ${hover ? "active" : ""}`}>
        {hover ? (
          <>
            <div className="preview-head">
              <span className="swatch" style={{ background: hover.color }} />
              <b>{hover.title}</b>
              <span className="preview-meta">{hover.meta}</span>
            </div>
            <div className="preview-text">
              {hover.text ?? "(Inhalt nicht aufgezeichnet — Segment stammt nicht aus dieser UI. Externalisierte Inhalte per Klick → Inspektor → expand_context_ref laden.)"}
            </div>
          </>
        ) : (
          <span className="muted">Über einen Block fahren, um den Inhalt zu sehen — Klick öffnet den Inspektor.</span>
        )}
      </div>

      <div className="memmap-legend">
        {kindsInUse.map(([kind, color]) => (
          <span key={kind} className="item">
            <span className="swatch" style={{ background: color }} />
            {kind}
          </span>
        ))}
        <span className="item">
          <span className="swatch memblock externalized" style={{ background: "#475569", border: "none" }} />
          externalisiert (Pointer)
        </span>
        <span className="item">
          <span className="swatch" style={{ background: "#334155", opacity: 0.4 }} />
          evicted / compacted
        </span>
        <span className="item">
          <span className="swatch pin-swatch" />
          gepinnt
        </span>
      </div>
    </div>
  );
}
