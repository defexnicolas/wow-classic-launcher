#!/usr/bin/env python3
"""Publica status.json para el launcher (rama `status` del repo publico).

Corre en la maquina del servidor. Cada --interval segundos:
  - comprueba los puertos del login y del mundo en local,
  - mezcla news.json (mantenimiento, mensaje, noticias, enlaces, version del launcher),
  - y si algo cambio (o cada --heartbeat segundos, para que el launcher sepa que el dato es fresco)
    fuerza un unico commit en la rama `status`. La rama no acumula historia.

Uso:
  python3 server/publish_status.py --once            # una pasada, imprime el JSON (no publica)
  python3 server/publish_status.py --loop            # servicio (ver server/classic-forever-status.service)
"""
import argparse
import datetime
import json
import os
import socket
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)


def port_open(host, port, timeout=2.0):
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False



def build_status(a):
    login = port_open("127.0.0.1", a.login_port)
    world = port_open("127.0.0.1", a.world_port)
    try:
        with open(a.news, encoding="utf-8") as f:
            extra = json.load(f)
    except FileNotFoundError:
        extra = {}
    realm = {"name": extra.get("realmName", "Classic Forever"), "login": login, "world": world}
    return {
        "schema": 1,
        "updated": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "realm": realm,
        "maintenance": bool(extra.get("maintenance", False)),
        "message": extra.get("message", ""),
        "news": extra.get("news", []),
        "links": extra.get("links", []),
        "launcher": extra.get("launcher", {}),
        "clientBuilds": extra.get("clientBuilds", []),
    }


def git(*args, cwd, check=True):
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, check=check)


def publish(a, status):
    """Un solo commit huerfano en la rama `status`, empujado con --force. No toca el arbol de trabajo."""
    body = json.dumps(status, ensure_ascii=False, indent=2) + "\n"
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".json") as f:
        f.write(body)
        tmp = f.name
    try:
        blob = git("hash-object", "-w", tmp, cwd=a.repo).stdout.strip()
    finally:
        os.unlink(tmp)
    tree = subprocess.run(["git", "mktree"], cwd=a.repo, input=f"100644 blob {blob}\tstatus.json\n",
                          capture_output=True, text=True, check=True).stdout.strip()
    env = dict(os.environ, GIT_AUTHOR_NAME="status-bot", GIT_AUTHOR_EMAIL="status-bot@localhost",
               GIT_COMMITTER_NAME="status-bot", GIT_COMMITTER_EMAIL="status-bot@localhost")
    commit = subprocess.run(["git", "commit-tree", tree, "-m", "status " + status["updated"]], cwd=a.repo, env=env,
                            capture_output=True, text=True, check=True).stdout.strip()
    git("push", "--force", "--quiet", a.remote, f"{commit}:refs/heads/{a.branch}", cwd=a.repo)


def comparable(status):
    s = dict(status)
    s.pop("updated", None)
    return json.dumps(s, sort_keys=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--once", action="store_true", help="una pasada: imprime el JSON sin publicar")
    p.add_argument("--loop", action="store_true", help="publicar en bucle")
    p.add_argument("--interval", type=int, default=60)
    p.add_argument("--heartbeat", type=int, default=240, help="publicar aunque nada cambie cada N s")
    p.add_argument("--news", default=os.path.join(HERE, "news.json"))
    p.add_argument("--repo", default=REPO)
    p.add_argument("--remote", default="origin")
    p.add_argument("--branch", default="status")
    p.add_argument("--login-port", type=int, default=1119)
    p.add_argument("--world-port", type=int, default=8085)
    a = p.parse_args()

    if a.once or not a.loop:
        print(json.dumps(build_status(a), ensure_ascii=False, indent=2))
        return

    last, last_push = None, 0.0
    while True:
        try:
            st = build_status(a)
            key = comparable(st)
            if key != last or time.time() - last_push >= a.heartbeat:
                publish(a, st)
                last, last_push = key, time.time()
                r = st["realm"]
                print(f"[status] {st['updated']} login={r['login']} world={r['world']}", flush=True)
        except Exception as e:
            print(f"[status] error: {e}", file=sys.stderr, flush=True)
        time.sleep(a.interval)


if __name__ == "__main__":
    main()
