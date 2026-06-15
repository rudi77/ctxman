interface Props {
  text: string;
}

/**
 * Kompakter Info-Marker: hält die (oft langen) Spec-Erläuterungen im Tooltip,
 * statt sie dauerhaft neben jede Panel-Überschrift zu schreiben. Hält das
 * Dashboard ruhig, ohne Information zu verlieren.
 */
export function InfoHint({ text }: Props) {
  return (
    <span className="info-hint" tabIndex={0} title={text} aria-label={text}>
      i
    </span>
  );
}
