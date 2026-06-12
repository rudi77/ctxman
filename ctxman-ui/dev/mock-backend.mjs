// Mock-Backend für den lokalen ctxman-Betrieb ohne echte LLM-Credentials:
//  - POST /v1/messages   → Anthropic-Messages-API-Mock (Compaction + Fact-Extraction)
//  - POST /memory/ingest → Promotion-Sink-Mock (nimmt fact_promoted-Payloads an)
//
// Start:  npm run mock   (Port 5999, überschreibbar via MOCK_PORT)
// Die ctxman-API darauf zeigen lassen:
//   $env:Compaction__Anthropic__BaseUrl = "http://localhost:5999"
//   $env:Compaction__Anthropic__ApiKey  = "mock-key"
// Sessions mit promotion.sink.url = http://localhost:5999/memory/ingest anlegen
// (Simulations-Labor und Spielwiese tun das per Default).

import http from "node:http";

const PORT = Number(process.env.MOCK_PORT ?? 5999);

const server = http.createServer((req, res) => {
  let body = "";
  req.on("data", (chunk) => (body += chunk));
  req.on("end", () => {
    if (req.method === "POST" && req.url === "/v1/messages") {
      let items = 0;
      let model = "?";
      try {
        const parsed = JSON.parse(body);
        model = parsed.model ?? "?";
        items = String(parsed.messages?.[0]?.content ?? "").split("\n\n---\n\n").length;
      } catch {
        // defekter Body → trotzdem antworten, der Mock soll nie blockieren
      }
      const text =
        `[mock-compaction] Konsolidierte Zusammenfassung von ${items} Segmenten ` +
        `(Modell ${model}): zentrale Fakten, Entscheidungen und offene Punkte wurden erhalten.`;
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ content: [{ type: "text", text }] }));
      console.log(`[mock] compaction/extraction: ${items} Segmente (${model})`);
      return;
    }

    if (req.method === "POST" && req.url === "/memory/ingest") {
      res.writeHead(202, { "content-type": "application/json" });
      res.end(JSON.stringify({ ok: true }));
      let kind = "?";
      try {
        kind = JSON.parse(body).kind ?? "?";
      } catch {
        // ignorieren
      }
      console.log(`[mock] promotion fact empfangen (kind=${kind})`);
      return;
    }

    res.writeHead(404, { "content-type": "application/json" });
    res.end(JSON.stringify({ error: `unknown route ${req.method} ${req.url}` }));
  });
});

server.listen(PORT, () => console.log(`[mock] ctxman Mock-Backend läuft auf http://localhost:${PORT}`));
