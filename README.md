# Nahafuchu

Windows tray app for converting text typed with the wrong English or Hebrew keyboard layout.

Press `F10` while an editable control has focus. If the focused editor has a selection, Nahafuchu converts that selection. Otherwise it selects and converts the current line. After conversion, it asks Windows to switch to the matching input language.

Nahafuchu avoids LangOver's stale clipboard failure in two ways:

1. It asks Windows UI Automation whether the selection belongs to the focused editable control.
2. Before `Ctrl+C`, it writes a unique marker to the clipboard and requires Windows' clipboard sequence number to change. An unchanged clipboard is treated as a failed copy and nothing is pasted.

The low-level keyboard hook consumes plain `F10`, so the foreground application does not also open its menu.

## Portable version

Unzip `Nahafuchu-Portable-1.0.2.zip` to a folder you can write to and run `Nahafuchu.exe`. The app does not need administrator rights.

## Installer version

Run `Nahafuchu-Setup-1.0.2.exe` to install per user under `%LOCALAPPDATA%\Programs\Nahafuchu`. Setup adds a Start menu shortcut and an uninstall entry. No administrator rights are needed.

For a silent per-user installation, run `Nahafuchu-Setup-1.0.2.exe /S`. Silent setup uses the default install folder, creates a Start menu shortcut and uninstall entry, and does not show dialogs or launch the app. It returns exit code `0` on success and `1` on failure. Failures are logged to `%TEMP%\Nahafuchu-Setup.log`.

