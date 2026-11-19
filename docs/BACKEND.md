# Backend · desarrollo y verificación

Servicio REST ASP.NET Core .NET 10 y cálculo aislado en C#. SQLite es solo para desarrollo y pruebas locales. SQL Server es el proveedor de despliegue. Las reglas son ficticias; no usar esta demo para pagar nómina real.

## Ejecutar localmente

Con el SDK .NET 10 en el PATH, en la raíz:

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet dev-certs https
read -rsp 'Contraseña inicial (12–128 caracteres): ' Bootstrap__Password
export Bootstrap__Password
ASPNETCORE_ENVIRONMENT=Development Database__Initialize=true Bootstrap__Username=admin \
  dotnet run --project src/Reference.Api --no-launch-profile --urls https://localhost:5443
```

`Database__Initialize=true` crea el esquema local. El bootstrap crea una sola cuenta administradora si aún no existe; no sobrescribe la contraseña. Retirar `Bootstrap__Password` después de inicializar. No usar `curl -k` ni exponer el certificado privado. Para la demo, exportar solo la parte pública del certificado de desarrollo (sin clave privada) y pasar `--ca-file`:

```bash
dotnet dev-certs https --export-path /tmp/payroll-local.crt --format Pem
python3 scripts/seed_demo.py --url https://localhost:5443 --month 2026-04 --ca-file /tmp/payroll-local.crt
```

La carga por API pregunta contraseñas por terminal, crea cuatro cuentas ficticias, dos salarios, novedades y una actividad. Úsala una sola vez sobre una base limpia y un mes abierto; no ejecuta el cierre. Si falla entre pasos, pueden quedar datos parciales. [Contrato REST](API.md) · [Reglas](REGLAS-NOMINA.md).

### Demo completa de Cifra Firme

Desde la raíz, con Node.js 24+ y .NET 10:

```bash
npm --prefix frontend ci
VITE_DEMO=true npm --prefix frontend run build
dotnet publish src/Reference.Api -c Release -o /tmp/cifra-firme-demo
dotnet dev-certs https
read -rsp 'Contraseña local desechable (mínimo 12 caracteres): ' Demo__Password
export Demo__Password
export Bootstrap__Password="$Demo__Password"
(
  cd /tmp/cifra-firme-demo
  ASPNETCORE_ENVIRONMENT=Development Database__Initialize=true \
    Bootstrap__Username=admin Demo__Seed=true \
    ConnectionStrings__Payroll='Data Source=cifra-demo.db' \
    dotnet Reference.Api.dll --urls https://localhost:5443
)
unset Demo__Password Bootstrap__Password
```

Abrir `https://localhost:5443`. Entrar como `admin`, `demo-jefa`, `demo-ana` o `demo-conta` con la contraseña local configurada. El selector de perfiles solo completa el usuario.

El seeder es transaccional e idempotente: solo corre en **Development + SQLite**, sobre una base nueva más el administrador inicial. Carga 40 personas liquidables por defecto (`Demo__Workers=4` a `150`), cuatro cuadrillas cuando el tamaño lo permite, roles base y uno personalizado, una cuenta inactiva, novedades, tres estados de Kanban, febrero y marzo cerrados y abril de 2026 abierto. Volver a iniciarlo no sobrescribe cambios; para repetir la demo inicial, indicar otro archivo SQLite. No habilitar `VITE_DEMO` al compilar para producción.

Con `playwright-cli` y Chromium instalados, `node scripts/test_frontend.mjs` verifica la web publicada contra una base desechable. Acepta `DOTNET_BIN`, `CIFRA_TEST_PORT` y `CIFRA_SHOTS` para ruta del SDK, puerto y carpeta de capturas. El navegador admite el certificado de desarrollo solo dentro del contexto local de prueba.

## Decisiones y garantías

- **API modular en un proceso:** ASP.NET Core, EF Core y una biblioteca de cálculo pura. No se necesita un servicio separado ni una cola para 40 personas. SQLite acelera la iteración, pero no reemplaza SQL Server en las pruebas de compatibilidad y concurrencia.
- **Duplicados y cierre:** clave primaria `(PersonId, Date)` para novedades y clave del mes para cierres. Las transacciones toman primero un bloqueo de escritura sobre una fila técnica (`Clock`), cambiando `Gate`. Eso ordena las escrituras; la vista previa toma el mismo bloqueo para leer un conjunto consistente. `Revision` cambia solo al editar insumos (es global, así que un cambio de otro mes también invalida una revisión). El servicio compara esa revisión y escribe cierre, recibos y auditoría juntos. Si sube la contención, medir primero y separar el reloj por período.
- **Histórico:** el recibo se lee de la persistencia sin recalcular. No hay rutas para modificarlo o eliminarlo. La garantía validada es de la aplicación; un administrador directo de la base tiene otros privilegios.
- **Acceso:** `PasswordHasher`, cookies protegidas, protección CSRF, límite de intentos por IP y revisión de cuenta/rol en cada solicitud autenticada. Las mutaciones vuelven a comprobar permisos dentro de la transacción. Borrar personal significa desactivar y revocar sus sesiones.
- **Límites deliberados:** cuadrilla y salario actuales, sin prorrateo, sin reapertura. La jefatura registra novedades, no consulta sueldos. El sistema solo calcula y conserva un cierre; no transfiere dinero.

## SQL Server y Somee

Seguí la [guía Somee](SOMEE.md) para crear la base, generar el script SQL, publicar la app detrás de IIS y configurar secretos.

La CI incluye integración con SQL Server en Docker. Localmente, ejecutá `TEST_SQLSERVER="<conexión de un servidor desechable>" dotnet test tests/Reference.Api.Tests`. El fixture crea una base distinta por caso; nunca le des acceso a producción.
