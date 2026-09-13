"""Physical UI checks for the production device list; never clears history or settings."""
import argparse
import importlib.util
import json
import re
import time
from pathlib import Path
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('device', Path(__file__).with_name('android-device-acceptance.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    device = module.Device(args.adb, args.serial)
    args.output.mkdir(parents=True, exist_ok=True)
    checks = []

    def check(passed, description):
        checks.append({'check': description, 'passed': bool(passed)})
        (args.output / 'checks.json').write_text(json.dumps(checks, ensure_ascii=False, indent=2), encoding='utf-8')
        assert passed, description

    def capture(name):
        path = args.output / (name + '.png')
        device.capture(path)
        return ET.parse(path.with_suffix('.xml')).getroot()

    def labels(tree):
        return [n.get('text') or n.get('content-desc') or '' for n in tree.iter('node')]

    def scanning(tree):
        return any('正在扫描附近新设备' in label for label in labels(tree))

    def pull():
        device.guard()
        # Start inside the list below its fixed search box. Coordinates scale to the display.
        size = list(map(int, re.findall(r'\d+', device.shell('wm', 'size').splitlines()[-1])))
        width, height = size[-2:]
        device.shell('input', 'swipe', str(width // 2), str(int(height * .23)),
                     str(width // 2), str(int(height * .56)), '480')

    device.guard()
    initial = capture('initial')
    check('设备与会话' in labels(initial), 'production device list is open')
    check(not any(label in ('扫描', '扫描中') for label in labels(initial)), 'nearby scan button is removed')
    # Let the bounded startup scan finish, including an early connection stop.
    time.sleep(7)
    check(not scanning(capture('startup-finished')), 'startup refresh finishes')
    for index in range(4):
        time.sleep(4)
        check(not scanning(capture(f'idle-{index}')), 'idle does not periodically restart scanning')
    # Short content must still accept a pull gesture, with stable group header heights.
    for name in ('已连接', '离线', '附近新设备'):
        hits = [s for s in labels(capture('before-collapse-' + name)) if '收起' in s and name in s]
        if hits:
            device.click(hits[0])
    collapsed = capture('collapsed')
    def headers(tree):
        return {n.get('text'): n.get('bounds') for n in tree.iter('node')
                if n.get('text') in ('已连接', '离线', '附近新设备')}
    pull()
    # A second pull while active must not extend the original five-second window.
    # Do it before the UI dump, which itself can take several seconds on a phone.
    pull()
    refreshing = capture('pull-refreshing')
    check(scanning(refreshing), 'pull down on short collapsed list starts real scan')
    check(headers(collapsed) == headers(refreshing), 'refresh indicator does not shift group rows')
    time.sleep(6)
    check(not scanning(capture('pull-finished')), 'refresh indicator ends after one scan window')
    device.click('设置')
    capture('settings')
    device.click('会话')
    check(not scanning(capture('returned-from-settings')), 'returning from settings does not rescan')
    device.guard()
    device.shell('input', 'keyevent', 'KEYCODE_HOME')
    time.sleep(1)
    device.shell('am', 'start', '-W', '-n', module.APP + '/.MainActivity', '--ez',
                 'bluelink.acceptance.keep_screen_on', 'true')
    check(not scanning(capture('foreground-again')), 'repeated foreground entry does not rescan')
    for name in ('已连接', '离线', '附近新设备'):
        hits = [s for s in labels(capture('before-expand-' + name)) if '展开' in s and name in s]
        if hits:
            device.click(hits[0])
    capture('final')
    print(json.dumps({'checks': len(checks), 'passed': all(c['passed'] for c in checks)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
