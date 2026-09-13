"""Regression for the normal BlueLink conversation above the physical device IME.

Requires a connected peer and an empty draft. Only types/removes a known QA draft;
never sends a message, changes keyboard settings, or opens other applications.
Uses the same scoped foreground guard as other physical-device acceptance tests.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import time
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('device', Path(__file__).with_name('android-device-acceptance.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def bounds(node):
    return list(map(int, re.findall(r'\d+', node.get('bounds'))))


def field(root):
    parents = {child: parent for parent in root.iter() for child in parent}
    labels = [node for node in root.iter('node') if node.get('content-desc') == '输入消息']
    assert len(labels) == 1, 'Open a normal Chinese conversation with a connected peer'
    node = labels[0]
    while node.get('class') != 'android.widget.EditText':
        node = parents[node]
    return node


def named(root, label):
    hits = [node for node in root.iter('node') if node.get('content-desc') == label]
    assert len(hits) == 1, label
    return hits[0]


def ime_top(window):
    hits = re.findall(r'type=ime frame=\[\d+,(\d+)\]\[\d+,\d+\][^\n]*visible=true', window)
    return int(hits[0]) if hits else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--peer-name', help='Open this uniquely named peer from the BlueLink home screen')
    args = parser.parse_args()
    device = module.Device(args.adb, args.serial)
    device.guard()
    if args.peer_name:
        device.click(args.peer_name)
        time.sleep(0.8)
    args.output.mkdir(parents=True, exist_ok=True)
    density = int(re.findall(r'(?:Physical|Override) density: (\d+)', device.shell('wm', 'density'))[-1]) / 160
    results = []
    draft = ''
    baseline = None

    def tree():
        device.guard()
        return ET.fromstring(device.tree())

    def key(*keys):
        device.guard()
        device.shell('input', 'keyevent', *keys)

    def focus():
        node = field(tree())
        assert node.get('enabled') == 'true', 'Connected conversation required'
        x1, y1, x2, y2 = bounds(node)
        device.guard()
        device.shell('input', 'tap', str((x1+x2)//2), str((y1+y2)//2))
        time.sleep(0.8)

    def snapshot(name, keyboard):
        nonlocal baseline
        device.capture(args.output / (name + '.png'))
        root = ET.parse(args.output / (name + '.xml')).getroot()
        window = device.shell('dumpsys', 'window')
        assert re.search(r'mCurrentFocus=.*com\.bluelink\.android/\.?(?:com\.bluelink\.android\.)?MainActivity', window), 'Normal MainActivity required'
        top = ime_top(window)
        assert (top is not None) == keyboard, 'Unexpected keyboard state'
        header = bounds(named(root, '返回设备与会话'))
        composer = bounds(field(root))
        buttons = [bounds(named(root, label)) for label in ('选择文件', '发送消息')]
        if baseline is None:
            baseline = {'header': header, 'composer': composer, 'buttons': buttons}
        assert header == baseline['header'] and header[3] > header[1], 'Keyboard panned the conversation header'
        assert abs(buttons[0][3] - buttons[1][3]) <= 1, 'Buttons not bottom-aligned'
        # UIA reports the editable text area after four lines, but a 48dp touch
        # target for a short field. Compare the visible border, not those mixed bounds.
        input_bottom = bounds(named(root, '输入消息'))[3] + 10 * density
        button_bottom = buttons[0][3] - 4 * density
        assert abs(input_bottom - button_bottom) <= 2, 'Visible input/button bottoms differ'
        if keyboard:
            assert max(composer[3], buttons[0][3], buttons[1][3]) < top, 'Composer overlaps keyboard'
            assert 6 * density <= top - input_bottom <= 10 * density, 'Duplicate/excessive bottom inset'
        elif not draft:
            assert composer == baseline['composer'], 'Composer failed to return after hiding IME'
        assert field(root).get('text', '').replace('\\n', '\n') == draft, 'Unexpected draft content'
        result = {'state': name, 'imeTop': top, 'header': header, 'input': composer, 'buttons': buttons}
        results.append(result)
        print(json.dumps(result, ensure_ascii=False), flush=True)
        (args.output / 'geometry.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')

    def type_line(text, newline=False):
        nonlocal draft
        assert field(tree()).get('text', '').replace('\\n', '\n') == draft, 'Draft changed outside the test'
        if newline:
            key('KEYCODE_ENTER')
            draft += '\n'
        device.guard()
        device.shell('input', 'text', text)
        draft += text
        time.sleep(0.4)

    def clear_test_draft():
        nonlocal draft
        if not draft:
            return
        node = field(tree())
        assert node.get('text', '').replace('\\n', '\n') == draft and node.get('focused') == 'true', 'Refusing to clear an unexpected draft'
        key('KEYCODE_MOVE_END')
        key(*(['KEYCODE_DEL'] * len(draft)))
        draft = ''
        assert field(tree()).get('text', '') == '', 'QA draft did not clear'

    assert field(tree()).get('text', '') == '', 'Keep the existing user draft; test requires an empty composer'
    try:
        snapshot('closed-empty', False)
        focus()
        snapshot('open-empty', True)
        type_line('BlueLink-IME-QA')
        snapshot('open-single', True)
        for index in range(2, 7):
            type_line('Line-' + str(index), newline=True)
        snapshot('open-multiline-scroll', True)
        clear_test_draft()
        key('KEYCODE_BACK')
        time.sleep(0.8)
        snapshot('closed-restored', False)
        focus()
        snapshot('open-again', True)
        key('KEYCODE_BACK')
        time.sleep(0.8)
        snapshot('closed-final', False)
    finally:
        if draft:
            clear_test_draft()
    print('Normal conversation IME regression passed; no messages sent.', flush=True)


if __name__ == '__main__':
    main()
