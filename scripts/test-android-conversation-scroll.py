"""Physical UI regression using the production conversation and preview with isolated QA messages."""
import argparse, importlib.util, json, re, time
from pathlib import Path
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('device', Path(__file__).with_name('android-device-acceptance.py'))
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)

def bounds(node): return list(map(int, re.findall(r'-?\d+', node.get('bounds'))))
def labels(root): return [(n.get('text') or n.get('content-desc'), bounds(n)) for n in root.iter('node') if n.get('text') or n.get('content-desc')]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True); parser.add_argument('--serial', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args(); device = module.Device(args.adb, args.serial); args.output.mkdir(parents=True, exist_ok=True)
    checks = []
    def capture(name):
        path = args.output / (name + '.png'); device.capture(path)
        return ET.parse(path.with_suffix('.xml')).getroot()
    def check(value, detail):
        checks.append({'check': detail, 'passed': bool(value)})
        (args.output/'checks.json').write_text(json.dumps(checks, ensure_ascii=False, indent=2), encoding='utf-8')
        assert value, detail
    def tail(root, name):
        values = dict(labels(root)); box = values.get(name); composer = values['选择文件']
        check(box is not None, name + ' is visible')
        check(0 < composer[1] - box[3] < 90, name + ' reaches bottom above composer')
    def swipe(dy):
        device.guard(); device.shell('input','swipe','520','1150','520',str(1150+dy),'480'); time.sleep(1)
    device.launch_scene('conversation-scroll'); time.sleep(1)
    capture('initial')
    device.click('接收图片'); time.sleep(2); tail(capture('received-loaded'), 'QA-image-1-0.png')
    device.click('发送图片'); time.sleep(2); tail(capture('sent-loaded'), 'QA-image-2-0.png')
    device.click('接收图片'); time.sleep(2); tail(capture('third-image'), 'QA-image-3-0.png')
    # Scroll up so preview return must preserve a history viewport, not simply return to bottom.
    swipe(420)
    before = capture('before-preview'); before_values = labels(before)
    candidates = [(name,b) for name,b in before_values if name.startswith('QA-image-') and b[1]>620 and b[3]<2170]
    check(bool(candidates), 'historical image fully visible before preview')
    device.click(candidates[0][0]); time.sleep(1); capture('preview')
    device.click('关闭图片预览'); time.sleep(1)
    after = capture('after-preview')
    def anchors(root): return [(name,b) for name,b in labels(root) if name.startswith(('QA-image-', 'QA history'))]
    check(anchors(before) == anchors(after), 'preview return preserves all visible message bounds')
    device.click('接收图片'); time.sleep(2)
    history = capture('incoming-while-reading')
    check(anchors(after) == anchors(history), 'incoming image does not pull reader away from history')
    device.click('发送图片'); time.sleep(2); tail(capture('own-send-from-history'), 'QA-image-5-0.png')
    device.click('多图消息'); time.sleep(2); tail(capture('tall-last-message'), 'QA-image-6-5.png')
    # Production device group headers must keep their own label/chevron geometry during toggles.
    device.launch_scene('device-state-connected'); time.sleep(1)
    for group in ('已连接','离线','附近新设备'):
        before = capture('group-'+group+'-expanded'); values = dict(labels(before))
        key = '收起' + group
        # Localized accessible descriptions have a space in some resource versions.
        hits = [(name,b) for name,b in labels(before) if '收起' in name and group in name]
        check(len(hits)==1, group+' collapse action exists')
        original = values[group]; arrow = hits[0][1]
        device.click(hits[0][0]); time.sleep(.5)
        collapsed = capture('group-'+group+'-collapsed'); newvalues = dict(labels(collapsed)); newlabel = newvalues[group]
        hits2 = [(name,b) for name,b in labels(collapsed) if '展开' in name and group in name]
        check(len(hits2)==1, group+' expand action exists')
        check(original[1:] == newlabel[1:], group+' label stays at same position')
        check(arrow[1:] == hits2[0][1][1:], group+' header height stays stable')
        device.click(hits2[0][0]); time.sleep(.5)
    print(json.dumps({'checks':len(checks),'passed':all(v['passed'] for v in checks)}, ensure_ascii=False))

if __name__=='__main__': main()
