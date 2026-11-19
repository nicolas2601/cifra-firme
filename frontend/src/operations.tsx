import { useState, type FormEvent, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { write, type Activity, type CrewPerson, type Kind, type Permission, type Person, type Role, type Session, type TaskState, type WorkEvent } from "./api";
import { Bars, EventChart, eventLabels } from "./charts";
import { Badge, crewName, Empty, Failure, Loading, Modal, Notice, periodName, Table, Title, useData } from "./ui";

const laneLabels: Record<TaskState, string> = { pending: "Pendientes", inProgress: "En curso", finished: "Terminadas" };
const permissions: Record<Permission, string> = {
  peopleManage: "Administrar personal", rolesManage: "Administrar roles", eventsAll: "Novedades de todas las cuadrillas",
  eventsCrew: "Novedades de su cuadrilla", payrollClose: "Revisar y cerrar nómina", payrollReadAll: "Consultar todos los cierres",
  payrollReadOwn: "Consultar comprobantes propios", payrollExport: "Exportar CSV", activityManage: "Administrar actividades",
  activityReadOwn: "Consultar actividades propias"
};
const input = (form: FormData, name: string) => String(form.get(name) ?? "").trim();
const nameOf = (person: CrewPerson) => `${person.firstName} ${person.lastName}`;
const encoded = encodeURIComponent;

function Editor({ title, close, save, children, remove, removeWarning }: { title: string; close: () => void;
  save: (data: FormData) => Promise<unknown>; children: ReactNode; remove?: () => Promise<unknown>; removeWarning?: string }) {
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const [confirm, setConfirm] = useState(false);
  async function run(action: () => Promise<unknown>) {
    if (busy) return;
    setFailure(null);
    setBusy(true);
    try { await action(); close(); } catch (error) { setFailure(error); } finally { setBusy(false); }
  }
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const fields = new FormData(e.currentTarget);
    void run(() => save(fields));
  }
  return <Modal title={confirm ? `Confirmar: ${title.toLowerCase()}` : title} close={close} busy={busy}>
    {confirm && remove ? <>
      <p>{removeWarning ?? "Esta acción no se puede deshacer."}</p>{!!failure && <Failure error={failure} />}
      <div className="dialog-actions"><button className="button outline" autoFocus disabled={busy} onClick={() => { setFailure(null); setConfirm(false); }}>Volver</button>
        <button className="button danger" disabled={busy} onClick={() => void run(remove)}>Confirmar</button></div>
    </> : <form className="fields" onSubmit={submit}>
      {children}
      {!!failure && <Failure error={failure} />}
      <div className="dialog-actions">
        {remove && <button type="button" className="button danger-outline" disabled={busy} onClick={() => { setFailure(null); setConfirm(true); }}>Retirar</button>}
        <button type="button" className="button outline" disabled={busy} onClick={close}>Cancelar</button>
        <button className="button primary" disabled={busy}>{busy ? "Guardando…" : "Guardar cambios"}</button>
      </div>
    </form>}
  </Modal>;
}

function Attendance({ person, month, setMonth }: Pick<Props, "person" | "month" | "setMonth">) {
  const client = useQueryClient();
  const crew = useData<CrewPerson[]>("/crew/people");
  const [personId, setPersonId] = useState("");
  const [edit, setEdit] = useState<WorkEvent | null>(null);
  const [status, setStatus] = useState("");
  const [failure, setFailure] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const selected = crew.data?.find(p => p.id === personId);
  const eventsPath = `/people/${personId}/events`;
  const listPath = `${eventsPath}?month=${month}`;
  const events = useData<WorkEvent[]>(listPath, !!selected);
  const history = useData<{ period: string }[]>("/payroll", person.permissions.includes("payrollReadAll"));
  const closed = history.data?.some(row => row.period.slice(0, 7) === month);
  const full = person.permissions.includes("eventsAll");
  const crewCounts = new Map<string, number>();
  for (const member of crew.data ?? []) {
    const label = crewName(member.crew);
    crewCounts.set(label, (crewCounts.get(label) ?? 0) + 1);
  }
  async function change(method: "POST" | "PUT" | "DELETE", event?: WorkEvent) {
    const path = method === "POST" ? eventsPath : `${eventsPath}/${encoded(edit!.date)}`;
    await write(path, method, event);
    void client.invalidateQueries({ queryKey: [listPath] });
    void client.invalidateQueries({ predicate: q => String(q.queryKey[0]).endsWith("/preview") });
  }
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (busy || closed || !selected) return;
    const form = e.currentTarget;
    const data = new FormData(form);
    setStatus("");
    setFailure(null);
    setBusy(true);
    try {
      await change("POST", { date: input(data, "date"), kind: input(data, "kind") as Kind });
      setStatus("Novedad registrada.");
      form.reset();
    } catch (error) { setFailure(error); } finally { setBusy(false); }
  }
  function clear() { setStatus(""); setFailure(null); }
  return <>
    <Title context={periodName(month)} title={full ? "Novedades del período" : "Mi cuadrilla"}
      help={full ? "Revisá las novedades de las personas antes de liquidar el período." : `${crewName(person.crew)} · Solo ves personas de tu cuadrilla.`}
      action={<label className="period-input">Período<input type="month" value={month} min="0001-01" max="9999-12" onChange={e => {
        if (/^\d{4}-(0[1-9]|1[0-2])$/.test(e.target.value)) { setMonth(e.target.value); clear(); }
      }} /></label>} />
    {crew.isPending ? <Loading /> : crew.isError ? <Failure error={crew.error} retry={() => void crew.refetch()} /> : <>
      {closed && <Notice title="Período cerrado" tone="good">Las novedades de este período son de solo lectura.</Notice>}
      <div className="split">
        <form className="panel fields" onSubmit={submit}>
          <h2>Registrar novedad</h2>
          <label>Persona<select value={personId} onChange={e => { setPersonId(e.target.value); clear(); }} required>
            <option value="">Seleccioná una persona</option>
            {crew.data.map(p => <option key={p.id} value={p.id}>{nameOf(p)}</option>)}</select></label>
          {!!selected && <small className="subtle">{crewName(selected.crew)}</small>}
          {!closed && <>
            <label>Fecha<input name="date" type="date" min={`${month}-01`} max={`${month}-${new Date(Number(month.slice(0, 4)), Number(month.slice(5)), 0).getDate()}`}
              aria-describedby={failure ? "event-error" : undefined} required /></label>
            <label>Tipo<select name="kind">{Object.entries(eventLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
            {!!failure && <div id="event-error"><Failure error={failure} /></div>}
            {status && <Notice title={status} tone="good" />}
            <button className="button primary" disabled={busy}>{busy ? "Guardando…" : "Guardar novedad"}</button>
          </>}
        </form>
        <div className="dashboard-stack">{!selected ? <>
          <Notice title="Una novedad por día">Seleccioná una persona para consultar sus registros.</Notice>
          {crewCounts.size > 0 && <Bars title="Personal por cuadrilla" caption="Personas activas disponibles para tu perfil."
            bars={Array.from(crewCounts, ([label, value]) => ({ label, value }))} />}
        </> :
          events.isPending ? <Loading /> : events.isError ? <Failure error={events.error} retry={() => void events.refetch()} /> :
            events.data.length ? <>
              <EventChart events={events.data} title={`Novedades de ${nameOf(selected)}`} caption={`Registros de ${periodName(month)}.`} />
              <Table title="Novedades registradas" headers={["Fecha", "Tipo", ...(closed ? [] : ["Acción"])]}>
              {events.data.map(row => <tr key={row.date}><th scope="row" className="mono">{row.date}</th><td>{eventLabels[row.kind]}</td>
                {!closed && <td><button className="row-link" onClick={() => setEdit(row)}>Editar <span className="sr-only">{row.date}</span></button></td>}</tr>)}
            </Table></> : <div className="panel"><Empty>Esta persona no tiene novedades en el período.</Empty></div>}</div>
      </div>
    </>}
    {edit && <Editor title={`Novedad del ${edit.date}`} close={() => setEdit(null)}
      save={data => change("PUT", { date: edit.date, kind: input(data, "kind") as Kind })} remove={() => change("DELETE")}>
      <label>Tipo<select name="kind" defaultValue={edit.kind}>{Object.entries(eventLabels).map(([key, text]) => <option value={key} key={key}>{text}</option>)}</select></label>
    </Editor>}
  </>;
}

function Directory() {
  const client = useQueryClient();
  const [page, setPage] = useState(1);
  const [edit, setEdit] = useState<Person | "new" | null>(null);
  const size = 25;
  const people = useData<Person[]>(`/people?page=${page}&size=${size}`);
  const roles = useData<Role[]>("/roles", !!edit);
  const current = edit === "new" ? null : edit;
  const refresh = () => client.invalidateQueries({ predicate: ({ queryKey: [key] }) =>
    typeof key === "string" && (key.startsWith("/people?") || key === "/crew/people" || key.endsWith("/preview")) });
  async function save(form: FormData) {
    const contact = (key: string) => input(form, key).split(/\r?\n/).map(s => s.trim()).filter(Boolean);
    await write(current ? `/people/${current.id}` : "/people", current ? "PUT" : "POST", {
      username: input(form, "username"), firstName: input(form, "first"), lastName: input(form, "last"), document: input(form, "document"),
      role: input(form, "role"), crew: input(form, "crew") || null, salary: Number(input(form, "salary")), active: form.has("active"),
      addresses: contact("addresses"), phones: contact("phones"), password: form.get("password") || null
    });
    await refresh();
  }
  return <>
    <Title context="Configuración" title="Personal" help="Administrá cuentas y asignaciones sin perder su histórico."
      action={<button className="button primary" onClick={() => setEdit("new")}>Agregar persona</button>} />
    {people.isPending ? <Loading /> : people.isError ? <Failure error={people.error} retry={() => void people.refetch()} /> : <>
      {people.data.length > 0 && <Bars title="Estado de las cuentas" caption={`Solo las cuentas de la página ${page} del directorio.`}
        bars={[ { label: "Activas", value: people.data.filter(p => p.active).length },
          { label: "Inactivas", value: people.data.filter(p => !p.active).length } ]} />}
      <Table title="Directorio" headers={["Persona", "Cuadrilla", "Estado"]} meta={`Página ${page}`}>
        {people.data.map(p => <tr key={p.id}><th scope="row"><button className="row-link" onClick={() => setEdit(p)}>{nameOf(p)}</button></th>
          <td>{crewName(p.crew)}</td><td><Badge tone={p.active ? "good" : "soft"}>{p.active ? "Activa" : "Inactiva"}</Badge></td></tr>)}
      </Table>
      {!people.data.length && <Empty>No hay personas en esta página.</Empty>}
      <div className="pagination"><button className="button outline" disabled={page === 1} onClick={() => setPage(page - 1)}>Anterior</button>
        <button className="button outline" disabled={people.data.length < size} onClick={() => setPage(page + 1)}>Siguiente</button></div>
    </>}
    {edit && roles.data && <Editor title={current ? nameOf(current) : "Alta de personal"} close={() => setEdit(null)} save={save}
      remove={current?.active ? async () => { await write(`/people/${current.id}`, "DELETE"); await refresh(); } : undefined}
      removeWarning="Se desactiva la cuenta. El histórico de nómina se conserva.">
      <div className="field-grid">
        <label>Nombre<input name="first" defaultValue={current?.firstName} required maxLength={80} /></label>
        <label>Apellido<input name="last" defaultValue={current?.lastName} required maxLength={80} /></label>
        <label>Usuario<input name="username" defaultValue={current?.username} required minLength={2} maxLength={48} pattern={"[A-Za-z0-9_.\\-]+"} /></label>
        <label>Documento<input name="document" defaultValue={current?.document} required minLength={2} maxLength={48} pattern={"[A-Za-z0-9_.\\-]+"} /></label>
        <label>Rol<select name="role" defaultValue={current?.role ?? "worker"}>{roles.data.map(r => <option key={r.key} value={r.key}>{r.name}</option>)}</select></label>
        <label>Cuadrilla<input name="crew" defaultValue={current?.crew ?? ""} maxLength={32} /></label>
        <label>Salario mensual / COP<input name="salary" type="number" min={0} max={999999999999} defaultValue={current?.salary ?? 0} required /></label>
        <label>{current ? "Cambiar contraseña (opcional)" : "Contraseña inicial"}<input name="password" autoComplete="new-password"
          type="password" minLength={12} maxLength={128} required={!current} /></label>
        <label>Direcciones (una por línea)<textarea name="addresses" defaultValue={current?.addresses.join("\n")} required /></label>
        <label>Teléfonos (uno por línea)<textarea name="phones" defaultValue={current?.phones.join("\n")} required /></label>
      </div>
      <label className="check"><input name="active" type="checkbox" defaultChecked={current?.active ?? true} /> Cuenta activa</label>
    </Editor>}
    {edit && !roles.data && <Modal title="Ficha de personal" close={() => setEdit(null)}>{roles.isError ?
      <Failure error={roles.error} retry={() => void roles.refetch()} /> : <Loading />}<button className="button outline" onClick={() => setEdit(null)}>Cancelar</button></Modal>}
  </>;
}

function Roles({ person }: { person: Session }) {
  const list = useData<Role[]>("/roles");
  const [edit, setEdit] = useState<Role | "new" | null>(null);
  const current = edit === "new" ? null : edit;
  async function save(form: FormData) {
    await write(current ? `/roles/${encoded(current.key)}` : "/roles", current ? "PUT" : "POST",
      { key: input(form, "key"), name: input(form, "name"), permissions: form.getAll("permission") });
    await list.refetch();
  }
  return <>
    <Title context="Configuración" title="Roles y permisos" help="Los roles base son fijos. Podés definir permisos para un rol personalizado."
      action={<button className="button primary" onClick={() => setEdit("new")}>Crear rol</button>} />
    {list.isPending ? <Loading /> : list.isError ? <Failure error={list.error} retry={() => void list.refetch()} /> : <>
      <Bars title="Tipos de rol" caption="Roles base y personalizados configurados."
        bars={[ { label: "Base", value: list.data.filter(r => r.builtIn).length },
          { label: "Personalizados", value: list.data.filter(r => !r.builtIn).length } ]} />
      <Table title="Roles de Cifra Firme" headers={["Rol", "Tipo", "Permisos"]}>{list.data.map(r => <tr key={r.key}>
        <th scope="row">{r.builtIn ? r.name : <button className="row-link" onClick={() => setEdit(r)}>{r.name}</button>}</th>
        <td><Badge>{r.builtIn ? "Base" : "Personalizado"}</Badge></td><td>{r.permissions.map(key => permissions[key]).join(" · ") || "Sin permisos"}</td></tr>)}</Table></>}
    {edit && <Editor title={current ? current.name : "Nuevo rol"} close={() => setEdit(null)} save={save}
      remove={current ? async () => { await write(`/roles/${encoded(current.key)}`, "DELETE"); await list.refetch(); } : undefined}>
      {!current && <label>Clave<input name="key" minLength={2} maxLength={32} required /></label>}
      <label>Nombre del rol<input name="name" defaultValue={current?.name} maxLength={80} required /></label>
      <fieldset><legend>Permisos</legend>{person.permissions.map(p =>
        <label className="check" key={p}><input type="checkbox" name="permission" value={p} defaultChecked={current?.permissions.includes(p)} />{permissions[p]}</label>)}</fieldset>
    </Editor>}
  </>;
}

function Kanban({ person }: { person: Session }) {
  const all = person.permissions.includes("activityManage");
  const peopleManage = person.permissions.includes("peopleManage");
  const list = useData<Activity[]>("/activities");
  const people = useData<Person[]>("/people?size=150", all && peopleManage);
  const next = useData<Person[]>("/people?page=2&size=150", all && peopleManage && people.data?.length === 150);
  const [edit, setEdit] = useState<Activity | "new" | null>(null);
  const current = edit === "new" ? null : edit;
  const names = new Map([...(people.data ?? []), ...(next.data ?? [])].map(p => [p.id, p] as const));
  const lanes = (Object.keys(laneLabels) as TaskState[]).map(key => ({
    key, label: laneLabels[key], tasks: (list.data ?? []).filter(task => task.status === key)
  }));
  async function save(form: FormData) {
    await write(current ? `/activities/${current.id}` : "/activities", current ? "PUT" : "POST", {
      title: input(form, "title"), status: input(form, "status"), assigneeId: input(form, "assigneeId") || current?.assigneeId || person.id
    });
    await list.refetch();
  }
  return <>
    <Title context="Seguimiento del equipo" title={all ? "Actividades" : "Mis actividades"}
      help="El tablero organiza tareas. Los cambios de estado no alteran la nómina."
      action={all && <button className="button primary" onClick={() => setEdit("new")}>Crear actividad</button>} />
    {list.isPending ? <Loading /> : list.isError ? <Failure error={list.error} retry={() => void list.refetch()} /> : <>
      <Bars title="Actividades por estado" caption={all ? "Actividades registradas en el tablero." : "Solo actividades asignadas a tu cuenta."}
        bars={lanes.map(({ label, tasks }) => ({ label, value: tasks.length }))} />
      <div className="kanban">{lanes.map(({ key, label, tasks }) =>
        <section key={key} className="lane"><header><h2>{label}</h2><Badge>{tasks.length}</Badge></header>
          {tasks.length ? tasks.map(task => <article className="task" key={task.id}>
            {all ? <button onClick={() => setEdit(task)} className="row-link">{task.title}</button> : <strong>{task.title}</strong>}
            <p>{task.assigneeId === person.id ? "Asignada a vos" : names.has(task.assigneeId) ? nameOf(names.get(task.assigneeId)!) : "Asignación registrada"}</p>
          </article>) : <Empty>No hay actividades en esta columna.</Empty>}</section>
      )}</div></>}
    {edit && <Editor title={current ? "Editar actividad" : "Nueva actividad"} close={() => setEdit(null)} save={save}
      remove={current ? async () => { await write(`/activities/${current.id}`, "DELETE"); await list.refetch(); } : undefined}>
      <label>Título<input name="title" defaultValue={current?.title} required maxLength={160} /></label>
      <label>Estado<select name="status" defaultValue={current?.status ?? "pending"}>
        {Object.entries(laneLabels).map(([key, text]) => <option key={key} value={key}>{text}</option>)}</select></label>
      {peopleManage && names.size > 0 && <label>Responsable<select name="assigneeId" defaultValue={current?.assigneeId ?? person.id}>
        {[...names.values()].filter(p => p.active || p.id === current?.assigneeId).map(p => <option key={p.id} value={p.id}>{nameOf(p)}</option>)}</select></label>}
    </Editor>}
  </>;
}

type Props = { route: string; month: string; setMonth: (month: string) => void; person: Session };
export default function Operations(props: Props) {
  if (props.route === "novedades") return <Attendance {...props} />;
  if (props.route === "personal") return <Directory />;
  if (props.route === "roles") return <Roles person={props.person} />;
  return <Kanban person={props.person} />;
}
