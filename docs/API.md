# API de nómina

Base URL `/api`, JSON camelCase, COP en pesos enteros, fechas `AAAA-MM-DD` y períodos `AAAA-MM`. Ejemplos y ejecuciones usan datos ficticios. No hay registro público.

## Sesión y errores

1. `GET /auth/csrf` devuelve `{ "token": "…" }` y una cookie. Enviar el valor en `X-CSRF-Token` con cada `POST`, `PUT` y `DELETE`.
2. `POST /auth/login` recibe `{ "username": "…", "password": "…" }`. Envía una cookie de sesión `HttpOnly`, `Secure`, `SameSite=Strict`; el cuerpo devuelve perfil mínimo y `permissions`, nunca la contraseña.
3. Volver a pedir `GET /auth/csrf` después del login para asociar el token a la identidad nueva. Conservar ambas cookies; navegador y API deben compartir origen.
4. `GET /auth/me` comprueba sesión y devuelve permisos actuales. `POST /auth/logout` limpia la cookie.

`400` indica entrada o CSRF inválidos; `401` sesión faltante/inválida; `403` permiso o alcance insuficiente; `404` recurso ausente; `409` conflicto de negocio; `429` demasiados intentos de acceso. Los errores de negocio incluyen `detail` en Problem Details.

## Rutas

| Método y ruta | Datos y acceso |
|---|---|
| `GET /people?page=1&size=50`, `GET /people/{id}` | Administración; `size` entre 1 y 150. |
| `POST /people`, `PUT /people/{id}` | Administración. Campos: `username`, `firstName`, `lastName`, `document`, `role`, `crew` (opcional salvo alcance de cuadrilla), `salary`, `active`, `addresses`, `phones`, `password` (obligatorio para crear, opcional para editar). |
| `DELETE /people/{id}` | Desactiva y revoca sesión; no borra datos. El último administrador activo no se puede desactivar o reasignar. |
| `GET /roles`, `GET /roles/{key}` | Administración de personas o roles. |
| `POST /roles`, `PUT /roles/{key}`, `DELETE /roles/{key}` | Administración de roles. Crear: `key`, `name`, `permissions`. Editar: `name`, `permissions`. Roles base fijos; un rol asignado no se elimina. |
| `GET /crew/people` | Administración: personas activas liquidables; jefatura: solo su cuadrilla. No devuelve salarios ni documentos. |
| `GET /people/{id}/events?month=AAAA-MM`, `GET /people/{id}/events/{date}` | Administración o jefatura dentro de su cuadrilla. |
| `POST /people/{id}/events`, `PUT /people/{id}/events/{date}`, `DELETE /people/{id}/events/{date}` | Escribir `date`, `kind` (`worked`, `medicalLeave`, `absence`, `additionalDay`). PUT puede omitir `date` si coincide con la ruta. No permite duplicar el día ni modificar un período cerrado. |
| `GET /payroll/{month}/preview` | Administración. Devuelve `revision`, `total`, recibos previstos (`people`) y una lista de personas bloqueantes (`blockedPeople`). No persiste la vista previa ni cierra el mes. |
| `POST /payroll/{month}/close` | Administración. Enviar `{ "revision": … }` de la última revisión. Cierra con `201`; reintento con la misma revisión, `200`; datos nuevos, período ya cerrado con otra revisión o conflictos, `409`. |
| `GET /payroll`, `GET /payroll/{month}` | Administración y contabilidad consultan el cierre completo. El listado incluye el `total` de los recibos guardados por período; para trabajadores incluye solo el `total` de su propio recibo. Listado limitado a 60 períodos recientes. |
| `GET /payroll/{month}/people/{id}` | Administración/contabilidad o trabajador dueño del recibo. Devuelve salario congelado, conceptos, total, reglas y las novedades que se usaron. |
| `GET /payroll/{month}/export` | Administración y contabilidad. CSV del histórico, una fila por persona y una de total. |
| `GET /activities`, `GET /activities/{id}` | Administración: todas; trabajador/jefatura: asignadas a la cuenta. |
| `POST /activities`, `PUT /activities/{id}`, `DELETE /activities/{id}` | Requiere `activityManage`. Campos de escritura: `title`, `assigneeId`, `status` (`pending`, `inProgress`, `finished`). No modifica la nómina. |

Roles base: `admin`, `lead`, `worker` y `accountant`. Permisos personalizables: `peopleManage`, `rolesManage`, `eventsAll`, `eventsCrew`, `payrollClose`, `payrollReadAll`, `payrollReadOwn`, `payrollExport`, `activityManage`, `activityReadOwn`. Un rol delegado no puede conceder permisos que no tenga.

`GET /health/live` y `GET /health/ready` no requieren sesión y están fuera de `/api`. El segundo verifica conexión y esquema mínimo; no devuelve información de la conexión.
