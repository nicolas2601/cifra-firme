export type Permission = "peopleManage" | "rolesManage" | "eventsAll" | "eventsCrew" | "payrollClose" |
  "payrollReadAll" | "payrollReadOwn" | "payrollExport" | "activityManage" | "activityReadOwn";

export type Session = { id: string; firstName: string; lastName: string; username: string; role: string; crew: string | null; permissions: Permission[] };
export type Person = Omit<Session, "permissions"> & { document: string; salary: number; active: boolean; addresses: string[]; phones: string[] };
export type CrewPerson = Pick<Person, "id" | "firstName" | "lastName" | "crew">;
export type Role = { key: string; name: string; builtIn: boolean; permissions: Permission[] };
export type Kind = "worked" | "medicalLeave" | "absence" | "additionalDay";
export type WorkEvent = { date: string; kind: Kind };
export type Receipt = {
  personId: string; displayName: string; period: string; monthlySalary: number; total: number; rulesVersion: string;
  absenceAdjustment: number; medicalLeaveAdjustment: number; additionalDayAdjustment: number;
  events: WorkEvent[];
};
export type Preview = { period: string; total: number; revision: number; people: Receipt[]; blockedPeople: string[] };
export type Summary = { period: string; total: number; peopleCount: number; revision: number; rulesVersion: string; closedAt: string };
export type Closure = { summary: Summary; people: Receipt[] };
export type HistoryRow = { period: string; rulesVersion: string; closedAt?: string; total?: number };
export type TaskState = "pending" | "inProgress" | "finished";
export type Activity = { id: string; title: string; assigneeId: string; status: TaskState };

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

async function request(path: string, options: RequestInit = {}) {
  let response: Response;
  try {
    response = await fetch(`/api${path}`, { ...options, credentials: "same-origin", cache: "no-store" });
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    throw new ApiError(0, "No pudimos conectar. Revisá tu conexión e intentá de nuevo.");
  }
  if (response.ok) return response;
  if (response.status === 401 && !["/auth/login", "/auth/me"].includes(path)) window.dispatchEvent(new Event("cifra:expired"));
  const body = await response.json().catch(() => null);
  const messages: Record<number, string> = {
    400: "Revisá los datos e intentá de nuevo.", 401: "Revisá tu usuario y contraseña.",
    403: "Tu perfil no tiene permiso para esta acción.", 404: "No encontramos este registro.",
    409: "Los datos cambiaron. Volvé a revisar.", 429: "Demasiados intentos. Esperá un minuto y volvé a probar."
  };
  throw new ApiError(response.status, response.status < 500 && typeof body?.detail === "string" ? body.detail :
    messages[response.status] ?? "No pudimos completar la operación. Intentá de nuevo.");
}

export async function api<T>(path: string, signal?: AbortSignal): Promise<T> {
  return (await request(path, { signal })).json();
}

export async function write<T = void>(path: string, method: "POST" | "PUT" | "DELETE", data?: unknown): Promise<T> {
  // Fetch for each write: the token is scoped to the current authenticated identity.
  const { token } = await api<{ token: string }>("/auth/csrf");
  const response = await request(path, {
    method, headers: { "X-CSRF-Token": token, ...(data === undefined ? {} : { "Content-Type": "application/json" }) },
    body: data === undefined ? undefined : JSON.stringify(data)
  });
  return response.status === 204 ? (undefined as T) : response.json();
}

export async function downloadCsv(month: string) {
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) throw new Error("Período inválido.");
  const file = await (await request(`/payroll/${month}/export`)).blob();
  const url = URL.createObjectURL(file);
  const link = document.createElement("a");
  link.href = url;
  link.download = `nomina-${month}.csv`;
  document.body.append(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 5000);
}
