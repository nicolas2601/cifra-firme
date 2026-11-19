import { useId } from "react";
import { type Kind, type HistoryRow, type WorkEvent } from "./api";
import { cop, periodName } from "./ui";

export const eventLabels: Record<Kind, string> = {
  worked: "Día laborado", medicalLeave: "Incapacidad", absence: "Ausencia", additionalDay: "Día adicional"
};

type Bar = { label: string; value: number };
const count = new Intl.NumberFormat("es-CO");

export function Bars({ title, caption, bars, format = count.format }: {
  title: string; caption: string; bars: Bar[]; format?: (value: number) => string
}) {
  const titleId = useId();
  const captionId = useId();
  const max = Math.max(1, ...bars.map(({ value }) => value));
  return <figure className="panel chart" aria-labelledby={titleId} aria-describedby={captionId}>
    <figcaption>
      <h2 id={titleId}>{title}</h2>
      <p id={captionId}>{caption}</p>
    </figcaption>
    <ul role="list">
      {bars.map(({ label, value }) => <li key={label}>
        <span>{label}<span className="sr-only">: </span><strong>{format(value)}</strong></span>
        <svg aria-hidden="true" focusable="false" viewBox="0 0 100 4" preserveAspectRatio="none">
          <rect className="chart-track" width="100" height="4" />
          <rect className="chart-fill" width={100 * value / max} height="4" />
        </svg>
      </li>)}
    </ul>
  </figure>;
}

export function EventChart({ events, title = "Novedades por tipo", caption = "Registros incluidos en esta liquidación." }: {
  events: WorkEvent[]; title?: string; caption?: string
}) {
  return <Bars title={title} caption={caption} bars={(Object.keys(eventLabels) as Kind[]).map(kind => ({
    label: eventLabels[kind], value: events.filter(event => event.kind === kind).length
  }))} />;
}

export function HistoryChart({ rows, own = false }: { rows: HistoryRow[]; own?: boolean }) {
  const bars = rows.slice(0, 4).reverse().flatMap(row =>
    row.total === undefined ? [] : [{ label: periodName(row.period), value: row.total }]);
  if (!bars.length) return null;
  return <Bars title={own ? "Mi neto por mes" : "Nómina cerrada por mes"}
    caption={own ? "Importes de tus últimos comprobantes · COP" : "Total guardado en los últimos cierres · COP"}
    bars={bars} format={cop} />;
}
