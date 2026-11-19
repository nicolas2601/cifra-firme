# Desplegar Cifra Firme en Somee

Este procedimiento instala la aplicación en **Windows/IIS + SQL Server**. Es distinto de la demo local SQLite. No publica secretos ni requiere Node.js en el servidor. Usá un sitio y una base desechables si vas a cargar datos ficticios.

## 1. Crear el sitio y la base

1. Creá el sitio Windows en el panel de Somee y verificá que el plan y servidor admitan **ASP.NET Core 10**. Confirmá también HTTPS para el dominio asignado. [Planes](https://somee.com/HostingPlans.aspx).
2. En **MS SQL**, creá una base vacía y guardá sus datos de conexión de forma privada. No subas un archivo `.db` de SQLite. [Conexión SQL Server](https://www.somee.com/doka/Help/Article/103).
3. Desde tu equipo, generá el script idempotente. Pasá una cadena ficticia a la *herramienta de diseño* para seleccionar SQL Server; el script no se conecta a esa dirección.

   ```bash
   dotnet tool install --global dotnet-ef --version 10.0.15
   mkdir -p .local
   Database__Provider=SqlServer \
     ConnectionStrings__Payroll='Server=localhost;Database=CifraFirme;Integrated Security=true' \
     dotnet ef migrations script --idempotent \
     --project src/Reference.Api --startup-project src/Reference.Api \
     --output .local/cifra-esquema.sql
   ```

4. Aplicá el script a **la base asignada**, usando SSMS o **Run scripts** del panel. Revisá las tablas y `__EFMigrationsHistory` antes de arrancar. No ejecutes el script contra una base que contenga información real. [Despliegue de la base](https://somee.com/DOKA/Help/Article/72/Deploy_MS_SQL_database).

## 2. Publicar la aplicación para IIS

```bash
npm --prefix frontend ci
VITE_DEMO=false npm --prefix frontend run build
dotnet publish src/Reference.Api -c Release -r win-x64 --no-self-contained \
  -p:AspNetCoreHostingModel=OutOfProcess -o .local/somee
```

Comprobá que estén `web.config`, `Reference.Api.dll` y `wwwroot/index.html` en la carpeta publicada. El sitio de demo se verificó en IIS en modo `outofprocess`; el modo `inprocess` devolvió `500.30` en ese servidor. Esta publicación x64 requiere el runtime de ASP.NET Core 10 de Somee. Si tu sitio usa otra arquitectura o no tiene el runtime, verificá la arquitectura y la cuota antes de cambiar la publicación. [Modos de despliegue de Somee](https://www.somee.com/DOKA/Help/Article/214/How_should_I_deploy_ASP.NET_Core_applications%3F_(Deployment_Modes)).

En la configuración privada del sitio prepará:

| Configuración | Valor |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Database__Provider` | `SqlServer` |
| `ConnectionStrings__Payroll` | Cadena real de Somee, con TLS validado y acceso a la base asignada. |
| `Database__Initialize` | `false`, porque aplicaste el SQL antes. |
| `Demo__Seed` | `false`. El seeder completo no se ejecuta en hosting. |
| `Bootstrap__Username` / `Bootstrap__Password` | Solo durante el primer arranque. Elegí una contraseña exclusiva para el sitio. |

Si la conexión SQL informa que el nombre del certificado no coincide, verificá con el proveedor el nombre DNS que figura en el certificado del servidor SQL. Cuando el alias de la base difiere de ese nombre, configurá `HostNameInCertificate` con el nombre verificado y conservá `Encrypt=True;TrustServerCertificate=False`. No uses el nombre de otro servidor.

Si tu panel no ofrece variables, agregalas en el `web.config` **de la publicación local**, dentro del elemento `<aspNetCore>` ya generado. Convertí su cierre `/>` en un par de etiquetas si hace falta. Los valores de ejemplo no sirven como credenciales; escapá caracteres especiales de XML (por ejemplo, `&amp;`). No hagas commit de esa copia.

```xml
<environmentVariables>
  <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
  <environmentVariable name="Database__Provider" value="SqlServer" />
  <environmentVariable name="ConnectionStrings__Payroll" value="REEMPLAZAR_EN_LA_COPIA_PRIVADA" />
  <environmentVariable name="Bootstrap__Username" value="admin" />
  <environmentVariable name="Bootstrap__Password" value="REEMPLAZAR_SOLO_PRIMER_ARRANQUE" />
</environmentVariables>
```

Subí **el contenido** de `.local/somee` con File Manager o FTPS. `web.config` y `Reference.Api.dll` van en la raíz del sitio, no dentro de `wwwroot`. Para actualizar, respaldá primero los archivos y la base, y poné el sitio en mantenimiento mientras reemplazás binarios. [Instrucciones IIS de Somee](https://somee.com/DOKA/Help/Article/203/Deploy_ASP.Net_Core_application).

## 3. Verificar y cargar datos ficticios

1. Abrí `https://TU-SITIO/health/ready`: debe devolver `{"status":"ready"}`. Entrá al sitio como administrador. Luego quitá los dos valores `Bootstrap__*` de la configuración privada y reciclá el sitio; la cuenta ya quedó guardada.
2. En una **base destinada solamente a la demo**, podés cargar una muestra reducida desde tu equipo, con Python 3:

   ```bash
   python3 scripts/seed_demo.py --url https://TU-SITIO --month 2026-04
   ```

   Crea dos personas con salario, dos perfiles de operación, las novedades de Ana y una actividad. No cierra el período ni es repetible sobre la misma base. Si falla, puede dejar datos parciales. Para probar el histórico, cerrá marzo desde la web; después mantené abril abierto hasta que quieras probar el cierre.
3. Verificá acceso HTTPS, nómina y comprobante, CSV, una denegación por rol y acceso tras reiniciar. No compartas la clave de administración con visitantes. Si las sesiones se pierden al reciclar IIS, confirmá con Somee cómo persistir las claves de Data Protection en un directorio privado y protegido; no uses `wwwroot`.

**Si falla:** `503` en `/health/ready` indica que la verificación de la base no pasó: comprobá conexión, permisos y migraciones. Para 500/502 de arranque revisá el runtime, `web.config` y los logs privados del panel. Si no carga la web, revisá `wwwroot/index.html` y sus archivos estáticos. No publiques logs ni cadenas de conexión.

**Verificación real:** la [demo de Cifra Firme](https://cifrafirme.somee.com/) está desplegada en el plan gratuito. Si el panel indica que el script SQL terminó, comprobá también `__EFMigrationsHistory` en la base asignada antes de arrancar. Para actualizar binarios bloqueados por IIS, usá `app_offline.htm` durante la copia y retiralo al terminar.
