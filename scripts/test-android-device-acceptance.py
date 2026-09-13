"""Offline guard regressions: no device commands or input are performed."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location('acceptance', Path(__file__).with_name('android-device-acceptance.py'))
acceptance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acceptance)


def window(owner, title='弹出式窗口', window_id='90f8db3'):
    return (f'  Window #11 Window{{{window_id} u0 {title}}}:\n'
            f'    mOwnerUid=10341 showForAllUsers=false package={owner} appop=NONE\n')


class GuardTests(unittest.TestCase):
    def test_localized_compose_popup_is_identified_by_owner(self):
        state = '  mCurrentFocus=Window{90f8db3 u0 弹出式窗口}\n' + window(acceptance.APP)
        self.assertEqual(acceptance.APP, acceptance.focused_package(state))

    def test_activity_title_cannot_impersonate_bluelink(self):
        state = '  mCurrentFocus=Window{90f8db3 u0 com.bluelink.android/.MainActivity}\n'
        state += window('example.unrelated', 'com.bluelink.android/.MainActivity')
        self.assertEqual('example.unrelated', acceptance.focused_package(state))

    def test_similar_window_id_cannot_match(self):
        state = '  mCurrentFocus=Window{90f8db u0 弹出式窗口}\n' + window(acceptance.APP)
        with self.assertRaises(RuntimeError):
            acceptance.focused_package(state)

    def test_missing_focus_cannot_fall_back_to_background_bluelink(self):
        with self.assertRaises(RuntimeError):
            acceptance.focused_package('  mCurrentFocus=null\n' + window(acceptance.APP))

    def test_unrelated_installer_prompt_is_rejected(self):
        device = object.__new__(acceptance.Device)
        device.foreground = Mock(return_value='com.miui.packageinstaller')
        device.tree = Mock(return_value='<hierarchy><node text="Another app"/></hierarchy>')
        with self.assertRaises(RuntimeError):
            device.guard()

    def test_focus_change_before_input_stops_click(self):
        device = object.__new__(acceptance.Device)
        device.foreground = Mock(side_effect=[acceptance.APP, 'com.android.systemui'])
        device.tree = Mock(return_value='<hierarchy><node text="设置" bounds="[0,0][100,100]"/></hierarchy>')
        device.shell = Mock()
        with self.assertRaises(RuntimeError):
            device.click('设置')
        device.shell.assert_not_called()

    def test_scene_redirected_to_main_activity_is_not_accepted(self):
        device = object.__new__(acceptance.Device)
        device.shell = Mock(return_value='Activity: com.bluelink.android/.MainActivity')
        device.guard = Mock()
        with self.assertRaises(RuntimeError):
            device.launch_scene('settings-privacy')
        device.guard.assert_not_called()

    def test_scene_launch_rechecks_foreground(self):
        device = object.__new__(acceptance.Device)
        device.shell = Mock(return_value='Activity: com.bluelink.android/.AcceptanceActivity')
        device.guard = Mock(side_effect=RuntimeError('Foreground changed'))
        with self.assertRaises(RuntimeError):
            device.launch_scene('settings-privacy')
        device.guard.assert_called_once()


if __name__ == '__main__':
    unittest.main()
