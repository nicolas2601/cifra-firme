import { lazy, Suspense, useEffect, useRef, useState, useSyncExternalStore, type FormEvent } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, write, type Permission, type Receipt, type Session } from "./api";
import { Badge, Brand, demo, Failure, Icon, Loading, monthToday, ReceiptPaper, Title } from "./ui";
import "./styles.css";

const PayrollPages = lazy(() => import("./payroll"));
const Operations = lazy(() => import("./operations"));
const queryClient = new QueryClient({ defaultOptions: { queries: {
  retry: false, refetchOnWindowFocus: false, staleTime: 0, gcTime: 60_000
} } });
const sessionKey = ["session"];
function dropSession(client: QueryClient) {
  // Keep the active session observer; purge only application data from the outgoing identity.
  client.setQueryData(sessionKey, null);
  client.removeQueries({ predicate: query => query.queryKey[0] !== sessionKey[0] });
  window.history.replaceState(null, "", window.location.pathname);
}

const roleNames: Record<string, string> = { admin: "Administración", lead: "Jefatura", worker: "Trabajador", accountant: "Contabilidad" };
const links = [
  ["resumen", "Resumen", "home", ["payrollClose"]],
  ["novedades", "Novedades", "calendar", ["eventsAll", "eventsCrew"]],
  ["nomina", "Nómina", "doc", ["payrollClose"]],
  ["historico", "Histórico", "lock", ["payrollReadAll"]],
  ["comprobantes", "Mis comprobantes", "doc", ["payrollReadOwn"]],
  ["personal", "Personal", "people", ["peopleManage"]],
  ["roles", "Roles", "lock", ["rolesManage"]],
  ["actividades", "Actividades", "grid", ["activityManage", "activityReadOwn"]]
] as const;
const subscribe = (callback: () => void) => {
  window.addEventListener("hashchange", callback);
  return () => window.removeEventListener("hashchange", callback);
};
const currentHash = () => window.location.hash;

const sample: Receipt = {
  personId: "", displayName: "Ana Rivera", period: "2026-04-01", monthlySalary: 1_800_000, total: 1_695_000,
  rulesVersion: "demo-v1", absenceAdjustment: -120_000, medicalLeaveAdjustment: -60_000, additionalDayAdjustment: 75_000,
  events: (["absence", "absence", "medicalLeave", "medicalLeave", "medicalLeave", "additionalDay"] as const)
    .map((kind, i) => ({ date: `2026-04-0${i + 2}`, kind }))
};

const demoProfiles = [
  ["Administración", "admin", "Revisá novedades, cerrá el mes y consultá el histórico.", "home"],
  ["Jefatura", "demo-jefa", "Registrá novedades de las personas de tu cuadrilla.", "calendar"],
  ["Trabajador", "demo-ana", "Consultá únicamente tus comprobantes cerrados.", "doc"],
  ["Contabilidad", "demo-conta", "Consultá cierres y descargá su CSV.", "lock"]
];

function Login({ signed, expired }: { signed: (person: Session) => void; expired: boolean }) {
  const [profile, setProfile] = useState("");
  const [showDemo, setShowDemo] = useState(false);
  const [visible, setVisible] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (busy) return;
    const fields = new FormData(e.currentTarget);
    setFailure(null);
    setBusy(true);
    try {
      const person = await write<Session>("/auth/login", "POST", {
        username: String(fields.get("username")).trim(), password: fields.get("password")
      });
      signed(person);
    } catch (error) {
      setFailure(error);
    } finally { setBusy(false); }
  }
  if (showDemo) return <main className="demo-picker">
    <div><Brand variant="mark-nav" /><h1>Probá Cifra Firme<br />desde cada rol.</h1>
      <p>Son cuentas ficticias. Elegí un perfil e ingresá la contraseña configurada para la demo.</p>
      <button className="button outline" onClick={() => setShowDemo(false)}>← Volver al acceso</button></div>
    <div className="profile-list">{demoProfiles.map(([name, user, desc, icon]) =>
      <button className="profile-option" key={user} onClick={() => { setProfile(user); setShowDemo(false); }}>
        <Icon name={icon} /><span><strong>{name}</strong><small>{desc}</small></span><span aria-hidden="true">→</span>
      </button>
    )}</div>
  </main>;
  return <main className="login">
    <section className="login-panel">
      <header><Brand /></header>
      <div className="login-content">
        <h1>Ingresá a tu cuenta</h1>
        <p>Consultá tu nómina o gestioná el período según tu perfil.</p>
        {expired && <p className="login-alert" role="status">Tu sesión venció. Volvé a ingresar para continuar.</p>}
        <form onSubmit={submit} aria-busy={busy}>
          <label>Usuario<input name="username" autoComplete="username" autoCapitalize="none" spellCheck={false}
            maxLength={48} placeholder="Ej. ana.rivera" defaultValue={profile} required /></label>
          <div className="labeled-field"><label htmlFor="login-password">Contraseña</label><span className="password-field">
            <input id="login-password" name="password" type={visible ? "text" : "password"} autoComplete="current-password" maxLength={128}
              placeholder="Ingresá tu contraseña" aria-describedby={failure ? "login-error" : undefined} required />
            <button type="button" onClick={() => setVisible(v => !v)} aria-label={visible ? "Ocultar contraseña" : "Mostrar contraseña"}>
              {visible ? "Ocultar" : "Mostrar"}
            </button>
          </span></div>
          <div className="login-feedback">{!!failure && <div id="login-error"><Failure error={failure} /></div>}</div>
          <button className="button primary login-submit" disabled={busy}><Icon name="go-light" />{busy ? "Ingresando…" : "Iniciar sesión"}</button>
          {demo && <button type="button" className="button outline login-submit" onClick={() => setShowDemo(true)}>Explorar con perfiles demo</button>}
        </form>
      </div>
      <footer>Uso interno{demo ? " · Datos ficticios de demostración" : " · Acceso autorizado"}</footer>
    </section>
    <aside className="login-art" aria-label="Ejemplo de comprobante de Cifra Firme">
      <div className="art-heading"><p className="eyebrow">Abril 2026 &nbsp; / &nbsp; Nómina de demostración</p><p className="art-title">Cada cifra<br />tiene respaldo.</p>
        <p>Del registro de una novedad al comprobante que queda.</p></div>
      <div className="art-deck" aria-hidden="true">
        <div className="ledger"><Brand variant="mark-ledger" /><p>Registro mensual</p><span className="eyebrow">Operaciones / Abril</span><div className="ledger-lines" /></div>
        <div className="art-receipt"><ReceiptPaper receipt={sample} closed artwork /></div>
      </div>
      <div className="art-foot"><Badge tone="gold">Cierre de muestra</Badge><p className="eyebrow">01 &nbsp; / &nbsp; Archivo de nómina</p></div>
    </aside>
  </main>;
}

function Workspace({ person, signout }: { person: Session; signout: () => Promise<void> }) {
  const hash = useSyncExternalStore(subscribe, currentHash);
  const [requested, id] = hash.replace(/^#\/?/, "").split("/");
  const allowed = links.filter(item => item[3].some(p => person.permissions.includes(p as Permission))
    && !(item[0] === "comprobantes" && person.permissions.includes("payrollReadAll")));
  const route = allowed.find(item => item[0] === requested)?.[0] ?? (requested ? null : allowed[0]?.[0]);
  const [month, setMonth] = useState(monthToday);
  const [mobileNav, setMobileNav] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [working, setWorking] = useState(false);
  const isPayroll = route === "resumen" || route === "nomina" || route === "historico" || route === "comprobantes";
  const role = roleNames[person.role] ?? person.role;
  useEffect(() => {
    if (hash) document.getElementById("contenido")?.focus();
  }, [hash]);
  async function exit() {
    setError(null);
    setWorking(true);
    try { await signout(); } catch (e) { setError(e); } finally { setWorking(false); }
  }
  return <div className="workspace">
    <a className="skip" href="#contenido" onClick={e => { e.preventDefault(); document.getElementById("contenido")?.focus(); }}>Ir al contenido</a>
    <aside className="sidebar">
      <div className="sidebar-head"><Brand variant="mark-nav" />
        <button className="mobile-menu button" type="button" aria-expanded={mobileNav} aria-controls="area-nav"
          onClick={() => setMobileNav(v => !v)}>{mobileNav ? "Cerrar menú" : "Menú"}</button>
      </div>
      <div id="area-nav" className={`side-content ${mobileNav ? "expanded" : ""}`}>
        <p className="eyebrow side-caption">Operación de nómina</p>
        <nav aria-label="Navegación principal">{allowed.map(([key, label, icon]) =>
          <a key={key} aria-current={route === key ? "page" : undefined} href={`#/${key}`} onClick={() => setMobileNav(false)}>
            <Icon name={icon} />{key === "historico" && person.role === "accountant" ? "Períodos cerrados" :
              key === "novedades" && !person.permissions.includes("eventsAll") ? "Mi cuadrilla" : label}
          </a>)}</nav>
        <div className="side-footer">
          <button onClick={exit} disabled={working}><Icon name="exit" />{working ? "Cerrando…" : "Cerrar sesión"}</button>
        </div>
      </div>
    </aside>
    <div className="workspace-body">
      <header className="topbar"><span>Espacio de trabajo <span className="subtle">/</span> {role}</span>
        <div>{demo && <Badge>Demo · Datos ficticios</Badge>}<span className="avatar" aria-label={`${person.firstName} ${person.lastName}`}>{person.firstName[0]}{person.lastName[0]}</span><span className="role-name">{role}</span></div>
      </header>
      <main id="contenido" className="workspace-main" tabIndex={-1}>
        {!!error && <Failure error={error} />}
        <Suspense fallback={<Loading />}>
          {isPayroll ? <PayrollPages key={route} route={route} id={id} month={month} setMonth={setMonth} person={person} /> :
            route ? <Operations key={route} route={route} month={month} setMonth={setMonth} person={person} /> :
              <Title context="Cifra Firme" title={requested ? "Sección no disponible" : "Sin secciones asignadas"}
                help="Usá el menú para elegir una sección disponible para tu perfil." />}
        </Suspense>
      </main>
    </div>
  </div>;
}

function App() {
  const client = useQueryClient();
  const [expired, setExpired] = useState(false);
  const signingOut = useRef(false);
  const session = useQuery({ queryKey: sessionKey, queryFn: async ({ signal }) => {
    try { return await api<Session>("/auth/me", signal); }
    catch (e) { if (e instanceof ApiError && e.status === 401) return null; throw e; }
  }, staleTime: Infinity });
  useEffect(() => {
    function expire() {
      if (signingOut.current || !client.getQueryData(sessionKey)) return;
      setExpired(true);
      dropSession(client);
    }
    window.addEventListener("cifra:expired", expire);
    return () => window.removeEventListener("cifra:expired", expire);
  }, [client]);
  if (session.isPending) return <main className="boot"><Brand /><Loading /></main>;
  if (session.isError) return <main className="boot"><Brand /><Failure error={session.error} retry={() => void session.refetch()} /></main>;
  if (!session.data) return <Login expired={expired} signed={person => {
    window.history.replaceState(null, "", window.location.pathname);
    setExpired(false);
    client.setQueryData(sessionKey, person);
  }} />;
  return <Workspace key={session.data.id} person={session.data} signout={async () => {
    signingOut.current = true;
    try {
      await client.cancelQueries();
      await write("/auth/logout", "POST").catch(error => {
        if (!(error instanceof ApiError && error.status === 401)) throw error;
      });
      setExpired(false);
      dropSession(client);
    } finally { signingOut.current = false; }
  }} />;
}

createRoot(document.getElementById("root")!).render(<QueryClientProvider client={queryClient}><App /></QueryClientProvider>);
