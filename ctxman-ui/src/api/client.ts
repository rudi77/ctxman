import {
  ApiError,
  type AppendSegmentInput,
  type AppendSegmentsResponse,
  type CreateSessionRequest,
  type CreateSessionResponse,
  type EventsResponse,
  type ExpandRefResponse,
  type GcResponse,
  type Healthz,
  type PopFrameResponse,
  type PushFrameResponse,
  type RenderResponse,
  type ReplaceStaticSegmentsResponse,
  type SessionDetail,
  type StaticSegmentInput,
} from "./types";

// Im Dev-Betrieb proxied Vite "/api" auf die ctxman-API (siehe vite.config.ts).
const BASE: string = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

const TENANT_KEY = "ctxman-ui.tenant";

export function getTenant(): string {
  return localStorage.getItem(TENANT_KEY) ?? "";
}

export function setTenant(tenant: string): void {
  if (tenant) localStorage.setItem(TENANT_KEY, tenant);
  else localStorage.removeItem(TENANT_KEY);
}

function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  idempotency?: boolean;
  ifMatch?: number;
}

async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = {};
  if (opts.body !== undefined) headers["Content-Type"] = "application/json";
  // Auth-Modus none: Tenant kommt aus dem X-Tenant-Id-Header (Spec §4.1), sonst default_tenant.
  const tenant = getTenant();
  if (tenant) headers["X-Tenant-Id"] = tenant;
  if (opts.idempotency) headers["Idempotency-Key"] = newIdempotencyKey();
  if (opts.ifMatch !== undefined) headers["If-Match"] = String(opts.ifMatch);

  const res = await fetch(`${BASE}${path}`, {
    method: opts.method ?? (opts.body !== undefined ? "POST" : "GET"),
    headers,
    body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
  });

  let parsed: unknown = null;
  const text = await res.text();
  if (text) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = text;
    }
  }

  if (!res.ok) {
    throw new ApiError(res.status, parsed, describeError(res.status, parsed));
  }
  return parsed as T;
}

function describeError(status: number, body: unknown): string {
  if (body && typeof body === "object" && "error" in body) {
    return `HTTP ${status}: ${(body as { error: unknown }).error}`;
  }
  return `HTTP ${status}`;
}

export const api = {
  healthz: () => request<Healthz>("/healthz"),

  createSession: (body: CreateSessionRequest) =>
    request<CreateSessionResponse>("/v1/sessions", { body, idempotency: true }),

  getSession: (sid: string) => request<SessionDetail>(`/v1/sessions/${sid}`),

  archiveSession: (sid: string) =>
    request<void>(`/v1/sessions/${sid}/archive`, { method: "POST", body: {}, idempotency: true }),

  appendSegments: (sid: string, segments: AppendSegmentInput[]) =>
    request<AppendSegmentsResponse>(`/v1/sessions/${sid}/segments`, {
      body: segments.length === 1 ? segments[0] : { segments },
      idempotency: true,
    }),

  render: (sid: string, provider: string, scope: "path" | "frame", turnAdvance: boolean) =>
    request<RenderResponse>(`/v1/sessions/${sid}/render`, {
      body: { provider, scope, turn_advance: turnAdvance },
      idempotency: turnAdvance,
    }),

  replaceStaticSegments: (sid: string, segments: StaticSegmentInput[], ifMatch: number) =>
    request<ReplaceStaticSegmentsResponse>(`/v1/sessions/${sid}/static-segments`, {
      method: "PUT",
      body: { segments },
      idempotency: true,
      ifMatch,
    }),

  pushFrame: (sid: string, label: string) =>
    request<PushFrameResponse>(`/v1/sessions/${sid}/frames`, {
      body: { label },
      idempotency: true,
    }),

  popFrame: (sid: string, fid: string, returnContent: string, returnKind?: string) =>
    request<PopFrameResponse>(`/v1/sessions/${sid}/frames/${fid}`, {
      method: "DELETE",
      body: { return_content: returnContent, return_kind: returnKind },
      idempotency: true,
    }),

  pin: (sid: string, segid: string) =>
    request<void>(`/v1/sessions/${sid}/segments/${segid}/pin`, { method: "POST", body: {} }),

  unpin: (sid: string, segid: string) =>
    request<void>(`/v1/sessions/${sid}/segments/${segid}/pin`, { method: "DELETE" }),

  gc: (sid: string, level: "minor" | "major") =>
    request<GcResponse>(`/v1/sessions/${sid}/gc`, { body: { level } }),

  expandRef: (sid: string, segid: string) =>
    request<ExpandRefResponse>(`/v1/sessions/${sid}/refs/${segid}`),

  events: (sid: string, afterSeq: number) =>
    request<EventsResponse>(`/v1/sessions/${sid}/events?after_seq=${afterSeq}`),
};
