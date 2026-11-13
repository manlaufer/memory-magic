using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MemoryMagic.Models;
using Microsoft.Win32;

namespace MemoryMagic.Services;

/// <summary>
/// Categorized Windows Debloater &amp; Performance Optimizer modeled on Schneegans' Windows Unattend cleanup scope:
/// 1) Pre-installed UWP/Appx &amp; Provisioned Appx Bloatware (+ OneDrive desktop client)
/// 2) Windows Optional Capabilities &amp; Features (DISM / Remove-WindowsCapability / Disable-WindowsOptionalFeature)
/// 3) Performance &amp; Privacy Registry/Power Tweaks (CloudContent, Cortana, Widgets, Copilot, Telemetry, Hibernation)
/// Enforces hard-coded core OS safeguards and supports pre-execution System Restore Points.
/// </summary>
public sealed class WindowsDebloaterService
{
    private static readonly string[] ProtectedAppxPrefixes =
    [
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.SecHealthUI",
        "Microsoft.Windows.Apprep.ChxApp",
        "Microsoft.Windows.AssignedAccessLockApp",
        "Microsoft.Windows.PeopleExperienceHost",
        "Microsoft.Windows.PinningConfirmationDialog",
        "Microsoft.Windows.XGpuEjectDialog",
        "Microsoft.LockApp",
        "Microsoft.MicrosoftEdge",
        "Microsoft.EdgeWebView2",
        "Microsoft.Win32WebViewHost",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.VCLibs",
        "Microsoft.NET.Native",
        "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.Services.Store.Engagement",
        "Microsoft.Advertising.Xaml",
        "Microsoft.WebMediaExtensions",
        "Microsoft.HEIFImageExtension",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.AV1VideoExtension",
        "Microsoft.RawImageExtension",
        "Microsoft.WebpImageExtension",
        "Microsoft.WindowsTerminal",
        "Windows.CBSPreview",
        "windows.immersivecontrolpanel",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.AsyncTextService",
        "Microsoft.BioEnrollment",
        "Microsoft.CredDialogHost",
        "Microsoft.ECApp",
        "Microsoft.Windows.CapturePicker",
        "Microsoft.Windows.OOBENetwork",
        "NcsiUwpApp"
    ];

    private static readonly (string Pattern, string DisplayName, string Reason)[] SchneegansAppxCatalog =
    [
        ("Microsoft.BingNews", "Bing News", "Pre-installed MSN/Bing News feed app."),
        ("Microsoft.BingWeather", "Bing Weather", "Pre-installed MSN/Bing Weather app."),
        ("Microsoft.BingFinance", "Bing Finance / Money", "Pre-installed Bing Finance app."),
        ("Microsoft.BingSports", "Bing Sports", "Pre-installed Bing Sports app."),
        ("Microsoft.BingSearch", "Bing Search", "Microsoft Bing Search UWP extension."),
        ("Clipchamp.Clipchamp", "Clipchamp", "Pre-installed video editor app."),
        ("Microsoft.Copilot", "Microsoft Copilot App", "Pre-installed Windows Copilot UWP app."),
        ("Microsoft.549981C3F5F10", "Cortana", "Legacy Cortana assistant UWP package."),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", "Diagnostic feedback reporting app."),
        ("Microsoft.GetHelp", "Get Help", "Microsoft support & troubleshooting companion."),
        ("Microsoft.Getstarted", "Get Started / Tips", "Windows Tips & onboarding app."),
        ("Microsoft.WindowsMaps", "Windows Maps", "Offline/online Windows Maps app."),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", "Windows Mixed Reality VR portal."),
        ("Microsoft.MicrosoftOfficeHub", "Office Hub (Microsoft 365)", "Office promotional launcher & hub."),
        ("Microsoft.Office.OneNote", "OneNote (UWP)", "Pre-installed OneNote UWP app."),
        ("Microsoft.OutlookForWindows", "Outlook for Windows (New)", "Pre-installed web-based Outlook client."),
        ("Microsoft.MSPaint", "Paint 3D", "3D modeling/Paint 3D package (legacy Paint unaffected)."),
        ("Microsoft.Microsoft3DViewer", "3D Viewer", "Pre-installed 3D model viewer."),
        ("Microsoft.People", "Microsoft People", "Contacts integration app."),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", "Pre-installed RPA automation desktop app."),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", "Remote assistance UWP app."),
        ("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "Pre-installed Solitaire games bundle."),
        ("Microsoft.MicrosoftStickyNotes", "Sticky Notes", "Desktop sticky notes UWP app."),
        ("MSTeams", "Microsoft Teams", "Consumer/personal Microsoft Teams client."),
        ("MicrosoftTeams", "Microsoft Teams (Legacy)", "Pre-installed Teams chat integration."),
        ("Microsoft.Todos", "Microsoft To Do", "Task list synchronization app."),
        ("Microsoft.XboxApp", "Xbox App / Console Companion", "Xbox social & companion app."),
        ("Microsoft.GamingApp", "Xbox Gaming App", "Modern Xbox PC gaming hub."),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "In-game overlay (XboxGamingOverlay)."),
        ("Microsoft.XboxGameOverlay", "Xbox Game Overlay", "Background Xbox overlay service."),
        ("Microsoft.XboxSpeechToTextOverlay", "Xbox Speech-to-Text Overlay", "Xbox accessibility speech overlay."),
        ("Microsoft.Xbox.TCUI", "Xbox Live TCUI", "Xbox Live title-callable UI."),
        ("Microsoft.ZuneMusic", "Media Player / Groove Music", "Pre-installed Zune/Groove audio app."),
        ("Microsoft.ZuneVideo", "Movies & TV (Zune Video)", "Pre-installed Zune video player."),
        ("Microsoft.WindowsSoundRecorder", "Sound Recorder", "Audio voice recorder UWP app."),
        ("Microsoft.YourPhone", "Phone Link (Your Phone)", "Android/iOS phone sync background app."),
        ("microsoft.windowscommunicationsapps", "Mail and Calendar", "Legacy UWP Mail & Calendar suite."),
        ("Microsoft.Windows.DevHome", "Dev Home", "Pre-installed developer dashboard app.")
    ];

    private static readonly (string NamePrefix, string DisplayName, DebloatItemKind Kind, string Reason)[] SchneegansCapabilityFeatureCatalog =
    [
        ("Browser.InternetExplorer", "Internet Explorer Mode", DebloatItemKind.WindowsCapability, "Legacy IE11 engine capability (Remove-WindowsCapability)."),
        ("MathRecognizer", "Math Recognizer", DebloatItemKind.WindowsCapability, "Handwritten math input recognizer capability."),
        ("App.StepsRecorder", "Steps Recorder", DebloatItemKind.WindowsCapability, "Legacy problem steps recorder (psr.exe)."),
        ("Microsoft.Windows.WordPad", "WordPad", DebloatItemKind.WindowsCapability, "Legacy WordPad rich-text accessory."),
        ("Media.WindowsMediaPlayer", "Legacy Windows Media Player (Capability)", DebloatItemKind.WindowsCapability, "Legacy Windows Media Player 12 capability."),
        ("Microsoft.Windows.PowerShell.ISE", "PowerShell ISE", DebloatItemKind.WindowsCapability, "Legacy PowerShell Integrated Scripting Environment."),
        ("Hello.Face", "Windows Hello Face", DebloatItemKind.WindowsCapability, "Facial recognition biometric capability (remove if camera lacks IR)."),
        ("App.Support.QuickAssist", "Quick Assist (Capability)", DebloatItemKind.WindowsCapability, "Built-in Quick Assist capability package."),
        ("MicrosoftWindowsPowerShellV2Root", "PowerShell 2.0 Engine", DebloatItemKind.OptionalFeature, "Deprecated PowerShell v2.0 optional feature."),
        ("WindowsMediaPlayer", "Legacy Windows Media Player (Feature)", DebloatItemKind.OptionalFeature, "Windows Media Player optional feature."),
        ("Printing-XPSServices-Features", "XPS Viewer & XPS Services", DebloatItemKind.OptionalFeature, "Legacy XPS document writer/viewer feature."),
        ("WorkFolders-Client", "Work Folders Client", DebloatItemKind.OptionalFeature, "Enterprise Work Folders sync feature."),
        ("FaxServicesClientPackage", "Windows Fax and Scan", DebloatItemKind.OptionalFeature, "Legacy fax & scan optional feature.")
    ];

    private static readonly string[] ProtectedCapabilityPrefixes =
    [
        "Language.Basic", "Language.Fonts", "Language.Handwriting", "Language.OCR",
        "Language.Speech", "Language.TextToSpeech", "NetFX3", "OpenSSH.Client",
        "Windows.Client.ShellComponents", "DirectX.", "OneCoreUAP."
    ];

    private static readonly string[] ProtectedFeaturePrefixes =
    [
        "NetFx", "Windows-Defender", "SearchEngine", "WCF-",
        "Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform",
        "HypervisorPlatform", "Containers", "Printing-PrintToPDFServices"
    ];

    private readonly PrivilegeService _privileges;
    private readonly AuditLogger _logger;

    public WindowsDebloaterService(PrivilegeService privileges, AuditLogger logger)
    {
        _privileges = privileges;
        _logger = logger;
    }

    public async Task<DebloatResult> CreateRestorePointAsync(
        string description = "MemoryMagic Pre-Debloat Backup",
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        if (!_privileges.IsElevated())
            return new DebloatResult(false, "Administrator elevation required to create a System Restore Point.");

        onOutput?.Invoke($"Initializing Windows System Restore checkpoint '{description}'...");
        string script = """
            $ErrorActionPreference = 'Stop'
            Write-Output 'Ensuring System Restore is enabled on system drive...'
            Enable-ComputerRestore -Drive ($env:SystemDrive + '\') -ErrorAction SilentlyContinue
            Write-Output 'Calling Checkpoint-Computer (this may take 15-45 seconds)...'
            Checkpoint-Computer -Description $env:MM_DEBLOAT_TARGET -RestorePointType 'MODIFY_SETTINGS' -Verbose
            Write-Output 'Checkpoint-Computer completed.'
            """;

        (int code, _, string stderr) = await Task.Run(() => RunPowerShell(script, description, ct, onOutput), ct).ConfigureAwait(false);
        if (code == 0)
        {
            string msg = $"Created System Restore Point: '{description}'.";
            _logger.Info("Debloater", msg);
            return new DebloatResult(true, msg);
        }

        string err = $"Restore point error: {(string.IsNullOrWhiteSpace(stderr) ? $"Exit code {code}" : stderr.Trim())}";
        _logger.Warn("Debloater", err);
        return new DebloatResult(false, err, stderr);
    }

    public async Task<(IReadOnlyList<DebloatCandidate> Items, string? Note)> ScanAsync(
        bool includeProtectedCore = false,
        CancellationToken ct = default)
    {
        return await Task.Run(() => ScanCore(includeProtectedCore, ct), ct).ConfigureAwait(false);
    }

    public async Task<DebloatResult> ExecuteAsync(
        DebloatCandidate candidate,
        Action<string>? onOutput = null,
        CancellationToken ct = default,
        Action<double, string>? onProgress = null)
    {
        onProgress?.Invoke(10, $"Validating '{candidate.DisplayName}'...");

        if (candidate.SafetyLevel == DebloatSafetyLevel.ProtectedCore)
        {
            string blocked = $"Blocked '{candidate.DisplayName}': {candidate.SafetyReason}";
            onOutput?.Invoke(blocked);
            _logger.Warn("Debloater", blocked);
            return new DebloatResult(false, blocked);
        }

        if (candidate.RequiresElevation && !_privileges.IsElevated())
        {
            string err = $"Administrator privileges required for '{candidate.DisplayName}'. Click 'Elevate (UAC)' first.";
            onOutput?.Invoke(err);
            return new DebloatResult(false, err);
        }

        return await Task.Run(() => ExecuteCore(candidate, onOutput, onProgress, ct), ct).ConfigureAwait(false);
    }

    public async Task<BatchDebloatSummary> ExecuteBatchAsync(
        IEnumerable<DebloatCandidate> candidates,
        Action<int, int, string>? onStepProgress = null,
        Action<string>? onOutput = null,
        CancellationToken ct = default,
        Action<double, string>? onProgress = null)
    {
        List<DebloatCandidate> list = candidates.ToList();
        int total = list.Count;
        int succeeded = 0, failed = 0, skippedProtected = 0;
        var messages = new List<string>();

        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested)
            {
                onOutput?.Invoke("Execution canceled by user.");
                break;
            }

            DebloatCandidate item = list[i];
            double itemStart = (i * 100.0) / total;
            double itemSpan = 100.0 / total;
            void ReportItemProgress(double localPct, string stepMsg)
            {
                double globalPct = itemStart + (Math.Clamp(localPct, 0, 100) / 100.0) * itemSpan;
                string label = total > 1 ? $"[{i + 1}/{total}] {stepMsg}" : stepMsg;
                onProgress?.Invoke(globalPct, label);
            }

            onStepProgress?.Invoke(i + 1, total, $"Processing ({i + 1}/{total}): {item.DisplayName}");
            onOutput?.Invoke($"--- [{i + 1}/{total}] {item.Kind}: {item.DisplayName} ---");

            if (item.SafetyLevel == DebloatSafetyLevel.ProtectedCore)
            {
                skippedProtected++;
                string skipMsg = $"Skipped protected: {item.DisplayName}";
                messages.Add(skipMsg);
                onOutput?.Invoke(skipMsg);
                ReportItemProgress(100, skipMsg);
                continue;
            }

            DebloatResult res = await ExecuteAsync(item, onOutput, ct, ReportItemProgress).ConfigureAwait(false);
            if (res.Succeeded) succeeded++; else failed++;
            messages.Add(res.Message);
            onOutput?.Invoke(res.Succeeded ? $"OK: {res.Message}" : $"FAILED: {res.Message}");
        }

        return new BatchDebloatSummary(succeeded, failed, skippedProtected, messages);
    }

    private (IReadOnlyList<DebloatCandidate> Items, string? Note) ScanCore(
        bool includeProtectedCore,
        CancellationToken ct)
    {
        var items = new List<DebloatCandidate>();
        bool isElevated = _privileges.IsElevated();
        string? note = null;

        // 1. Scan installed Appx packages & merge with Schneegans Bloatware catalog
        var installedAppx = QueryInstalledAppx(isElevated, ct);
        var seenAppxNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string pattern, string friendly, string reason) in SchneegansAppxCatalog)
        {
            AppxRecord? match = installedAppx.FirstOrDefault(p =>
                p.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                seenAppxNames.Add(match.Name);

            items.Add(new DebloatCandidate(
                Id: match?.PackageFullName ?? $"APPX_PATTERN:{pattern}",
                Name: match?.Name ?? pattern,
                DisplayName: friendly,
                Publisher: match is not null ? ShortenPublisher(match.Publisher) : "Microsoft (Catalog)",
                Version: match is not null ? $"Installed ({match.Version})" : "Not Installed",
                Kind: DebloatItemKind.AppxPackage,
                SafetyLevel: DebloatSafetyLevel.SafeToRemove,
                SafetyReason: reason,
                RequiresElevation: false,
                IsCurrentlyApplied: match is null));
        }

        // OneDrive Desktop Client
        string? oneDriveSetup = FindOneDriveSetupPath();
        items.Add(new DebloatCandidate(
            Id: "ONEDRIVE_DESKTOP",
            Name: "Microsoft.OneDrive.Desktop",
            DisplayName: "OneDrive (Desktop Client)",
            Publisher: "Microsoft Corporation",
            Version: oneDriveSetup is not null ? "Installed" : "Not Installed",
            Kind: DebloatItemKind.AppxPackage,
            SafetyLevel: DebloatSafetyLevel.SafeToRemove,
            SafetyReason: "Pre-installed OneDrive sync client (OneDriveSetup.exe /uninstall).",
            RequiresElevation: false,
            IsCurrentlyApplied: oneDriveSetup is null));

        // Add any extra installed packages (e.g., third-party OEM bloat or ProtectedCore if requested)
        foreach (AppxRecord pkg in installedAppx)
        {
            if (seenAppxNames.Contains(pkg.Name))
                continue;

            (DebloatSafetyLevel safety, string reason) = ClassifyAppx(pkg.Name, pkg.NonRemovable, pkg.IsFramework, pkg.SignatureKind);
            if (safety == DebloatSafetyLevel.ProtectedCore && !includeProtectedCore)
                continue;

            items.Add(new DebloatCandidate(
                Id: pkg.PackageFullName,
                Name: pkg.Name,
                DisplayName: FormatFriendlyName(pkg.Name),
                Publisher: ShortenPublisher(pkg.Publisher),
                Version: $"Installed ({pkg.Version})",
                Kind: DebloatItemKind.AppxPackage,
                SafetyLevel: safety,
                SafetyReason: reason,
                RequiresElevation: false));
        }

        // 2. Windows Optional Capabilities & Features (always listed; live DISM state queried when elevated)
        HashSet<string>? installedCaps = null;
        HashSet<string>? enabledFeatures = null;

        if (isElevated)
        {
            installedCaps = QueryInstalledCapabilities(ct);
            enabledFeatures = QueryEnabledFeatures(ct);
        }
        else
        {
            note = "All Schneegans categories loaded. Elevate (UAC) to query live DISM states and apply Admin tweaks/DISM removals.";
        }

        var seenCapOrFeat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string prefix, string friendly, DebloatItemKind kind, string reason) in SchneegansCapabilityFeatureCatalog)
        {
            string? exactId = null;
            string stateText;
            bool alreadyRemoved = false;

            if (kind == DebloatItemKind.WindowsCapability && installedCaps is not null)
            {
                exactId = installedCaps.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                alreadyRemoved = exactId is null;
                stateText = exactId is not null ? "Installed" : "Removed";
            }
            else if (kind == DebloatItemKind.OptionalFeature && enabledFeatures is not null)
            {
                exactId = enabledFeatures.FirstOrDefault(f => f.Equals(prefix, StringComparison.OrdinalIgnoreCase));
                alreadyRemoved = exactId is null;
                stateText = exactId is not null ? "Enabled" : "Disabled";
            }
            else
            {
                stateText = "Requires Admin";
            }

            if (exactId is not null)
                seenCapOrFeat.Add(exactId);

            items.Add(new DebloatCandidate(
                Id: exactId ?? $"DISM_PREFIX:{prefix}",
                Name: exactId ?? prefix,
                DisplayName: friendly,
                Publisher: kind == DebloatItemKind.WindowsCapability ? "DISM Capability" : "Windows Feature",
                Version: stateText,
                Kind: kind,
                SafetyLevel: DebloatSafetyLevel.SafeToRemove,
                SafetyReason: reason,
                RequiresElevation: true,
                IsCurrentlyApplied: alreadyRemoved));
        }

        if (includeProtectedCore && installedCaps is not null)
        {
            foreach (string cap in installedCaps.Where(c => !seenCapOrFeat.Contains(c)))
            {
                (DebloatSafetyLevel safety, string reason) = ClassifyCapability(cap);
                if (safety == DebloatSafetyLevel.ProtectedCore)
                {
                    items.Add(new DebloatCandidate(cap, cap, cap.Split('~')[0], "DISM Capability", "Installed", DebloatItemKind.WindowsCapability, safety, reason, true));
                }
            }
        }

        if (includeProtectedCore && enabledFeatures is not null)
        {
            foreach (string feat in enabledFeatures.Where(f => !seenCapOrFeat.Contains(f)))
            {
                (DebloatSafetyLevel safety, string reason) = ClassifyFeature(feat);
                if (safety == DebloatSafetyLevel.ProtectedCore)
                {
                    items.Add(new DebloatCandidate(feat, feat, feat, "Windows Feature", "Enabled", DebloatItemKind.OptionalFeature, safety, reason, true));
                }
            }
        }

        // 3. Performance & Privacy Registry/Power Tweaks
        AddSystemTweaks(items);

        // 4. Classic Desktop Programs (Win32 / MSI via Registry Uninstall)
        AddInstalledWin32Programs(items);

        return (items, note);
    }

    private DebloatResult ExecuteCore(DebloatCandidate candidate, Action<string>? onOutput, Action<double, string>? onProgress, CancellationToken ct)
    {
        if (candidate.Kind == DebloatItemKind.SystemTweak)
        {
            onProgress?.Invoke(60, $"Applying system tweak '{candidate.DisplayName}'...");
            onOutput?.Invoke($"Applying system tweak '{candidate.DisplayName}'...");
            DebloatResult tweakRes = ApplySystemTweak(candidate);
            onProgress?.Invoke(100, tweakRes.Succeeded ? $"Applied '{candidate.DisplayName}'." : $"Failed '{candidate.DisplayName}'.");
            return tweakRes;
        }

        if (candidate.Kind == DebloatItemKind.Win32Program)
            return UninstallWin32Program(candidate, onOutput, onProgress, ct);

        if (candidate.Id == "ONEDRIVE_DESKTOP")
            return UninstallOneDriveDesktop(onOutput, onProgress, ct);

        string script;
        string targetEnv;

        if (candidate.Kind == DebloatItemKind.AppxPackage)
        {
            targetEnv = candidate.Name;
            script = BuildAppxRemovalScript(_privileges.IsElevated());
            onOutput?.Invoke($"Enumerating and removing Appx package(s) matching '{targetEnv}'...");
        }
        else if (candidate.Kind == DebloatItemKind.WindowsCapability)
        {
            string prefix = candidate.Id.StartsWith("DISM_PREFIX:", StringComparison.Ordinal)
                ? candidate.Id["DISM_PREFIX:".Length..]
                : candidate.Id;
            targetEnv = prefix;
            onOutput?.Invoke($"Disabling Windows Capability '{targetEnv}' via DISM...");
            script = """
                $ErrorActionPreference = 'Stop'
                $caps = Get-WindowsCapability -Online | Where-Object { $_.State -eq 'Installed' -and ($_.Name -eq $env:MM_DEBLOAT_TARGET -or $_.Name -like ($env:MM_DEBLOAT_TARGET + '*')) }
                if (-not $caps) { Write-Output "No matching installed capability found for $($env:MM_DEBLOAT_TARGET)." }
                foreach ($c in $caps) {
                    Write-Output "Removing capability: $($c.Name)"
                    Remove-WindowsCapability -Online -Name $c.Name | Out-Null
                    Write-Output "Removed capability: $($c.Name)"
                }
                """;
        }
        else // OptionalFeature
        {
            string feat = candidate.Id.StartsWith("DISM_PREFIX:", StringComparison.Ordinal)
                ? candidate.Id["DISM_PREFIX:".Length..]
                : candidate.Id;
            targetEnv = feat;
            onOutput?.Invoke($"Disabling Optional Feature '{targetEnv}' via DISM...");
            script = """
                $ErrorActionPreference = 'Stop'
                Write-Output "Disabling feature: $($env:MM_DEBLOAT_TARGET)"
                Disable-WindowsOptionalFeature -Online -FeatureName $env:MM_DEBLOAT_TARGET -NoRestart | Out-Null
                Write-Output "Disabled feature: $($env:MM_DEBLOAT_TARGET)"
                """;
        }

        onProgress?.Invoke(60, $"Executing removal for '{candidate.DisplayName}'...");
        (int code, string stdout, string stderr) = RunPowerShell(script, targetEnv, ct, onOutput);
        ct.ThrowIfCancellationRequested();

        onProgress?.Invoke(90, $"Verifying removal for '{candidate.DisplayName}'...");
        if (code == 0)
        {
            string msg = $"Completed {candidate.Kind}: '{candidate.DisplayName}'.";
            _logger.Info("Debloater", msg);
            onProgress?.Invoke(100, msg);
            return new DebloatResult(true, msg, stdout);
        }

        string osReason = !string.IsNullOrWhiteSpace(stderr)
            ? stderr.Trim()
            : $"Process exit code {code} ({new Win32Exception(code).Message})";
        string errMsg = $"Failed '{candidate.DisplayName}': {osReason}";
        _logger.Warn("Debloater", errMsg);
        onProgress?.Invoke(100, errMsg);
        return new DebloatResult(false, errMsg, stderr);
    }

    private static string BuildAppxRemovalScript(bool isElevated)
    {
        if (!isElevated)
        {
            return """
                $ErrorActionPreference = 'Stop'
                $pkgs = Get-AppxPackage | Where-Object { $_.Name -eq $env:MM_DEBLOAT_TARGET -or $_.Name -like ('*' + $env:MM_DEBLOAT_TARGET + '*') }
                foreach ($p in $pkgs) {
                    Write-Output "Removing Appx package: $($p.PackageFullName)"
                    Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop
                    Write-Output "Removed: $($p.PackageFullName)"
                }
                """;
        }

        return """
            $ErrorActionPreference = 'Stop'
            $pkgs = Get-AppxPackage -AllUsers | Where-Object { $_.Name -eq $env:MM_DEBLOAT_TARGET -or $_.Name -like ('*' + $env:MM_DEBLOAT_TARGET + '*') }
            foreach ($p in $pkgs) {
                Write-Output "Removing AllUsers Appx package: $($p.PackageFullName)"
                Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction SilentlyContinue
                Remove-AppxPackage -Package $p.PackageFullName -ErrorAction SilentlyContinue
            }
            $prov = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $env:MM_DEBLOAT_TARGET -or $_.DisplayName -like ('*' + $env:MM_DEBLOAT_TARGET + '*') }
            foreach ($pr in $prov) {
                Write-Output "Deprovisioning image package: $($pr.PackageName)"
                Remove-AppxProvisionedPackage -Online -PackageName $pr.PackageName -ErrorAction SilentlyContinue | Out-Null
            }
            """;
    }

    private DebloatResult UninstallWin32Program(
        DebloatCandidate candidate,
        Action<string>? onOutput,
        Action<double, string>? onProgress,
        CancellationToken ct)
    {
        string? rawCmd = candidate.UninstallCommand;
        if (string.IsNullOrWhiteSpace(rawCmd))
            return new DebloatResult(false, $"No uninstall command registered for '{candidate.DisplayName}'.");

        try
        {
            onProgress?.Invoke(10, $"Validating uninstall command for '{candidate.DisplayName}'...");
            (string fileName, string arguments) = ParseUninstallCommand(rawCmd.Trim());
            if (string.IsNullOrWhiteSpace(fileName))
                return new DebloatResult(false, $"Invalid uninstall command for '{candidate.DisplayName}': '{rawCmd}'.");

            if (Path.IsPathRooted(fileName) && !File.Exists(fileName))
                return new DebloatResult(false, $"Uninstaller executable not found at '{fileName}'.");

            ct.ThrowIfCancellationRequested();

            onProgress?.Invoke(30, $"Checking running processes for '{candidate.DisplayName}'...");
            TerminateTargetProcesses(fileName, onOutput);
            ct.ThrowIfCancellationRequested();

            onProgress?.Invoke(60, $"Executing uninstaller for '{candidate.DisplayName}'...");
            onOutput?.Invoke($"Launching uninstaller: \"{fileName}\" {arguments}".Trim());

            using var proc = StartUninstallerProcess(fileName, arguments, onOutput);
            if (proc is null)
                return new DebloatResult(false, $"Could not launch uninstaller for '{candidate.DisplayName}'.");

            onOutput?.Invoke($"Uninstaller PID {proc.Id} started. Waiting for completion...");
            while (!proc.WaitForExit(250))
            {
                if (ct.IsCancellationRequested)
                {
                    onOutput?.Invoke($"Cancellation requested; terminating uninstaller PID {proc.Id}...");
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    throw new OperationCanceledException(ct);
                }
            }

            proc.WaitForExit();
            int exitCode = proc.ExitCode;
            onOutput?.Invoke($"Uninstaller PID {proc.Id} exited with code {exitCode}.");

            if (exitCode == 0 || exitCode == 3010)
            {
                onProgress?.Invoke(90, $"Cleaning up residual entries for '{candidate.DisplayName}'...");
                CleanupResidualUninstallKey(candidate, fileName, onOutput);

                string suffix = exitCode == 3010 ? " (reboot required)" : "";
                string msg = $"Uninstalled '{candidate.DisplayName}' (exit code {exitCode}){suffix}.";
                _logger.Info("Debloater", msg);
                onProgress?.Invoke(100, msg);
                return new DebloatResult(true, msg);
            }

            string reason = exitCode == 1602
                ? "Exit code 1602 (User canceled installer/uninstaller)"
                : $"Exit code {exitCode} ({new Win32Exception(exitCode).Message})";
            string failMsg = $"Uninstaller failed for '{candidate.DisplayName}': {reason}.";
            _logger.Warn("Debloater", failMsg);
            onProgress?.Invoke(100, failMsg);
            return new DebloatResult(false, failMsg);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            string err = $"Uninstall failed for '{candidate.DisplayName}': Win32 error {ex.NativeErrorCode} — {ex.Message}";
            onOutput?.Invoke(err);
            _logger.Warn("Debloater", err);
            return new DebloatResult(false, err);
        }
        catch (Exception ex)
        {
            string err = $"Uninstall failed for '{candidate.DisplayName}': {ex.Message}";
            onOutput?.Invoke(err);
            _logger.Warn("Debloater", err);
            return new DebloatResult(false, err);
        }
    }

    private static Process? StartUninstallerProcess(string fileName, string arguments, Action<string>? onOutput)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            onOutput?.Invoke("Elevation required by uninstaller manifest; retrying via ShellExecute...");
            return Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
        }
    }

    private static void TerminateTargetProcesses(string uninstallerPath, Action<string>? onOutput)
    {
        if (!Path.IsPathRooted(uninstallerPath))
            return;

        string? installDir = Path.GetDirectoryName(uninstallerPath);
        if (string.IsNullOrWhiteSpace(installDir))
            return;

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (installDir.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
            return;

        int selfPid = Environment.ProcessId;
        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == selfPid || proc.Id <= 4)
                    continue;

                string? exePath = proc.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                    continue;

                if (exePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(exePath, uninstallerPath, StringComparison.OrdinalIgnoreCase))
                {
                    onOutput?.Invoke($"Stopping running target process '{proc.ProcessName}' (PID {proc.Id})...");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
            catch
            {
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void CleanupResidualUninstallKey(DebloatCandidate candidate, string uninstallerPath, Action<string>? onOutput)
    {
        if (!candidate.Id.StartsWith("WIN32|", StringComparison.Ordinal))
            return;

        string[] parts = candidate.Id.Split('|', 4);
        if (parts.Length != 4)
            return;

        if (!Enum.TryParse(parts[1], out RegistryHive hive) || !Enum.TryParse(parts[2], out RegistryView view))
            return;

        string keyName = parts[3];
        if (Path.IsPathRooted(uninstallerPath) && File.Exists(uninstallerPath))
            return;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            if (root?.OpenSubKey(keyName) is not null)
            {
                root.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                onOutput?.Invoke($"Removed residual uninstall registry key '{keyName}'.");
            }
        }
        catch
        {
        }
    }

    private static (string FileName, string Arguments) ParseUninstallCommand(string raw)
    {
        string expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
        string fileName;
        string arguments;

        if (expanded.StartsWith('"'))
        {
            int endQuote = expanded.IndexOf('"', 1);
            if (endQuote > 1)
            {
                fileName = expanded[1..endQuote].Trim();
                arguments = expanded[(endQuote + 1)..].Trim();
            }
            else
            {
                fileName = expanded.Trim('"');
                arguments = string.Empty;
            }
        }
        else
        {
            int exeIdx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0)
            {
                fileName = expanded[..(exeIdx + 4)].Trim();
                arguments = expanded[(exeIdx + 4)..].Trim();
            }
            else
            {
                int spaceIdx = expanded.IndexOf(' ');
                fileName = spaceIdx > 0 ? expanded[..spaceIdx] : expanded;
                arguments = spaceIdx > 0 ? expanded[(spaceIdx + 1)..].Trim() : string.Empty;
            }
        }

        string exeName = Path.GetFileName(fileName);
        if (exeName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            arguments = arguments.Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);
            if (!arguments.Contains("/qn", StringComparison.OrdinalIgnoreCase) &&
                !arguments.Contains("/quiet", StringComparison.OrdinalIgnoreCase) &&
                !arguments.Contains("/passive", StringComparison.OrdinalIgnoreCase) &&
                !arguments.Contains("/qb", StringComparison.OrdinalIgnoreCase))
            {
                arguments = $"{arguments} /qn".Trim();
            }

            if (!arguments.Contains("/norestart", StringComparison.OrdinalIgnoreCase))
                arguments = $"{arguments} /norestart".Trim();

            return ("msiexec.exe", arguments);
        }

        return (fileName, arguments);
    }

    private static void AddInstalledWin32Programs(List<DebloatCandidate> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", requiresAdmin: true, seen, items);
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", requiresAdmin: true, seen, items);
        ReadUninstallHive(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", requiresAdmin: false, seen, items);
    }

    private static void ReadUninstallHive(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        bool requiresAdmin,
        HashSet<string> seen,
        List<DebloatCandidate> items)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? root = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (root is null)
                return;

            foreach (string keyName in root.GetSubKeyNames())
            {
                using RegistryKey? appKey = root.OpenSubKey(keyName, writable: false);
                if (appKey is null)
                    continue;

                if (appKey.GetValue("SystemComponent") is int sys && sys == 1)
                    continue;

                if (appKey.GetValue("ParentKeyName") is not null)
                    continue;

                string? displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                string? uninstallCmd = appKey.GetValue("UninstallString")?.ToString()?.Trim();
                string? quietCmd = appKey.GetValue("QuietUninstallString")?.ToString()?.Trim();
                // Prefer QuietUninstallString (includes silent switches like /S or /qn) since ExecutionProgressDialog renders live progress
                string? effectiveCmd = !string.IsNullOrWhiteSpace(quietCmd) ? quietCmd : uninstallCmd;
                if (string.IsNullOrWhiteSpace(effectiveCmd))
                    continue;

                string? installLocation = appKey.GetValue("InstallLocation")?.ToString()?.Trim();
                if (!IsValidInstalledEntry(effectiveCmd, keyName, installLocation))
                    continue;

                string version = appKey.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "Installed";
                string publisher = appKey.GetValue("Publisher")?.ToString()?.Trim() ?? "Unknown";

                string dedupeKey = $"{displayName}|{version}";
                if (!seen.Add(dedupeKey))
                    continue;

                bool isProtectedCore =
                    displayName.Contains("Edge WebView2", StringComparison.OrdinalIgnoreCase) ||
                    displayName.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase) && !displayName.Contains("Update", StringComparison.OrdinalIgnoreCase);

                DebloatSafetyLevel safety = isProtectedCore
                    ? DebloatSafetyLevel.ProtectedCore
                    : DebloatSafetyLevel.Caution;

                string reason = isProtectedCore
                    ? "Core WebView2/Browser runtime required by Windows widgets & apps."
                    : "Installed desktop application (runs registered uninstaller).";

                items.Add(new DebloatCandidate(
                    Id: $"WIN32|{hive}|{view}|{keyName}",
                    Name: displayName,
                    DisplayName: displayName,
                    Publisher: publisher,
                    Version: version,
                    Kind: DebloatItemKind.Win32Program,
                    SafetyLevel: safety,
                    SafetyReason: reason,
                    RequiresElevation: requiresAdmin,
                    IsCurrentlyApplied: false,
                    UninstallCommand: effectiveCmd));
            }
        }
        catch
        {
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductStateW(string szProduct);

    private static bool IsValidInstalledEntry(string rawUninstallCmd, string registryKeyName, string? installLocation)
    {
        (string fileName, string arguments) = ParseUninstallCommand(rawUninstallCmd);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? productCode = ExtractBracedGuid(arguments) ?? ExtractBracedGuid(registryKeyName);
            if (productCode is null)
                return false;

            // INSTALLSTATE_DEFAULT (5) confirms the MSI product is actively installed
            return MsiQueryProductStateW(productCode) == 5;
        }

        if (Path.IsPathRooted(fileName))
            return File.Exists(fileName);

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            string expandedLoc = Environment.ExpandEnvironmentVariables(installLocation).Trim().Trim('"');
            if (Path.IsPathRooted(expandedLoc) && !Directory.Exists(expandedLoc))
                return false;
        }

        string sysDirExe = Path.Combine(Environment.SystemDirectory, fileName);
        if (File.Exists(sysDirExe))
            return true;

        string winDirExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), fileName);
        return File.Exists(winDirExe);
    }

    private static string? ExtractBracedGuid(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        int start = input.IndexOf('{');
        while (start >= 0)
        {
            int end = input.IndexOf('}', start + 1);
            if (end < 0)
                break;

            string candidate = input[start..(end + 1)];
            if (Guid.TryParse(candidate, out _))
                return candidate;

            start = input.IndexOf('{', start + 1);
        }

        return null;
    }

    private DebloatResult UninstallOneDriveDesktop(Action<string>? onOutput, Action<double, string>? onProgress, CancellationToken ct)
    {
        onProgress?.Invoke(10, "Locating OneDriveSetup.exe...");
        string? setupPath = FindOneDriveSetupPath();
        if (setupPath is null)
        {
            onProgress?.Invoke(100, "OneDrive desktop client is already not installed.");
            return new DebloatResult(true, "OneDrive desktop client is already not installed.");
        }

        onProgress?.Invoke(30, "Stopping running OneDrive processes...");
        foreach (Process p in Process.GetProcessesByName("OneDrive"))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { }
            finally { p.Dispose(); }
        }

        onProgress?.Invoke(60, "Executing OneDriveSetup.exe /uninstall...");
        onOutput?.Invoke($"Running \"{setupPath}\" /uninstall...");
        using var proc = StartUninstallerProcess(setupPath, "/uninstall", onOutput);

        if (proc is null)
            return new DebloatResult(false, "Failed to launch OneDriveSetup.exe /uninstall.");

        while (!proc.WaitForExit(250))
        {
            if (ct.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException(ct);
            }
        }

        proc.WaitForExit();
        int code = proc.ExitCode;
        onProgress?.Invoke(90, "Verifying OneDrive uninstallation...");
        string msg = $"Executed OneDriveSetup.exe /uninstall (exit code {code}).";
        onOutput?.Invoke(msg);
        _logger.Info("Debloater", msg);
        if (code == 0 || code == 3010)
        {
            onProgress?.Invoke(100, msg);
            return new DebloatResult(true, msg);
        }

        string failMsg = $"OneDrive uninstall failed (exit code {code}: {new Win32Exception(code).Message}).";
        onProgress?.Invoke(100, failMsg);
        return new DebloatResult(false, failMsg);
    }

    private DebloatResult ApplySystemTweak(DebloatCandidate candidate)
    {
        try
        {
            switch (candidate.Id)
            {
                case "tweak-consumer-features":
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1);
                    SetDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0);
                    SetDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0);
                    SetDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0);
                    SetDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0);
                    break;

                case "tweak-cortana":
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0);
                    break;

                case "tweak-widgets":
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0);
                    SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0);
                    break;

                case "tweak-copilot":
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                    SetDword(RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                    break;

                case "tweak-telemetry":
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0);
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1);
                    break;

                case "tweak-storage-sense":
                    const string ssPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
                    SetDword(RegistryHive.CurrentUser, ssPath, "01", 1);
                    SetDword(RegistryHive.CurrentUser, ssPath, "04", 1);
                    SetDword(RegistryHive.CurrentUser, ssPath, "08", 1);
                    SetDword(RegistryHive.CurrentUser, ssPath, "32", 1);
                    break;

                case "tweak-edge-boost":
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0);
                    SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0);
                    break;

                case "tweak-hibernation":
                    using (var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = "-h off",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        proc?.WaitForExit(15_000);
                        if (proc is null || proc.ExitCode != 0)
                            return new DebloatResult(false, $"powercfg -h off failed (exit {proc?.ExitCode}).");
                    }
                    break;

                default:
                    return new DebloatResult(false, $"Unknown tweak '{candidate.Id}'.");
            }

            string msg = $"Applied tweak: '{candidate.DisplayName}'.";
            _logger.Info("Debloater", msg);
            return new DebloatResult(true, msg);
        }
        catch (Exception ex)
        {
            string err = $"Failed tweak '{candidate.DisplayName}': {ex.Message}";
            _logger.Warn("Debloater", err);
            return new DebloatResult(false, err);
        }
    }

    private static void AddSystemTweaks(List<DebloatCandidate> items)
    {
        bool consumerOff = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures") == 1;
        items.Add(new DebloatCandidate(
            "tweak-consumer-features", "DisableWindowsConsumerFeatures",
            "Disable Consumer Features & Suggested Apps", "CloudContent Policy",
            consumerOff ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Disables CloudContent consumer features and silent Store app suggestions.", true, consumerOff));

        bool cortanaOff = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana") == 0;
        items.Add(new DebloatCandidate(
            "tweak-cortana", "AllowCortana=0",
            "Disable Cortana Integration", "Windows Search Policy",
            cortanaOff ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Stops Cortana background indexing & assistant overhead.", true, cortanaOff));

        bool widgetsOff = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests") == 0;
        items.Add(new DebloatCandidate(
            "tweak-widgets", "AllowNewsAndInterests=0",
            "Disable Widgets / News & Interests", "Windows Shell Policy",
            widgetsOff ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Stops taskbar Widgets WebView2 feed processes from consuming RAM.", true, widgetsOff));

        bool copilotOff = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1;
        items.Add(new DebloatCandidate(
            "tweak-copilot", "TurnOffWindowsCopilot=1",
            "Disable Windows Copilot", "Windows Copilot Policy",
            copilotOff ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Disables taskbar & shell Copilot integration via policy.", true, copilotOff));

        bool telemetryMin = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0;
        items.Add(new DebloatCandidate(
            "tweak-telemetry", "AllowTelemetry=0",
            "Minimize Telemetry & Diagnostics", "DataCollection Policy",
            telemetryMin ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Sets AllowTelemetry=0 and disables Windows feedback prompts.", true, telemetryMin));

        bool storageSense = GetDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01") == 1;
        items.Add(new DebloatCandidate(
            "tweak-storage-sense", "StorageSenseAutoCleanup",
            "Enable Storage Sense Auto-Cleanup", "Storage Policy (User)",
            storageSense ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Automatically cleans temporary files & stale Recycle Bin items.", false, storageSense));

        bool edgeBoostOff = GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled") == 0;
        items.Add(new DebloatCandidate(
            "tweak-edge-boost", "EdgeStartupBoost=0",
            "Disable Edge Startup Boost & Background Mode", "Microsoft Edge Policy",
            edgeBoostOff ? "Applied" : "Default", DebloatItemKind.SystemTweak, DebloatSafetyLevel.SafeToRemove,
            "Prevents msedge.exe from pre-loading or staying resident after closing.", true, edgeBoostOff));

        bool hiberExists = File.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "hiberfil.sys"));
        items.Add(new DebloatCandidate(
            "tweak-hibernation", "powercfg -h off",
            "Disable Hibernation (powercfg -h off)", "Power Configuration",
            hiberExists ? "Enabled" : "Disabled", DebloatItemKind.SystemTweak, DebloatSafetyLevel.Caution,
            "Reclaims multi-GB hiberfil.sys disk space; disables Hibernate & Fast Startup.", true, !hiberExists));
    }

    private List<AppxRecord> QueryInstalledAppx(bool isElevated, CancellationToken ct)
    {
        var list = new List<AppxRecord>();
        string cmd = isElevated ? "Get-AppxPackage -AllUsers" : "Get-AppxPackage";
        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            {{cmd}} | Group-Object Name | ForEach-Object {
                $pkg = $_.Group | Select-Object -First 1
                [PSCustomObject]@{
                    Name = $pkg.Name
                    PackageFullName = $pkg.PackageFullName
                    Publisher = $pkg.Publisher
                    Version = $pkg.Version.ToString()
                    NonRemovable = [bool]$pkg.NonRemovable
                    IsFramework = [bool]$pkg.IsFramework
                    SignatureKind = [string]$pkg.SignatureKind
                }
            } | ConvertTo-Json -Compress
            """;

        (int code, string stdout, _) = RunPowerShell(script, null, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            return list;

        using var doc = JsonDocument.Parse(stdout);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
                if (TryReadAppx(el) is { } r) list.Add(r);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (TryReadAppx(doc.RootElement) is { } r) list.Add(r);
        }

        return list;
    }

    private static AppxRecord? TryReadAppx(JsonElement el)
    {
        string name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
        string full = el.TryGetProperty("PackageFullName", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(full))
            return null;

        return new AppxRecord(
            name,
            full,
            el.TryGetProperty("Publisher", out var pub) ? pub.GetString() ?? "" : "",
            el.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "",
            el.TryGetProperty("NonRemovable", out var nr) && nr.GetBoolean(),
            el.TryGetProperty("IsFramework", out var fw) && fw.GetBoolean(),
            el.TryGetProperty("SignatureKind", out var sk) ? sk.GetString() ?? "" : "");
    }

    private HashSet<string> QueryInstalledCapabilities(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-WindowsCapability -Online | Where-Object { $_.State -eq 'Installed' } | ForEach-Object { $_.Name } | ConvertTo-Json -Compress
            """;
        (int code, string stdout, _) = RunPowerShell(script, null, ct);
        if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
            ParseStringArrayOrScalar(stdout, set);
        return set;
    }

    private HashSet<string> QueryEnabledFeatures(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-WindowsOptionalFeature -Online | Where-Object { $_.State -eq 'Enabled' } | ForEach-Object { $_.FeatureName } | ConvertTo-Json -Compress
            """;
        (int code, string stdout, _) = RunPowerShell(script, null, ct);
        if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
            ParseStringArrayOrScalar(stdout, set);
        return set;
    }

    private static void ParseStringArrayOrScalar(string json, HashSet<string> target)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string? val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) target.Add(val);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.String)
        {
            string? val = doc.RootElement.GetString();
            if (!string.IsNullOrWhiteSpace(val)) target.Add(val);
        }
    }

    private static (DebloatSafetyLevel Level, string Reason) ClassifyAppx(
        string name, bool nonRemovable, bool isFramework, string signatureKind)
    {
        if (isFramework)
            return (DebloatSafetyLevel.ProtectedCore, "Protected shared app runtime/framework dependency.");
        if (nonRemovable)
            return (DebloatSafetyLevel.ProtectedCore, "Marked NonRemovable by Windows OS.");

        foreach (string prefix in ProtectedAppxPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (DebloatSafetyLevel.ProtectedCore, "Core Windows Shell, Settings, Store, or WebView2 dependency.");
        }

        if (signatureKind.Equals("System", StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase))
        {
            return (DebloatSafetyLevel.ProtectedCore, "Core system-signed Windows component.");
        }

        return (DebloatSafetyLevel.Caution, "Installed Store/third-party app; verify before removing.");
    }

    private static (DebloatSafetyLevel Level, string Reason) ClassifyCapability(string capName)
    {
        foreach (string prefix in ProtectedCapabilityPrefixes)
        {
            if (capName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (DebloatSafetyLevel.ProtectedCore, "Core Windows language, runtime, or shell capability.");
        }
        return (DebloatSafetyLevel.Caution, "Optional Windows capability.");
    }

    private static (DebloatSafetyLevel Level, string Reason) ClassifyFeature(string featureName)
    {
        foreach (string prefix in ProtectedFeaturePrefixes)
        {
            if (featureName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (DebloatSafetyLevel.ProtectedCore, "Protected Windows runtime, security, or virtualization feature.");
        }
        return (DebloatSafetyLevel.Caution, "Windows optional feature.");
    }

    private static string? FindOneDriveSetupPath()
    {
        string sys64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SysWOW64\OneDriveSetup.exe");
        if (File.Exists(sys64)) return sys64;
        string sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\OneDriveSetup.exe");
        return File.Exists(sys32) ? sys32 : null;
    }

    private static int? GetDword(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName) is int i ? i : null;
        }
        catch { return null; }
    }

    private static void SetDword(RegistryHive hive, string subKey, string valueName, int value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(subKey, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPowerShell(
        string script, string? targetEnvValue, CancellationToken ct, Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (targetEnvValue is not null)
            psi.Environment["MM_DEBLOAT_TARGET"] = targetEnvValue;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powershell.exe.");
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        using var outDone = new ManualResetEventSlim(false);
        using var errDone = new ManualResetEventSlim(false);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.Set(); return; }
            stdoutSb.AppendLine(e.Data);
            if (!string.IsNullOrWhiteSpace(e.Data))
                onOutput?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.Set(); return; }
            stderrSb.AppendLine(e.Data);
            if (!string.IsNullOrWhiteSpace(e.Data))
                onOutput?.Invoke($"[STDERR] {e.Data}");
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        proc.WaitForExit();
        outDone.Wait(2000);
        errDone.Wait(2000);
        return (proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    private static string FormatFriendlyName(string rawName)
    {
        int dot = rawName.IndexOf('.');
        return dot > 0 && dot < rawName.Length - 1 ? rawName[(dot + 1)..] : rawName;
    }

    private static string ShortenPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "Unknown";
        foreach (string part in publisher.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return trimmed[3..];
        }
        return publisher.Length > 40 ? publisher[..40] + "..." : publisher;
    }

    private sealed record AppxRecord(
        string Name,
        string PackageFullName,
        string Publisher,
        string Version,
        bool NonRemovable,
        bool IsFramework,
        string SignatureKind);
}
