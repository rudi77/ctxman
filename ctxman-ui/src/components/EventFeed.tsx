import { useMemo, useState } from "react";
import type { EventItem } from "../api/types";
import { eventColor } from "../lib/colors";
import { formatTime } from "../lib/format";

interface Props {
  events: EventItem[];
}

/** Live-Event-Feed (Outbox-Stream, Spec §6) — neueste zuerst, filterbar, Payload aufklappbar. */
export function EventFeed({ events }: Props) {
  const [filter, setFilter] = useState<Set<string>>(new Set());
  const [expanded, setExpanded] = useState<string | null>(null);

  const types = useMemo(() => {
    const set = new Set<string>();
    for (const ev of events) set.add(ev.type);
    return Array.from(set).sort();
  }, [events]);

  const visible = useMemo(() => {
    const list = filter.size === 0 ? events : events.filter((ev) => filter.has(ev.type));
    return [...list].reverse().slice(0, 150);
  }, [events, filter]);

  const toggle = (type: string) => {
    setFilter((prev) => {
      const next = new Set(prev);
      if (next.has(type)) next.delete(type);
      else next.add(type);
      return next;
    });
  };

  return (
    <div>
      <div className="feed-filter">
        {types.map((t) => (
          <button key={t} className={filter.has(t) ? "on" : ""} style={{ borderLeft: `3px solid ${eventColor(t)}` }} onClick={() => toggle(t)}>
            {t}
          </button>
        ))}
        {filter.size > 0 && (
          <button onClick={() => setFilter(new Set())}>✕ Filter zurücksetzen</button>
        )}
      </div>
      <div className="event-feed">
        {visible.length === 0 && <span className="muted">Noch keine Events.</span>}
        {visible.map((ev) => (
          <div key={ev.id}>
            <div
              className="event-row"
              style={{ borderLeftColor: eventColor(ev.type) }}
              onClick={() => setExpanded(expanded === ev.id ? null : ev.id)}
            >
              <span className="seq">#{ev.seq}</span>
              <span className="type" style={{ color: eventColor(ev.type) }}>{ev.type}</span>
              <span className="time">{formatTime(ev.created_at)}</span>
            </div>
            {expanded === ev.id && (
              <div className="event-payload">{JSON.stringify(ev.payload, null, 2)}</div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
