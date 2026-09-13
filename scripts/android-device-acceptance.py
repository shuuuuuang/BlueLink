"""Scoped UI checks on an explicitly selected physical BlueLink device.

Never launches an emulator, changes device configuration, or clears app data.
Every input and screenshot checks the foreground package again. Installer and
permission prompts additionally have to identify BlueLink in their UI text.
"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

APP = 'com.bluelink.android'
PERMISSION_HOSTS = {
    'com.google.android.permissioncontroller', 'com.android.permissioncontroller',
    'com.miui.securitycenter', 'com.miui.packageinstaller',
    'com.android.packageinstaller', 'com.google.android.packageinstaller',
}


def focused_package(state):
    # Compose sheets can be titled "弹出式窗口" rather than an activity name.
    # Resolve the exact focused window's owner, never trust its display title.
    focus = re.search(r'mCurrentFocus=Window\{([\w]+)\b', state)
    if focus:
        window = re.search(r'^  Window #\d+ Window\{' + re.escape(focus[1]) +
                           r'\b[^\n]*\n(.*?)(?=^  Window #|\Z)', state, re.M | re.S)
        if window:
            owner = re.search(r'^\s*mOwnerUid=\d+ [^\n]*\bpackage=([\w.]+)', window[1], re.M)
            if owner:
                return owner[1]
    raise RuntimeError('No verified focused window owner; input and capture stopped')


class Device:
    def __init__(self, adb, serial):
        self.command = [adb, '-s', serial]
        if serial.startswith('emulator-') or self.shell('getprop', 'ro.kernel.qemu').strip() == '1':
            raise RuntimeError('Physical device required; emulator use is prohibited')

    def run(self, *args, binary=False):
        result = subprocess.run(self.command + list(args), capture_output=True,
                                timeout=30, check=True)
        return result.stdout if binary else result.stdout.decode('utf-8', errors='replace')

    def shell(self, *args):
        return self.run('shell', *args)

    def foreground(self):
        return focused_package(self.shell('dumpsys', 'window'))

    def tree(self):
        self.shell('uiautomator', 'dump', '/data/local/tmp/bluelink-acceptance.xml')
        return self.shell('cat', '/data/local/tmp/bluelink-acceptance.xml')

    def guard(self):
        package = self.foreground()
        if package == APP:
            return package
        if package in PERMISSION_HOSTS:
            content = self.tree()
            if any(name in content for name in ['蓝联', 'BlueLink', APP]):
                return package
        raise RuntimeError('Stopped outside BlueLink or its identified permission/install prompt: ' + package)

    def capture(self, path):
        self.guard()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.with_suffix('.xml').write_text(self.tree(), encoding='utf-8')
        self.guard()
        path.write_bytes(self.run('exec-out', 'screencap', '-p', binary=True))

    def launch_scene(self, scene, theme='light', language='zh-CN'):
        if not re.fullmatch(r'[a-zA-Z0-9-]+', scene):
            raise ValueError('Invalid BlueLink scene')
        if theme not in ('light', 'dark') or language not in ('zh-CN', 'zh-TW', 'en'):
            raise ValueError('Invalid BlueLink appearance')
        # An existing launcher task can otherwise receive the intent in MainActivity.
        result = self.shell('am', 'start', '-W', '-f', '0x18000000', '-n',
                            APP + '/.AcceptanceActivity', '--es', 'scene', scene,
                            '--es', 'theme', theme, '--es', 'language', language)
        if 'Activity: ' + APP + '/.AcceptanceActivity' not in result:
            raise RuntimeError('Expected BlueLink AcceptanceActivity; scene launch was redirected')
        self.guard()
        return result

    def click(self, label, long=False):
        self.guard()
        nodes = ET.fromstring(self.tree()).iter('node')
        hits = [n for n in nodes if label in (n.get('text'), n.get('content-desc'))]
        if len(hits) != 1:
            raise RuntimeError(f'Expected one exact UI label {label!r}, found {len(hits)}')
        left, top, right, bottom = map(int, re.findall(r'\d+', hits[0].get('bounds')))
        self.guard()
        x, y = str((left + right) // 2), str((top + bottom) // 2)
        if long:
            self.shell('input', 'swipe', x, y, x, y, '650')
        else:
            self.shell('input', 'tap', x, y)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('action', choices=['capture', 'click', 'longclick', 'back', 'status'])
    parser.add_argument('value', nargs='?')
    args = parser.parse_args()
    device = Device(args.adb, args.serial)
    if args.action == 'capture':
        device.capture(Path(args.value))
    elif args.action in ('click', 'longclick'):
        device.click(args.value, long=args.action == 'longclick')
    elif args.action == 'back':
        device.guard()
        device.shell('input', 'keyevent', 'KEYCODE_BACK')
    else:
        print(json.dumps({'foreground': device.foreground(), 'physicalDevice': True}))


if __name__ == '__main__':
    main()
