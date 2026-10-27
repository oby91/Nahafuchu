using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Nahafuchu;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, "Local\\Nahafuchu", out var createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new LayoutFixContext());
    }
}

internal sealed class LayoutFixContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly KeyboardHook _keyboardHook;
    private readonly Control _dispatcher;
    private bool _operationInProgress;

    public LayoutFixContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Text = "Nahafuchu: F10 converts text and switches language",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        _dispatcher = new Control();
        _dispatcher.CreateControl();
        _keyboardHook = new KeyboardHook(() => _dispatcher.BeginInvoke(OnF10Pressed));
        Diagnostics.Write("Nahafuchu started and keyboard hook installed.");
    }

    private void OnF10Pressed()
    {
        if (_operationInProgress)
            return;

        _operationInProgress = true;
        _ = ConvertFocusedTextAsync();
    }

    private async Task ConvertFocusedTextAsync()
    {
        try
        {
            var focus = FocusSnapshot.TryCapture();
            if (focus is null)
            {
                Diagnostics.Write("F10 ignored: Windows did not return a focused element.");
                ShowFailure("No focused editor was found.");
                return;
            }

            if (!focus.IsEditable)
            {
                Diagnostics.Write($"F10 ignored: focused element is not editable ({focus.Description}).");
                ShowFailure("The focused item is not an editable text field.");
                return;
            }

            Diagnostics.Write($"F10 accepted: {focus.Description}; selection={focus.HasSelection}.");

            if (!focus.HasSelection)
            {
                NativeInput.PressKey(Keys.Home);
                NativeInput.PressChord(Keys.ShiftKey, Keys.End);
                await Task.Delay(40);

                if (!focus.IsStillActive())
                {
                    Diagnostics.Write("Stopped after selecting the line: focus changed.");
                    return;
                }
            }

            var sourceText = focus.TryGetSelectedText();
            if (!string.IsNullOrEmpty(sourceText))
            {
                Diagnostics.Write("Read selected text directly from the focused editor through UI Automation.");
            }
            else
            {
                Diagnostics.Write("UI Automation returned no selected text; trying guarded clipboard copy.");
                sourceText = await ClipboardExchange.CopyFreshTextAsync(focus);
            }

            if (string.IsNullOrEmpty(sourceText))
            {
                Diagnostics.Write("Stopped: Ctrl+C did not produce fresh text from the focused editor.");
                ShowFailure("The focused editor did not provide fresh copied text.");
                return;
            }

            var converted = KeyboardLayoutConverter.Convert(sourceText);
            Diagnostics.Write($"Conversion produced output: sourceLength={sourceText.Length}, outputLength={converted.Length}.");
            if (converted == sourceText)
            {
                Diagnostics.Write("Stopped: the keyboard-layout conversion did not change the text.");
                return;
            }

            if (!focus.IsStillActive())
            {
                Diagnostics.Write("Stopped before paste: focus changed.");
                return;
            }

            await ClipboardExchange.PasteTextAsync(converted, focus);
            Diagnostics.Write("Conversion pasted successfully.");

            var switchToHebrew = KeyboardLayoutConverter.TargetIsHebrew(sourceText);
            await InputLanguageSwitcher.SwitchAsync(focus, switchToHebrew);
        }
        catch (ExternalException)
        {
            ShowFailure("The clipboard is currently busy.");
        }
        catch (ElementNotAvailableException)
        {
            ShowFailure("The focused editor changed during conversion.");
        }
        catch (Exception exception)
        {
            Diagnostics.Write($"Unexpected failure: {exception}");
            ShowFailure("Conversion failed. See Nahafuchu.log for details.");
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private void ShowFailure(string message)
    {
        _trayIcon.BalloonTipTitle = "Nahafuchu";
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(1500);
    }

    protected override void ExitThreadCore()
    {
        _keyboardHook.Dispose();
        _dispatcher.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkF10 = 0x79;
    private const uint LlkhfInjected = 0x10;

    private readonly Action _onPressed;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly IntPtr _hook;
    private bool _f10Held;

    public KeyboardHook(Action onPressed)
    {
        _onPressed = onPressed;
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            if (data.VkCode == VkF10 && !NativeInput.AnyModifierDown())
            {
                var message = wParam.ToInt32();
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    _f10Held = true;
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    var shouldRun = _f10Held;
                    _f10Held = false;
                    if (shouldRun)
                        _onPressed();
                }

                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_hook);
    }
}

internal sealed class FocusSnapshot
{
    private readonly IntPtr _foregroundWindow;
    private readonly int _processId;
    private readonly string _className;

    public bool IsEditable { get; }
    public bool HasSelection { get; }
    public string Description { get; }
    public IntPtr ForegroundWindow => _foregroundWindow;

    private FocusSnapshot(IntPtr foregroundWindow, int processId, string className, bool isEditable, bool hasSelection, string description)
    {
        _foregroundWindow = foregroundWindow;
        _processId = processId;
        _className = className;
        IsEditable = isEditable;
        HasSelection = hasSelection;
        Description = description;
    }

    public static FocusSnapshot? TryCapture()
    {
        var element = AutomationElement.FocusedElement;
        if (element is null)
            return null;

        var isEditable = IsEditableElement(element);
        var hasSelection = HasTextSelection(element);
        return new FocusSnapshot(
            NativeMethods.GetForegroundWindow(),
            element.Current.ProcessId,
            element.Current.ClassName,
            isEditable,
            hasSelection,
            $"{element.Current.ControlType.ProgrammaticName}, class={element.Current.ClassName}, process={element.Current.ProcessId}");
    }

    public bool IsStillActive()
    {
        if (NativeMethods.GetForegroundWindow() != _foregroundWindow)
            return false;

        var element = AutomationElement.FocusedElement;
        return element is not null
            && element.Current.ProcessId == _processId
            && element.Current.ClassName == _className
            && IsEditableElement(element);
    }

    public string? TryGetSelectedText()
    {
        var element = AutomationElement.FocusedElement;
        if (element is null
            || NativeMethods.GetForegroundWindow() != _foregroundWindow
            || element.Current.ProcessId != _processId
            || !IsEditableElement(element)
            || !element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject))
        {
            return null;
        }

        var selected = ((TextPattern)textObject).GetSelection()
            .Select(range => range.GetText(-1))
            .Where(text => !string.IsNullOrEmpty(text));

        var result = string.Concat(selected);
        return string.IsNullOrEmpty(result) ? null : result;
    }

    public string? TryGetEditorText()
    {
        var element = AutomationElement.FocusedElement;
        if (element is null
            || NativeMethods.GetForegroundWindow() != _foregroundWindow
            || element.Current.ProcessId != _processId
            || !IsEditableElement(element))
        {
            return null;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject))
            return ((ValuePattern)valueObject).Current.Value;

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject))
            return ((TextPattern)textObject).DocumentRange.GetText(-1);

        return null;
    }

    private static bool IsEditableElement(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject))
            return !((ValuePattern)valueObject).Current.IsReadOnly;

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out _))
            return element.Current.IsKeyboardFocusable && element.Current.IsEnabled;

        return element.Current.ControlType == ControlType.Edit
            && element.Current.IsKeyboardFocusable
            && element.Current.IsEnabled;
    }

    private static bool HasTextSelection(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject))
            return false;

        var ranges = ((TextPattern)textObject).GetSelection();
        return ranges.Any(range => !string.IsNullOrEmpty(range.GetText(1)));
    }
}

internal static class Diagnostics
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Nahafuchu.log");

    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal static class ClipboardExchange
{
    public static async Task<string?> CopyFreshTextAsync(FocusSnapshot focus)
    {
        var marker = $"Nahafuchu:{Guid.NewGuid():N}";
        Clipboard.SetText(marker);
        var markerSequence = NativeMethods.GetClipboardSequenceNumber();

        NativeInput.PressChord(Keys.ControlKey, Keys.C);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(20);

            if (NativeMethods.GetClipboardSequenceNumber() == markerSequence)
                continue;

            if (!focus.IsStillActive())
            {
                Diagnostics.Write("Clipboard changed, but the focused editor no longer matched.");
                return null;
            }

            if (!Clipboard.ContainsText())
                return null;

            var text = Clipboard.GetText();
            return text == marker ? null : text;
        }

        Diagnostics.Write("Clipboard sequence number did not change after synthetic Ctrl+C.");
        return null;
    }

    public static async Task PasteTextAsync(string text, FocusSnapshot focus)
    {
        var before = focus.TryGetEditorText();
        Clipboard.SetText(text);
        NativeInput.PressChord(Keys.ControlKey, Keys.V);

        if (before is not null)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(20);
                var current = focus.TryGetEditorText();
                if (current is not null
                    && current != before
                    && current.Contains(text, StringComparison.Ordinal))
                {
                    Diagnostics.Write("Observed the converted text in the editor after paste.");
                    await Task.Delay(150);
                    return;
                }
            }

            Diagnostics.Write("Editor text did not report a change within the paste deadline.");
        }

        await Task.Delay(750);
    }
}

internal sealed class ClipboardSnapshot
{
    private readonly Dictionary<string, object> _data = new(StringComparer.Ordinal);

    public static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot();
        var source = Clipboard.GetDataObject();
        if (source is null)
            return snapshot;

        foreach (var format in source.GetFormats(false))
        {
            try
            {
                var value = source.GetData(format, false);
                if (value is not null)
                    snapshot._data[format] = value;
            }
            catch (ExternalException)
            {
            }
        }

        return snapshot;
    }

    public void Restore()
    {
        try
        {
            var restored = new DataObject();
            foreach (var item in _data)
                restored.SetData(item.Key, false, item.Value);

            Clipboard.SetDataObject(restored, true, 3, 30);
        }
        catch (ExternalException)
        {
        }
    }
}

internal static class KeyboardLayoutConverter
{
    private const string EnglishKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,./";
    private const string HebrewKeys = "/'קראטוןםפ][שדגכעיחלךף,זסבהנמצתץ.";

    private static readonly IReadOnlyDictionary<char, char> EnglishToHebrew =
        EnglishKeys.Zip(HebrewKeys).ToDictionary(pair => pair.First, pair => pair.Second);

    private static readonly IReadOnlyDictionary<char, char> HebrewToEnglish =
        HebrewKeys.Zip(EnglishKeys).ToDictionary(pair => pair.First, pair => pair.Second);

    public static string Convert(string input)
    {
        var hebrewCount = input.Count(IsHebrewLetter);
        var latinCount = input.Count(IsLatinLetter);
        var map = hebrewCount > latinCount ? HebrewToEnglish : EnglishToHebrew;

        var output = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            var lookup = char.IsAsciiLetterUpper(character)
                ? char.ToLowerInvariant(character)
                : character;
            output.Append(map.TryGetValue(lookup, out var converted) ? converted : character);
        }

        return output.ToString();
    }

    public static bool TargetIsHebrew(string input)
    {
        var hebrewCount = input.Count(IsHebrewLetter);
        var latinCount = input.Count(IsLatinLetter);
        return hebrewCount <= latinCount;
    }

    private static bool IsHebrewLetter(char value) => value is >= '\u05D0' and <= '\u05EA';
    private static bool IsLatinLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

internal static class InputLanguageSwitcher
{
    private const uint WmInputLangChangeRequest = 0x0050;
    private const ushort HebrewLanguageId = 0x040D;
    private const ushort EnglishPrimaryLanguageId = 0x0009;

    public static async Task SwitchAsync(FocusSnapshot focus, bool switchToHebrew)
    {
        if (!focus.IsStillActive())
        {
            Diagnostics.Write("Input language was not switched because editor focus changed after conversion.");
            return;
        }

        var localeId = switchToHebrew ? "0000040D" : "00000409";
        var keyboardLayout = NativeMethods.LoadKeyboardLayout(localeId, 0);
        if (keyboardLayout == IntPtr.Zero)
        {
            Diagnostics.Write($"Input language switch failed: LoadKeyboardLayout({localeId}) failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return;
        }

        var requestedLanguageId = (ushort)(keyboardLayout.ToInt64() & 0xFFFF);
        var requestedPrimaryLanguageId = requestedLanguageId & 0x03FF;
        var expectedPrimaryLanguageId = switchToHebrew ? HebrewLanguageId & 0x03FF : EnglishPrimaryLanguageId;
        if (requestedPrimaryLanguageId != expectedPrimaryLanguageId)
        {
            Diagnostics.Write($"Input language switch unavailable: requested locale {localeId}, Windows returned language ID {requestedLanguageId:X4}.");
            return;
        }

        if (!NativeMethods.PostMessage(focus.ForegroundWindow, WmInputLangChangeRequest, IntPtr.Zero, keyboardLayout))
        {
            Diagnostics.Write($"Input language switch request failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return;
        }

        Diagnostics.Write($"Requested input language switch to {(switchToHebrew ? "Hebrew" : "English")} after conversion.");
        var threadId = NativeMethods.GetWindowThreadProcessId(focus.ForegroundWindow, out _);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(50);
            var activeLayout = NativeMethods.GetKeyboardLayout(threadId);
            var activeLanguageId = (ushort)(activeLayout.ToInt64() & 0xFFFF);
            var activePrimaryLanguageId = activeLanguageId & 0x03FF;
            if (activePrimaryLanguageId == expectedPrimaryLanguageId)
            {
                Diagnostics.Write($"Verified active input language: {(switchToHebrew ? "Hebrew" : "English")} (layout {activeLanguageId:X4}).");
                return;
            }
        }

        Diagnostics.Write($"Input language request was posted, but Windows did not report the requested language for target thread {threadId}.");
    }
}

internal static class NativeInput
{
    public static bool AnyModifierDown() =>
        IsDown(Keys.ControlKey) || IsDown(Keys.ShiftKey) || IsDown(Keys.Menu)
        || IsDown(Keys.LWin) || IsDown(Keys.RWin);

    public static void PressKey(Keys key)
    {
        var sequence = key switch
        {
            Keys.Home => "{HOME}",
            Keys.End => "{END}",
            _ => throw new NotSupportedException($"Unsupported key: {key}")
        };

        System.Windows.Forms.SendKeys.SendWait(sequence);
        Diagnostics.Write($"SendKeys sent {sequence}.");
    }

    public static void PressChord(Keys modifier, Keys key)
    {
        if (modifier == Keys.ControlKey && key is Keys.C or Keys.V)
        {
            NativeMethods.KeybdEvent((byte)Keys.ControlKey, 0, 0, UIntPtr.Zero);
            Thread.Sleep(12);
            NativeMethods.KeybdEvent((byte)key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(12);
            NativeMethods.KeybdEvent((byte)key, 0, 2, UIntPtr.Zero);
            Thread.Sleep(12);
            NativeMethods.KeybdEvent((byte)Keys.ControlKey, 0, 2, UIntPtr.Zero);
            Diagnostics.Write($"keybd_event sent Control+{key}.");
            return;
        }

        var sequence = (modifier, key) switch
        {
            (Keys.ShiftKey, Keys.End) => "+{END}",
            _ => throw new NotSupportedException($"Unsupported chord: {modifier}+{key}")
        };

        System.Windows.Forms.SendKeys.SendWait(sequence);
        Diagnostics.Write($"SendKeys sent {sequence}.");
    }

    private static bool IsDown(Keys key) => (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0;

}

internal static class NativeMethods
{
    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    internal static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadKeyboardLayout(string layoutId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern short VkKeyScanEx(char character, IntPtr keyboardLayout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyboardState, StringBuilder buffer, int bufferSize, uint flags, IntPtr keyboardLayout);
}

