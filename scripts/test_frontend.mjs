import { spawn, spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { connect } from "node:net";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const commands = { dotnet: process.env.DOTNET_BIN || "dotnet", browser: process.env.PLAYWRIGHT_CLI || "playwright-cli" };
const port = Number(process.env.CIFRA_TEST_PORT || "5445");
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("Puerto de prueba inválido.");
const origin = `https://localhost:${port}`;
const location = await mkdtemp(join(process.env.TMPDIR || tmpdir(), "cifra-e2e-"));
const password = randomBytes(20).toString("hex");
const session = `cifra-e2e-${process.pid}`;
const shots = process.env.CIFRA_SHOTS ? resolve(process.env.CIFRA_SHOTS) : undefined;
let server;

function run(cmd, args, extra = {}) {
  const transport = spawnSync(cmd, args, { cwd: root, stdio: "inherit", ...extra });
  if (transport.error || transport.status !== 0) throw transport.error ?? new Error(`${cmd} falló (${transport.status}).`);
  return transport;
}

const portOpen = () => new Promise(resolve => {
  const socket = connect({ port, host: "127.0.0.1" });
  socket.on("connect", () => { socket.end(); resolve(true); });
  socket.on("error", () => resolve(false));
});

try {
  if (await portOpen()) throw new Error(`El puerto ${port} está en uso. Elegí otro con CIFRA_TEST_PORT.`);
  if (shots) await mkdir(shots, { recursive: true });
  run("npm", ["--prefix", "frontend", "run", "build"], { env: { ...process.env, VITE_DEMO: "true" } });
  run(commands.dotnet, ["publish", "src/Reference.Api", "-c", "Release", "-o", join(location, "publish")]);
  run(commands.dotnet, ["dev-certs", "https"]);
  server = spawn(commands.dotnet, ["Reference.Api.dll", "--urls", origin], {
    cwd: join(location, "publish"), stdio: ["ignore", "pipe", "pipe"],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", Database__Provider: "Sqlite",
      ConnectionStrings__Payroll: `Data Source=${join(location, "test.db")}`, Bootstrap__Username: "admin", Bootstrap__Password: password,
      Database__Initialize: "true", Demo__Seed: "true", Demo__Workers: "8", Demo__Password: password,
      Logging__LogLevel__Microsoft_EntityFrameworkCore: "Warning" }
  });
  const ready = Promise.withResolvers();
  let log = "";
  const timer = setTimeout(() => ready.reject(Error(`Kestrel no inició.\n${log}`)), 30_000);
  for (const pipe of [server.stdout, server.stderr]) pipe.on("data", chunk => {
    log = (log + chunk).slice(-8192);
    if (log.includes(`Now listening on: ${origin}`)) ready.resolve();
  });
  server.on("error", ready.reject);
  server.on("exit", code => ready.reject(Error(`Kestrel salió (${code}).\n${log}`)));
  try { await ready.promise; } finally { clearTimeout(timer); }

  const path = join(location, "browser-run.js");
  const body = await readFile(join(root, "tests/browser/cifra-firme.js"), "utf8");
  await writeFile(path, `async page => (${body.trim()})(page, ${JSON.stringify({ origin, password, shots })})\n`, { mode: 0o600 });
  run(commands.browser, [`-s=${session}`, "open", "about:blank"], { cwd: location });
  const result = spawnSync(commands.browser, [`-s=${session}`, "--raw", "run-code", `--filename=${path}`],
    { cwd: location, stdio: "pipe", encoding: "utf8" });
  const output = `${result.stdout ?? ""}${result.stderr ?? ""}`.replaceAll(password, "[credencial omitida]").trim();
  if (result.error || result.status !== 0 || !output.includes("PASS: acceso"))
    throw result.error ?? new Error(output || `Playwright CLI terminó con código ${result.status}.`);
  console.log(output);
} finally {
  spawnSync(commands.browser, [`-s=${session}`, "close"], { cwd: location, stdio: "ignore" });
  if (server && server.exitCode === null) {
    await new Promise(resolve => { server.once("exit", resolve); server.kill("SIGTERM"); });
  }
  await rm(location, { recursive: true, force: true });
}
