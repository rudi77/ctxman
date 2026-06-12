import { useMemo } from "react";
import type { TimelinePoint } from "../state/eventReducer";
import { WATERMARK_COLORS } from "../lib/colors";
import { formatTokens } from "../lib/format";

interface Props {
  points: TimelinePoint[];
  budget: number;
  watermarks: { soft: number; hard: number; emergency: number };
}

/**
 * tokens_total über die render_served-Events (Spec §6) — sichtbar wird, wie GC
 * den Context wieder unter die Watermarks drückt (Sägezahn-Muster).
 * Epoch-Wechsel werden als vertikale Markierungen eingezeichnet.
 */
export function TokenTimeline({ points, budget, watermarks }: Props) {
  const W = 1000;
  const H = 230;
  const padL = 56;
  const padR = 12;
  const padT = 14;
  const padB = 24;

  const yMax = Math.max(budget, ...points.map((p) => p.tokensTotal)) * 1.05 || 1;
  const x = (i: number) =>
    points.length <= 1 ? padL : padL + (i / (points.length - 1)) * (W - padL - padR);
  const y = (tokens: number) => padT + (1 - tokens / yMax) * (H - padT - padB);

  const path = useMemo(() => {
    if (points.length === 0) return "";
    return points.map((p, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(p.tokensTotal).toFixed(1)}`).join(" ");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [points, yMax]);

  const area = points.length > 1 ? `${path} L${x(points.length - 1).toFixed(1)},${y(0)} L${x(0).toFixed(1)},${y(0)} Z` : "";

  const epochMarks = useMemo(() => {
    const marks: { i: number; epoch: number }[] = [];
    for (let i = 1; i < points.length; i++) {
      if (points[i].staticEpoch !== points[i - 1].staticEpoch) marks.push({ i, epoch: points[i].staticEpoch });
    }
    return marks;
  }, [points]);

  if (points.length === 0) {
    return <span className="muted">Noch keine Renders — Timeline erscheint mit dem ersten render_served-Event.</span>;
  }

  const last = points[points.length - 1];

  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: "100%", display: "block" }}>
      <defs>
        <linearGradient id="tl-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#6366f1" stopOpacity="0.45" />
          <stop offset="100%" stopColor="#6366f1" stopOpacity="0.02" />
        </linearGradient>
      </defs>

      {/* Watermark-Linien */}
      {(
        [
          ["soft", watermarks.soft, WATERMARK_COLORS.soft],
          ["hard", watermarks.hard, WATERMARK_COLORS.hard],
          ["emergency", watermarks.emergency, WATERMARK_COLORS.emergency],
          ["budget", 1, "#e2e8f0"],
        ] as const
      ).map(([name, f, color]) => (
        <g key={name}>
          <line x1={padL} y1={y(budget * f)} x2={W - padR} y2={y(budget * f)} stroke={color} strokeWidth={1} strokeDasharray="4 4" opacity={0.55} />
          <text x={padL - 4} y={y(budget * f) + 3.5} fill={color} fontSize={10} textAnchor="end" fontFamily="monospace" opacity={0.8}>
            {name === "budget" ? formatTokens(budget) : name}
          </text>
        </g>
      ))}

      {/* Epoch-Bump-Markierungen */}
      {epochMarks.map((m) => (
        <g key={m.i}>
          <line x1={x(m.i)} y1={padT} x2={x(m.i)} y2={H - padB} stroke="#8b5cf6" strokeWidth={1.5} strokeDasharray="2 3" />
          <text x={x(m.i) + 3} y={padT + 9} fill="#8b5cf6" fontSize={10} fontFamily="monospace">e{m.epoch}</text>
        </g>
      ))}

      {/* Fläche + Linie */}
      {area && <path d={area} fill="url(#tl-fill)" />}
      <path d={path} fill="none" stroke="#818cf8" strokeWidth={2.2} strokeLinejoin="round" />

      {/* Letzter Punkt */}
      <circle cx={x(points.length - 1)} cy={y(last.tokensTotal)} r={4.5} fill="#22d3ee">
        <animate attributeName="r" values="4.5;6.5;4.5" dur="1.6s" repeatCount="indefinite" />
      </circle>
      <text x={Math.min(x(points.length - 1) + 8, W - 70)} y={y(last.tokensTotal) - 8} fill="#22d3ee" fontSize={12} fontWeight={700} fontFamily="monospace">
        {formatTokens(last.tokensTotal)}
      </text>

      <text x={W - padR} y={H - 8} fill="#64748b" fontSize={10} textAnchor="end" fontFamily="monospace">
        {points.length} Renders
      </text>
    </svg>
  );
}
