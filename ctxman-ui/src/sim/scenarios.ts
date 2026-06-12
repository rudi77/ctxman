import type { StaticSegmentInput } from "../api/types";
import type { Scenario } from "./types";

// Szenario-Bibliothek: jedes Szenario erzählt einen typischen Agent-Lebenslauf
// und ist so parametrisiert, dass der jeweilige ctxman-Mechanismus (Watermarks,
// Frames, Externalisierung, Epoch-Bumps, Emergency-Pfad) sicher sichtbar wird.

function staticBase(extraSources: string[] = []): StaticSegmentInput[] {
  const segments: StaticSegmentInput[] = [
    {
      kind: "system_prompt",
      role: "system",
      content: "Du bist ein autonomer Agent. Arbeite die Aufgabe ab, nutze Tools, dokumentiere Entscheidungen.",
      source: "core",
    },
    { kind: "tool_def", role: "system", source: "core", content: JSON.stringify({ name: "search_web", description: "Websuche" }) },
    { kind: "tool_def", role: "system", source: "core", content: JSON.stringify({ name: "read_file", description: "Datei lesen" }) },
  ];
  for (const src of extraSources) {
    segments.push({
      kind: "tool_def",
      role: "system",
      source: src,
      content: JSON.stringify({ name: `${src.replace(/[^a-z0-9]/gi, "_")}_query`, description: `Tool aus ${src}` }),
    });
  }
  return segments;
}

export const SCENARIOS: Scenario[] = [
  {
    id: "quick-qa",
    emoji: "⚡",
    name: "Kurzer Q&A-Task",
    tagline: "Baseline ohne GC-Stress",
    description:
      "Ein kurzer Frage-Antwort-Lauf: wenige Turns, kleine Nachrichten, eine kleine Tool-Unit. " +
      "Der Context bleibt weit unter der Soft-Watermark — so sieht eine Session aus, in der ctxman nichts tun muss.",
    watchFor: ["Watermark bleibt durchgehend „ok\"", "Token-Timeline flach", "kein einziges GC-Event im Feed"],
    policy: { budgetTokens: 30_000 },
    staticSegments: staticBase(),
    params: [{ key: "turns", label: "Turns", min: 2, max: 8, default: 4 }],
    async run(ctx, p) {
      await ctx.task("Task: Beantworte die Frage des Users zu ctxman-Watermarks.");
      for (let t = 1; t <= p.turns; t++) {
        ctx.phase(`Turn ${t}/${p.turns}`);
        await ctx.appendUser(ctx.rand(30, 80));
        await ctx.render();
        if (ctx.chance(0.5)) {
          await ctx.toolUnit(ctx.rand(100, 350));
          await ctx.render();
        }
        await ctx.appendAssistant(ctx.rand(60, 140));
        await ctx.sleep(500);
      }
    },
  },

  {
    id: "marathon",
    emoji: "🏃",
    name: "Marathon-Langläufer",
    tagline: "Sehr lange Aufgabe, GC unter Dauerlast",
    description:
      "Ein langer Agent-Lauf mit stetigem Tool-Einsatz. Die Soft-Watermark wird mehrfach gerissen " +
      "(Minor GC: Externalisierung + TTL-Eviction), bei Hard startet die Major Collection. Gepinnte " +
      "decisions/tasks überleben alles. Hinweis: Compaction-Summaries erscheinen nur, wenn ein " +
      "Compaction-LLM-Backend konfiguriert ist (sonst wirkt nur der Minor-Pfad).",
    watchFor: [
      "Sägezahn in der Token-Timeline",
      "watermark_crossed-Events (soft/hard)",
      "schraffierte (externalisierte) und ausgegraute (evicted) Blöcke",
      "📌-Segmente bleiben über den ganzen Lauf stehen",
    ],
    policy: { budgetTokens: 25_000 },
    staticSegments: staticBase(["mcp:github"]),
    params: [{ key: "turns", label: "Turns", min: 10, max: 80, default: 40 }],
    async run(ctx, p) {
      await ctx.task("Task: Migriere das Monorepo auf .NET 9 und dokumentiere jede Architektur-Entscheidung.");
      for (let t = 1; t <= p.turns; t++) {
        ctx.phase(`Turn ${t}/${p.turns}`);
        await ctx.appendUser(ctx.rand(40, 140));
        await ctx.render();
        const units = ctx.chance(0.35) ? 2 : 1;
        for (let u = 0; u < units; u++) {
          const big = ctx.chance(0.5);
          await ctx.toolUnit(big ? ctx.rand(2200, 6000) : ctx.rand(150, 600), ctx.chance(0.3) ? "read_file" : "search_web");
          await ctx.render();
        }
        await ctx.appendAssistant(ctx.rand(80, 200));
        if (t % 5 === 0) await ctx.decision(`Entscheidung aus Turn ${t}: Modul ${t / 5} bleibt auf netstandard2.0.`);
        if (t % 8 === 0) await ctx.tryPageFault();
        await ctx.sleep(400);
      }
    },
  },

  {
    id: "subagents",
    emoji: "🪆",
    name: "Agent mit Subagents",
    tagline: "Frames: push, verschachtelt arbeiten, pop mit Return",
    description:
      "Der Hauptagent delegiert Teilaufgaben an Subagents — jeder läuft in einem eigenen Frame (Spec §2.5). " +
      "Ein Subagent startet selbst einen Sub-Subagent (verschachtelt, strikt LIFO). Beim Pop werden die " +
      "Frame-Segmente evicted und nur das Return-Segment bleibt im Parent — der Context des Hauptagenten " +
      "bleibt schlank, egal wie viel die Subagents gewühlt haben.",
    watchFor: [
      "Frame-Stack wächst und schrumpft (LIFO)",
      "frame_pushed/frame_popped im Event-Feed",
      "subagent_return-Blöcke in der Memory-Map",
      "Frame-Segmente verschwinden beim Pop (evicted)",
    ],
    policy: { budgetTokens: 30_000 },
    staticSegments: staticBase(),
    params: [{ key: "subagents", label: "Subagents", min: 1, max: 5, default: 3 }],
    async run(ctx, p) {
      await ctx.task("Task: Erstelle einen Marktreport — delegiere Recherche an Subagents.");
      await ctx.appendUser(120);
      await ctx.render();

      for (let s = 1; s <= p.subagents; s++) {
        ctx.phase(`Subagent ${s}/${p.subagents}: research_${s}`);
        const frame = await ctx.pushFrame(`research_${s}`);

        for (let t = 0; t < ctx.rand(2, 4); t++) {
          await ctx.toolUnit(ctx.rand(800, 3000));
          await ctx.render(true, "frame"); // isolierte Subagent-Sicht (scope=frame)
          await ctx.appendAssistant(ctx.rand(60, 120));
        }

        if (s === 1) {
          // Verschachtelung: der erste Subagent delegiert selbst weiter (LIFO: Kind zuerst poppen).
          ctx.phase("…startet einen Sub-Subagent (verschachtelt)");
          const nested = await ctx.pushFrame(`research_${s}_detail`);
          await ctx.toolUnit(ctx.rand(1000, 2500));
          await ctx.render(true, "frame");
          await ctx.popFrame(nested, `Detail-Findings aus research_${s}_detail: 2 Quellen verifiziert.`);
        }

        await ctx.popFrame(frame, `Subagent research_${s}: Zusammenfassung mit ${ctx.rand(2, 5)} Findings.`);
        await ctx.render();
        await ctx.sleep(600);
      }

      ctx.phase("Synthese im Hauptagent");
      await ctx.appendAssistant(250);
      await ctx.decision("Entscheidung: Report-Struktur folgt den Subagent-Returns, Quelle 3 wird verworfen.");
      await ctx.render();
    },
  },

  {
    id: "tool-storm",
    emoji: "🌪",
    name: "Tool-Sturm & Page Faults",
    tagline: "Externalisierung und expand_context_ref im Loop",
    description:
      "Ein Agent feuert große Tool-Results (über dem Externalize-Threshold) in schneller Folge. Der Minor GC " +
      "lagert sie in den Blob Store aus (Pointer statt Werte); später holt der Agent einzelne Inhalte per " +
      "Page Fault zurück (GET /refs → ref_expansion). Niedriger Threshold (1k), damit es schnell sichtbar wird.",
    watchFor: [
      "segment_externalized-Events + schraffierte Blöcke",
      "Page-Fault-Zähler steigt",
      "ref_expansion-Segmente (sehr kurze TTL — werden schnell wieder evicted)",
      "410 Gone, wenn das Ziel schon weg ist",
    ],
    policy: { budgetTokens: 20_000, externalizeThresholdTokens: 1000 },
    staticSegments: staticBase(),
    params: [{ key: "waves", label: "Wellen", min: 2, max: 10, default: 5 }],
    async run(ctx, p) {
      await ctx.task("Task: Analysiere 50 Logdateien und extrahiere Fehler-Muster.");
      for (let w = 1; w <= p.waves; w++) {
        ctx.phase(`Welle ${w}/${p.waves}: Tool-Salve`);
        await ctx.appendUser(ctx.rand(30, 70));
        await ctx.render();
        for (let u = 0; u < 3; u++) {
          await ctx.toolUnit(ctx.rand(1200, 4500), "read_file");
        }
        await ctx.render();
        await ctx.appendAssistant(ctx.rand(80, 150));
        // Renders altern die TTLs (tool_result: 2 Turns) — danach Page Faults probieren.
        await ctx.render();
        await ctx.sleep(800);
        ctx.phase(`Welle ${w}: Page Faults auf frühere Results`);
        await ctx.tryPageFault();
        await ctx.tryPageFault();
        await ctx.render();
      }
    },
  },

  {
    id: "mcp-toggle",
    emoji: "🔌",
    name: "MCP-Toggle-Chaos",
    tagline: "Static-Epoch-Bumps zur Laufzeit",
    description:
      "Der User schaltet MCP-Server mitten in der Session ein und aus (Spec §4.2 — Kern-Anwendungsfall). " +
      "Jeder Toggle ist ein Epoch-Bump mit Diff; on_tool_removed externalisiert die Units entfernter Tools. " +
      "Beim Re-Enable derselben Quellen-Kombination fällt der cache_prefix_hash auf den alten Wert zurück — " +
      "kanonische Sortierung macht den Prefix byte-identisch.",
    watchFor: [
      "static_epoch_bumped-Events mit added/removed im Payload",
      "Epoch-Marker (e1, e2, …) in der Token-Timeline",
      "Static-Epoche zählt hoch, cache_prefix_hash wiederholt sich beim Re-Enable",
    ],
    policy: { budgetTokens: 30_000 },
    staticSegments: staticBase(["mcp:github", "mcp:jira"]),
    params: [{ key: "toggles", label: "Toggles", min: 2, max: 8, default: 4 }],
    async run(ctx, p) {
      await ctx.task("Task: Triage der offenen Issues über GitHub und Jira.");
      const withBoth = staticBase(["mcp:github", "mcp:jira"]);
      const onlyGithub = staticBase(["mcp:github"]);
      const onlyCore = staticBase();

      await ctx.appendUser(80);
      await ctx.render();
      await ctx.toolUnit(ctx.rand(800, 1500), "mcp_github_query");
      await ctx.render();

      const states: { label: string; segments: typeof withBoth }[] = [
        { label: "Jira AUS", segments: onlyGithub },
        { label: "GitHub auch AUS", segments: onlyCore },
        { label: "beide wieder AN", segments: withBoth },
        { label: "Jira AUS", segments: onlyGithub },
        { label: "beide wieder AN", segments: withBoth },
        { label: "alles AUS", segments: onlyCore },
        { label: "beide wieder AN", segments: withBoth },
        { label: "Jira AUS", segments: onlyGithub },
      ];

      for (let i = 0; i < p.toggles; i++) {
        const next = states[i % states.length];
        ctx.phase(`Toggle ${i + 1}/${p.toggles}: ${next.label}`);
        await ctx.epochBump(next.segments);
        await ctx.appendUser(ctx.rand(30, 60));
        await ctx.render();
        if (ctx.chance(0.6)) {
          await ctx.toolUnit(ctx.rand(300, 1200));
          await ctx.render();
        }
        await ctx.appendAssistant(ctx.rand(60, 120));
        await ctx.sleep(900);
      }
    },
  },

  {
    id: "budget-crunch",
    emoji: "🚨",
    name: "Budget-Crunch",
    tagline: "Emergency-Watermark und der 413-Retry-Pfad",
    description:
      "Ein absichtlich winziges Budget (6k) trifft auf große Tool-Results. Die Emergency-Stufe läuft synchron " +
      "im Render (nur I/O-freie Eviction); reicht das nicht, antwortet ctxman mit 413 Budget Exceeded und der " +
      "Client wartet und wiederholt — genau dieser Retry-Pfad wird hier sichtbar.",
    watchFor: [
      "Watermark springt auf hard/emergency",
      "413-Retries im Simulations-Log",
      "Token-Timeline kratzt an der Budget-Linie und fällt schlagartig",
      "unit_evicted-Schübe im Event-Feed",
    ],
    policy: { budgetTokens: 6_000, externalizeThresholdTokens: 1500 },
    staticSegments: staticBase(),
    params: [{ key: "turns", label: "Turns", min: 4, max: 20, default: 10 }],
    async run(ctx, p) {
      await ctx.task("Task: Verarbeite den Datenbank-Dump (viel zu groß für dieses Budget).");
      for (let t = 1; t <= p.turns; t++) {
        ctx.phase(`Turn ${t}/${p.turns}`);
        await ctx.appendUser(ctx.rand(40, 90));
        await ctx.render();
        await ctx.toolUnit(ctx.rand(1500, 3500), "read_file");
        await ctx.render();
        await ctx.appendAssistant(ctx.rand(60, 120));
        if (t % 3 === 0) await ctx.decision(`Zwischenstand nach Turn ${t} gesichert.`);
        await ctx.sleep(400);
      }
    },
  },
];
