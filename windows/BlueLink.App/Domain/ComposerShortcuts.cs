using System.Windows.Input;

namespace BlueLink.Domain;

internal static class ComposerShortcuts
{
    public const string Enter = "enter";
    public const string ControlEnter = "ctrl-enter";

    public static string Normalize(string? value) => value == ControlEnter ? ControlEnter : Enter;
    public static string HintKey(string? value) => Normalize(value) == ControlEnter
        ? "Ctrl+Enter 发送 · Enter 换行" : "Enter 发送 · Shift+Enter 换行";

    // ImeProcessed and composition confirmation must stay with the input method.
    public static bool IsSendKey(string? value, Key key, ModifierKeys modifiers, bool composing) =>
        !composing && key == Key.Enter && modifiers ==
        (Normalize(value) == ControlEnter ? ModifierKeys.Control : ModifierKeys.None);
}
