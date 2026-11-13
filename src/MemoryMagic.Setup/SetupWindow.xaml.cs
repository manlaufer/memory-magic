using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MemoryMagic.Setup;

public partial class SetupWindow : Window
{
    private const string EmbeddedPayloadName = "MemoryMagic.Setup.Resources.MemoryMagic.exe.gz";
    private const string EmbeddedGuideName = "MemoryMagic.Setup.Resources.UserGuide.html";
    private const long RequiredSpaceBytes = 75L * 1024L * 1024L;

    private readonly bool _isAdmin;
    private readonly Grid[] _pages;
    private readonly TextBlock[] _stepLabels;
    private int _currentPageIndex;
    private bool _isInstalling;
    private string _installedExePath = string.Empty;
    private string _installedGuidePath = string.Empty;

    public SetupWindow(string? initialInstallDir = null)
    {
        InitializeComponent();

        MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height - 24);
        MaxWidth = Math.Max(600, SystemParameters.WorkArea.Width - 24);

        _isAdmin = IsAdministrator();
        _pages = new[] { Page0Welcome, Page1Location, Page2Options, Page3Progress, Page4Finish };
        _stepLabels = new[] { StepLabel0, StepLabel1, StepLabel2, StepLabel3, StepLabel4 };

        string defaultDir = !string.IsNullOrWhiteSpace(initialInstallDir)
            ? initialInstallDir
            : _isAdmin
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MemoryMagic")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MemoryMagic");

        InstallPathTextBox.Text = defaultDir;
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (TryFindResource("HeaderShimmerStoryboard") is Storyboard sb)
            sb.Begin(this);

        ValidateInstallPath();
        ShowPage(0, animate: false);
    }

    private void ShowPage(int index, bool animate = true)
    {
        _currentPageIndex = Math.Clamp(index, 0, _pages.Length - 1);

        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == _currentPageIndex ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _stepLabels.Length; i++)
        {
            if (i == _currentPageIndex)
            {
                _stepLabels[i].Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                _stepLabels[i].FontWeight = FontWeights.SemiBold;
            }
            else if (i < _currentPageIndex)
            {
                _stepLabels[i].Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                _stepLabels[i].FontWeight = FontWeights.Normal;
            }
            else
            {
                _stepLabels[i].Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                _stepLabels[i].FontWeight = FontWeights.Normal;
            }
        }

        UpdateNavigationButtons();

        if (animate)
        {
            var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation(22.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            PagesHost.BeginAnimation(OpacityProperty, fade);
            PageSlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        }
    }

    private void UpdateNavigationButtons()
    {
        switch (_currentPageIndex)
        {
            case 0:
                BackButton.Visibility = Visibility.Collapsed;
                NextButton.Content = "Next ›";
                NextButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                break;
            case 1:
                BackButton.Visibility = Visibility.Visible;
                NextButton.Content = "Next ›";
                NextButton.IsEnabled = ValidateInstallPath();
                CancelButton.IsEnabled = true;
                break;
            case 2:
                BackButton.Visibility = Visibility.Visible;
                NextButton.Content = "Install";
                NextButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                SummaryPathText.Text = $"Destination: {InstallPathTextBox.Text.Trim()}";
                break;
            case 3:
                BackButton.Visibility = Visibility.Collapsed;
                NextButton.Content = "Installing...";
                NextButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                break;
            case 4:
                BackButton.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                NextButton.Content = "Finish";
                NextButton.IsEnabled = true;
                break;
        }
    }

    private void OnInstallPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentPageIndex == 1)
            NextButton.IsEnabled = ValidateInstallPath();
    }

    private bool ValidateInstallPath()
    {
        string path = InstallPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            AvailableSpaceText.Text = "Invalid path";
            AvailableSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            PathValidationText.Text = "Please enter a valid rooted folder path (e.g., C:\\Program Files\\MemoryMagic).";
            PathValidationText.Visibility = Visibility.Visible;
            return false;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("Missing drive root.");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                AvailableSpaceText.Text = "Drive not ready";
                AvailableSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                PathValidationText.Text = $"Drive {root} is not ready.";
                PathValidationText.Visibility = Visibility.Visible;
                return false;
            }

            long freeBytes = drive.AvailableFreeSpace;
            double freeGb = freeBytes / (1024d * 1024d * 1024d);
            AvailableSpaceText.Text = $"{freeGb:F2} GB";

            if (freeBytes < RequiredSpaceBytes)
            {
                AvailableSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                PathValidationText.Text = "Insufficient free disk space on the selected drive.";
                PathValidationText.Visibility = Visibility.Visible;
                return false;
            }

            AvailableSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            PathValidationText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            AvailableSpaceText.Text = "Unavailable";
            AvailableSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            PathValidationText.Text = $"Invalid installation path: {ex.Message}";
            PathValidationText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select the folder where Memory Magic will be installed:",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        string current = InstallPathTextBox.Text.Trim();
        if (Directory.Exists(current))
            dialog.SelectedPath = current;

        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            string selected = dialog.SelectedPath.TrimEnd('\\');
            if (!selected.EndsWith("MemoryMagic", StringComparison.OrdinalIgnoreCase))
                selected = Path.Combine(selected, "MemoryMagic");

            InstallPathTextBox.Text = selected;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentPageIndex > 0 && !_isInstalling)
            ShowPage(_currentPageIndex - 1);
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentPageIndex == 0)
        {
            ShowPage(1);
        }
        else if (_currentPageIndex == 1)
        {
            if (ValidateInstallPath())
                ShowPage(2);
        }
        else if (_currentPageIndex == 2)
        {
            string targetDir = InstallPathTextBox.Text.Trim();
            if (!_isAdmin && RequiresElevation(targetDir))
            {
                if (TryRelaunchElevated(targetDir))
                    Close();
                return;
            }

            ShowPage(3);
            await RunInstallationAsync(
                targetDir,
                DesktopShortcutCheck.IsChecked == true,
                StartMenuShortcutCheck.IsChecked == true);
        }
        else if (_currentPageIndex == 4)
        {
            if (OpenUserGuideCheck.IsChecked == true && File.Exists(_installedGuidePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _installedGuidePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    App.LogException("Failed to open User Guide", ex);
                }
            }

            if (LaunchAfterInstallCheck.IsChecked == true && File.Exists(_installedExePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _installedExePath,
                        WorkingDirectory = Path.GetDirectoryName(_installedExePath) ?? string.Empty,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    App.LogException("Failed to launch installed application", ex);
                }
            }
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (!_isInstalling)
            Close();
    }

    private async Task RunInstallationAsync(string targetDir, bool createDesktopShortcut, bool createStartMenuShortcut)
    {
        _isInstalling = true;
        try
        {
            App.Log($"Starting installation to '{targetDir}' (Desktop={createDesktopShortcut}, StartMenu={createStartMenuShortcut})");

            UpdateProgress(10, "Stopping running Memory Magic instances...", "Checking active processes");
            await Task.Run(() =>
            {
                foreach (Process p in Process.GetProcessesByName("MemoryMagic"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
            });

            UpdateProgress(28, "Preparing installation directory...", targetDir);
            Directory.CreateDirectory(targetDir);

            string installedExe = Path.Combine(targetDir, "MemoryMagic.exe");
            string installedGuide = Path.Combine(targetDir, "UserGuide.html");
            _installedExePath = installedExe;
            _installedGuidePath = installedGuide;

            UpdateProgress(62, "Extracting MemoryMagic.exe...", "Decompressing standalone binary and user guide");
            await Task.Run(() =>
            {
                ExtractEmbeddedExecutable(installedExe);
                ExtractEmbeddedGuide(installedGuide);
            });

            UpdateProgress(80, "Creating uninstaller...", "Writing Uninstall-MemoryMagic.cmd");
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Memory Magic.lnk");
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Memory Magic");
            string startMenuLink = Path.Combine(startMenuDir, "Memory Magic.lnk");
            string regSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MemoryMagic";
            string regRootName = _isAdmin ? "HKLM" : "HKCU";
            string uninstallCmd = Path.Combine(targetDir, "Uninstall-MemoryMagic.cmd");

            await Task.Run(() =>
            {
                string uninstallBody =
                    "@echo off\r\n" +
                    "taskkill /F /IM MemoryMagic.exe /T >nul 2>&1\r\n" +
                    $"del /F /Q \"{desktopLink}\" >nul 2>&1\r\n" +
                    $"rmdir /S /Q \"{startMenuDir}\" >nul 2>&1\r\n" +
                    $"reg delete \"{regRootName}\\{regSubKey}\" /f >nul 2>&1\r\n" +
                    $"start \"\" /B cmd /c \"ping 127.0.0.1 -n 2 >nul & rmdir /S /Q \"\"{targetDir}\"\"\"\r\n";

                File.WriteAllText(uninstallCmd, uninstallBody, System.Text.Encoding.ASCII);

                if (createDesktopShortcut)
                {
                    ShortcutHelper.CreateShortcut(
                        desktopLink,
                        installedExe,
                        targetDir,
                        "Memory Magic - System Memory Optimizer & Performance Monitor");
                }

                if (createStartMenuShortcut)
                {
                    Directory.CreateDirectory(startMenuDir);
                    ShortcutHelper.CreateShortcut(
                        startMenuLink,
                        installedExe,
                        targetDir,
                        "Memory Magic - System Memory Optimizer & Performance Monitor");
                }

                RegistryKey baseKey = _isAdmin ? Registry.LocalMachine : Registry.CurrentUser;
                using RegistryKey? key = baseKey.CreateSubKey(regSubKey, true);
                if (key is not null)
                {
                    key.SetValue("DisplayName", "Memory Magic", RegistryValueKind.String);
                    key.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
                    key.SetValue("Publisher", "Memory Magic", RegistryValueKind.String);
                    key.SetValue("InstallLocation", targetDir, RegistryValueKind.String);
                    key.SetValue("DisplayIcon", installedExe, RegistryValueKind.String);
                    key.SetValue("UninstallString", $"\"{uninstallCmd}\"", RegistryValueKind.String);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            });

            UpdateProgress(100, "Installation complete!", "Ready");
            await Task.Delay(320);

            FinishPathText.Text = $"Installed to:\n{targetDir}";
            App.Log("Installation completed successfully.");
            ShowPage(4);
        }
        catch (Exception ex)
        {
            App.LogException("Installation failed", ex);
            MessageBox.Show(
                $"Installation failed:\n\n{ex.Message}",
                "Memory Magic Setup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShowPage(2);
        }
        finally
        {
            _isInstalling = false;
        }
    }

    private void UpdateProgress(double targetPercent, string status, string detail)
    {
        InstallStatusText.Text = status;
        InstallDetailText.Text = detail;
        InstallPercentText.Text = $"{targetPercent:F0}%";

        var anim = new DoubleAnimation(targetPercent, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        InstallProgressBar.BeginAnimation(ProgressBar.ValueProperty, anim);
    }

    private static void ExtractEmbeddedExecutable(string destinationPath)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        using Stream? resourceStream = asm.GetManifestResourceStream(EmbeddedPayloadName);
        if (resourceStream is null)
            throw new FileNotFoundException($"Embedded application payload '{EmbeddedPayloadName}' was not found.");

        using var gzip = new GZipStream(resourceStream, CompressionMode.Decompress);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        gzip.CopyTo(output);
    }

    private static void ExtractEmbeddedGuide(string destinationPath)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        using Stream? resourceStream = asm.GetManifestResourceStream(EmbeddedGuideName);
        if (resourceStream is null)
            return;

        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        resourceStream.CopyTo(output);
    }

    private static bool RequiresElevation(string targetDir)
    {
        try
        {
            string probeDir = Directory.Exists(targetDir)
                ? targetDir
                : Path.GetPathRoot(targetDir) ?? targetDir;

            if (Directory.Exists(targetDir))
            {
                string testFile = Path.Combine(targetDir, $".write-test-{Guid.NewGuid():N}.tmp");
                using (File.Create(testFile)) { }
                File.Delete(testFile);
                return false;
            }

            string? parent = Path.GetDirectoryName(targetDir);
            while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);

            if (!string.IsNullOrEmpty(parent))
            {
                string testFile = Path.Combine(parent, $".write-test-{Guid.NewGuid():N}.tmp");
                using (File.Create(testFile)) { }
                File.Delete(testFile);
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool TryRelaunchElevated(string targetDir)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--install-dir \"{targetDir}\"",
                Verb = "runas",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
