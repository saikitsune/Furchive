"""Default-only extracted package contracts. Never enables production test IPC."""
from pathlib import Path, PurePosixPath
import hashlib
import os
import json
import plistlib
import shutil
import subprocess
import tarfile
import zipfile


def prepare(target, output, host):
    payload = output / 'payload'
    payload.mkdir()
    if host == 'Linux':
        sources = list((target / 'bundle/deb').glob('*.deb'))
        if len(sources) != 1:
            raise ValueError('expected exactly one fresh Debian package')
        artifact = output / ('Furchive-default-Linux-' + sources[0].name)
        shutil.copy2(sources[0], artifact)
        with (output / 'filesystem.tar').open('wb') as stream:
            subprocess.run(['dpkg-deb', '--fsys-tarfile', str(artifact)], stdout=stream, check=True, timeout=60)
        with tarfile.open(output / 'filesystem.tar') as archive:
            inventory((m.name, 'file' if m.isfile() else 'dir' if m.isdir() else 'link') for m in archive)
        subprocess.run(['dpkg-deb', '--extract', str(artifact), str(payload)], check=True, timeout=60)
        (output / 'filesystem.tar').unlink()
        binary = payload / 'usr/bin/furchive-desktop'
    elif host == 'Windows':
        artifact = output / 'Furchive-default-Windows.zip'
        with zipfile.ZipFile(artifact, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            archive.write(target / 'furchive-desktop.exe', 'furchive-desktop.exe')
            archive.writestr('DEFAULT.txt', 'Default no-self-test debug portable payload. Requires system WebView2. Not an installer or release.\n')
        with zipfile.ZipFile(artifact) as archive:
            inventory((m.filename, 'file') for m in archive.infolist())
            if archive.namelist() != ['furchive-desktop.exe', 'DEFAULT.txt']:
                raise ValueError('unexpected portable inventory')
            archive.extractall(payload)
        binary = payload / 'furchive-desktop.exe'
    elif host == 'Darwin':
        apps = list((target / 'bundle/macos').glob('*.app'))
        if len(apps) != 1:
            raise ValueError('expected one full .app')
        artifact = output / 'Furchive-default-macOS.tar.gz'
        with tarfile.open(artifact, 'w:gz') as archive:
            archive.add(apps[0], arcname=apps[0].name)
        with tarfile.open(artifact) as archive:
            inventory((m.name, 'file' if m.isfile() else 'dir' if m.isdir() else 'link') for m in archive)
            archive.extractall(payload, filter='data')
        info = plistlib.loads((payload / apps[0].name / 'Contents/Info.plist').read_bytes())
        name = info['CFBundleExecutable']
        if not isinstance(name, str) or not name or '/' in name or '\\' in name or name in ('.', '..'):
            raise ValueError('invalid macOS executable')
        binary = payload / apps[0].name / 'Contents/MacOS' / name
    else:
        raise ValueError('unsupported packaging host')
    members = []
    for path in sorted(payload.rglob('*')):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError('nonregular extracted member')
        if path.is_file():
            members.append({'path': path.relative_to(payload).as_posix(), 'sha256': digest(path), 'size': path.stat().st_size})
    if not binary.is_file() or not binary.resolve().is_relative_to(payload.resolve()):
        raise ValueError('missing or escaped executable')
    row = {'schema': 1, 'host': host, 'profile': 'debug-unoptimized', 'self_test': False,
           'artifact': artifact.name, 'artifact_sha256': digest(artifact),
           'executable': binary.relative_to(output).as_posix(), 'binary_sha256': digest(binary),
           'members': members, 'native_runtime': 'UNRUN', 'release_ready': False}
    (output / 'package.json').write_text(json.dumps(row, indent=2) + '\n')
    return row

IDENTIFIER = 'dev.furchive.desktop.alpha'


def named_receipt(receipt, expected):
    if (not expected or len(set(expected)) != len(expected)
            or receipt.get('ids') != expected or sorted(receipt.get('passed', [])) != sorted(expected)
            or receipt.get('errors') != [] or receipt.get('skipped') != []):
        raise ValueError('missing/duplicate/failed/skipped named host tests')
    return True


def fresh(path):
    if path.exists() or path.is_symlink():
        raise ValueError('refusing stale evidence directory')
    path.mkdir(parents=True)


def default_features(features):
    if features != ['custom-protocol']:
        raise ValueError('expected exact default packaged feature set: custom-protocol only')
    return features


def bind_packaged_binary(target, packaged, host):
    """Only identical bytes or Tauri's unique Linux bundle marker substitution.

    Never rewrite either binary, nor accept arbitrary build/package differences.
    Compare bounded chunks; large debug executables need not fit in memory.
    """
    if digest(target) == digest(packaged):
        return 'identical'
    if host != 'Linux' or target.stat().st_size != packaged.stat().st_size:
        raise ValueError('packaged executable differs from actual default build target')
    import mmap
    before = b'__TAURI_BUNDLE_TYPE_VAR_UNK'
    after = b'__TAURI_BUNDLE_TYPE_VAR_DEB'
    with target.open('rb') as source, packaged.open('rb') as output:
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as original, mmap.mmap(output.fileno(), 0, access=mmap.ACCESS_READ) as bundled:
            offset = original.find(before)
            if (offset < 0 or original.find(before, offset + 1) >= 0
                    or bundled[offset:offset + len(after)] != after):
                raise ValueError('invalid or ambiguous Tauri Debian marker')
            for start, end in ((0, offset), (offset + len(before), len(original))):
                for pos in range(start, end, 1024 * 1024):
                    stop = min(end, pos + 1024 * 1024)
                    if original[pos:stop] != bundled[pos:stop]:
                        raise ValueError('non-marker packaged executable delta')
    return 'tauri-deb-marker'


def build_receipt(text, binary):
    matches = []
    for line in text.splitlines():
        try:
            row = json.loads(line)
        except json.JSONDecodeError:
            continue
        if (isinstance(row, dict) and row.get('reason') == 'compiler-artifact'
                and row.get('target', {}).get('name') == 'furchive-desktop'
                and row.get('target', {}).get('kind') == ['bin']):
            matches.append(row)
    if len(matches) != 1 or Path(matches[0].get('executable', '')).resolve() != binary.resolve():
        raise ValueError('missing/duplicate/wrong-path native compiler artifact')
    default_features(matches[0].get('features'))
    return matches[0]


def cosmetic_restore(original, current):
    cosmetic = b''.join(line.replace(b',"', b', "') if line.startswith(b'self-test = ') else line
                        for line in original.splitlines(keepends=True))
    if cosmetic == current or original == current:
        return original
    raise ValueError('refusing non-cosmetic manifest drift')


def supervise(command, log, timeout):
    """Single-threaded Linux owner: adopt lost-parent descendants before launch.

    No peer child may already exist. Popen alone reaps its direct child; this
    owner reaps only separately adopted children, preserving the real raw exit.
    """
    import ctypes
    import psutil
    import threading
    import time
    import sys
    if sys.platform != 'linux' or threading.active_count() != 1:
        raise RuntimeError('supervision requires a dedicated single-threaded Linux owner')
    owner = psutil.Process()
    if owner.children(recursive=True):
        raise RuntimeError('supervision refuses an owner with pre-existing children')
    libc = ctypes.CDLL(None, use_errno=True)
    before = ctypes.c_int()
    if libc.prctl(37, ctypes.byref(before), 0, 0, 0) != 0 or before.value != 0:
        raise RuntimeError('cannot establish exclusive child-subreaper ownership')
    if libc.prctl(36, 1, 0, 0, 0) != 0:
        raise RuntimeError('child-subreaper admission refused')
    proc = None
    owned = {}
    timed_out = False
    cleanup_failed = False
    try:
        proc = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        owned[proc.pid] = psutil.Process(proc.pid)
        deadline = time.monotonic() + timeout
        while proc.poll() is None:
            for child in owner.children(recursive=True):
                owned.setdefault(child.pid, child)
            if time.monotonic() >= deadline:
                timed_out = True
                break
            time.sleep(.025)
    finally:
        if proc is not None:
            # The kernel reparents every orphan to this exclusive subreaper,
            # including setsid/double-fork children that ancestry polls missed.
            cleanup_deadline = time.monotonic() + 10
            while True:
                for child in owner.children(recursive=True):
                    owned.setdefault(child.pid, child)
                live = []
                for child in owned.values():
                    try:
                        if child.is_running() and child.status() != psutil.STATUS_ZOMBIE:
                            live.append(child)
                            if time.monotonic() < cleanup_deadline - 5:
                                child.terminate()
                            else:
                                child.kill()
                    except psutil.NoSuchProcess:
                        pass
                # Never let psutil.wait_procs consume Popen's direct wait status.
                proc.poll()
                for pid, child in list(owned.items()):
                    if pid == proc.pid:
                        continue
                    try:
                        if child.ppid() == os.getpid():
                            os.waitpid(pid, os.WNOHANG)
                    except (psutil.NoSuchProcess, ChildProcessError):
                        pass
                remaining = owner.children(recursive=True)
                if not remaining and proc.poll() is not None:
                    break
                if time.monotonic() >= cleanup_deadline:
                    cleanup_failed = True
                    break
                time.sleep(.02)
            proc.wait(timeout=1)
            survivors = []
            for child in owner.children(recursive=True):
                try:
                    if child.status() != psutil.STATUS_ZOMBIE:
                        survivors.append(child.pid)
                except psutil.NoSuchProcess:
                    pass
            log.write((json.dumps({'owned_cleanup_pids': sorted(owned), 'survivors': survivors,
                                   'raw_exit': proc.returncode, 'timed_out': timed_out,
                                   'subreaper': True}) + '\n').encode())
            log.flush()
        else:
            survivors = []
        # Only restore once the exclusively owned descendant set has been reaped.
        if not cleanup_failed and not owner.children(recursive=True):
            if libc.prctl(36, before.value, 0, 0, 0) != 0:
                raise RuntimeError('failed to restore child-subreaper state')
        if cleanup_failed or survivors:
            raise RuntimeError('owned descendants survived cleanup or could not be reaped')
    if timed_out:
        raise TimeoutError('owned native worker deadline; cleanup exit is not success')
    if proc is None:
        raise RuntimeError('native worker was not launched')
    return proc.returncode


def isolation(host, owned, source=None):
    if host != 'Linux':
        raise ValueError('UNRUN: Windows KnownFolder ignores environment; macOS webview containment unverified')
    source = os.environ if source is None else source
    allowed = ('PATH', 'DISPLAY', 'XAUTHORITY', 'DBUS_SESSION_BUS_ADDRESS',
               'LD_LIBRARY_PATH', 'WEBKIT_EXEC_PATH', 'GSETTINGS_SCHEMA_DIR',
               'GST_PLUGIN_SYSTEM_PATH_1_0', 'GST_PLUGIN_SCANNER', 'XDG_DATA_DIRS')
    env = {k: source[k] for k in allowed if k in source}
    env.update(HOME=str(owned / 'home'), XDG_DATA_HOME=str(owned / 'data'),
               XDG_CONFIG_HOME=str(owned / 'config'), XDG_CACHE_HOME=str(owned / 'cache'),
               XDG_RUNTIME_DIR=str(owned / 'runtime'), TMPDIR=str(owned / 'tmp'),
               LANG='C.UTF-8', LC_ALL='C.UTF-8', GTK_MODULES='gail:atk-bridge')
    return env, owned / 'data' / IDENTIFIER


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def inventory(entries):
    names = []
    for name, kind in entries:
        # Permit dpkg's single leading ./, but reject other aliases.
        name = name.removeprefix('./').removesuffix('/')
        if name == '.':
            continue
        p = PurePosixPath(name)
        if (not name or p.is_absolute() or '..' in p.parts or '\\' in name
                or p.as_posix() != name or kind not in ('file', 'dir') or name in names):
            raise ValueError('unsafe, duplicate or nonregular package member: ' + name)
        names.append(name)
    return names


def lifecycle(adapter, binary, data_root, nonce, settings):
    """A zero exit is never an observation. Revalidate before requesting close."""
    phases = []
    for phase in (1, 2):
        child = adapter.launch(phase)
        try:
            row = adapter.observe(child)
            expected = {'pid': child['pid'], 'path': binary, 'nonce': nonce,
                        'data_root': data_root, 'settings': settings}
            if (not adapter.alive(child) or any(row.get(k) != v for k, v in expected.items())
                    or not row.get('window') or row.get('product') is not True
                    or row.get('nonblank') is not True):
                raise ValueError('missing, stale, foreign or early-exit native evidence')
            exit_code = adapter.close(child, row)
            if exit_code != 0 or adapter.alive(child):
                raise ValueError('normal owned-window close did not exit zero')
            phases.append(dict(row, normal_close_exit=exit_code))
        finally:
            adapter.cleanup(child)
    return {'status': 'PASS', 'phases': phases}


def main():
    import argparse
    import importlib.util
    import platform
    import sys
    import uuid
    import signal
    import socket
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--build-log', type=Path)
    parser.add_argument('--restore-cosmetic-manifest', action='store_true')
    parser.add_argument('--target', type=Path, default=Path(__file__).resolve().parents[1] / 'apps/desktop/src-tauri/target/debug')
    parser.add_argument('--prepare-only', action='store_true')
    parser.add_argument('--runtime-package', type=Path, help='existing package.json; never builds or installs')
    parser.add_argument('--namespace-worker', action='store_true', help=argparse.SUPPRESS)
    parser.add_argument('--parent-net', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.restore_cosmetic_manifest:
        repo = Path(__file__).resolve().parents[2]
        name = 'rust/apps/desktop/src-tauri/Cargo.toml'
        original = subprocess.check_output(['git', 'show', 'HEAD:' + name], cwd=repo)
        path = repo / name
        path.write_bytes(cosmetic_restore(original, path.read_bytes()))
        return 0
    if args.output is None:
        parser.error('--output required')
    output = args.output.resolve()
    fresh(output)
    status = {'schema': 1, 'status': 'UNRUN', 'release_ready': False}
    try:
        if args.runtime_package:
            package_root = args.runtime_package.resolve().parent
            package = json.loads(args.runtime_package.read_text())
            default_features(package.get('compiler_features'))
            binary = package_root / package['executable']
            artifact = package_root / package['artifact']
            if (not binary.resolve().is_relative_to(package_root) or binary.is_symlink()
                    or not artifact.resolve().is_relative_to(package_root)
                    or digest(binary) != package['binary_sha256']
                    or digest(artifact) != package['artifact_sha256']):
                raise ValueError('package path/hash drift')
        else:
            if args.build_log is None:
                raise ValueError('required actual --build-log compiler JSON receipt')
            target_binary = args.target.resolve() / ('furchive-desktop.exe' if platform.system() == 'Windows' else 'furchive-desktop')
            build = build_receipt(args.build_log.read_text(), target_binary)
            package = prepare(args.target.resolve(), output, platform.system())
            package['build_binary_binding'] = bind_packaged_binary(target_binary, output / package['executable'], platform.system())
            package['build_binary_sha256'] = digest(target_binary)
            package['compiler_features'] = build['features']
            package['build_log_sha256'] = digest(args.build_log)
            repo = Path(__file__).resolve().parents[2]
            package['source_commit'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
            package['source_tree'] = subprocess.check_output(['git', 'rev-parse', 'HEAD^{tree}'], cwd=repo, text=True).strip()
            (output / 'package.json').write_text(json.dumps(package, indent=2) + '\n')
            package_root = output
            binary = output / package['executable']
        status.update(artifact_sha256=package['artifact_sha256'], binary_sha256=package['binary_sha256'])
        if args.prepare_only:
            status['reason'] = 'static package inspection only; NOT native smoke'
            return 0
        # Refuse unsupported profile isolation BEFORE starting any application.
        env, data_root = isolation(platform.system(), output / 'profile')
        if not args.namespace_worker:
            command = ['unshare', '--user', '--map-current-user', '--net', '--',
                       sys.executable, str(Path(__file__).resolve()), '--namespace-worker',
                       '--parent-net', os.readlink('/proc/self/ns/net'),
                       '--runtime-package', str(package_root / 'package.json'),
                       '--output', str(output / 'namespace')]
            with (output / 'namespace.log').open('wb') as stream:
                exit_code = supervise(command, stream, 150)
            status['namespace_exit'] = exit_code
            if exit_code != 0:
                raise ValueError('UNRUN: unprivileged network namespace/runtime refused; raw namespace.log retained; no fallback')
            receipt = json.loads((output / 'namespace/smoke.json').read_text())
            if receipt.get('status') != 'PASS':
                raise ValueError('namespace worker returned zero without native PASS')
            status.update(receipt)
            return 0
        def interrupted(signum, frame):
            raise TimeoutError('native worker interrupted; cleanup required')
        signal.signal(signal.SIGTERM, interrupted)
        if not env.get('DBUS_SESSION_BUS_ADDRESS', '').startswith('unix:path='):
            raise ValueError('UNRUN: isolated D-Bus session requires filesystem Unix socket; abstract/TCP propagation unverified')
        if (os.readlink('/proc/self/ns/net') == args.parent_net
                or sorted(name for _, name in socket.if_nameindex()) != ['lo']):
            raise ValueError('network containment not established')
        # Bring up only private loopback for Tauri media and private AT-SPI/D-Bus.
        # CAP_NET_ADMIN is scoped to this new user-owned namespace, never host root.
        subprocess.run(['ip', 'link', 'set', 'lo', 'up'], check=True, timeout=5)
        # Native display/session must be provided by the isolated hosted command;
        # no app is launched if the required tools/dependencies are unavailable.
        spec = importlib.util.spec_from_file_location('default_linux', Path(__file__).with_name('default-linux.py'))
        if spec is None or spec.loader is None:
            raise ValueError('missing native adapter')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        for name in ('xdotool', 'wmctrl'):
            if not shutil.which(name):
                raise ValueError('UNRUN: missing native dependency ' + name)
        for key in ('HOME', 'XDG_DATA_HOME', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'XDG_RUNTIME_DIR', 'TMPDIR'):
            Path(env[key]).mkdir(parents=True, mode=0o700, exist_ok=True)
        data_root.mkdir(parents=True)
        nonce = uuid.uuid4().hex
        sentinel = str(100000 + int(nonce[:5], 16))
        # Harmless controls-only persistence fixture, not runtime authority.
        seed = {'version': 1, 'restore_session': True, 'last_session': {
            'mode': 'pool', 'id': int(sentinel), 'ratings': {'only': ['safe']}, 'offset': 0}}
        (data_root / 'settings.json').write_text(json.dumps(seed))
        adapter = module.LinuxNative(binary.resolve(), output, env, data_root, nonce, sentinel)
        status.update(lifecycle(adapter, str(binary.resolve()), str(data_root), nonce, sentinel))
        status['network'] = 'fresh unprivileged network namespace; lo only; no external route'
        return 0
    except Exception as error:
        status['status'] = 'FAIL' if args.namespace_worker else 'UNRUN'
        status['reason'] = type(error).__name__ + ': ' + str(error)
        print(status['reason'], file=sys.stderr)
        return 2
    finally:
        (output / 'smoke.json').write_text(json.dumps(status, indent=2) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())
