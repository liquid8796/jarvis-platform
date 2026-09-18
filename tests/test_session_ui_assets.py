"""Deterministic checks for the new WPF pages; rendering is covered by Jarvis.Agent.UiSmoke."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / 'jarvis-agent/src/Jarvis.Agent.Desktop'
XAML = '{http://schemas.microsoft.com/winfx/2006/xaml}'
WPF = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'

def luminance(hex_color):
    channels = [int(hex_color[i:i+2], 16) / 255 for i in (1, 3, 5)]
    linear = [c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4 for c in channels]
    return sum(c * k for c, k in zip(linear, (.2126, .7152, .0722)))

def contrast(a, b):
    lo, hi = sorted((luminance(a), luminance(b)))
    return (hi + .05) / (lo + .05)

class SessionUiAssets(unittest.TestCase):
    def test_semantic_text_pairs_meet_normal_text_contrast(self):
        root = ET.parse(UI / 'Themes/Controls.xaml').getroot()
        colors = {node.attrib[XAML + 'Key']: node.attrib['Color'] for node in root if node.tag == WPF + 'SolidColorBrush'}
        colors['OnAccent'] = '#FFFFFF' if colors['OnAccent'].lower() == 'white' else colors['OnAccent']
        for text, background in [('Text', 'Panel'), ('Muted', 'Panel'), ('Danger', 'Panel'), ('Warning', 'Panel'), ('OnAccent', 'Accent')]:
            with self.subTest(pair=(text, background)):
                self.assertGreaterEqual(contrast(colors[text], colors[background]), 4.5)

    def test_primary_actions_and_filter_have_accessible_names(self):
        for file in ['ExecutionSettingsView.xaml', 'SessionsView.xaml']:
            root = ET.parse(UI / 'Views' / file).getroot()
            for element in root.iter(WPF + 'TextBox'):
                self.assertTrue(element.get('AutomationProperties.Name'), file)
        limits = (UI / 'Views/ExecutionSettingsView.xaml').read_text(encoding='utf-8')
        sessions = (UI / 'Views/SessionsView.xaml').read_text(encoding='utf-8')
        self.assertIn('Modifiers="Control"', limits)
        self.assertIn('Key="F5"', sessions)

    def test_details_can_scroll_at_minimum_height_and_list_is_bounded(self):
        root = ET.parse(UI / 'Views/SessionsView.xaml').getroot()
        self.assertIsNotNone(root.find(WPF + 'ScrollViewer'))
        text = (UI / 'Views/SessionsView.xaml').read_text(encoding='utf-8')
        self.assertIn('VirtualizingPanel.VirtualizationMode="Recycling"', text)
        self.assertIn('RowDefinition Height="260"', text)

    def test_session_rows_show_selectable_name_and_id(self):
        sessions = (UI / 'Views/SessionsView.xaml').read_text(encoding='utf-8')
        controls = (UI / 'Themes/SessionControls.xaml').read_text(encoding='utf-8')
        self.assertIn('Text="{Binding Label, Mode=OneWay}"', sessions)
        self.assertIn('Text="{Binding SessionId, Mode=OneWay}"', sessions)
        self.assertGreaterEqual(sessions.count('Style="{StaticResource SessionSelectableText}"'), 2)
        self.assertIn('x:Key="SessionSelectableText"', controls)
        self.assertIn('Property="IsReadOnly" Value="True"', controls)
        self.assertIn('Property="IsReadOnlyCaretVisible" Value="True"', controls)
        self.assertIn('Property="KeyboardNavigation.IsTabStop" Value="False"', controls)
        self.assertIn('Property="Cursor" Value="IBeam"', controls)

    def test_new_controls_have_keyboard_focus_style(self):
        source = (UI / 'Themes/SessionControls.xaml').read_text(encoding='utf-8')
        self.assertIn('x:Key="SessionFocus"', source)
        self.assertGreaterEqual(source.count('Property="FocusVisualStyle"'), 2)

if __name__ == '__main__':
    unittest.main(verbosity=2)
