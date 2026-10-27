using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
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
        Application.Run(new LayoutFixContext(AppSettings.Load()));
    }
}

internal enum DisplayLanguage
{
    English,
    Hebrew
}

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nahafuchu",
        "settings.json");

    public DisplayLanguage Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "he"
        ? DisplayLanguage.Hebrew
        : DisplayLanguage.English;
    public bool IsEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool IsHebrew => Language == DisplayLanguage.Hebrew;

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (settings is not null && Enum.IsDefined(settings.Language))
                return settings;
        }
        catch
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public AppSettings Clone() => new()
    {
        Language = Language,
        IsEnabled = IsEnabled,
        StartWithWindows = StartWithWindows
    };
}

internal sealed class LayoutFixContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly KeyboardHook _keyboardHook;
    private readonly Control _dispatcher;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _aboutItem;
    private readonly ToolStripMenuItem _helpItem;
    private readonly ToolStripMenuItem _exitItem;
    private AppSettings _settings;
    private bool _operationInProgress;

    public LayoutFixContext(AppSettings settings)
    {
        _settings = settings;
        try
        {
            StartupRegistration.SetEnabled(_settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            Diagnostics.Write($"Could not synchronize startup setting: {exception}");
            _settings.StartWithWindows = false;
        }

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _enabledItem = new ToolStripMenuItem { CheckOnClick = true, Checked = _settings.IsEnabled };
        _enabledItem.Click += (_, _) => ToggleEnabled();
        _settingsItem = new ToolStripMenuItem();
        _settingsItem.Click += (_, _) => ShowSettings();
        _aboutItem = new ToolStripMenuItem();
        _aboutItem.Click += (_, _) => ShowAbout();
        _helpItem = new ToolStripMenuItem();
        _helpItem.Click += (_, _) => OpenHelp();
        _exitItem = new ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitThread();
        _menu.Items.AddRange([_enabledItem, new ToolStripSeparator(), _settingsItem, _helpItem, _aboutItem, new ToolStripSeparator(), _exitItem]);

        _trayIcon = new NotifyIcon
        {
            Text = string.Empty,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true
        };
        ApplyLanguage();

        _dispatcher = new Control();
        _dispatcher.CreateControl();
        _keyboardHook = new KeyboardHook(() => _dispatcher.BeginInvoke(OnF10Pressed), () => _settings.IsEnabled);
        Diagnostics.Write("Nahafuchu started and keyboard hook installed.");
    }

    private void OnF10Pressed()
    {
        if (!_settings.IsEnabled || _operationInProgress)
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
        _trayIcon.BalloonTipTitle = _settings.IsHebrew ? "נהפוכו" : "Nahafuchu";
        _trayIcon.BalloonTipText = LocalizeFailure(message);
        _trayIcon.ShowBalloonTip(1500);
    }

    private string LocalizeFailure(string message)
    {
        if (!_settings.IsHebrew)
            return message;

        return message switch
        {
            "No focused editor was found." => "לא נמצא שדה עריכה פעיל.",
            "The focused item is not an editable text field." => "הפריט הפעיל אינו שדה טקסט לעריכה.",
            "The focused editor did not provide fresh copied text." => "שדה העריכה הפעיל לא סיפק טקסט חדש להעתקה.",
            "The clipboard is currently busy." => "לוח הגזירים תפוס כרגע.",
            "The focused editor changed during conversion." => "שדה העריכה השתנה במהלך ההמרה.",
            "Conversion failed. See Nahafuchu.log for details." => "ההמרה נכשלה. פרטים מופיעים בקובץ Nahafuchu.log.",
            _ => message
        };
    }

    private void ApplyLanguage()
    {
        var hebrew = _settings.IsHebrew;
        _menu.RightToLeft = hebrew ? RightToLeft.Yes : RightToLeft.No;
        _enabledItem.Text = _settings.IsEnabled
            ? (hebrew ? "השבת את נהפוכו" : "Disable Nahafuchu")
            : (hebrew ? "הפעל את נהפוכו" : "Enable Nahafuchu");
        _enabledItem.Checked = _settings.IsEnabled;
        _settingsItem.Text = hebrew ? "הגדרות..." : "Settings...";
        _helpItem.Text = hebrew ? "עזרה" : "Help";
        _aboutItem.Text = hebrew ? "אודות נהפוכו" : "About Nahafuchu";
        _exitItem.Text = hebrew ? "יציאה" : "Exit";
        _trayIcon.Text = hebrew
            ? "נהפוכו: F10 ממיר טקסט ומחליף שפה"
            : "Nahafuchu: F10 converts text layout";
    }

    private void ToggleEnabled()
    {
        _settings.IsEnabled = _enabledItem.Checked;
        try
        {
            _settings.Save();
            Diagnostics.Write(_settings.IsEnabled ? "Conversion enabled from tray menu." : "Conversion paused from tray menu.");
            ApplyLanguage();
        }
        catch (Exception exception)
        {
            Diagnostics.Write($"Could not save enabled setting: {exception}");
            _settings.IsEnabled = !_enabledItem.Checked;
            ApplyLanguage();
            ShowFailure("Conversion failed. See Nahafuchu.log for details.");
        }
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        var updated = dialog.UpdatedSettings;
        try
        {
            StartupRegistration.SetEnabled(updated.StartWithWindows);
            updated.Save();
            _settings = updated;
            ApplyLanguage();
            Diagnostics.Write($"Settings saved: language={_settings.Language}, startWithWindows={_settings.StartWithWindows}.");
        }
        catch (Exception exception)
        {
            Diagnostics.Write($"Could not apply settings: {exception}");
            MessageBox.Show(
                dialog,
                _settings.IsHebrew ? "לא ניתן לשמור את ההגדרות. בדוק את הרשאות המשתמש." : "Settings could not be saved. Check your user permissions.",
                _settings.IsHebrew ? "נהפוכו" : "Nahafuchu",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ShowAbout()
    {
        using var dialog = new AboutDialog(_settings.IsHebrew);
        dialog.ShowDialog();
    }

    private void OpenHelp()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/oby91/Nahafuchu") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Diagnostics.Write($"Could not open GitHub help page: {exception}");
            ShowFailure("Conversion failed. See Nahafuchu.log for details.");
        }
    }

    protected override void ExitThreadCore()
    {
        _keyboardHook.Dispose();
        _dispatcher.Dispose();
        _menu.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class SettingsDialog : Form
{
    private readonly ComboBox _language = new();
    private readonly Label _shortcutLabel = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly bool _hebrew;

    public AppSettings UpdatedSettings { get; }

    public SettingsDialog(AppSettings current)
    {
        UpdatedSettings = current.Clone();
        _hebrew = current.IsHebrew;
        Text = _hebrew ? "הגדרות נהפוכו" : "Nahafuchu Settings";
        ClientSize = new Size(430, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = _hebrew ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = _hebrew;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var languageLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 28),
            Text = _hebrew ? "שפת תצוגה:" : "Display language:"
        };
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Items.AddRange(["English", "עברית"]);
        _language.SelectedIndex = current.Language == DisplayLanguage.Hebrew ? 1 : 0;
        _language.Location = new Point(24, 55);
        _language.Size = new Size(375, 28);

        _shortcutLabel.AutoSize = true;
        _shortcutLabel.Location = new Point(24, 98);
        _shortcutLabel.Text = _hebrew ? "קיצור מקשים: F10" : "Keyboard shortcut: F10";

        _startWithWindows.AutoSize = true;
        _startWithWindows.Checked = current.StartWithWindows;
        _startWithWindows.Location = new Point(24, 128);
        _startWithWindows.Text = _hebrew ? "הפעל את נהפוכו עם הכניסה ל־Windows" : "Start Nahafuchu when I sign in to Windows";

        var save = new Button
        {
            Text = _hebrew ? "שמור" : "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(220, 170),
            Size = new Size(86, 32)
        };
        save.Click += (_, _) =>
        {
            UpdatedSettings.Language = _language.SelectedIndex == 1 ? DisplayLanguage.Hebrew : DisplayLanguage.English;
            UpdatedSettings.StartWithWindows = _startWithWindows.Checked;
        };
        var cancel = new Button
        {
            Text = _hebrew ? "ביטול" : "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(313, 170),
            Size = new Size(86, 32)
        };

        Controls.AddRange([languageLabel, _language, _shortcutLabel, _startWithWindows, save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
    }
}

internal sealed class AboutDialog : Form
{
    public AboutDialog(bool hebrew)
    {
        var name = hebrew ? "נהפוכו" : "Nahafuchu";
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
        Text = hebrew ? "אודות נהפוכו" : "About Nahafuchu";
        ClientSize = new Size(460, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = hebrew ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = hebrew;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var logo = new PictureBox
        {
            Image = Icon.ToBitmap(),
            Location = new Point(24, 22),
            Size = new Size(56, 56),
            SizeMode = PictureBoxSizeMode.StretchImage
        };
        var heading = new Label
        {
            Text = name,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Location = new Point(94, 28),
            Size = new Size(330, 34)
        };
        var description = new Label
        {
            Text = hebrew
                ? "תיקון טקסט שהוקלד בפריסת מקלדת שגויה. F10 ממיר את השורה או את הטקסט המסומן."
                : "Fix text typed with the wrong keyboard layout. F10 converts the current line or selected text.",
            Location = new Point(25, 100),
            Size = new Size(410, 52),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var versionLabel = new Label
        {
            Text = hebrew ? $"גרסה {version}" : $"Version {version}",
            Location = new Point(25, 158),
            Size = new Size(410, 22),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var github = new Button
        {
            Text = hebrew ? "פרויקט ב־GitHub" : "Nahafuchu on GitHub",
            Location = new Point(147, 194),
            Size = new Size(150, 32)
        };
        github.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/oby91/Nahafuchu") { UseShellExecute = true });
        var close = new Button
        {
            Text = hebrew ? "סגור" : "Close",
            DialogResult = DialogResult.OK,
            Location = new Point(310, 194),
            Size = new Size(90, 32)
        };
        Controls.AddRange([logo, heading, description, versionLabel, github, close]);
        AcceptButton = close;
        CancelButton = close;
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nahafuchu";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("Could not open the current user's startup settings.");

        if (enabled)
            key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
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
    private readonly Func<bool> _isEnabled;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly IntPtr _hook;
    private bool _f10Held;

    public KeyboardHook(Action onPressed, Func<bool> isEnabled)
    {
        _onPressed = onPressed;
        _isEnabled = isEnabled;
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
                    if (!_isEnabled())
                        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
                    _f10Held = true;
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    var shouldRun = _f10Held;
                    _f10Held = false;
                    if (shouldRun)
                    {
                        _onPressed();
                        return new IntPtr(1);
                    }
                    return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
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

