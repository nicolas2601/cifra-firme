"""Carga datos ficticios en una API de demostración usando el flujo normal."""

import argparse
from datetime import date
from getpass import getpass
from http.cookiejar import CookieJar
import json
import re
import ssl
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import HTTPRedirectHandler, HTTPSHandler, HTTPCookieProcessor, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, help="Origen HTTPS de una base exclusivamente de demostración.")
    parser.add_argument("--month", required=True, help="Período ficticio abierto (AAAA-MM).")
    parser.add_argument("--admin", default="admin")
    parser.add_argument("--ca-file", help="Certificado CA local; no desactiva la verificación TLS.")
    args = parser.parse_args()
    url = urlparse(args.url)
    if url.scheme != "https" or not url.hostname or url.path not in ("", "/") or url.query or url.fragment or url.username:
        parser.error("--url debe ser un origen HTTPS sin credenciales ni ruta.")
    if not re.fullmatch(r"[0-9]{4}-[0-9]{2}", args.month):
        parser.error("--month debe tener formato AAAA-MM.")
    date.fromisoformat(f"{args.month}-01")

    client = build_opener(NoRedirect(), HTTPCookieProcessor(CookieJar()),
                          HTTPSHandler(context=ssl.create_default_context(cafile=args.ca_file)))
    token = None

    def request(path, payload=None):
        headers = {"Accept": "application/json"}
        if payload is not None:
            headers.update({"Content-Type": "application/json", "X-CSRF-Token": token})
        data = json.dumps(payload).encode() if payload is not None else None
        with client.open(Request(args.url.rstrip("/") + path, data, headers), timeout=20) as response:
            return json.load(response)

    token = request("/api/auth/csrf")["token"]
    request("/api/auth/login", {"username": args.admin, "password": getpass("Contraseña de administración: ")})
    token = request("/api/auth/csrf")["token"]
    password = getpass("Contraseña para las cuatro cuentas demo (12–128 caracteres): ")
    if not 12 <= len(password) <= 128 or password.isspace():
        parser.error("Contraseña demo inválida.")

    for key, first, role, salary, crew in (
        ("ana", "Ana", "worker", 1_800_000, "norte"),
        ("ivan", "Iván", "worker", 2_000_000, "sur"),
        ("jefa", "Luz", "lead", 0, "norte"),
        ("conta", "Sol", "accountant", 0, None),
    ):
        person = request("/api/people", {
            "username": f"demo-{key}", "firstName": first, "lastName": "Prueba", "document": f"FICTICIO-{key}",
            "role": role, "salary": salary, "crew": crew, "active": True, "password": password,
            "addresses": ["Dirección ficticia 123"], "phones": ["3000000000"],
        })
        if key == "ana":
            for day, kind in ((2, "absence"), (3, "absence"), (4, "medicalLeave"), (5, "medicalLeave"),
                              (6, "medicalLeave"), (7, "additionalDay"), (8, "worked")):
                request(f"/api/people/{person['id']}/events", {"date": f"{args.month}-{day:02}", "kind": kind})
            request("/api/activities", {"title": "Revisar equipo de ejemplo", "assigneeId": person["id"], "status": "pending"})
    result = request(f"/api/payroll/{args.month}/preview")
    print(f"Se cargaron las cuentas demo-ana, demo-ivan, demo-jefa y demo-conta. Período abierto: {args.month}.")
    print(f"Vista previa: {len(result['people'])} personas, total COP {result['total']}. No se cerró el período.")


if __name__ == "__main__":
    try:
        main()
    except HTTPError as error:
        raise SystemExit(f"Se detuvo la carga: HTTP {error.code} en {urlparse(error.url).path}. Puede haber datos parciales.")
    except (URLError, ValueError) as error:
        raise SystemExit(f"No se pudo completar la carga: {error}")
