import { useState, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, downloadCsv, write, type Closure, type CrewPerson, type HistoryRow, type Preview, type Receipt, type Session, type Summary } from "./api";
import { EventChart, HistoryChart } from "./charts";
import { Badge, capitalize, cop, crewName, Empty, Failure, Icon, Loading, Modal, monthName, Notice, periodName, ReceiptPaper, Table, Title, useData } from "./ui";

type Props = { route: string; id?: string; person: Session; month: string; setMonth: (month: string) => void };
const validPeriod = (s?: string): s is string => !!s && /^\d{4}-(0[1-9]|1[0-2])$/.test(s);

export function MonthInput({ month, setMonth }: Pick<Props, "month" | "setMonth">) {
  return <label className="period-input">Período<input type="month" min="0001-01" max="9999-12" value={month}
    onChange={e => { if (validPeriod(e.target.value)) setMonth(e.target.value); }} /></label>;
}

function HistoryTable({ rows, own = false, title = "Períodos cerrados" }: { rows: HistoryRow[]; own?: boolean; title?: string }) {
  if (rows.length === 0) return <div className="panel"><Empty>{own ? "Todavía no hay comprobantes para tu cuenta." : "Aún no hay períodos guardados."}</Empty></div>;
  return <Table title={title} headers={["Período", "Estado", own ? "Neto / COP" : "Reglas"]}>
    {rows.map(row => <tr key={row.period}>
      <th scope="row"><a className="row-link" href={`#/${own ? "comprobantes" : "historico"}/${row.period.slice(0, 7)}`}>{periodName(row.period)}</a></th>
      <td><Badge tone="good">Cerrado</Badge></td><td className="mono">{own && row.total !== undefined ? cop(row.total) : row.rulesVersion}</td>
    </tr>)}
  </Table>;
}

function Home({ person, month, setMonth }: Props) {
  const canRead = person.permissions.includes("payrollReadAll");
  const history = useData<HistoryRow[]>("/payroll", canRead);
  const current = history.data?.some(row => row.period.slice(0, 7) === month);
  return <>
    <Title context={periodName(month)} title={current ? `${capitalize(monthName(month))} quedó cerrado` : `${capitalize(monthName(month))} está por cerrar`}
      help="Revisá las novedades, confirmá los importes y guardá el cierre del período."
      action={<MonthInput month={month} setMonth={setMonth} />} />
    <section className="home-band band">
      <div><Badge tone={current ? "good" : "gold"}>{current ? "Cierre guardado" : "Período seleccionado"}</Badge>
        <h2>{current ? "Consultá el cierre guardado" : `Prepará el cierre de ${monthName(month)}`}</h2>
        <p>Del registro de novedades al comprobante definitivo.</p>
        <a className="button gold" href={current ? `#/historico/${month}` : "#/nomina"}><Icon name="go-dark" />{current ? "Ver cierre" : "Revisar nómina"}</a>
      </div>
      <ol className="steps"><li><span>01</span> Registrar novedades</li><li><span>02</span> Revisar la nómina</li><li><span>03</span> Guardar el cierre</li></ol>
    </section>
    {canRead && (history.isPending ? <Loading /> : history.isError ? <Failure error={history.error} retry={() => void history.refetch()} /> :
      <div className="dashboard-grid">
        <HistoryChart rows={history.data} />
        <HistoryTable rows={history.data.slice(0, 4)} title="Últimos períodos" />
      </div>)}
  </>;
}

function PayrollBand({ total, count, state, children, justClosed }: { total: number; count: number; state: "preview" | "stale" | "closed"; children?: ReactNode; justClosed?: boolean }) {
  return <section className="band payroll-band" aria-label="Resumen del período">
    <div><p className="eyebrow">Total del período &nbsp; / &nbsp; COP</p><p className="period-total" data-testid="period-total">{cop(total)}</p>
      <p>{count} {count === 1 ? "persona" : "personas"} · Reglas demo-v1</p></div>
    <div className="band-state" aria-live="polite"><Badge tone={state === "closed" ? "good" : state === "stale" ? "bad" : "gold"} effect={justClosed}>
      {state === "closed" ? "Cierre guardado" : state === "stale" ? "Revisión desactualizada" : "Lista para revisar"}
    </Badge></div>
    <div className="band-actions">{children}</div>
  </section>;
}

function ReceiptsTable({ entries, closed, crews = [] }: { entries: Receipt[]; closed: boolean; crews?: CrewPerson[] }) {
  const [selected, setSelected] = useState<Receipt | null>(null);
  const [page, setPage] = useState(0);
  const limit = 12;
  const byId = new Map(crews.map(p => [p.id, p.crew] as const));
  const showCrews = !closed && crews.length > 0;
  const sorted = [...entries].sort((a, b) => a.displayName.localeCompare(b.displayName, "es-CO"));
  const last = Math.max(0, Math.ceil(entries.length / limit) - 1);
  const current = Math.min(page, last);
  return <>
    {entries.length ? <Table title={closed ? "Comprobantes guardados" : "Personas incluidas"}
      headers={["Persona", ...(showCrews ? ["Cuadrilla"] : []), "Novedades", "Neto / COP"]} meta={`${entries.length} personas`}>
      {sorted.slice(current * limit, (current + 1) * limit).map(row => <tr key={row.personId}>
        <th scope="row"><button className="row-link" aria-label={`Ver comprobante de ${row.displayName}`} onClick={() => setSelected(row)}>{row.displayName}<span aria-hidden="true"> ↗</span></button></th>
        {showCrews && <td>{crewName(byId.get(row.personId) ?? null)}</td>}
        <td className="mono">{row.events.length}</td><td className="mono">{cop(row.total)}</td>
      </tr>)}
    </Table> : <div className="panel"><Empty>No hay personas liquidables para este período.</Empty></div>}
    {last > 0 && <div className="pagination"><span className="subtle" aria-live="polite">Página {current + 1} de {last + 1}</span>
      <button className="button outline" disabled={current === 0} onClick={() => setPage(current - 1)}>Anterior</button>
      <button className="button outline" disabled={current === last} onClick={() => setPage(current + 1)}>Siguiente</button></div>}
    {selected && <Modal title={closed ? "Comprobante guardado" : "Desglose individual"} close={() => setSelected(null)} drawer>
      <button className="button outline drawer-close" autoFocus onClick={() => setSelected(null)}>Cerrar comprobante</button>
      <ReceiptPaper receipt={selected} closed={closed} />
    </Modal>}
  </>;
}

function ReviewMonth({ month, setMonth, person }: Props) {
  const query = useQueryClient();
  const readAll = person.permissions.includes("payrollReadAll");
  const canSeeCrews = person.permissions.includes("eventsAll");
  const crews = useData<CrewPerson[]>("/crew/people", canSeeCrews);
  const [saved, setSaved] = useState<Closure | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const preview = useQuery({ queryKey: [`/payroll/${month}/preview`], staleTime: Infinity,
    queryFn: ({ signal }) => api<Preview>(`/payroll/${month}/preview`, signal) });
  const knownClosed = preview.error instanceof ApiError && preview.error.status === 409;
  const existing = useData<Closure>(`/payroll/${month}`, readAll && knownClosed && !saved);
  const closed = saved ?? existing.data;
  const review = preview.data;
  const monthLabel = monthName(month);
  const title = closed || knownClosed ? `${capitalize(monthLabel)} quedó cerrado` : `Revisar nómina de ${monthLabel}`;
  async function complete() {
    if (!review || busy || stale) return;
    setBusy(true);
    setFailure(null);
    try {
      const summary = await write<Summary>(`/payroll/${month}/close`, "POST", { revision: review.revision });
      const closure = { summary, people: review.people };
      setSaved(closure);
      query.setQueryData([`/payroll/${month}`], closure);
      void query.invalidateQueries({ queryKey: ["/payroll"] });
      void query.invalidateQueries({ queryKey: [`/payroll/${month}/preview`], refetchType: "none" });
      setConfirm(false);
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        if (readAll) {
          try {
            setSaved(await api<Closure>(`/payroll/${month}`));
            setConfirm(false);
            return;
          } catch (check) {
            if (!(check instanceof ApiError && check.status === 404)) {
              setFailure(check);
              return;
            }
          }
        }
        setStale(true);
        setConfirm(false);
      }
      setFailure(error);
    } finally { setBusy(false); }
  }
  async function refresh() {
    setFailure(null);
    const result = await preview.refetch();
    if (result.isSuccess) setStale(false);
  }
  return <>
    <Title context={periodName(month)} title={title} help={closed || knownClosed ? "Los importes y sus comprobantes quedaron guardados." :
      "Comprobá los importes y las novedades antes de guardar el período."}
      action={<MonthInput month={month} setMonth={setMonth} />} />
    {closed ? <>
      <PayrollBand total={closed.summary.total} count={closed.people.length} state="closed" justClosed={!!saved}>
        {readAll && <a className="button gold" href="#/historico"><Icon name="go-dark" />Ver histórico</a>}
      </PayrollBand>
      <Notice title="Este período es de solo lectura" tone="good">El cierre no envía pagos. Cambiar un salario no modifica un comprobante guardado.</Notice>
      <EventChart events={closed.people.flatMap(row => row.events)} caption="Registros conservados en los comprobantes." />
      <ReceiptsTable entries={closed.people} closed />
    </> : knownClosed ? readAll && existing.isPending ? <Loading /> : existing.isError ? <Failure error={existing.error} retry={() => void existing.refetch()} />
      : <Notice title="Período cerrado" tone="good">Tu perfil no tiene acceso al histórico.</Notice>
    : preview.isPending ? <Loading /> : preview.isError ? <Failure error={preview.error} retry={() => void refresh()} />
    : review && <>
      <PayrollBand total={review.total} count={review.people.length} state={stale ? "stale" : "preview"}>
        {stale ? <button className="button gold" disabled={preview.isFetching} onClick={() => void refresh()}>Volver a revisar</button> :
          <button className="button gold" disabled={review.blockedPeople.length > 0 || review.people.length === 0 || busy}
            onClick={() => { setFailure(null); setConfirm(true); }}><Icon name="lock-dark" />Cerrar período</button>}
      </PayrollBand>
      {stale ? <Notice title="La vista previa ya no está vigente" tone="bad">Volvé a revisar los importes antes de cerrar. {!confirm && failure instanceof Error ? failure.message : ""}</Notice> :
        review.blockedPeople.length ? <Notice title="No podés cerrar este período" tone="bad">Hay {review.blockedPeople.length} persona(s) inactivas o sin salario con novedades. Corregí el conflicto antes de cerrar.</Notice> :
          <Notice title="Estás viendo una vista previa">Guardaremos el cálculo de esta revisión. Seleccioná una persona para verificar su comprobante.</Notice>}
      <EventChart events={review.people.flatMap(row => row.events)} />
      <ReceiptsTable entries={review.people} closed={false} crews={crews.data} />
      {confirm && <Modal title={`¿Cerrar ${monthLabel} de ${month.slice(0, 4)}?`} close={() => setConfirm(false)} destructive busy={busy}>
        <Badge tone="warn">Acción definitiva</Badge>
        <p>Se guardarán los comprobantes de {review.people.length} personas. El período no se puede reabrir desde Cifra Firme.</p>
        <div className="confirm-total"><span className="eyebrow">Total que queda guardado</span><strong>{cop(review.total)} COP</strong></div>
        {!!failure && <Failure error={failure} />}
        <div className="dialog-actions"><button className="button outline" autoFocus disabled={busy} onClick={() => setConfirm(false)}>Cancelar</button>
          <button className="button primary" disabled={busy} onClick={() => void complete()}>{busy ? "Guardando…" : "Confirmar cierre"}</button></div>
      </Modal>}
    </>}
  </>;
}

function History({ person, id, route }: Props) {
  const own = route === "comprobantes";
  const history = useData<HistoryRow[]>("/payroll", !id && (!own || !person.permissions.includes("payrollReadAll")));
  const receipt = useData<Receipt>(`/payroll/${id}/people/${person.id}`, own && validPeriod(id));
  const detail = useData<Closure>(`/payroll/${id}`, !own && validPeriod(id));
  const [failure, setFailure] = useState<unknown>(null);
  const [downloading, setDownloading] = useState(false);
  const exportable = person.permissions.includes("payrollExport");
  async function exportFile() {
    if (!id || downloading) return;
    setDownloading(true);
    setFailure(null);
    try { await downloadCsv(id); } catch (error) { setFailure(error); } finally { setDownloading(false); }
  }
  if (id) {
    const result = own ? receipt : detail;
    if (!validPeriod(id)) return <Failure error={Error("El período no es válido.")} />;
    const back = <a className="button outline" href={`#/${route}`}>← Volver al listado</a>;
    return <>
      <Title context={periodName(id)} title={own ? "Comprobante guardado" : `Cierre de ${monthName(id)}`}
        help="Guardado a partir del cierre del período. Los datos posteriores no cambian este resultado." action={back} />
      {result.isPending ? <Loading /> : result.isError ? <Failure error={result.error} retry={() => void result.refetch()} />
        : own && receipt.data ? <div className="receipt-layout"><ReceiptPaper receipt={receipt.data} closed />
          <div className="dashboard-stack">
            <Notice title="Solo lectura" tone="good">Este total no cambia al editar datos del personal.</Notice>
            <EventChart events={receipt.data.events} caption="Registros de tu comprobante guardado." />
          </div></div>
        : detail.data && <>
          <PayrollBand total={detail.data.summary.total} count={detail.data.people.length} state="closed">
            {exportable && <button className="button gold" disabled={downloading} onClick={() => void exportFile()}>{downloading ? "Descargando…" : "Descargar CSV"}</button>}
          </PayrollBand>
          {!!failure && <Failure error={failure} />}
          <EventChart events={detail.data.people.flatMap(row => row.events)} caption="Registros conservados en los comprobantes." />
          <ReceiptsTable entries={detail.data.people} closed />
        </>}
    </>;
  }
  return <>
    <Title context={own ? "Consulta personal" : "Cierres guardados"} title={own ? "Mis comprobantes" : person.role === "accountant" ? "Períodos cerrados" : "Histórico de nómina"}
      help={own ? `${person.firstName} ${person.lastName} · Solo se muestran comprobantes de tu cuenta.` : "Consultá los períodos cerrados y sus comprobantes en solo lectura."} />
    {own && person.permissions.includes("payrollReadAll") ?
      <a className="button outline" href="#/historico">Consultar períodos en el histórico</a> :
      history.isPending ? <Loading /> : history.isError ? <Failure error={history.error} retry={() => void history.refetch()} /> :
        <div className="dashboard-grid">
          <HistoryChart rows={history.data} own={own} />
          <HistoryTable rows={history.data} own={own} title={own ? "Comprobantes disponibles" : "Períodos cerrados"} />
        </div>}
  </>;
}

export default function PayrollPages(props: Props) {
  if (props.route === "resumen") return <Home {...props} />;
  if (props.route === "nomina") return <ReviewMonth key={props.month} {...props} />;
  return <History key={props.id ?? "list"} {...props} />;
}
