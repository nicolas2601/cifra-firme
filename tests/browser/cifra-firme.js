async (initialPage, { origin, password, shots } = {}) => {
    if (!password || !/^https:\/\/localhost:\d+$/.test(origin ?? "")) throw new Error("Usá una instancia HTTPS local y una credencial desechable.");
    const context = await initialPage.context().browser().newContext({ ignoreHTTPSErrors: true, acceptDownloads: true });
    const page = await context.newPage();
    page.setDefaultTimeout(6500);
    const requests = [];
    page.on("response", res => {
        const path = res.url().slice(origin.length).split("?")[0];
        if (path.startsWith("/api/")) requests.push(`${res.request().method()} ${path} ${res.status()}`);
    });
    await page.setViewportSize({ width: 1440, height: 900 });
    const heading = (name) => page.getByRole("heading", { name, exact: true }).first();
    const button = (name) => page.getByRole("button", { name, exact: true });
    const link = (name) => page.getByRole("link", { name, exact: true }).first();
    const text = (value) => page.getByText(value, { exact: true }).first();
    const screenshot = async (name) => {
        if (shots) await page.screenshot({ path: `${shots}/${name}.png`, fullPage: true, animations: "disabled" });
    };
    const signin = async (user) => {
        await page.getByLabel("Usuario", { exact: true }).fill(user);
        await page.getByLabel("Contraseña", { exact: true }).fill(password);
        await button("Iniciar sesión").click();
    };
    const logout = async () => {
        if (await button("Menú").isVisible()) await button("Menú").click();
        await button("Cerrar sesión").click();
        await heading("Ingresá a tu cuenta").waitFor();
        if (await page.getByText("Tu sesión venció.", { exact: false }).isVisible())
            throw new Error("El cierre voluntario de sesión no debe mostrarse como expiración.");
    };
    try {
    await page.goto(origin, { waitUntil: "networkidle" });
    await heading("Ingresá a tu cuenta").waitFor({ timeout: 4000 });
    if (await page.locator(".art-receipt").evaluate(el => getComputedStyle(el).animationName) !== "arrive-receipt")
        throw new Error("No está aplicada la entrada del comprobante.");
    await screenshot("01-acceso");
    await page.getByLabel("Usuario", { exact: true }).fill("admin");
    await page.getByLabel("Contraseña", { exact: true }).fill("credencial-incorrecta");
    await button("Iniciar sesión").click();
    await page.getByRole("alert").filter({ hasText: "Revisá tu usuario y contraseña" }).waitFor();
    await signin("admin");
    await heading("Abril está por cerrar").waitFor();
    await heading("Nómina cerrada por mes").waitFor();
    await screenshot("02-resumen");
    await link("Revisar nómina").click();
    await heading("Revisar nómina de abril").waitFor();
    await button("Cerrar período").waitFor();
    await heading("Novedades por tipo").waitFor();
    await screenshot("03-revision");
    await button("Ver comprobante de Ana Rivera").click();
    await heading("Ana Rivera").waitFor();
    await text("$1.695.000").waitFor();
    await text("2 ausencias").waitFor();
    await text("3 incapacidades").waitFor();
    await text("1 día adicional").waitFor();
    await page.keyboard.press("Escape");
    await button("Cerrar período").click();
    await page.getByRole("alertdialog").waitFor();
    if (!(await button("Cancelar").evaluate(el => el === document.activeElement))) throw new Error("La confirmación no enfoca Cancelar.");
    await screenshot("04-confirmar");
    await page.keyboard.press("Escape");
    await page.getByRole("alertdialog").waitFor({ state: "hidden" });
    const adj = await page.request.get(`${origin}/api/crew/people`).then(r => r.json());
    const ana = adj.find(p => p.firstName === "Ana");
    const token = await page.request.get(`${origin}/api/auth/csrf`).then(r => r.json());
    const mutation = await page.request.post(`${origin}/api/people/${ana.id}/events`, {
        data: { date: "2026-04-15", kind: "worked" }, headers: { "X-CSRF-Token": token.token }
    });
    if (mutation.status() !== 201) throw new Error("La preparación de la revisión desactualizada falló.");
    await button("Cerrar período").click();
    await button("Confirmar cierre").click();
    await text("Revisión desactualizada").waitFor();
    await button("Volver a revisar").click();
    await button("Cerrar período").click();
    const amount = text("$" + (await page.request.get(`${origin}/api/payroll/2026-04/preview`).then(r => r.json())).total.toLocaleString("de-DE"));
    const before = await amount.first().boundingBox();
    await button("Confirmar cierre").click();
    await heading("Abril quedó cerrado").waitFor();
    await text("Cierre guardado").waitFor();
    if (await text("Cierre guardado").evaluate(el => getComputedStyle(el).animationName) !== "arrive-seal")
        throw new Error("No está aplicada la confirmación animada.");
    const after = await amount.boundingBox();
    if (!before || !after || Math.abs(before.y - after.y) > 1) throw new Error("El importe se desplazó.");
    await screenshot("05-cierre");
    await link("Ver histórico").click();
    await link("Abril 2026").click();
    const download = page.waitForEvent("download");
    await button("Descargar CSV").click();
    if ((await download).suggestedFilename() !== "nomina-2026-04.csv") throw new Error("Nombre CSV incorrecto.");
    await link("Roles").click();
    await button("Crear rol").click();
    await page.getByLabel("Clave", { exact: true }).fill("qa-consulta");
    await page.getByLabel("Nombre del rol", { exact: true }).fill("Consulta QA");
    await page.getByRole("checkbox", { name: "Consultar todos los cierres", exact: true }).check();
    await button("Guardar cambios").click();
    await button("Consulta QA").waitFor();
    await screenshot("09-roles");
    await link("Actividades").click();
    await button("Crear actividad").click();
    await page.getByLabel("Título", { exact: true }).fill("Actividad de verificación");
    await page.getByRole("combobox", { name: "Estado", exact: true }).selectOption("inProgress");
    await button("Guardar cambios").click();
    await button("Actividad de verificación").waitFor();
    await page.getByRole("figure", { name: "Actividades por estado" }).getByText("En curso: 2", { exact: true }).waitFor();
    await screenshot("10-actividades");
    await link("Personal").click();
    await button("Ana Rivera").click();
    await heading("Ana Rivera").waitFor();
    await page.getByLabel("Salario mensual / COP", { exact: true }).fill("2200000");
    await button("Guardar cambios").click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
    await screenshot("11-personal");
    await logout();
    await signin("demo-jefa");
    await heading("Mi cuadrilla").waitFor();
    if (await text("David Rojas").count()) throw new Error("La jefatura ve otra cuadrilla.");
    await page.getByLabel("Período", { exact: true }).fill("2026-05");
    await page.getByRole("combobox", { name: "Persona", exact: true }).selectOption({ label: "Ana Rivera" });
    await page.getByLabel("Fecha", { exact: true }).fill("2026-05-08");
    await button("Guardar novedad").click();
    await text("Novedad registrada.").waitFor();
    await page.getByLabel("Fecha", { exact: true }).fill("2026-05-08");
    await button("Guardar novedad").click();
    await page.getByRole("alert").filter({ hasText: "Ya existe una novedad" }).waitFor();
    await page.getByRole("figure", { name: "Novedades de Ana Rivera" }).getByText("Día laborado: 1", { exact: true }).waitFor();
    await screenshot("06-conflicto");
    await logout();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await screenshot("07-acceso-movil");
    if (await page.locator(".art-receipt").evaluate(el => getComputedStyle(el).animationName) !== "none")
        throw new Error("El movimiento reducido no desactiva la entrada del comprobante.");
    await signin("demo-ana");
    await heading("Mis comprobantes").waitFor();
    await heading("Mi neto por mes").waitFor();
    await screenshot("12-trabajador");
    await link("Abril 2026").click();
    await heading("Ana Rivera").waitFor();
    await text("$1.695.000").waitFor();
    await text("2 ausencias").waitFor();
    await screenshot("08-recibo-movil");
    if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error("Desborde horizontal en móvil.");
    if ((await page.request.get(`${origin}/api/payroll/2026-04/export`)).status() !== 403) throw new Error("El trabajador pudo exportar.");
    await logout();
    await signin("demo-conta");
    await heading("Períodos cerrados").waitFor();
    await heading("Nómina cerrada por mes").waitFor();
    await screenshot("13-contabilidad");
    await link("Abril 2026").click();
    await button("Descargar CSV").waitFor();
    const accountToken = await page.request.get(`${origin}/api/auth/csrf`).then(r => r.json());
    await page.request.post(`${origin}/api/auth/logout`, { headers: { "X-CSRF-Token": accountToken.token } });
    await button("Descargar CSV").click();
    await page.getByText("Tu sesión venció.", { exact: false }).waitFor();
    return "PASS: acceso, cuatro roles, cierre, conflicto de revisión, histórico inmutable, CSV, personal, roles personalizados, Kanban, novedad duplicada, teclado y móvil.";
    } catch (error) {
        const headings = await page.getByRole("heading").allTextContents().catch(() => []);
        const alerts = await page.getByRole("alert").allTextContents().catch(() => []);
        await screenshot("fallo").catch(() => {});
        throw new Error(`${error.message}\nPantalla: ${headings.slice(0, 5).join(", ")}\nAlertas: ${alerts.join(", ")}\nHTTP: ${requests.slice(-16).join(" | ")}`);
    } finally {
        await context.close();
    }
}
