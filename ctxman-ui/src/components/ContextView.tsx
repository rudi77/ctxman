import { useState } from "react";
import { api } from "../api/client";
import type { RenderResponse } from "../api/types";

interface Props {
  sessionId: string;
}

interface AnthropicFragment {
  system?: unknown;
  tools?: { name?: string }[];
  messages?: { role?: string; content?: unknown }[];
}

/**
 * Live-Context-Ansicht: holt per POST /render (turn_advance=false, zählt also
 * keinen Turn) das echte Provider-Request-Fragment und zeigt es lesbar an —
 * das ist exakt das, was das Modell in diesem Moment sehen würde. Ground Truth
 * vom Service, unabhängig von der Event-Rekonstruktion der Memory-Map.
 */
export function ContextView({ sessionId }: Props) {
  const [provider, setProvider] = useState("anthropic");
  const [result, setResult] = useState<RenderResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [raw, setRaw] = useState(false);

  const load = async () => {
    setBusy(true);
    setError(null);
    try {
      setResult(await api.render(sessionId, provider, "path", false));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setResult(null);
    } finally {
      setBusy(false);
    }
  };

  // Content-Block-Arrays (Anthropic) lesbar machen: text-Blöcke als Text,
  // tool_use/tool_result kompakt mit Typ-Präfix; alles andere als JSON.
  const renderContent = (content: unknown): string => {
    if (typeof content === "string") return content;
    if (Array.isArray(content)) {
      return content
        .map((block) => {
          if (block && typeof block === "object") {
            const b = block as Record<string, unknown>;
            if (b.type === "text" && typeof b.text === "string") return b.text;
            if (b.type === "tool_use") return `[tool_use ${b.name ?? "?"} · id ${b.id ?? "?"}] ${JSON.stringify(b.input ?? {})}`;
            if (b.type === "tool_result") {
              const inner = typeof b.content === "string" ? b.content : renderContent(b.content);
              return `[tool_result · für ${b.tool_use_id ?? "?"}] ${inner}`;
            }
          }
          return JSON.stringify(block);
        })
        .join("\n──\n");
    }
    return JSON.stringify(content, null, 2);
  };

  const fragment = (result?.request_fragment ?? null) as AnthropicFragment | null;

  return (
    <div>
      <div className="row" style={{ marginBottom: 10 }}>
        <select value={provider} onChange={(e) => setProvider(e.target.value)}>
          <option value="anthropic">anthropic</option>
          <option value="openai">openai</option>
        </select>
        <button className="primary" disabled={busy} onClick={() => void load()}>
          {result ? "🔄 Neu rendern" : "▶ Context rendern"}
        </button>
        {result && (
          <>
            <span className="chip">
              tokens <b>{result.tokens_total}</b>
            </span>
            <span className={`badge ${result.watermark_state}`}>{result.watermark_state}</span>
            <label className="muted" style={{ display: "flex", gap: 5, alignItems: "center", marginLeft: "auto" }}>
              <input type="checkbox" checked={raw} onChange={(e) => setRaw(e.target.checked)} /> Roh-JSON
            </label>
          </>
        )}
      </div>
      <p className="muted" style={{ marginTop: 0 }}>
        Holt das echte Request-Fragment via <code>POST /render</code> mit <code>turn_advance=false</code> — zählt
        keinen Turn, erzeugt aber ein <code>render_served</code>-Event.
      </p>

      {error && <div className="error-banner">{error}</div>}

      {result && raw && <div className="json-view">{JSON.stringify(result.request_fragment, null, 2)}</div>}

      {fragment && !raw && (
        <div className="ctx-view">
          {fragment.system !== undefined && fragment.system !== null && (
            <div className="ctx-msg system">
              <div className="ctx-role">system</div>
              <div className="ctx-content">{renderContent(fragment.system)}</div>
            </div>
          )}
          {fragment.tools && fragment.tools.length > 0 && (
            <div className="ctx-msg tools">
              <div className="ctx-role">tools</div>
              <div className="ctx-content">
                <div className="row">
                  {fragment.tools.map((t, i) => (
                    <span key={i} className="chip">🔧 {t.name ?? `tool_${i}`}</span>
                  ))}
                </div>
              </div>
            </div>
          )}
          {(fragment.messages ?? []).map((m, i) => (
            <div key={i} className={`ctx-msg ${m.role ?? ""}`}>
              <div className="ctx-role">{m.role ?? "?"}</div>
              <div className="ctx-content">{renderContent(m.content)}</div>
            </div>
          ))}
          {(fragment.messages ?? []).length === 0 && (
            <span className="muted">Keine Messages — das Working Set ist leer (oder alles evicted).</span>
          )}
        </div>
      )}
    </div>
  );
}
