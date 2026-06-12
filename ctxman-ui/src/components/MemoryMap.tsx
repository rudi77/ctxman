import { useMemo, useState } from "react";
import type { SegmentView } from "../state/eventReducer";
import type { StaticSegmentInput } from "../api/types";
import { kindColor } from "../lib/colors";
import { formatTokens, shortId } from "../lib/format";

interface Props {
  segments: SegmentView[];
  staticEpoch: number;
  /** Bekannte Static-Segmente (nur bei Spielwiesen-Sessions) — sonst Aggregat-Block. */
  knownStatic?: StaticSegmentInput[];
  staticTokensEstimate: number;
  selectedId: string | null;
  onSelect: (segment: SegmentView | null) => void;
  pinnedIds: ReadonlySet<string>;
}

/**
 * Die Memory-Map: das Speicherverwaltungs-Bild der Spec (§1) wörtlich genommen.
 * Static-Region oben (Stack-Analogie), Working Set darunter (Heap). Jedes Segment
 * ist ein Block, Breite ∝ Tokens, Farbe = Kind, Schraffur = externalisiert,
 * ausgegraut/verkleinert = evicted/compacted (Soft-Delete bleibt sichtbar — Audit, I3).
 */
export function MemoryMap({
  segments,
  staticEpoch,
  knownStatic,
  staticTokensEstimate,
  selectedId,
  onSelect,
  pinnedIds,
}: Props) {
  const [showDead, setShowDead] = useState(true);

  const working = useMemo(
    () => segments.filter((s) => s.region === "working").sort((a, b) => a.seq - b.seq),
    [segments],
  );

  const liveTokens = working
    .filter((s) => s.state === "live" || s.state === "externalized")
    .reduce((sum, s) => sum + s.tokens, 0);

  const maxTokens = Math.max(staticTokensEstimate, ...working.map((s) => s.tokens), 1);
  // Blockbreite: logarithmisch skaliert, damit ein 9k-Tool-Result die Map nicht sprengt,
  // kleine Segmente aber klickbar bleiben.
  const widthFor = (tokens: number) => {
    const min = 26;
    const max = 280;
    const f = Math.log(1 + tokens) / Math.log(1 + maxTokens);
    return Math.round(min + f * (max - min));
  };

  const visible = showDead ? working : working.filter((s) => s.state === "live" || s.state === "externalized");

  const renderBlock = (s: SegmentView) => {
    const dead = s.state === "evicted" || s.state === "compacted";
    const classes = [
      "memblock",
      s.state === "externalized" ? "externalized" : "",
      dead ? "dead" : "",
      s.compacting ? "compacting" : "",
      selectedId === s.id ? "selected" : "",
    ]
      .filter(Boolean)
      .join(" ");
    return (
      <div
        key={s.id}
        className={classes}
        style={{ width: widthFor(s.tokens), background: kindColor(s.kind) }}
        title={`${s.kind} · seq ${s.seq} · ${formatTokens(s.tokens)} tok · ${s.state}${s.expansions ? ` · ${s.expansions}× expandiert` : ""} · ${shortId(s.id)}`}
        onClick={() => onSelect(selectedId === s.id ? null : s)}
      >
        {pinnedIds.has(s.id) && <span className="pin">📌</span>}
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
      <div className="memmap-region-label">
        <span>Static Region · Epoche {staticEpoch} · {formatTokens(staticTokensEstimate)} tok · immutable (I1)</span>
        <span className="line" />
      </div>
      <div className="memmap">
        {knownStatic && knownStatic.length > 0 ? (
          knownStatic.map((s, i) => (
            <div
              key={i}
              className="memblock"
              style={{ width: widthFor(Math.max(40, Math.round(s.content.length / 4))), background: kindColor(s.kind) }}
              title={`${s.kind}${s.source ? ` · source ${s.source}` : ""} · ~${formatTokens(Math.round(s.content.length / 4))} tok`}
            >
              <span className="blk-label">{s.source ?? s.kind.replace(/_/g, " ")}</span>
            </div>
          ))
        ) : (
          <div
            className="memblock"
            style={{ width: widthFor(staticTokensEstimate), background: kindColor("system_prompt") }}
            title={`Static-Region (Aggregat) · ~${formatTokens(staticTokensEstimate)} tok — Segmente nur für Spielwiesen-Sessions bekannt`}
          >
            <span className="blk-label">static (aggregiert)</span>
          </div>
        )}
      </div>

      <div className="memmap-region-label" style={{ marginTop: 16 }}>
        <span>Working Set · {working.filter((s) => s.state === "live" || s.state === "externalized").length} render-eligible · {formatTokens(liveTokens)} tok</span>
        <span className="line" />
        <button className="small" onClick={() => setShowDead((v) => !v)}>
          {showDead ? "evicted/compacted ausblenden" : "evicted/compacted einblenden"}
        </button>
      </div>
      <div className="memmap">
        {visible.length === 0 && <span className="muted">Noch keine Working-Segmente — auf der Spielwiese anhängen.</span>}
        {visible.map(renderBlock)}
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
        <span className="item">📌 gepinnt</span>
      </div>
    </div>
  );
}
