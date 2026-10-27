using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NahafuchuSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(argument => argument.Equals("/S", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("/silent", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                SetupWizard.InstallSilently();
                return 0;
            }
            catch (Exception exception)
            {
                SetupWizard.WriteFailureLog(exception);
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupWizard());
        return 0;
    }
}

internal sealed class SetupWizard : Form
{
    private const string AppName = "Nahafuchu";
    private const string AppVersion = "1.0.2";
    private const string PayloadResource = "NahafuchuSetup.Payload.exe";
    private const string UninstallerResource = "NahafuchuSetup.Uninstall.ps1";

    private readonly Panel _content = new();
    private readonly Label _heading = new();
    private readonly Label _subheading = new();
    private readonly Button _backButton = new();
    private readonly Button _nextButton = new();
    private readonly Button _cancelButton = new();
    private string _destination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppName);
    private bool _createDesktopShortcut;
    private CheckBox? _desktopShortcut;
    private CheckBox? _launchAfterInstall;
    private int _page;

    public SetupWizard()
    {
        Text = "Nahafuchu Setup";
        ClientSize = new Size(640, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var header = new Panel
        {
            BackColor = Color.FromArgb(245, 247, 250),
            Location = new Point(0, 0),
            Size = new Size(640, 88)
        };
        var logo = new PictureBox
        {
            Image = Icon.ToBitmap(),
            Location = new Point(24, 18),
            Size = new Size(52, 52),
            SizeMode = PictureBoxSizeMode.StretchImage
        };
        _heading.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        _heading.Location = new Point(94, 17);
        _heading.Size = new Size(520, 32);
        _subheading.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _subheading.ForeColor = Color.FromArgb(90, 98, 108);
        _subheading.Location = new Point(96, 51);
        _subheading.Size = new Size(520, 22);
        header.Controls.AddRange([logo, _heading, _subheading]);

        _content.Location = new Point(0, 88);
        _content.Size = new Size(640, 254);
        _content.Padding = new Padding(28, 24, 28, 18);

        var footer = new Panel
        {
            BackColor = Color.FromArgb(245, 247, 250),
            Location = new Point(0, 342),
            Size = new Size(640, 78)
        };
        var separator = new Panel
        {
            BackColor = Color.FromArgb(220, 224, 230),
            Location = new Point(0, 0),
            Size = new Size(640, 1)
        };
        _backButton.Location = new Point(292, 23);
        _backButton.Size = new Size(92, 32);
        _backButton.Text = "< Back";
        _backButton.Click += (_, _) =>
        {
            _page = Math.Max(0, _page - 1);
            RenderPage();
        };

        _nextButton.Location = new Point(390, 23);
        _nextButton.Size = new Size(104, 32);
        _nextButton.Text = "Next >";
        _nextButton.Click += NextClicked;

        _cancelButton.Location = new Point(500, 23);
        _cancelButton.Size = new Size(92, 32);
        _cancelButton.Text = "Cancel";
        _cancelButton.Click += (_, _) => Close();

        footer.Controls.AddRange([separator, _backButton, _nextButton, _cancelButton]);
        Controls.AddRange([_content, footer, header]);
        AcceptButton = _nextButton;
        CancelButton = _cancelButton;

        RenderPage();
    }

    private void RenderPage()
    {
        _content.Controls.Clear();
        _desktopShortcut = null;
        _launchAfterInstall = null;

        switch (_page)
        {
            case 0:
                _heading.Text = "Welcome to Nahafuchu Setup";
                _subheading.Text = "This wizard will guide you through the installation.";
                AddBody("Nahafuchu converts text typed with the wrong Hebrew or English keyboard layout.\n\nClick Next to continue, or Cancel to exit Setup.");
                break;
            case 1:
                _heading.Text = "Choose Install Location";
                _subheading.Text = "Select the folder where Nahafuchu should be installed.";
                AddBody("Setup will install Nahafuchu for your Windows user. No administrator rights are needed.");
                AddDestinationControls();
                break;
            case 2:
                _heading.Text = "Ready to Install";
                _subheading.Text = "Setup is ready to begin installing Nahafuchu.";
                AddBody($"Destination folder:\n{_destination}\n\nClick Install to copy the program files and create your shortcuts.");
                break;
            case 3:
                _heading.Text = "Installing Nahafuchu";
                _subheading.Text = "Please wait while Setup installs Nahafuchu.";
                AddInstallingControls();
                break;
            case 4:
                _heading.Text = "Installation Complete";
                _subheading.Text = "Nahafuchu has been installed successfully.";
                AddBody("Click Finish to close Setup.");
                _launchAfterInstall = new CheckBox
                {
                    AutoSize = true,
                    Checked = true,
                    Location = new Point(30, 115),
                    Text = "Launch Nahafuchu now"
                };
                _content.Controls.Add(_launchAfterInstall);
                break;
        }

        _backButton.Enabled = _page is 1 or 2;
        _backButton.Visible = _page is not (0 or 3 or 4);
        _cancelButton.Visible = _page is not 4;
        _cancelButton.Enabled = _page != 3;
        _nextButton.Enabled = true;
        _nextButton.Visible = true;
        _nextButton.Text = _page switch
        {
            2 => "Install",
            3 => "Installing...",
            4 => "Finish",
            _ => "Next >"
        };
        _nextButton.Enabled = _page != 3;
    }

    private void AddBody(string text)
    {
        var body = new Label
        {
            Font = new Font("Segoe UI", 10),
            Location = new Point(30, 28),
            Size = new Size(548, 150),
            Text = text
        };
        _content.Controls.Add(body);
    }

    private void AddDestinationControls()
    {
        var label = new Label
        {
            AutoSize = true,
            Location = new Point(30, 86),
            Text = "Install Nahafuchu to:"
        };
        var path = new TextBox
        {
            Location = new Point(30, 112),
            Size = new Size(424, 27),
            Text = _destination
        };
        path.TextChanged += (_, _) => _destination = path.Text.Trim();

        var browse = new Button
        {
            Location = new Point(464, 110),
            Size = new Size(114, 30),
            Text = "Browse..."
        };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose the folder where Nahafuchu will be installed",
                SelectedPath = Directory.Exists(_destination) ? _destination : Path.GetDirectoryName(_destination) ?? _destination,
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                path.Text = dialog.SelectedPath;
            }
        };

        _desktopShortcut = new CheckBox
        {
            AutoSize = true,
            Checked = _createDesktopShortcut,
            Location = new Point(30, 158),
            Text = "Create a desktop shortcut"
        };
        _desktopShortcut.CheckedChanged += (_, _) => _createDesktopShortcut = _desktopShortcut.Checked;
        _content.Controls.AddRange([label, path, browse, _desktopShortcut]);
    }

    private void AddInstallingControls()
    {
        var message = new Label
        {
            AutoSize = true,
            Location = new Point(30, 55),
            Text = "Copying files and creating shortcuts..."
        };
        var progress = new ProgressBar
        {
            Location = new Point(30, 88),
            Size = new Size(548, 24),
            Style = ProgressBarStyle.Marquee
        };
        _content.Controls.AddRange([message, progress]);
    }

    private async void NextClicked(object? sender, EventArgs e)
    {
        if (_page == 1 && !ValidateDestination())
            return;

        if (_page == 2)
        {
            _page = 3;
            RenderPage();
            await InstallAsync();
            return;
        }

        if (_page == 4)
        {
            if (_launchAfterInstall?.Checked == true)
                StartInstalledApp();
            Close();
            return;
        }

        _page++;
        RenderPage();
    }

    private bool ValidateDestination()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_destination))
                throw new ArgumentException("Choose a valid destination folder.");
            _destination = Path.GetFullPath(_destination);
            if (_destination.Length < 4)
                throw new ArgumentException("Choose a valid destination folder.");
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Nahafuchu Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private async Task InstallAsync()
    {
        await Task.Delay(120);
        try
        {
            var executable = Path.Combine(_destination, "Nahafuchu.exe");
            await Task.Run(() =>
            {
                StopRunningNahafuchu();
                Directory.CreateDirectory(_destination);
                ExtractResource(PayloadResource, executable);
                ExtractResource(UninstallerResource, Path.Combine(_destination, "Uninstall.ps1"));
            });

            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName, $"{AppName}.lnk"),
                executable,
                _destination);
            if (_createDesktopShortcut)
            {
                CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"),
                    executable,
                    _destination);
            }

            RegisterUninstaller(_destination, executable);
            _page = 4;
            RenderPage();
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            MessageBox.Show(
                this,
                $"Nahafuchu could not be installed.\n\n{exception.Message}\n\nDetails: {Path.Combine(Path.GetTempPath(), "Nahafuchu-Setup.log")}",
                "Nahafuchu Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _page = 2;
            RenderPage();
        }
    }

    private static void StopRunningNahafuchu()
    {
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                        throw new IOException("An existing Nahafuchu instance did not close.");
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception exception)
                {
                    throw new IOException("Close the running Nahafuchu application and try Setup again.", exception);
                }
            }
        }
    }

    internal static void InstallSilently()
    {
        var destination = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppName);
        var executable = Path.Combine(destination, "Nahafuchu.exe");

        StopRunningNahafuchu();
        Directory.CreateDirectory(destination);
        ExtractResource(PayloadResource, executable);
        ExtractResource(UninstallerResource, Path.Combine(destination, "Uninstall.ps1"));
        CreateShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName, $"{AppName}.lnk"),
            executable,
            destination);
        RegisterUninstaller(destination, executable);
    }

    private static void ExtractResource(string resourceName, string destination)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Setup payload is missing: {resourceName}");
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private static void CreateShortcut(string shortcutPath, string executable, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is not available to create shortcuts.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not initialize Windows Script Host.");
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = workingDirectory;
                shortcut.IconLocation = $"{executable},0";
                shortcut.Save();
            }
            finally
            {
                if (Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            if (Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void RegisterUninstaller(string destination, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nahafuchu",
            writable: true) ?? throw new IOException("Could not register Nahafuchu in Windows Apps and Features.");
        key.SetValue("DisplayName", AppName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
        key.SetValue("Publisher", AppName, RegistryValueKind.String);
        key.SetValue("InstallLocation", destination, RegistryValueKind.String);
        key.SetValue("DisplayIcon", executable, RegistryValueKind.String);
        key.SetValue(
            "UninstallString",
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(destination, "Uninstall.ps1")}\"",
            RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private void StartInstalledApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_destination, "Nahafuchu.exe"))
            {
                UseShellExecute = true,
                WorkingDirectory = _destination
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Nahafuchu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static void WriteFailureLog(Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "Nahafuchu-Setup.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

