export function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(2)}M`;
  if (n >= 10_000) return `${(n / 1000).toFixed(1)}k`;
  if (n >= 1000) return `${(n / 1000).toFixed(2)}k`;
  return String(n);
}

export function formatPercent(fraction: number): string {
  return `${(fraction * 100).toFixed(1)}%`;
}

export function shortId(id: string): string {
  return id.length > 10 ? `${id.slice(0, 6)}…${id.slice(-4)}` : id;
}

export function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString("de-DE");
  } catch {
    return iso;
  }
}

/** Erzeugt Fülltext mit grob der gewünschten Token-Zahl (Heuristik ~4 Zeichen/Token). */
export function loremTokens(tokens: number, seed: string): string {
  const words = [
    "context", "segment", "render", "budget", "eviction", "frame", "policy",
    "tenant", "epoch", "cache", "prefix", "token", "watermark", "compaction",
    "promotion", "pointer", "blob", "summary", "unit", "turn", "session",
  ];
  let out = `[${seed}] `;
  let i = 0;
  while (out.length < tokens * 4) {
    out += words[(i * 7 + seed.length) % words.length] + " ";
    i++;
  }
  return out.trimEnd();
}
