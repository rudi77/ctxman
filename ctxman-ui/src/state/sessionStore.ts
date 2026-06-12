import { useCallback, useEffect, useState } from "react";
import type { StaticSegmentInput } from "../api/types";

// Die API hat (spec-gemäß) keinen List-Sessions-Endpunkt — die UI führt die ihr bekannten
// Sessions in localStorage: alles, was über die Spielwiese erstellt oder manuell per ID
// hinzugefügt wurde. Für Spielwiesen-Sessions merken wir uns zusätzlich Budget/Watermarks
// und die Static-Segmente (die Static-Region erzeugt keine Events, s. eventReducer).

export interface KnownSession {
  id: string;
  label: string;
  addedAt: string;
  budgetTokens?: number;
  watermarks?: { soft: number; hard: number; emergency: number };
  staticSegments?: StaticSegmentInput[];
}

const STORAGE_KEY = "ctxman-ui.sessions";

function load(): KnownSession[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as KnownSession[]) : [];
  } catch {
    return [];
  }
}

function persist(sessions: KnownSession[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
}

export function useKnownSessions() {
  const [sessions, setSessions] = useState<KnownSession[]>(load);

  useEffect(() => persist(sessions), [sessions]);

  const add = useCallback((session: KnownSession) => {
    setSessions((prev) => {
      if (prev.some((s) => s.id === session.id)) return prev;
      return [session, ...prev];
    });
  }, []);

  const remove = useCallback((id: string) => {
    setSessions((prev) => prev.filter((s) => s.id !== id));
  }, []);

  const update = useCallback((id: string, patch: Partial<KnownSession>) => {
    setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, ...patch } : s)));
  }, []);

  return { sessions, add, remove, update };
}

export const DEFAULT_BUDGET = 180_000;
export const DEFAULT_WATERMARKS = { soft: 0.6, hard: 0.8, emergency: 0.95 };
