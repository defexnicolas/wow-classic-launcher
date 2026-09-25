#!/usr/bin/env python3
"""Classic Forever para Linux (Proton / Wine). Hace lo mismo que ClassicForever.exe en Windows.

Uso con Steam + Proton (recomendado): anade WowB.exe como "juego ajeno a Steam", elige Proton en Compatibilidad y
pon en Opciones de lanzamiento:

    python3 /ruta/a/classic-forever-linux.py %command%

Uso con Wine a mano:

    python3 classic-forever-linux.py wine "/ruta/_classic_beta_/WowB.exe"

Engancharse a un juego ya abierto (requiere sudo o kernel.yama.ptrace_scope=0):

    sudo python3 classic-forever-linux.py --pid 12345 --game-dir "/ruta/_classic_beta_"

Que hace, y nada mas:
  1. Asegura WTF/BetaSuspendedTest.wtf con `SET portal "auth.gpon.com.co"` (copia tu Config.wtf la primera vez) y
     anade `-config BetaSuspendedTest.wtf` al comando del juego.
  2. Abre el juego con tu comando y espera a que WowB.exe prepare la conexion al reino.
  3. Busca en la memoria privada de lectura/escritura del cliente el almacen de 12 claves publicas y sustituye la del
     grupo 8 por la clave PUBLICA del servidor: 32 bytes, solo si el bloque entero coincide. Nunca toca codigo ni disco.
  4. Sigue vigilando (reaplica si el cliente recrea el almacen) y termina cuando se cierra el juego.
Solo biblioteca estandar de Python 3.
"""
import argparse
import datetime
import os
import re
import shutil
import subprocess
import sys
import time

PORTAL = 'auth.gpon.com.co'
CONFIG_NAME = 'BetaSuspendedTest.wtf'
EXE_NAME = 'WowB.exe'

# Mismas claves que src/Patcher.cs (medidas el 2026-09-20; iguales en 69977 y 70009).
KNOWN_KEYS = [bytes.fromhex(h) for h in (
    '9B0671C815DFF513BFD4A2B26AE1F84EC9106841B2FB620DB65F6ADE7C21AD06',  # 1  ancla de busqueda
    '112451B9843C2799E4750AA3D9B9AFABF536A645B863C4ADB2078B932B354E04',  # 2
    'A6B858485748BF38BE193517AB90F8DE169EFF0989EA9360DB346A378B0FFE15',  # 3
    '50FFDEB807F8FF73A299A16000AA6575C5945BE875ADFC8795374ED3415249AC',  # 4
    '9F842D078755647C008E5FE3E12B839A981DE02A1F520CB2545CC04CD2323E0E',  # 5
    'B8CC75864D8EF461D8DD6AA7425E2C63C24D3982066B8D773A1549BFE24E05EE',  # 6
    '76BB7BBDD9F34E124A4573C3AA227E3C87CC603506697D054F6FDEC342E56EBF',  # 7
    '1FD6DD8FA0EC30D39E3F72E755B8A045BDE0F70449DC71008B767C2EAA89FB9F',  # 8  <- la que se sustituye
    '15D618BD7DB577BD9A8D45769C59E4FC631633BF447398A4B489B4C26FBC03AD',  # 9  flag=0
    'B34EC592D2AC4993F5FFC85B15C3DA9078694051CB224345592AF7136D796C99',  # 10
    '9E91586DD4B115AB0568B21959E181F6545910BC5E2F30B8975CA09D7BC3EFCE',  # 11
    '769C824A1DADD19AEEFA420414D1732DAF44537F98AA229FC5477BCA55F9D59A',  # 12
)]
NEW_KEY = bytes.fromhex('02596F0D0C061A8B30745988FD72C59E29EC367FB0F341F28E0F08D037BAFC69')  # clave PUBLICA del servidor
TARGET_GROUP, ENTRY_SIZE, ENTRY_COUNT = 8, 40, 12
BLOCK_SIZE = ENTRY_SIZE * ENTRY_COUNT
EXPECTED_FLAGS = [1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1]
CHUNK = 8 * 1024 * 1024

LOG_FILE = None


def say(text, color=''):
    colors = {'green': '\033[92m', 'yellow': '\033[93m', 'red': '\033[91m', 'dim': '\033[90m', 'cyan': '\033[96m'}
    stamp = datetime.datetime.now().strftime('%H:%M:%S')
    print(f"{colors.get(color, '')}[{stamp}] {text}\033[0m", flush=True)
    if LOG_FILE:
        try:
            with open(LOG_FILE, 'a', encoding='utf-8') as f:
                f.write(datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S') + '  ' + text + '\n')
        except OSError:
            pass


def notify(title, body):
    if shutil.which('notify-send'):
        subprocess.run(['notify-send', '-a', 'Classic Forever', title, body], check=False,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


# ------------------------------------------------------------------------------------------- configuracion
def ensure_config(game_dir):
    wtf_dir = os.path.join(game_dir, 'WTF')
    os.makedirs(wtf_dir, exist_ok=True)
    wtf = os.path.join(wtf_dir, CONFIG_NAME)
    base = os.path.join(wtf_dir, 'Config.wtf')
    if not os.path.exists(wtf) and os.path.exists(base):
        shutil.copyfile(base, wtf)
        say(f'Creado WTF/{CONFIG_NAME} a partir de tu Config.wtf', 'dim')
    lines = open(wtf, encoding='utf-8', errors='replace').read().splitlines() if os.path.exists(wtf) else []
    wanted = f'SET portal "{PORTAL}"'
    changed = False
    if not any(l.startswith('SET portal ') for l in lines):
        lines.append(wanted)
        changed = True
    elif wanted not in lines:
        lines = [wanted if l.startswith('SET portal ') else l for l in lines]
        changed = True
    if not any(l.startswith('SET textLocale ') for l in lines):
        lines.append('SET textLocale "enUS"')
        changed = True
    if changed:
        with open(wtf, 'w', encoding='utf-8', newline='\r\n') as f:
            f.write('\n'.join(lines) + '\n')
        say(f'Configuracion actualizada: WTF/{CONFIG_NAME} (portal {PORTAL})', 'dim')


def game_dir_from_command(cmd):
    for a in reversed(cmd):
        if a.lower().endswith(EXE_NAME.lower()) and os.path.isfile(a):
            return os.path.dirname(os.path.abspath(a))
    return None


# ------------------------------------------------------------------------------------------- procesos
def cmdline(pid):
    try:
        with open(f'/proc/{pid}/cmdline', 'rb') as f:
            return f.read().replace(b'\0', b' ').decode(errors='replace')
    except OSError:
        return ''


def parent(pid):
    try:
        with open(f'/proc/{pid}/stat') as f:
            return int(f.read().rsplit(')', 1)[1].split()[1])
    except (OSError, ValueError, IndexError):
        return 0


def is_descendant(pid, ancestor):
    if pid == ancestor:   # el comando puede hacer exec directamente al cliente
        return True
    for _ in range(64):
        pid = parent(pid)
        if pid == ancestor:
            return True
        if pid <= 1:
            return False
    return False


WINE_EXE = re.compile(r'^[A-Za-z]:\\(?:[^\x00]*\\)?' + re.escape(EXE_NAME) + r'(?:\s|$)', re.I)


def find_clients(root=None):
    """PIDs del cliente. Bajo Wine/Proton el proceso real lleva como argv[0] la ruta de Windows (Z:\\...\\WowB.exe);
    los intermediarios (script proton, contenedor de Steam) llevan la ruta de Linux y no cuentan."""
    found = []
    for d in os.listdir('/proc'):
        if not d.isdigit():
            continue
        pid = int(d)
        if WINE_EXE.match(cmdline(pid)) and (root is None or is_descendant(pid, root)):
            found.append(pid)
    return sorted(found, key=rss, reverse=True)


def rss(pid):
    try:
        with open(f'/proc/{pid}/statm') as f:
            return int(f.read().split()[1])
    except (OSError, ValueError, IndexError):
        return 0


def alive(pid):
    return os.path.exists(f'/proc/{pid}') and rss(pid) > 0


# ------------------------------------------------------------------------------------------- memoria
def heap_regions(pid):
    """Regiones privadas de lectura/escritura sin fichero detras (el heap del cliente bajo Wine)."""
    regions = []
    with open(f'/proc/{pid}/maps') as f:
        for line in f:
            parts = line.split(maxsplit=5)
            perms, inode = parts[1], parts[4]
            path = parts[5].strip() if len(parts) > 5 else ''
            if perms != 'rw-p' or inode != '0' or path.startswith('[v') or path == '[stack]':
                continue
            lo, hi = (int(x, 16) for x in parts[0].split('-'))
            regions.append((lo, hi))
    return regions


def region_of(pid, addr, size):
    for lo, hi in heap_regions(pid):
        if lo <= addr and addr + size <= hi:
            return lo, hi
    return None


def scan(pid, needle):
    hits, total = [], 0
    with open(f'/proc/{pid}/mem', 'rb', buffering=0) as mem:
        for lo, hi in heap_regions(pid):
            off = lo
            while off < hi:
                take = min(CHUNK, hi - off)
                try:
                    mem.seek(off)
                    buf = mem.read(take)
                except OSError:
                    break
                total += len(buf)
                i = buf.find(needle)
                while i >= 0:
                    if (off + i) % 4 == 0:
                        hits.append(off + i)
                    i = buf.find(needle, i + 1)
                if take < CHUNK:
                    break
                off += CHUNK - len(needle)
    return sorted(set(hits)), total


def read(pid, addr, size):
    with open(f'/proc/{pid}/mem', 'rb', buffering=0) as mem:
        mem.seek(addr)
        return mem.read(size)


def store_state(pid, array):
    if not region_of(pid, array, BLOCK_SIZE):
        return 'invalid'
    try:
        raw = read(pid, array, BLOCK_SIZE)
    except OSError:
        return 'invalid'
    if len(raw) != BLOCK_SIZE:
        return 'invalid'
    for e in range(ENTRY_COUNT):
        o = e * ENTRY_SIZE
        if int.from_bytes(raw[o:o + 4], 'little') != e + 1:
            return 'invalid'
        key = raw[o + 4:o + 36]
        ok = key in (KNOWN_KEYS[e], NEW_KEY) if e == TARGET_GROUP - 1 else key == KNOWN_KEYS[e]
        if not ok or raw[o + 36] != EXPECTED_FLAGS[e] or raw[o + 37:o + 40] != b'\x7f\x00\x00':
            return 'invalid'
    o8 = (TARGET_GROUP - 1) * ENTRY_SIZE + 4
    return 'patched' if raw[o8:o8 + 32] == NEW_KEY else 'original'


def find_stores(pid):
    t0 = time.time()
    hits, total = scan(pid, KNOWN_KEYS[0])
    found = [(h - 4, s) for h in hits for s in [store_state(pid, h - 4)] if s != 'invalid']
    return found, f'{total // (1024 * 1024)} MB, {time.time() - t0:.1f} s, {len(hits)} copias, {len(found)} almacen(es)'


def patch(pid, array):
    addr = array + (TARGET_GROUP - 1) * ENTRY_SIZE + 4
    before = KNOWN_KEYS[TARGET_GROUP - 1]
    for attempt in range(20):
        if not region_of(pid, addr, 32):
            raise RuntimeError('la clave no esta en memoria privada de lectura/escritura')
        if read(pid, addr, 32) != before:
            time.sleep(0.3)
            continue
        with open(f'/proc/{pid}/mem', 'r+b', buffering=0) as mem:
            mem.seek(addr)
            mem.write(NEW_KEY)
        if read(pid, addr, 32) != NEW_KEY:
            raise RuntimeError('verificacion de relectura fallida')
        break
    else:
        raise RuntimeError('los bytes cambiaron justo antes de escribir')
    if store_state(pid, array) != 'patched':
        raise RuntimeError('tras escribir, el almacen no valida como parcheado')
    say(f'clave del grupo 8 escrita en 0x{addr:X}', 'dim')


def ready():
    say('LISTO. Si el primer intento de entrar al reino fallo, vuelve a entrar SIN cerrar el juego.', 'green')
    notify('Classic Forever: listo', 'Si el primer intento de entrar al reino fallo, vuelve a entrar sin cerrar el juego.')


def watch(pid):
    store, ever, waiting_said = None, False, False
    while True:
        if not alive(pid):
            say('El juego se cerro. Hasta la proxima.', 'cyan')
            return 0
        if store is not None:
            st = store_state(pid, store)
            if st == 'patched':
                time.sleep(3)
                continue
            if st == 'original':
                say('El cliente restauro la clave original: la vuelvo a poner.', 'yellow')
                patch(pid, store)
                ready()
                continue
            say('El almacen se libero; si vuelves a conectar, lo buscare de nuevo.', 'dim')
            store = None
            continue
        try:
            found, info = find_stores(pid)
        except PermissionError:
            say('Linux no me deja leer la memoria del juego. Lanza el juego a traves de este script '
                '(opciones de lanzamiento de Steam: python3 /ruta/classic-forever-linux.py %command%) o usa sudo con --pid.', 'red')
            return 1
        if len(found) == 1:
            store, state = found[0]
            if state == 'patched':
                say('La clave ya estaba puesta.', 'green')
            else:
                say('Almacen de certificados encontrado: aplicando la clave del servidor...')
                try:
                    patch(pid, store)
                except (OSError, RuntimeError) as ex:
                    say(f'No se pudo parchear: {ex}. Reintento.', 'yellow')
                    store = None
                    time.sleep(2)
                    continue
            ever = True
            ready()
            continue
        if len(found) > 1:
            say(f'Hay {len(found)} almacenes validos a la vez: no escribo hasta que quede uno.', 'yellow')
        elif not waiting_said:
            say(f'Esperando a que entres al reino... (busqueda: {info})', 'dim')
            waiting_said = True
        time.sleep(15 if ever else 0.1)


# ------------------------------------------------------------------------------------------- principal
def main():
    global LOG_FILE
    ap = argparse.ArgumentParser(description='Classic Forever para Linux (Proton/Wine).',
                                 usage='%(prog)s [--game-dir DIR] [--pid PID] [comando del juego...]')
    ap.add_argument('--game-dir', help='carpeta _classic_beta_ (se deduce del comando si contiene WowB.exe)')
    ap.add_argument('--pid', type=int, help='engancharse a un WowB.exe ya abierto')
    ap.add_argument('command', nargs=argparse.REMAINDER, help='comando del juego (en Steam: %%command%%)')
    a = ap.parse_args()
    cmd = [c for c in a.command if c != '--']

    game_dir = a.game_dir or game_dir_from_command(cmd)
    if game_dir:
        os.makedirs(os.path.join(game_dir, 'Logs'), exist_ok=True)
        LOG_FILE = os.path.join(game_dir, 'Logs', 'launcher-linux.log')
        say('----- inicio', 'dim')
        ensure_config(game_dir)
    elif cmd:
        say('No encuentro WowB.exe en el comando: el portal no se puede configurar. Anade WowB.exe (no Battle.net) '
            'como juego en Steam, o usa --game-dir.', 'yellow')

    if a.pid:
        pid = a.pid
    elif cmd:
        if game_dir and not any(x.lower() == '-config' for x in cmd):
            cmd += ['-config', CONFIG_NAME]
        say('Abriendo el juego...', 'cyan')
        child = subprocess.Popen(cmd)
        pid = None
        for _ in range(180):
            time.sleep(1)
            clients = find_clients(child.pid)
            if clients:
                pid = clients[0]
                break
            if child.poll() is not None and not find_clients():
                say('El comando del juego termino sin abrir WowB.exe.', 'red')
                return 1
        if pid is None:
            say('El juego no arranco en 3 minutos.', 'red')
            return 1
    else:
        clients = find_clients()
        if not clients:
            ap.print_help()
            return 1
        pid = clients[0]
        say(f'El juego ya estaba abierto (PID {pid}): me engancho a el.', 'cyan')

    say(f'Juego en PID {pid}. Haz login y entra al reino.', 'cyan')
    try:
        return watch(pid)
    except KeyboardInterrupt:
        return 0


if __name__ == '__main__':
    sys.exit(main())
