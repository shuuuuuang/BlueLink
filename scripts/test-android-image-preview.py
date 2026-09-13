"""Physical UI regression: completed original pixels must win over a readable thumbnail."""
import argparse
import importlib.util
import json
import io
import time
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageCms

spec = importlib.util.spec_from_file_location('device', Path(__file__).with_name('android-device-acceptance.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--theme', choices=['light', 'dark'], default='light')
    parser.add_argument('--quick', action='store_true', help='Use one original for a second theme pass')
    args = parser.parse_args()
    device = module.Device(args.adb, args.serial)
    args.output.mkdir(parents=True, exist_ok=True)
    checks = []

    def check(value, label):
        checks.append({'check': label, 'passed': bool(value)})
        (args.output / 'checks.json').write_text(json.dumps(checks, ensure_ascii=False, indent=2), encoding='utf-8')
        assert value, label

    def capture(name):
        path = args.output / (name + '.png')
        device.capture(path)
        root = ET.parse(path.with_suffix('.xml')).getroot()
        labels = [n.get('text') or n.get('content-desc') for n in root.iter('node')]
        return path, labels

    def pixels(path, original=True, portrait=None):
        with Image.open(path) as image:
            # Android captures can be Display P3: compare fixture colors in sRGB.
            profile = image.info.get('icc_profile')
            rgb = ImageCms.profileToProfile(image, ImageCms.ImageCmsProfile(io.BytesIO(profile)),
                                            ImageCms.createProfile('sRGB'), outputMode='RGB') if profile else image.convert('RGB')
            colors = rgb.getdata()
            green = sum(g > 200 and r < 50 and b < 50 for r, g, b in colors)
            magenta = sum(r > 200 and g < 50 and b > 200 for r, g, b in colors)
            check(green > rgb.width * rgb.height * .025 if original else green == 0,
                  path.stem + ': original green pixels' if original else path.stem + ': no original pixels')
            check(magenta == 0, path.stem + ': thumbnail magenta pixels absent')
            if portrait is not None:
                mask = rgb.point(lambda _: 0).convert('L')
                mask.putdata([255 if g > 200 and r < 50 and b < 50 else 0 for r, g, b in rgb.getdata()])
                left, top, right, bottom = mask.getbbox()
                check((bottom - top > right - left) == portrait, path.stem + ': rendered rotation orientation')

    # Reset only the isolated scene, including after a failed run left its preview open.
    device.launch_scene('thumbnail-transfer')
    device.launch_scene('image-preview-original', theme=args.theme)
    for _ in range(20):
        time.sleep(1)
        device.guard()
        tree = device.tree()
        if 'QA decoder PASS' in tree or 'QA FAIL' in tree:
            break
    _, labels = capture('decoder-and-chat')
    check('QA decoder PASS 8' in labels, '8 on-device decoder checks pass: file/content original, cache, missing, incomplete, corrupt, bounded large, replacement')
    cases = [('文件原图', 'original.png', '2400 × 1600'),
                                          ('内容原图', 'content-original.png', '2400 × 1600'),
                                          ('大图', 'large.png', '8192 × 2048')]
    for button, filename, dimensions in cases[:1] if args.quick else cases:
        device.click(button)
        time.sleep(.6)
        device.click(filename)
        time.sleep(1)
        path, labels = capture(filename.replace('.png', '-preview'))
        check(any(dimensions in (label or '') for label in labels), filename + ': source metadata visible')
        pixels(path)
        device.click('放大图片')
        time.sleep(.3)
        device.click('向右旋转')
        time.sleep(.3)
        path, labels = capture(filename.replace('.png', '-zoom-rotate'))
        check('125%' in labels, filename + ': zoom control works')
        pixels(path, portrait=True)
        device.click('向左旋转')
        time.sleep(.3)
        path, labels = capture(filename.replace('.png', '-rotate-left'))
        check('125%' in labels, filename + ': left rotation preserves zoom')
        pixels(path, portrait=False)
        device.click('向左旋转')
        device.click('重置图片')
        time.sleep(.3)
        path, labels = capture(filename.replace('.png', '-reset'))
        check('100%' in labels, filename + ': reset works')
        pixels(path, portrait=False)
        device.click('关闭图片预览')
        time.sleep(.4)
    device.click('原图丢失')
    time.sleep(.6)
    device.click('missing-original.png')
    time.sleep(1)
    path, labels = capture('missing-original-preview')
    check('图片无法读取，文件可能已移动或删除' in labels, 'missing original reports unavailable despite readable thumbnail')
    pixels(path, original=False)
    device.click('关闭图片预览')
    print(json.dumps({'checks': len(checks), 'passed': all(v['passed'] for v in checks)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
