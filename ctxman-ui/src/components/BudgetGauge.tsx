import { WATERMARK_COLORS } from "../lib/colors";
import { formatPercent, formatTokens } from "../lib/format";

interface Props {
  tokensUsed: number;
  budget: number;
  watermarks: { soft: number; hard: number; emergency: number };
}

/**
 * Budget-Balken mit den drei Watermark-Zonen (Spec §3.1): grün bis soft,
 * gelb bis hard, orange bis emergency, rot bis 100 %. Der Füllstand zeigt
 * tokens_used relativ zum Budget B.
 */
export function BudgetGauge({ tokensUsed, budget, watermarks }: Props) {
  const frac = budget > 0 ? Math.min(tokensUsed / budget, 1.08) : 0;
  const W = 1000;
  const H = 46;
  const barY = 14;
  const barH = 18;

  const zones = [
    { from: 0, to: watermarks.soft, color: WATERMARK_COLORS.ok },
    { from: watermarks.soft, to: watermarks.hard, color: WATERMARK_COLORS.soft },
    { from: watermarks.hard, to: watermarks.emergency, color: WATERMARK_COLORS.hard },
    { from: watermarks.emergency, to: 1, color: WATERMARK_COLORS.emergency },
  ];

  const x = (f: number) => f * W;

  return (
    <div className="gauge-wrap">
      <svg viewBox={`0 0 ${W} ${H + 18}`} style={{ width: "100%", display: "block" }}>
        {/* Zonen-Hintergrund (gedimmt) */}
        {zones.map((z) => (
          <rect
            key={z.from}
            x={x(z.from)}
            y={barY}
            width={x(z.to) - x(z.from)}
            height={barH}
            fill={z.color}
            opacity={0.16}
          />
        ))}
        {/* Füllstand: pro Zone in voller Farbe bis zum aktuellen Stand */}
        {zones.map((z) => {
          const fillTo = Math.min(frac, z.to);
          if (fillTo <= z.from) return null;
          return (
            <rect
              key={`f${z.from}`}
              x={x(z.from)}
              y={barY}
              width={x(fillTo) - x(z.from)}
              height={barH}
              fill={z.color}
              opacity={0.9}
            >
              <animate attributeName="opacity" values="0.9;0.75;0.9" dur="2s" repeatCount="indefinite" />
            </rect>
          );
        })}
        {/* Watermark-Linien + Labels */}
        {(
          [
            ["soft", watermarks.soft],
            ["hard", watermarks.hard],
            ["emergency", watermarks.emergency],
          ] as const
        ).map(([name, f]) => (
          <g key={name}>
            <line x1={x(f)} y1={barY - 6} x2={x(f)} y2={barY + barH + 6} stroke="#94a3b8" strokeWidth={1.5} strokeDasharray="3 3" />
            <text x={x(f)} y={barY - 9} fill="#94a3b8" fontSize={11} textAnchor="middle" fontFamily="monospace">
              {name} {Math.round(f * 100)}%
            </text>
          </g>
        ))}
        {/* Aktueller Stand: Nadel */}
        <g>
          <line x1={x(Math.min(frac, 1.06))} y1={barY - 4} x2={x(Math.min(frac, 1.06))} y2={barY + barH + 4} stroke="#e2e8f0" strokeWidth={2.5} />
          <text
            x={Math.min(Math.max(x(frac), 30), W - 60)}
            y={barY + barH + 16}
            fill="#e2e8f0"
            fontSize={12}
            fontWeight={700}
            textAnchor="middle"
            fontFamily="monospace"
          >
            {formatPercent(budget > 0 ? tokensUsed / budget : 0)}
          </text>
        </g>
      </svg>
      <div className="gauge-labels">
        <span>{formatTokens(tokensUsed)} verwendet</span>
        <span>Budget {formatTokens(budget)}</span>
      </div>
    </div>
  );
}
