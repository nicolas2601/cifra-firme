import { useEffect, useId, useRef, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, type Kind, type Receipt } from "./api";

export const demo = import.meta.env.VITE_DEMO === "true";
const months = new Intl.DateTimeFormat("es-CO", { month: "long", timeZone: "UTC" });
const decimals = new Intl.NumberFormat("de-DE", { maximumFractionDigits: 0 });
export const cop = (n: number) => `${n < 0 ? "−" : ""}$${decimals.format(Math.abs(n))}`;
export const monthName = (value: string) => months.format(new Date(`${value.slice(0, 7)}-01T12:00:00Z`));
export const capitalize = (value: string) => value.charAt(0).toUpperCase() + value.slice(1);
export const periodName = (value: string) => `${capitalize(monthName(value))} ${value.slice(0, 4)}`;
export const crewName = (crew: string | null) => crew ? `Operaciones ${capitalize(crew)}` : "Sin cuadrilla";
export const errorText = (e: unknown) => e instanceof Error ? e.message : "No pudimos completar la operación.";
export const monthToday = () => {
  if (demo) return "2026-04";
  const day = new Date();
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, "0")}`;
};

export function useData<T>(path: string, enabled = true) {
  return useQuery({ queryKey: [path], queryFn: ({ signal }) => api<T>(path, signal), enabled });
}

export function Icon({ name }: { name: string }) {
  return <img className="icon" src={`/mark/${name}.svg`} alt="" aria-hidden="true" />;
}
export function Brand({ variant = "mark" }: { variant?: "mark" | "mark-nav" | "mark-ledger" | "mark-receipt" }) {
  return <span className={`brand brand--${variant}`}><img src={`/mark/${variant}.svg`} alt="" />Cifra Firme</span>;
}
export function Badge({ tone = "soft", children, effect }: { tone?: string; children: ReactNode; effect?: boolean }) {
  return <span className={`badge ${tone}${effect ? " seal-enter" : ""}`}>{children}</span>;
}
export function Title({ context, title, help, action }: { context: string; title: string; help: string; action?: ReactNode }) {
  return <header className="page-title"><div><p className="eyebrow">{context}</p><h1>{title}</h1><p>{help}</p></div>{action}</header>;
}
export function Notice({ title, children, tone = "warn" }: { title: string; children?: ReactNode; tone?: "warn" | "good" | "bad" }) {
  return <div className={`notice ${tone}`} role={tone === "bad" ? "alert" : "status"}><strong>{title}</strong>{children && <div>{children}</div>}</div>;
}
export function Loading() { return <p className="empty" role="status">Cargando información…</p>; }
export function Failure({ error, retry }: { error: unknown; retry?: () => void }) {
  return <Notice tone="bad" title={errorText(error)}>{retry && <button className="button outline" onClick={retry}>Reintentar</button>}</Notice>;
}
export function Empty({ children }: { children: ReactNode }) { return <p className="empty">{children}</p>; }
export function Table({ title, headers, children, meta }: { title: string; headers: string[]; children: ReactNode; meta?: ReactNode }) {
  const id = useId();
  return <section className="panel table-panel" aria-labelledby={id}>
    <header className="table-title"><h2 id={id}>{title}</h2>{meta && <span className="mono subtle">{meta}</span>}</header>
    <div className="table-scroll" tabIndex={0} role="region" aria-labelledby={id}><table>
      <thead><tr>{headers.map(label => <th scope="col" key={label}>{label}</th>)}</tr></thead><tbody>{children}</tbody>
    </table></div>
  </section>;
}

export function Modal({ title, close, destructive, children, drawer = false, busy = false }:
  { title: string; close: () => void; destructive?: boolean; children: ReactNode; drawer?: boolean; busy?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null);
  const id = useId();
  useEffect(() => {
    const dialog = ref.current!;
    const trigger = document.activeElement;
    dialog.showModal();
    return () => { dialog.close(); if (trigger instanceof HTMLElement && trigger.isConnected) trigger.focus(); };
  }, []);
  return <dialog ref={ref} aria-labelledby={id} role={destructive ? "alertdialog" : undefined}
    className={drawer ? "drawer" : "modal"}
    onCancel={e => { e.preventDefault(); if (!busy) close(); }}>
    <div className="dialog-body"><h2 id={id}>{title}</h2>{children}</div>
  </dialog>;
}

export function ReceiptPaper({ receipt, closed, artwork = false }: { receipt: Receipt; closed: boolean; artwork?: boolean }) {
  const label = (kind: Kind, singular: string, plural: string) => {
    const count = receipt.events.filter(e => e.kind === kind).length;
    return `${count} ${count === 1 ? singular : plural}`;
  };
  const concepts = [
    ["Salario base", receipt.monthlySalary], [label("absence", "ausencia", "ausencias"), receipt.absenceAdjustment],
    [label("medicalLeave", "incapacidad", "incapacidades"), receipt.medicalLeaveAdjustment],
    [label("additionalDay", "día adicional", "días adicionales"), receipt.additionalDayAdjustment]
  ] as const;
  return <article className={`paper ${artwork ? "sample-receipt" : ""}`}>
    <div className="paper-brand"><Brand variant="mark-receipt" /><span className="mono">{receipt.period.slice(5, 7)} / {receipt.period.slice(0, 4)}</span></div>
    {artwork ? <p className="paper-name">{receipt.displayName}</p> : <h2 className="paper-name">{receipt.displayName}</h2>}
    <p className="subtle">Nómina de {monthName(receipt.period)} · COP</p>
    <dl className="concepts">{concepts.map(([label, amount]) => <div key={label}><dt>{label}</dt><dd>{cop(amount)}</dd></div>)}</dl>
    <div className="paper-total"><p>Total del comprobante</p><strong>{cop(receipt.total)}</strong></div>
    <Badge tone={closed ? "good" : "warn"}>{closed ? "✓ Guardado · Solo lectura" : "Borrador · No guardado"}</Badge>
    <p className="paper-note">Reglas {receipt.rulesVersion} · Importe {closed ? "congelado" : "provisional"}</p>
  </article>;
}
