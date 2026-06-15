# ctxman: Virtual Memory Management for LLM Agent Contexts

**Rudi Dittrich**
Department of Computer Science, Stanford University
`rudi.dittrich77@gmail.com`

*Draft — June 2026*

---

## Abstract

Large language model (LLM) agents accumulate context — conversation turns, tool results, retrieved resources — far faster than model context windows grow, and far faster than the economics of inference permit. Today, context management is typically an ad-hoc concern scattered across agent frameworks: truncation heuristics, one-shot summarization, or framework-specific memory classes that conflate *what the model sees* with *how the agent loop is written*. We argue that the agent's context deserves the same treatment that operating systems and managed runtimes gave program memory: a first-class, separately managed resource with explicit regions, a garbage collector, and a page-fault mechanism.

We present **ctxman**, a standalone stateful service that manages LLM agent context as a memory hierarchy. ctxman partitions context into an immutable, cache-stable *static region* (system prompt, tool definitions — the stack analogy) and a dynamic *working set* (the heap), whose canonical representation is a sequence of typed *segments*; the provider message list is merely a deterministic render artifact. A two-tier garbage collector keeps the working set under a token budget: deterministic *minor collections* (clean-page eviction of refetchable content, externalization of large tool results into a content-addressed blob store, TTL-based eviction) and asynchronous, LLM-assisted *major collections* (fact promotion to a long-term memory sink, followed by lossy compaction). Externalized content remains reachable through a built-in `expand_context_ref` tool — a page fault that lazily re-expands content on model demand and simultaneously provides a liveness signal to the collector. Three design invariants distinguish ctxman from prior systems: (1) byte-stable render prefixes via canonical ordering and content-addressing, making provider-side prompt caching robust under runtime tool toggling; (2) unit-coupled collection that structurally prevents orphaned tool calls; and (3) a strict separation of the hot path (no LLM calls, no blob I/O) from background collection. We describe the design, its invariants, a .NET 9 implementation with provider adapters for Anthropic and OpenAI wire formats, and report early experience integrating ctxman as the context backend of a multi-agent orchestration framework.

---

## 1. Introduction

The context window is the only working memory an LLM has, and agentic workloads are brutal to it. A single observability agent investigating a Kubernetes incident can ingest a 50,000-token `kubectl` dump in one tool call; a coding agent re-reads files it has already seen; a research agent spawns subagents whose intermediate chatter is irrelevant the moment they return. The naive strategy — append everything, truncate from the front when full — destroys exactly the wrong information (early task framing and decisions) while preserving exactly the wrong information (stale tool output). The slightly-less-naive strategy — summarize everything when full — is lossy, expensive, and sits on the latency-critical path of the agent loop.

The deeper problem is architectural. In most agent frameworks the message list *is* the state: the agent loop owns it, mutates it in place, and every context-management decision is entangled with application logic. This conflation has three costs. First, **no reuse**: every framework, in every language, re-implements truncation, summarization, and memory from scratch, and the implementations diverge. Second, **no auditability**: when an agent "forgets" a constraint at turn 30, there is no record of which mutation discarded it or why. Third, **cache hostility**: provider-side prompt caching (now offered by all major inference APIs) rewards byte-stable prompt prefixes, but ad-hoc list mutation — reordering tools, prepending summaries, in-place edits — silently invalidates the cache and multiplies cost.

This paper makes a simple claim: *LLM context is program memory, and forty years of memory-management systems research applies almost unmodified.* Concretely:

- The system prompt and tool definitions are a **static region** — immutable per epoch, the analog of the code/stack segment, and the natural unit of cache stability.
- Conversation and tool results form a **working set** on a heap, subject to budget pressure.
- Large tool results should be **externalized** — replaced by a typed pointer (a summary plus a reference) into a backing store — rather than either kept inline or destroyed. This is swapping, not freeing.
- Content that can be losslessly re-fetched from its source (skill documents, MCP resources) is a **clean page**: under pressure it is dropped, never swapped, because the source of truth lies elsewhere.
- Re-expansion on demand is a **page fault**, raised by the model itself through a tool call, and doubles as a **liveness signal**: content the model faults back in is demonstrably still needed.
- Lossy summarization and the extraction of durable facts are **generational collection**: cheap deterministic minor collections run frequently; expensive LLM-assisted major collections run rarely, asynchronously, and never on the hot path.
- Subagent invocations are **stack frames**: pushed on spawn, popped with a return value, their locals reclaimed en masse — after a mandatory promotion pass so that frame-local decisions survive.

We embody this model in **ctxman**, a standalone, multi-tenant, provider-agnostic context-store service. The agent retains its own LLM credentials and makes its own model calls; ctxman answers exactly one question — *"what does the model see this turn?"* — by rendering the canonical segment store into a provider-specific request fragment. The design was developed against a written specification with explicit invariants (I1–I5, §3) and implemented in C#/.NET 9 with thin generated client SDKs.

**Contributions.**

1. A memory-management model for LLM agent context — regions, segments, units, frames, generational GC, page faults — with five enforceable invariants (§3).
2. A render-determinism design that makes provider prompt caching robust under *runtime tool mutation*: canonical static-region ordering plus content-addressed prefix hashing yields byte-identical prefixes for identical toolset combinations regardless of toggle order or history (§4.3).
3. A hot-path/background split in which the latency-critical `append`/`render` path performs only database reads and writes — no LLM calls, no blob I/O — with an explicit, retryable backpressure signal (`413 Budget Exceeded`) instead of lossy emergency drops (§4.4).
4. A unit-coupling rule that makes orphaned `tool_use` blocks — a hard provider-API error — structurally unrepresentable in render output, including across tool-source removal at runtime (§3.4).
5. An implementation and early deployment experience, including full event-stream auditability ("why did the agent no longer know X at turn 30?" is answerable by replay) (§5, §6).

---

## 2. Related Work

**Virtual context management.** MemGPT [Packer et al., 2023] pioneered the OS analogy for LLM memory, introducing a main-context/external-context hierarchy with the LLM itself acting as the memory controller via self-directed function calls, and has since evolved into the Letta agent framework. ctxman shares the paging vocabulary but inverts the control model: in MemGPT the *model* decides what to page in and out, spending tokens and turns on memory management; in ctxman eviction, externalization, and compaction are *deterministic service-side policies* (TTLs, size thresholds, watermarks), and the model participates only on the demand side, through the `expand_context_ref` page-fault tool. This keeps the hot path free of memory-management reasoning and makes collection behavior reproducible and auditable — at the cost of forgoing model judgment about relevance, a trade-off we make explicitly (non-goal N3, §3.6). RecurrentGPT [Zhou et al., 2023] and recursive-summarization agents [Wang et al., 2023] maintain a rolling natural-language state, which corresponds to ctxman's compaction summaries but without lossless externalization beneath them.

**Memory for agents.** Generative Agents [Park et al., 2023] introduced a memory stream with recency/importance/relevance retrieval; Reflexion [Shinn et al., 2023] and Voyager [Wang et al., 2023] persist distilled experience across episodes. These address *cross-episode* memory — what ctxman deliberately delegates to an external memory store behind its promotion interface (non-goal N2): ctxman extracts and *writes* durable facts ("promotion") but never reads them back; retrieval is the agent's concern. Retrieval-augmented generation [Lewis et al., 2020] is complementary in the same way: RAG decides what enters the context; ctxman manages what happens to it afterwards.

**Context compression.** Token-level prompt compression (LLMLingua [Jiang et al., 2023], context distillation) and KV-cache eviction policies (H2O [Zhang et al., 2023], StreamingLLM's attention sinks [Xiao et al., 2023], TOVA, SnapKV) operate *below* the API boundary, inside the serving stack, and require model access. ctxman operates strictly above the provider API and is therefore compatible with closed-source serving; its analog of "what to keep" is segment-level policy rather than attention statistics. The two layers compose: a provider may evict KV entries internally while ctxman manages the logical prompt.

**Framework memory modules.** LangChain/LangGraph, LlamaIndex, AutoGen, and Semantic Kernel each ship conversation-memory abstractions (buffer windows, summary memories, vector memories). These are *libraries embedded in one framework and one language*; their context policies execute in-process, are not shared across heterogeneous agents, and offer no audit trail. ctxman's central architectural bet is the opposite: context management as a *stateful service* with a REST contract, thin per-language SDKs, and the canonical representation owned by the service — chosen explicitly over both a context-aware LLM proxy (which would put a single point of failure and streaming complexity on the hot path) and a spec-plus-native-SDKs approach (which would force divergent GC reimplementations per language) (§4.1).

**Prompt caching.** Anthropic's prompt caching, OpenAI's automatic prefix caching, and Gemini's context caching all discount prefix re-reads, and vLLM and SGLang [Zheng et al., 2023] implement prefix/radix caching in open serving stacks. Existing agent frameworks treat cache-friendliness as a best-effort property; ctxman is, to our knowledge, the first system to make *byte-stable render prefixes a hard API guarantee* (invariant I4) — including under runtime mutation of the tool catalog, the case that defeats naive prefix stability (§4.3).

**Memory-management systems.** Our mechanisms are deliberate transplants: generational collection [Lieberman & Hewitt, 1983; Ungar, 1984] motivates the minor/major split with different cost models per generation; clean-vs-dirty page distinction motivates dropping refetchable content without a blob write; mark-and-sweep [McCarthy, 1960] with a grace period governs the blob store's lifecycle, with the segment table as the sole root set; and write barriers find their analog in the epoch-bump protocol that guards the static region. The transplant is not merely rhetorical — it imports the *discipline*: explicit invariants, a defined reclamation order (cheap before expensive, lossless before lossy), and the rule that the mutator (the agent) never blocks on collection except in a bounded emergency mode.

---

## 3. The Context Memory Model

### 3.1 Sessions, segments, and regions

A **session** represents one agent run. Its canonical state is not a message list but an append-ordered set of **segments** — the atomic unit of context. A segment carries a kind (open vocabulary: `system_prompt`, `tool_def`, `user_msg`, `tool_call`, `tool_result`, `skill_content`, `mcp_resource`, `task`, `decision`, `subagent_return`, `ref_expansion`, …), a role, content *or* a blob reference, token count, a stable global sequence number `seq`, liveness metadata (`created_turn`, `last_referenced_turn`), and a state in the lattice

```
live → externalized → evicted
live → compacted
```

where `evicted` and `compacted` segments never appear in render output but persist as soft-deleted rows for audit (invariant **I3**).

Segments belong to one of two **regions**:

- **Static** — system prompt, tool definitions, skill index. Immutable within a *static epoch* (invariant **I1**): no update, eviction, externalization, or compaction may touch a static segment; the only legal mutation is an explicit epoch bump (§4.3). This region is the cache-stable prefix.
- **Working** — everything else: the heap, and the only region the collector operates on.

A key inversion follows: **the provider message list is a render artifact, never the source of truth.** The same segment store renders to Anthropic's Messages format or OpenAI's Chat Completions format through stateless provider adapters; the domain model knows no provider.

### 3.2 Time: turns as the unit of decay

A *turn* is exactly one model call — a `render` with `turn_advance=true`. A tool loop with five model calls ages TTLs by five turns. This is deliberate: tool results decay relative to the *model's attention*, not relative to user interactions. All TTL policies (per-kind, declarative, §3.6) are expressed in turns, and `render` increments the turn counter atomically under an idempotency key so that a retried render never double-ages the context.

### 3.3 Pinning without reordering

Segments marked `pinned` (typically `task` and `decision` kinds) are untouchable by the collector — but they render *at their chronological position*. The tempting alternative, hoisting pinned content into a dedicated "important stuff" block, was rejected (invariant **I4**, non-goal N4): hoisting breaks role alternation, breaks chronology, and — decisively — invalidates the provider cache prefix every time something is pinned retroactively. Layout stability beats clever reordering.

### 3.4 Units: coupling that prevents orphans

Provider APIs reject a `tool_use` block whose `tool_result` is missing. ctxman therefore never collects coupled segments independently: a **unit** is a `tool_call` segment plus its correlated `tool_result` (via `tool_call_id`), and eviction, externalization, and compaction operate on units (invariant **I5**). The coupling is asymmetric by design: externalizing a unit replaces only the *result* with a summary-plus-reference while the call stays live (the model should still see *that* it called the tool and what shape came back); evicting a unit removes both. A render request that would expose an incomplete unit fails with `422` and the list of open calls — the error is structurally impossible to ship to the provider.

### 3.5 Frames: subagents as stack frames

Subagent invocations map onto a frame stack. `push` opens a frame; subsequent segments carry its `frame_id`. Rendering defaults to the current frame *path* (root plus open frames); an isolated `scope=frame` view (static region + pinned root segments + frame segments) supports subagents that must not see the parent's working set. `pop` evicts all frame segments en masse and materializes the supplied return value as a single `subagent_return` segment in the parent — but only *after* the promotion policy has run over the frame's segments, so that frame-local decisions are extracted before their context is reclaimed. Pop order is strict LIFO; popping a frame with open children is a `409`.

### 3.6 Policies and non-goals

All collection behavior is **declarative configuration, not code**: per-kind TTLs and externalization flags, watermark fractions, the externalization threshold (default 2,000 tokens), the compaction model and prompt template, and the promotion sink, frozen as a snapshot per session for reproducibility. Three non-goals sharpen the model: ctxman never calls the agent's LLM (N1 — it is not a proxy); it is not a cross-session knowledge base (N2 — it writes to a memory store, never reads); and v1 deliberately uses deterministic heuristics rather than per-segment LLM relevance scoring (N3) — TTL, size, recency, and the page-fault liveness signal cover the bulk of the benefit at none of the cost.

---

## 4. System Design

### 4.1 A service, not a library — and not a proxy

Three architectures were considered. **(A)** A context-store service with thin per-language client SDKs; the agent performs its own LLM call using a render result. **(B)** A context-aware LLM proxy, transparent to existing SDKs. **(C)** A specification implemented natively by per-language SDKs without a service. ctxman is (A). (B) was rejected because a proxy forfeits fine-grained control (pinning, frames), inherits streaming complexity, and becomes a single point of failure on the hot path. (C) was rejected because the GC — the hard part — would be implemented twice (Python and C#) and would inevitably diverge. Under (A), the service owns the canonical representation and all collection logic exactly once; SDKs are generated from the OpenAPI contract plus a thin convenience layer.

The failure mode of (A) — the service becomes unreachable — is handled by a mandatory **degraded mode** in every SDK: the SDK always caches the last successful render, appends locally and buffers when the service is down, and resynchronizes via idempotency keys on recovery. A ctxman outage degrades context *quality* (no GC, no frame discipline), never agent *availability*.

### 4.2 The hot path and the watermark ladder

The agent loop is: append segments → `render` → model call → append the response → repeat. The render response contains the provider-specific request fragment (system, tools, messages), cache-breakpoint recommendations, the built-in page-fault tool definition, and the current budget state. The hot path performs **only database reads and writes** — no LLM calls, no blob operations.

Collection is driven by three watermarks relative to the model budget *B*:

| Watermark | Default | Action |
|---|---|---|
| soft | 0.60·B | minor collection, async after the turn |
| hard | 0.80·B | major collection, async, prioritized |
| emergency | 0.95·B | synchronous minor collection *inside* render |

Only the emergency tier may delay the hot path, and it is restricted to operations with **no I/O side effects**: clean-page and TTL eviction, but no externalization (no blob write in the hot path) and no LLM calls. If that is insufficient, render answers `413 Budget Exceeded` and the client retries while the asynchronous major collection catches up. The design preference is explicit: *a retryable, visible error over a lossy emergency drop of unsecured content.*

### 4.3 Render determinism and cache stability under tool toggling

Provider prompt caching pays for byte-identical prefixes. ctxman makes prefix stability a hard guarantee through three mechanisms:

**Canonical serialization.** Rendering uses canonical JSON — sorted keys, no timestamps, defined whitespace, stable float formatting — so identical segment state yields byte-identical output, verified by golden-file tests.

**Canonical static ordering.** Within the static region, segments sort by `(source, kind, content_hash)` — *never* insertion order. Consequently the same *combination* of active tool sources produces the same prefix regardless of the order in which sources were registered or toggled.

**Epochs with content-addressed rollback.** Runtime mutation of the tool catalog — a user disabling an MCP server mid-session is a core use case, not an edge case — goes through an explicit epoch bump: a full replacement of the static region under optimistic concurrency, which increments `static_epoch`, emits an audit event with a computed tool diff, and applies a configurable policy (`keep | externalize | evict`, default `externalize`) to working units that reference removed tools — keeping their information as summary-plus-reference while removing full calls from the model's immediate view, which empirically reduces the model's tendency to keep invoking disabled tools. The crucial cache property: because ordering is canonical, *disable-then-re-enable returns to a byte-identical prefix*. `static_epoch` counts monotonically, but the `cache_prefix_hash` may revert to an earlier value — within the provider's cache TTL, re-enabling a server is a cache *hit*. The hash is also a determinism alarm: a changed `cache_prefix_hash` at constant `static_epoch` indicates a violated invariant.

A normative client-side protocol completes the design: before a bump, the SDK closes open units of the disabled source with synthetic error results (preserving I5), and debounces multiple toggles (default 250 ms) into a single bump, since every epoch potentially invalidates the provider cache.

### 4.4 The collector

**Minor collection** (deterministic, cheap, frequent) proceeds *cheap before expensive, lossless before lossy*:

1. **Clean-page eviction.** Refetchable segments (skill content, MCP resources) past TTL are dropped with *no blob write* — the source of truth is external, and the `origin` URI remains in the audit trail. This is dropping a file-backed page, not swapping.
2. **Externalization.** Non-refetchable ("dirty") segments above the size threshold move to the blob store; the segment keeps a generated `summary` — the "type signature" of the missing content: its first lines and structural hints — plus the reference. Lossless: a page fault recovers everything.
3. **TTL eviction.** Units past their per-kind TTL, neither pinned nor static, are evicted.

**Major collection** (LLM-assisted, asynchronous, rare) has a strict internal order:

1. **Promotion first, mandatory.** A promotion pass extracts durable facts — decisions, constraints, learned invariants — from the compaction window and writes them to the configured memory sink (webhook or adapter). Promotion is an *event* (`fact_promoted`), not a segment state: sources stay untouched and are then compacted normally. Compaction is lossy; promotion before compaction is what makes the loss acceptable.
2. **Compaction.** The window — all unpinned working units, oldest first, up to a configured share of the working budget — is summarized by a *separate, cheap* LLM backend (ctxman's own, never the agent's; configurable model and prompt template) into a single summary segment that *inherits the `seq` position of the oldest compacted segment*, preserving chronology.
3. **Snapshot isolation.** Compaction runs against a frozen `context_version`; segments appended meanwhile are simply outside the frozen window, excluding write conflicts by construction.

Per-session collections are serialized (advisory locks); the agent never observes a half-collected context — only, at the next render, a smaller one.

### 4.5 Page faults and approximated liveness

Every render response includes the definition of a built-in tool, `expand_context_ref(segment_id)`, in the target provider's schema. When the model invokes it, the SDK fetches `GET /refs/{segment_id}` and appends the content as a short-TTL `ref_expansion` segment — re-collectable, like any page that may be faulted in again. The fetch updates the source segment's `last_referenced_turn`: the page fault is simultaneously a **liveness signal**, turning pure TTL heuristics into approximate reference-based liveness. The eviction-after-expansion rate is exported as a metric — it directly measures TTL misconfiguration.

If the segment is gone (evicted, or its blob swept), the endpoint answers `410 Gone` *with* the summary and origin as best-remaining information — a defined degraded path, not a failure: refetchable content can be re-acquired through its original mechanism, and tool results retain whatever the policy chose to preserve.

### 4.6 Blob lifecycle: mark-and-sweep with the segment table as root set

Externalized content lives in a content-addressed (SHA-256), immutable blob store behind an adapter interface (filesystem and Azure Blob in v1). Content addressing buys deduplication across segments and sessions within a tenant — and therefore forbids inline deletion on single-segment eviction. Reclamation is a periodic mark-and-sweep whose *only* root set is the segment table: mark all keys with at least one live reference (`state = externalized`); sweep unreferenced blobs older than a grace period (default 72 h), which both closes the put-then-crash orphan race and leaves a forensic window. Every deletion emits an audit event with key, size, and reason. Nothing is ever deleted on the hot path.

### 4.7 Tenancy, auth, and auditability

Tenancy exists *internally always*: every request resolves to a tenant context before reaching any handler, every query is tenant-filtered, blob keys are tenant-prefixed — regardless of auth mode. The modes (`none`, `api_key`, `jwt`/OIDC) differ only in how the tenant is established, so upgrading from an embedded single-user deployment to a multi-tenant platform is a configuration change with no data migration. Authorization beyond tenant membership is an explicit extension point (a single-method handler interface) so an external policy decision point can be injected without ctxman knowing its protocol.

Every mutation and every GC operation emits an immutable event (outbox pattern): `segment_externalized`, `unit_evicted`, `compaction_completed {tokens_before, tokens_after}`, `fact_promoted`, `static_epoch_bumped {diff}`, `blob_swept`, `render_served {cache_prefix_hash}`, and so on. Goal G6 of the specification is phrased as a falsifiable query — *"why did the agent no longer know X at turn 30?"* — and the event stream answers it by replay: the UI used in our deployment reconstructs the full context state at any point in time purely from events.

---

## 5. Implementation

ctxman is implemented in C#/.NET 9: a core library (`Ctxman.Core`) containing the domain model, render pipeline, collectors, and all provider/storage/compaction interfaces with no ASP.NET dependency, and a service host (`Ctxman.Api`) exposing minimal-API endpoints under `/v1`, with EF Core/Npgsql persistence (PostgreSQL: `sessions`, `segments`, `frames`, `events`, `idempotency_keys`), channel-based hosted services for the GC workers, and a middleware pipeline of tenant resolution → authentication → authorization. IDs are ULIDs; the wire format is snake_case JSON; idempotency keys are mandatory on all mutating endpoints and on turn-advancing renders, with optimistic concurrency via `context_version`/`If-Match` to detect concurrent writers.

Provider adapters are stateless and registered by name: the Anthropic adapter renders tool results as user-message content blocks with the system prompt as a top-level parameter; the OpenAI adapter renders `role: tool` messages with the system prompt as the first message. A renderer-level *coalescing rule* merges adjacent same-role working segments left behind by eviction into single multi-block messages — providers requiring strict role alternation reject the list otherwise, so coalescing is part of the render guarantee, not adapter discretion. Token counting is pluggable (`ITokenCounter`); v1 uses a tiktoken port for OpenAI models and a conservative heuristic for Anthropic — exact counts are not critical because watermarks are ratios, and the heuristic is periodically calibratable against the provider's counting endpoint. Compaction backends (`ICompactionModel`: Anthropic, Azure OpenAI) and promotion sinks (webhook) are adapters; the blob store has filesystem and Azure implementations plus a sweep worker and a cold-storage exporter for archived sessions.

Determinism is tested with golden files: canonical render output is checked in and compared byte-for-byte. Unit tests run against the core without I/O; API tests run the full pipeline via an in-process test server with SQLite as the relational test double.

The deliberate sequencing of the build mirrors the model's claims: core store and deterministic render first (with prefix-hash golden tests), then minor GC and the page-fault tool, then major GC with promotion and compaction, then frames and SDKs, then hardening (auth modes, Azure storage, metrics, archiving).

---

## 6. Evaluation

We deliberately position this as a design-and-experience evaluation; a quantitative benchmark study (token cost, cache-hit rates, end-task quality under budget pressure) is in progress and out of scope for this draft. We evaluate along three questions.

**Q1 — Does the model hold up under a real integration?** We integrated ctxman as a switchable context backend of *pytaskforce*, a Python multi-agent orchestration framework, replacing its in-process message list. The integration surface matched the model's predictions: agent messages map onto segments with no impedance mismatch, subagent invocations map onto frames, and the agent loop shrank to append/render around its own model call. Two integration findings are instructive. First, the *degraded-mode* requirement (§4.1) proved essential in practice, not theoretical: the orchestrator must keep serving users when the context service restarts, and the buffered-resync design made the outage invisible apart from temporarily unmanaged growth. Second, error semantics at the seam matter: an early version surfaced a failed promotion during frame pop as an unhandled fault; the fix — a typed, retryable `503 promotion_failed` — follows directly from the invariant that promotion must precede destructive reclamation and may therefore legitimately block it.

**Q2 — Are the invariants enforceable, not aspirational?** Each invariant is backed by a mechanical check: I1 by `409` on static writes outside an epoch bump; I4 by byte-comparison golden tests over canonical renders plus the runtime `cache_prefix_hash` alarm (a hash change at constant epoch is a determinism violation by definition, and is monitored, not assumed); I5 by `422` with the offending unit list at render time, making the orphaned-tool-call class of provider errors unreachable; I3 by the soft-delete schema, which the event-replay UI exercises continuously since it reconstructs historical context states from segments that no longer render. The strongest qualitative result is the auditability goal: "what did the model see at turn N, and why" is answerable for every turn from the event stream alone, which we use routinely during development to debug agent behavior.

**Q3 — What does the design cost?** Three costs are inherent and worth stating plainly. (1) *A network hop on the hot path.* Every model call is preceded by a render round-trip; the mitigation is that the hot path is two indexed reads and the degraded mode caps the blast radius of unavailability. (2) *Heuristic relevance.* TTL/size/recency policies will evict content an LLM-based scorer might have kept; the page-fault mechanism bounds the damage (lossless recovery for externalized content, summaries for the rest), and the eviction-after-expansion metric makes the misconfiguration observable rather than silent. (3) *Compaction spend.* Major collections consume tokens on a secondary model; this is bounded by the watermark ladder (compaction runs only above 0.8·B), by the window share cap, and is itself accounted in events for chargeback.

---

## 7. Discussion and Limitations

**What the analogy does not give us.** Program memory has exact reference semantics; context has *relevance*, which is observable only through proxies (recency, the page-fault signal) or through model judgment we deliberately excluded from v1. The analogy also says nothing about *placement effects*: LLMs are sensitive to where information sits in the prompt ("lost in the middle"), and ctxman's strict no-reordering stance trades possible placement gains for cache stability and reproducibility. We consider this the right default but not the final word; a future adapter-level study could quantify the trade-off.

**Single-writer assumption.** Optimistic concurrency detects concurrent writers on one session but the model is fundamentally one-agent-one-session; multi-agent collaboration is expressed through frames within a session or separate sessions plus the external memory store, not through shared mutable context.

**Token-count approximation.** Watermarks tolerate approximate counting, but a systematic under-count compresses the safety margin between the emergency watermark and the real limit; the conservative-overhead heuristic and periodic calibration address this only statistically.

**Generality of policies.** The per-kind policy vocabulary was shaped by tool-using assistant workloads; whether the defaults transfer to e.g. long-horizon coding agents with heavy file-context churn is an open empirical question that the declarative policy layer at least makes cheap to explore.

---

## 8. Conclusion

ctxman treats the LLM context window as what it is: scarce, hierarchical memory under a hard budget, accessed by a mutator (the agent) that should never block on its management. Transplanting the systems playbook — static and heap regions, clean and dirty pages, generational collection, page faults, mark-and-sweep, write-barrier-like epochs — yields a context manager that is reusable across languages and frameworks, deterministic and cache-stable by contract rather than by luck, auditable to the level of "why did the model see exactly this," and strictly off the agent's hot path. The broader claim is methodological: as agents industrialize, their context deserves the same separation of concerns that memory management earned in the 1980s — owned by a runtime, governed by policy, observable by event, and boring in exactly the way infrastructure should be.

---

## References

*(Selected; formatted informally in this draft.)*

- C. Packer, S. Wooders, K. Lin, V. Fang, S. G. Patil, I. Stoica, J. E. Gonzalez. **MemGPT: Towards LLMs as Operating Systems.** arXiv:2310.08560, 2023.
- J. S. Park, J. O'Brien, C. J. Cai, M. R. Morris, P. Liang, M. S. Bernstein. **Generative Agents: Interactive Simulacra of Human Behavior.** UIST 2023.
- N. Shinn, F. Cassano, E. Berman, A. Gopinath, K. Narasimhan, S. Yao. **Reflexion: Language Agents with Verbal Reinforcement Learning.** NeurIPS 2023.
- G. Wang, Y. Xie, Y. Jiang, A. Mandlekar, C. Xiao, Y. Zhu, L. Fan, A. Anandkumar. **Voyager: An Open-Ended Embodied Agent with Large Language Models.** arXiv:2305.16291, 2023.
- P. Lewis et al. **Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks.** NeurIPS 2020.
- H. Jiang, Q. Wu, C.-Y. Lin, Y. Yang, L. Qiu. **LLMLingua: Compressing Prompts for Accelerated Inference of Large Language Models.** EMNLP 2023.
- Z. Zhang et al. **H2O: Heavy-Hitter Oracle for Efficient Generative Inference of Large Language Models.** NeurIPS 2023.
- G. Xiao, Y. Tian, B. Chen, S. Han, M. Lewis. **Efficient Streaming Language Models with Attention Sinks.** ICLR 2024.
- L. Zheng et al. **SGLang: Efficient Execution of Structured Language Model Programs.** 2023/2024.
- N. F. Liu, K. Lin, J. Hewitt, A. Paranjape, M. Bevilacqua, F. Petroni, P. Liang. **Lost in the Middle: How Language Models Use Long Contexts.** TACL 2024.
- W. Zhou et al. **RecurrentGPT: Interactive Generation of (Arbitrarily) Long Text.** arXiv:2305.13304, 2023.
- H. Lieberman, C. Hewitt. **A Real-Time Garbage Collector Based on the Lifetimes of Objects.** CACM 26(6), 1983.
- D. Ungar. **Generation Scavenging: A Non-Disruptive High Performance Storage Reclamation Algorithm.** ACM SIGSOFT/SIGPLAN, 1984.
- J. McCarthy. **Recursive Functions of Symbolic Expressions and Their Computation by Machine, Part I.** CACM 3(4), 1960.
- ctxman Specification v0.2, 2026. (Internal design document; the normative source for the invariants and API described here.)
