"""Linux X11 native adapter, fail closed. Hosted execution is separately required.

No browser/CDP/IPC injection. AT-SPI actions are restricted to the descendant
application; EWMH close targets the exact current PID-owned X11 window.
"""
from pathlib import Path
import json
import os

import subprocess
import time


class LinuxNative:
    def __init__(self, binary, output, env, data_root, nonce, sentinel):
        import psutil
        import pyatspi
        from Xlib import display
        self.psutil, self.atspi = psutil, pyatspi
        self.display = display.Display()
        self.binary, self.output, self.env = binary, output, env
        self.data_root, self.nonce, self.sentinel = data_root, nonce, sentinel

    def launch(self, phase):
        log = (self.output / f'phase-{phase}.log').open('wb')
        proc = subprocess.Popen([str(self.binary)], env=self.env, stdout=log,
                                stderr=subprocess.STDOUT, start_new_session=True)
        try:
            created = self.psutil.Process(proc.pid).create_time()
        except self.psutil.NoSuchProcess:
            proc.wait(timeout=5)
            log.close()
            raise ValueError('application exited at launch; raw log retained')
        child = {'pid': proc.pid, 'phase': phase, 'proc': proc, 'log': log, 'created': created}
        return child

    def alive(self, child):
        return child['proc'].poll() is None

    def identity(self, child):
        p = self.psutil.Process(child['pid'])
        if (not self.alive(child) or p.create_time() != child['created']
                or Path(p.exe()).resolve() != self.binary):
            raise ValueError('native executable identity changed')
        return p

    def window(self, child):
        from Xlib import X
        self.identity(child)
        atom = self.display.intern_atom
        root = self.display.screen().root
        prop = root.get_full_property(atom('_NET_CLIENT_LIST_STACKING'), X.AnyPropertyType)
        if prop is None:
            raise ValueError('window manager has no owned-client inventory')
        own = []
        for wid in prop.value:
            win = self.display.create_resource_object('window', int(wid))
            pid = win.get_full_property(atom('_NET_WM_PID'), X.AnyPropertyType)
            if pid is not None and int(pid.value[0]) == child['pid']:
                own.append(win)
            elif win.get_attributes().map_state == X.IsViewable:
                # Dedicated Xvfb must contain no peer clients, even for capture.
                raise ValueError('refusing capture/input with peer native client')
        if len(own) != 1:
            raise ValueError('expected one exact PID-owned native window')
        return own[0]

    def accessibles(self, child):
        p = self.identity(child)
        pids = {p.pid, *(c.pid for c in p.children(recursive=True))}
        nodes = []
        desktop = self.atspi.Registry.getDesktop(0)
        for app in desktop:
            if app.get_process_id() not in pids:
                continue
            pending = [app]
            while pending:
                node = pending.pop()
                nodes.append(node)
                if len(nodes) > 4000:
                    raise ValueError('unbounded accessibility tree')
                pending.extend(list(node))
        return nodes

    def observe(self, child):
        deadline = time.monotonic() + 30
        last = 'no observation'
        while time.monotonic() < deadline:
            if not self.alive(child):
                raise ValueError('application exited before native proof')
            try:
                win = self.window(child)
                nodes = self.accessibles(child)
                rows = [{'role': n.getRoleName(), 'name': n.name} for n in nodes]
                product = any(r['name'] == 'Furchive artwork browser' for r in rows)
                fields = [n for n in nodes if n.name == 'Pool ID']
                values = [n.queryText().getText(0, -1) for n in fields if 'Text' in n.get_interfaces()]
                if not product or self.sentinel not in values:
                    raise ValueError('native product route / persisted Pool ID not observed')
                # Saving through ordinary product controls proves a real settings write,
                # not merely our prelaunch file surviving beside a blank application.
                self.press(child, nodes, 'Load preferences')
                time.sleep(.3)
                nodes = self.accessibles(child)
                self.press(child, nodes, 'Save preferences')
                time.sleep(.3)
                nodes = self.accessibles(child)
                rows = [{'role': n.getRoleName(), 'name': n.name} for n in nodes]
                if not any(r['name'] == 'Preferences saved' for r in rows):
                    raise ValueError('no native acknowledged settings save')
                settings = self.data_root / 'settings.json'
                value = json.loads(settings.read_text())
                if value.get('last_session', {}).get('id') != int(self.sentinel):
                    raise ValueError('persisted settings sentinel changed')
                # Actual opened SQLite descriptors independently establish data root.
                opened = {Path(f.path).resolve() for f in self.identity(child).open_files()}
                if not {self.data_root / 'state.sqlite', self.data_root / 'cache.sqlite'} <= opened:
                    raise ValueError('actual application database root not verified')
                self.window(child)
                from Xlib import X
                from PIL import Image, ImageStat
                geom = win.get_geometry()
                if geom.depth != 24 or geom.width < 720 or geom.height < 480:
                    raise ValueError('unexpected native drawable geometry')
                pixels = win.get_image(0, 0, geom.width, geom.height, X.ZPixmap, 0xffffffff)
                image = Image.frombytes('RGB', (geom.width, geom.height), pixels.data, 'raw', 'BGRX')
                nonblank = max(ImageStat.Stat(image).stddev) > 5
                if not nonblank:
                    raise ValueError('blank native capture')
                file = self.output / f'phase-{child["phase"]}.png'
                image.save(file)
                (self.output / f'phase-{child["phase"]}-accessibility.json').write_text(json.dumps(rows, indent=2))
                self.identity(child)
                return {'pid': child['pid'], 'path': str(self.binary), 'window': str(win.id),
                        'nonce': self.nonce, 'data_root': str(self.data_root),
                        'settings': self.sentinel, 'product': True, 'nonblank': nonblank,
                        'capture': file.name, 'capture_api': 'X11 owned drawable XGetImage',
                        'settings_saved_via': 'AT-SPI normal product action',
                        'opened_databases': sorted(str(p) for p in opened if p.parent == self.data_root)}
            except (ValueError, NotImplementedError) as error:
                last = str(error)
                time.sleep(.25)
        raise TimeoutError('native observation deadline: ' + last)

    def press(self, child, nodes, name):
        self.window(child)
        found = [n for n in nodes if n.name == name and 'Action' in n.get_interfaces()]
        if len(found) != 1:
            raise ValueError('missing or ambiguous ordinary native action: ' + name)
        action = found[0].queryAction()
        indexes = [i for i in range(action.nActions) if action.getName(i) in ('click', 'press', 'activate')]
        if len(indexes) != 1 or not action.doAction(indexes[0]):
            raise ValueError('ordinary accessibility action refused')

    def close(self, child, row):
        self.identity(child)
        win = self.window(child)
        if str(win.id) != row['window']:
            raise ValueError('owned window changed before normal close')
        # wmctrl sends _NET_CLOSE_WINDOW, not XDestroyWindow / kill / global keys.
        subprocess.run(['wmctrl', '-ic', hex(win.id)], check=True, timeout=5)
        return child['proc'].wait(timeout=15)

    def cleanup(self, child):
        proc = child['proc']
        # Never kill by title/name, nor a recycled PID/process group. Session ID
        # plus birth timestamp bounds surviving webview descendants after close.
        owned = []
        for p in self.psutil.process_iter(['pid', 'create_time']):
            try:
                if os.getsid(p.pid) == proc.pid and p.create_time() >= child['created']:
                    owned.append(p)
            except (ProcessLookupError, self.psutil.NoSuchProcess):
                continue
        for p in owned:
            try:
                p.terminate()
            except self.psutil.NoSuchProcess:
                pass
        # Popen alone reaps its direct child. psutil may reap other descendants,
        # but must never consume the root's signal/exit status first.
        descendants = [p for p in owned if p.pid != proc.pid]
        _, alive = self.psutil.wait_procs(descendants, timeout=5)
        for p in alive:
            try:
                p.kill()
            except self.psutil.NoSuchProcess:
                pass
        _, survivors = self.psutil.wait_procs(alive, timeout=5)
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                raise ValueError('owned process did not reap')
        child['log'].close()
        (self.output / f'phase-{child["phase"]}-exit.json').write_text(json.dumps({
            'raw_exit': proc.returncode, 'owned_cleanup_pids': [p.pid for p in owned],
            'survivors': [p.pid for p in survivors], 'note': 'cleanup exits never qualify lifecycle'}))
        if survivors:
            raise ValueError('owned native process survivors')
