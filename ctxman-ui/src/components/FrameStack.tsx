import type { FrameView } from "../state/eventReducer";
import { shortId } from "../lib/format";

interface Props {
  frames: FrameView[];
  onPop?: (frame: FrameView) => void;
}

/**
 * Frame-Stack (Spec §2.5): Subagent-Aufrufe als Stack-Frames, Root unten,
 * offene Frames oben (column-reverse). Strikte LIFO-Disziplin — poppbar ist
 * nur der oberste offene Frame.
 */
export function FrameStack({ frames, onPop }: Props) {
  const open = frames.filter((f) => f.status === "open");
  const popped = frames.filter((f) => f.status === "popped").slice(-4);
  const tip = open.length > 0 ? open[open.length - 1] : null;

  return (
    <div className="frame-stack">
      <div className="frame-item root">
        <span className="label">root</span>
        <span className="meta">Session-Hauptframe</span>
      </div>
      {[...popped, ...open]
        .sort((a, b) => a.openedTurn - b.openedTurn || a.id.localeCompare(b.id))
        .map((f) => (
          <div key={f.id} className={`frame-item ${f.status}`}>
            <span className="label">{f.label || "(ohne Label)"}</span>
            <span className="meta">
              {shortId(f.id)} · Turn {f.openedTurn} · {f.status === "open" ? "offen" : "gepoppt"}
            </span>
            {f.status === "open" && onPop && f.id === tip?.id && (
              <button className="small" style={{ marginLeft: "auto" }} onClick={() => onPop(f)}>
                ⤴ pop
              </button>
            )}
          </div>
        ))}
    </div>
  );
}
