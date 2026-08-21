using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("DeepSeek Harness Tray")]
[assembly: AssemblyProduct("DeepSeek Harness Tray")]
[assembly: AssemblyDescription("Local Windows tray launcher for DeepSeek Harness")]
[assembly: AssemblyVersion("1.8.0.0")]
[assembly: AssemblyFileVersion("1.8.0.0")]

internal static class Program
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int awareness);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

    internal const string ProductName = "DeepSeek Harness";
    internal const string MutexName = @"Local\DeepSeekHarnessTrayLauncher";
    internal static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarnessTray");
    internal static readonly string InstalledExe = Path.Combine(AppDirectory, "DeepSeekHarnessTray.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "--make-icon", StringComparison.OrdinalIgnoreCase))
        {
            IconFactory.WriteIcon(args[1], args[2]);
            return;
        }
        if (args.Length == 1 && string.Equals(args[0], "--install", StringComparison.OrdinalIgnoreCase))
        {
            Install();
            return;
        }
        if (args.Length == 1 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            Uninstall();
            return;
        }

        Directory.CreateDirectory(AppDirectory);
        bool createdNew;
        using (var mutex = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew)
            {
                OpenSavedUrl();
                return;
            }

            EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(true);
            using (var context = new TrayApplicationContext())
                Application.Run(context);
        }
    }

    private static void EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2)) return;
        }
        catch { }

        try
        {
            if (SetProcessDpiAwareness(2) == 0) return;
        }
        catch { }

        try { SetProcessDPIAware(); } catch { }
    }

    private static void OpenSavedUrl()
    {
        string path = Path.Combine(AppDirectory, "dsh-web.url");
        try
        {
            if (!File.Exists(path)) return;
            Uri uri;
            if (Uri.TryCreate(File.ReadAllText(path).Trim(), UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                DshBrowserGate.TryOpen(uri.AbsoluteUri, respectDebounce: false);
        }
        catch { }
    }

    private static void Install()
    {
        PrepareGuiDefaults();
        try
        {
            Directory.CreateDirectory(AppDirectory);
            if (!TryCloseRunningLauncher()) return;
            string source = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, InstalledExe, true);
            DeleteIfPresent(Path.Combine(AppDirectory, "DeepSeekHarnessTray.ps1"));

            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"),
                InstalledExe);
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + ".lnk"),
                InstalledExe);
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);

            bool start = ModernDialog.Confirm(
                "Installation complete",
                ProductName + " is installed for your account. Shortcuts were added to the desktop and the Start menu.",
                "Start " + ProductName,
                "Close",
                DialogTone.Success);
            if (start) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ModernDialog.Inform("Installation failed", ex.Message, DialogTone.Failure);
        }
    }

    private static void Uninstall()
    {
        PrepareGuiDefaults();
        try
        {
            if (!TryCloseRunningLauncher()) return;
            DeleteIfPresent(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"));
            DeleteIfPresent(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + ".lnk"));
            DeleteIfPresent(InstalledExe);
            DeleteIfPresent(Path.Combine(AppDirectory, "DeepSeekHarnessTray.ps1"));
            DeleteIfPresent(Path.Combine(AppDirectory, "dsh-web.url"));
            DeleteIfPresent(Path.Combine(AppDirectory, "dsh-web.log"));
            DeleteIfPresent(Path.Combine(AppDirectory, "dsh-web-error.log"));
            DeleteIfPresent(Path.Combine(AppDirectory, "dsh-package-version.txt"));
            DeleteIfPresent(Path.Combine(AppDirectory, "dsh-update-pending.flag"));
            DeleteIfPresent(Path.Combine(AppDirectory, "dsh-web.last-open"));
            try { Directory.Delete(AppDirectory, false); } catch { }

            ModernDialog.Inform(
                "Uninstall complete",
                ProductName + " was removed, along with its shortcuts, logs, and saved address.",
                DialogTone.Success);
        }
        catch (Exception ex)
        {
            ModernDialog.Inform("Uninstall failed", ex.Message, DialogTone.Failure);
        }
    }

    private static void PrepareGuiDefaults()
    {
        EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(true);
    }

    /// <summary>
    /// Installing or removing the EXE fails while it is running, so offer to stop it first.
    /// Killing the launcher is safe because its job object takes the DSH server down with it.
    /// </summary>
    private static bool TryCloseRunningLauncher()
    {
        Process[] running = FindRunningLaunchers();
        if (running.Length == 0) return true;
        try
        {
            if (!ModernDialog.Confirm(
                    ProductName + " is running",
                    "The launcher has to close before it can be replaced. DSH stops with it and can be started again afterwards.",
                    "Close and continue",
                    "Cancel",
                    DialogTone.Question))
                return false;

            foreach (Process process in running)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch { }
            }
            return true;
        }
        finally
        {
            foreach (Process process in running) process.Dispose();
        }
    }

    private static Process[] FindRunningLaunchers()
    {
        var found = new List<Process>();
        int self = Process.GetCurrentProcess().Id;
        try
        {
            foreach (Process process in Process.GetProcessesByName("DeepSeekHarnessTray"))
            {
                if (process.Id == self) { process.Dispose(); continue; }
                found.Add(process);
            }
        }
        catch { }
        return found.ToArray();
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("Windows Script Host is unavailable.");
        object shell = Activator.CreateInstance(shellType);
        object shortcut = shellType.InvokeMember(
            "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
        Type shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDirectory });
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Start the DeepSeek Harness tray launcher" });
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
        if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
    }
}

/// <summary>
/// Opens DSH through one code path and suppresses duplicate launches when the installer
/// Start button and the desktop shortcut fire almost back-to-back.
/// </summary>
internal static class DshBrowserGate
{
    private const int DebounceMs = 8000;
    private static readonly string StampPath = Path.Combine(Program.AppDirectory, "dsh-web.last-open");
    private static readonly Mutex OpenMutex = new Mutex(false, @"Local\DeepSeekHarnessTrayDshOpen");

    internal static void ClearOpenStamp()
    {
        try { if (File.Exists(StampPath)) File.Delete(StampPath); } catch { }
    }

    internal static bool TryOpen(string url, bool respectDebounce)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!TryClaimOpen(respectDebounce)) return false;
        if (BrowserAppLauncher.TryOpen(url)) return true;
        if (BrowserAppLauncher.HasInstalledPwa(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryOpen(string url)
    {
        return TryOpen(url, respectDebounce: true);
    }

    private static bool TryClaimOpen(bool respectDebounce)
    {
        bool owned = false;
        try
        {
            try { owned = OpenMutex.WaitOne(0); }
            catch { return false; }
            if (!owned) return false;

            Directory.CreateDirectory(Program.AppDirectory);
            if (respectDebounce && File.Exists(StampPath))
            {
                DateTime written = File.GetLastWriteTimeUtc(StampPath);
                if ((DateTime.UtcNow - written).TotalMilliseconds < DebounceMs)
                    return false;
            }
            File.WriteAllText(StampPath, DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (owned)
            {
                try { OpenMutex.ReleaseMutex(); } catch { }
            }
        }
    }
}

/// <summary>
/// Opens DSH in an installed browser PWA when one is available, otherwise in a
/// standalone --app window, before falling back to a normal browser tab.
/// </summary>
internal static class BrowserAppLauncher
{
    private static readonly string[] StartMenuRoots =
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Start Menu\Programs")
    };

    private sealed class PwaCandidate
    {
        internal string ShortcutPath;
        internal string ProxyPath;
        internal string Arguments;
        internal int Score;
    }

    private static readonly Regex LaunchUrlArgumentPattern = new Regex(
        @"\s*--app-launch-url-for-shortcuts-menu-item(?:=\""[^\""]*\""|=\S+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppIdArgumentPattern = new Regex(
        @"--app-id=(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProfileArgumentPattern = new Regex(
        @"--profile-directory=(?:\""([^\""]+)\""|(\S+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool HasInstalledPwa(string url)
    {
        return FindInstalledPwa(url) != null;
    }

    internal static bool TryOpen(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        PwaCandidate installed = FindInstalledPwa(url);
        if (installed != null)
            return TryStart(installed.ProxyPath, SanitizePwaArguments(installed.Arguments));

        string browser = FindBrowserExecutable();
        if (browser != null && TryStart(browser, "--app=" + Quote(url)))
            return true;

        return false;
    }

    private static PwaCandidate FindInstalledPwa(string url)
    {
        PwaCandidate best = null;
        foreach (string root in StartMenuRoots)
            ConsiderStartMenu(root, url, ref best);
        ConsiderChromeProfiles(url, ref best);
        return best;
    }

    private static void ConsiderStartMenu(string root, string url, ref PwaCandidate best)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (string shortcutPath in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                string name;
                string target;
                string arguments;
                if (!TryReadShortcut(shortcutPath, out name, out target, out arguments)) continue;
                if (!IsBrowserProxy(target)) continue;
                if (arguments.IndexOf("--app-id=", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int score = ScoreCandidate(name, url);
                if (score <= 0) continue;
                ConsiderCandidate(ref best, shortcutPath, target, arguments, score);
            }
        }
        catch { }
    }

    private static void ConsiderChromeProfiles(string url, ref PwaCandidate best)
    {
        string proxy = FindChromeProxy();
        if (proxy == null) return;

        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\User Data");
        if (!Directory.Exists(userData)) return;

        try
        {
            foreach (string profileDir in Directory.GetDirectories(userData))
            {
                string profileName = Path.GetFileName(profileDir);
                if (!IsBrowserProfileName(profileName)) continue;

                string webApps = Path.Combine(profileDir, "Web Applications");
                if (!Directory.Exists(webApps)) continue;

                foreach (string appDir in Directory.GetDirectories(webApps, "_crx_*"))
                {
                    string appId = Path.GetFileName(appDir).Substring("_crx_".Length);
                    string displayName = ReadWebAppName(appDir);
                    int score = ScoreCandidate(displayName, url);
                    if (score <= 0) continue;

                    string arguments = "--profile-directory=" + profileName + " --app-id=" + appId;
                    ConsiderCandidate(ref best, null, proxy, arguments, score - 5);
                }
            }
        }
        catch { }
    }

    private static void ConsiderCandidate(
        ref PwaCandidate best,
        string shortcutPath,
        string proxyPath,
        string arguments,
        int score)
    {
        if (best != null && score <= best.Score) return;
        best = new PwaCandidate
        {
            ShortcutPath = shortcutPath,
            ProxyPath = proxyPath,
            Arguments = arguments,
            Score = score
        };
    }

    private static string SanitizePwaArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "";

        Match appId = AppIdArgumentPattern.Match(arguments);
        if (!appId.Success)
            return LaunchUrlArgumentPattern.Replace(arguments, "").Trim();

        var minimal = new StringBuilder();
        Match profile = ProfileArgumentPattern.Match(arguments);
        if (profile.Success)
        {
            string profileName = profile.Groups[1].Success ? profile.Groups[1].Value : profile.Groups[2].Value;
            minimal.Append("--profile-directory=").Append(Quote(profileName)).Append(' ');
        }
        minimal.Append("--app-id=").Append(appId.Groups[1].Value);
        return minimal.ToString();
    }

    private static int ScoreCandidate(string name, string url)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;

        Uri parsed;
        if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return 0;
        if (!IsLoopback(parsed)) return 0;

        if (string.Equals(name.Trim(), Program.ProductName, StringComparison.OrdinalIgnoreCase))
            return 100;

        string lower = name.ToLowerInvariant();
        if (lower.IndexOf("deepseek", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("harness", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("dsh", StringComparison.Ordinal) >= 0)
            return 50;

        return 0;
    }

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserProfileName(string profileName)
    {
        if (string.IsNullOrEmpty(profileName)) return false;
        if (profileName.IndexOf(' ') >= 0) return profileName.StartsWith("Profile ", StringComparison.Ordinal);
        return string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profileName, "Guest Profile", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWebAppName(string appDir)
    {
        try
        {
            foreach (string iconFile in Directory.GetFiles(appDir, "*.ico.md5"))
                return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(iconFile));
        }
        catch { }
        return null;
    }

    private static bool TryReadShortcut(string shortcutPath, out string name, out string target, out string arguments)
    {
        name = Path.GetFileNameWithoutExtension(shortcutPath);
        target = null;
        arguments = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            Type shortcutType = shortcut.GetType();
            target = shortcutType.InvokeMember(
                "TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            arguments = shortcutType.InvokeMember(
                "Arguments", BindingFlags.GetProperty, null, shortcut, null) as string;
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.ReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
            return !string.IsNullOrWhiteSpace(target);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBrowserProxy(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return false;
        string fileName = Path.GetFileName(targetPath);
        return string.Equals(fileName, "chrome_proxy.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "msedge_proxy.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "msedge.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "chrome.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindChromeProxy()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        for (int i = 0; i < roots.Length; i++)
        {
            if (string.IsNullOrEmpty(roots[i])) continue;
            string proxy = Path.Combine(roots[i], @"Google\Chrome\Application\chrome_proxy.exe");
            if (File.Exists(proxy)) return proxy;
        }
        return null;
    }

    private static string FindBrowserExecutable()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
        };
        for (int i = 0; i < candidates.Length; i++)
            if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i]))
                return candidates[i];
        return null;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static bool TryStart(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fileName)
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Checks the npm registry for a newer @deepseek-ai/dsh release and prefetches it into
/// the npx cache without blocking startup. Restarting DSH picks up @latest automatically.
/// </summary>
internal static class DshPackageUpdater
{
    internal const string PackageName = "@deepseek-ai/dsh";
    private static readonly string VersionState = Path.Combine(Program.AppDirectory, "dsh-package-version.txt");
    private static readonly string UpdatePendingFlag = Path.Combine(Program.AppDirectory, "dsh-update-pending.flag");
    private static readonly Regex VersionPattern = new Regex(
        @"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", RegexOptions.Compiled);
    private static int checking;

    internal static bool IsUpdatePending()
    {
        return File.Exists(UpdatePendingFlag);
    }

    internal static string PackageSpecForStart(bool restart)
    {
        if (restart || IsUpdatePending()) return PackageName + "@latest";
        return PackageName;
    }

    internal static void ScheduleBackgroundCheck(
        SynchronizationContext ui,
        Action<string> log,
        Action<string> onUpdateReady)
    {
        if (Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { RunBackgroundCheck(log, onUpdateReady, ui); }
            catch { }
            finally { Interlocked.Exchange(ref checking, 0); }
        });
    }

    internal static string PrefetchLatestBlocking(Action<string> log)
    {
        return PrefetchLatest(log);
    }

    internal static string ReadPendingVersion()
    {
        try
        {
            if (!File.Exists(UpdatePendingFlag)) return null;
            return ExtractVersion(File.ReadAllText(UpdatePendingFlag));
        }
        catch { return null; }
    }

    internal static void MarkRunningVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        try
        {
            File.WriteAllText(VersionState, version.Trim(), new UTF8Encoding(false));
            if (File.Exists(UpdatePendingFlag)) File.Delete(UpdatePendingFlag);
        }
        catch { }
    }

    private static void RunBackgroundCheck(
        Action<string> log,
        Action<string> onUpdateReady,
        SynchronizationContext ui)
    {
        string registryVersion = QueryRegistryVersion(log);
        if (registryVersion == null) return;

        string knownVersion = ReadKnownVersion();
        if (knownVersion != null &&
            string.Equals(knownVersion, registryVersion, StringComparison.OrdinalIgnoreCase)) return;
        if (knownVersion == null)
        {
            log("DSH update check deferred: running version is not known yet");
            return;
        }

        log("DSH update available: " + knownVersion + " -> " + registryVersion);
        try { File.WriteAllText(UpdatePendingFlag, registryVersion, new UTF8Encoding(false)); } catch { }
        Post(ui, delegate { onUpdateReady(registryVersion); });
    }

    private static string QueryRegistryVersion(Action<string> log)
    {
        string output = RunNpmCommand("npm.cmd view " + PackageName + " version", 30000);
        if (output == null)
        {
            log("DSH update check skipped: npm view failed");
            return null;
        }
        return ExtractVersion(output);
    }

    private static string PrefetchLatest(Action<string> log)
    {
        string output = RunNpmCommand("npx.cmd --yes " + PackageName + "@latest --version", 300000);
        if (output == null)
        {
            log("DSH update prefetch failed");
            return null;
        }
        string version = ExtractVersion(output);
        if (version == null) log("DSH update prefetch returned no version");
        return version;
    }

    private static string ReadKnownVersion()
    {
        try
        {
            if (!File.Exists(VersionState)) return null;
            return ExtractVersion(File.ReadAllText(VersionState));
        }
        catch { return null; }
    }

    private static string ExtractVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match match = VersionPattern.Match(text.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string RunNpmCommand(string command, int timeoutMs)
    {
        Process process = null;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"" + command + " 2>&1\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                StandardOutputEncoding = Encoding.UTF8
            };
            process = Process.Start(info);
            if (process == null) return null;

            var output = new StringBuilder();
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) output.AppendLine(e.Data);
            };
            process.BeginOutputReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            process.WaitForExit();
            if (process.ExitCode != 0) return null;
            string combined = output.ToString().Trim();
            return string.IsNullOrEmpty(combined) ? null : combined;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (process != null) process.Dispose();
        }
    }

    private static void Post(SynchronizationContext ui, SendOrPostCallback callback)
    {
        if (ui != null)
        {
            try { ui.Post(callback, null); return; }
            catch { }
        }
        callback(null);
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int DefaultPort = 3080;
    private const int ProbeIntervalMs = 1000;

    // A cold "npx --yes" install of DSH can take several minutes before the server binds.
    private const int ProbeTimeoutMs = 600000;

    private static readonly string DefaultUrl = "http://127.0.0.1:" + DefaultPort + "/";

    // NotifyIcon.Text rejects anything longer than this.
    private const int MaxTooltipLength = 63;

    private static readonly Regex AnsiPattern = new Regex(
        @"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex AnnouncedUrlPattern = new Regex(
        @"dsh web:\s*(https?://[^\s\x07\x1b]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LoopbackUrlPattern = new Regex(
        @"https?://(?:localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0)(?::\d+)?[^\s\x07\x1b]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string stdoutLog = Path.Combine(Program.AppDirectory, "dsh-web.log");
    private readonly string stderrLog = Path.Combine(Program.AppDirectory, "dsh-web-error.log");
    private readonly string urlState = Path.Combine(Program.AppDirectory, "dsh-web.url");
    private readonly object logLock = new object();
    private readonly SynchronizationContext ui;
    private readonly NotifyIcon tray;
    private readonly ModernContextMenuStrip menu;
    private readonly ToolStripMenuItem openItem;
    private readonly ToolStripMenuItem restartItem;

    private Process dsh;
    private ProcessJob job;
    private System.Windows.Forms.Timer probeTimer;
    private int probeElapsedMs;
    private string url;
    private int generation;
    private bool exiting;
    private bool startedWithLatest;

    internal TrayApplicationContext()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        DeleteUrlState();

        menu = new ModernContextMenuStrip
        {
            AutoSize = true,
            BackColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(31, 35, 40),
            Padding = new Padding(0, 0, 0, 0),
            Renderer = new ModernMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false
        };
        menu.MinimumSize = new Size(MeasureMenuWidth(menu.Font), 0);
        openItem = CreateMenuItem("Open DSH", menu.Font, delegate { OpenDsh(); });
        openItem.Enabled = false;
        restartItem = CreateMenuItem("Restart DSH", menu.Font, delegate { RestartDsh(); });
        var exitItem = CreateMenuItem("Exit", menu.Font, delegate { ExitLauncher(); });
        menu.Items.Add(openItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(new ToolStripSeparator
        {
            Margin = new Padding(0, 4, 0, 4)
        });
        menu.Items.Add(exitItem);
        menu.Opening += delegate { ApplyMenuRegion(); };
        menu.SizeChanged += delegate { ApplyMenuRegion(); };

        Icon applicationIcon = null;
        try { applicationIcon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
        tray = new NotifyIcon
        {
            Icon = applicationIcon ?? SystemIcons.Application,
            Text = Program.ProductName,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += delegate(object sender, MouseEventArgs e)
        {
            // NotifyIcon.Click also fires on right-click on Windows, which opens the
            // browser and immediately dismisses the context menu before it can be used.
            if (e.Button == MouseButtons.Left) OpenDsh();
        };
        StartDsh(restart: false);
    }

    private static ToolStripMenuItem CreateMenuItem(string text, Font font, EventHandler handler)
    {
        const int VerticalPadding = 15;

        int leftInset = TextRenderer.MeasureText("M", font).Width;
        int textHeight = TextRenderer.MeasureText(text, font).Height;
        int itemHeight = textHeight + VerticalPadding * 2;

        return new ModernMenuItem(text, handler)
        {
            AutoSize = false,
            Margin = new Padding(0, 0, 0, 0),
            Size = new Size(MeasureMenuWidth(font), itemHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            TextLeftInset = leftInset,
            TextRightInset = leftInset
        };
    }

    private static int MeasureMenuWidth(Font font)
    {
        int horizontalUnit = TextRenderer.MeasureText("M", font).Width;
        return TextRenderer.MeasureText("Restart DSH", font).Width + horizontalUnit * 5;
    }

    private void ApplyMenuRegion()
    {
        // DWM already rounds the popup on Windows 11; clipping it again would cut into
        // the shadow and leave hard edges.
        if (menu.NativeChrome) return;
        if (menu.Width < 2 || menu.Height < 2) return;
        Region oldRegion = menu.Region;
        using (GraphicsPath path = UiGeometry.RoundedRectangle(
            new Rectangle(0, 0, menu.Width, menu.Height), 12))
            menu.Region = new Region(path);
        if (oldRegion != null) oldRegion.Dispose();
    }

    private void StartDsh(bool restart)
    {
        if (dsh != null && !dsh.HasExited) return;
        url = null;
        openItem.Enabled = false;
        StopProbe();
        DeleteUrlState();
        DisposeJob();
        File.WriteAllText(stdoutLog, "", new UTF8Encoding(false));
        File.WriteAllText(stderrLog, "", new UTF8Encoding(false));
        int thisGeneration = ++generation;
        string packageSpec = DshPackageUpdater.PackageSpecForStart(restart);
        startedWithLatest = packageSpec.IndexOf("@latest", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!EnsurePortIsFree())
        {
            SetTrayText(Program.ProductName + " - Port " + DefaultPort + " in use");
            return;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"npx.cmd --yes " + packageSpec + " web --no-open\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { HandleOutput(e.Data, stdoutLog, thisGeneration); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { HandleOutput(e.Data, stderrLog, thisGeneration); };
            process.Exited += delegate
            {
                try { ui.Post(delegate(object state) { HandleExit(process, thisGeneration); }, null); }
                catch { }
            };
            ProcessJob pending = ProcessJob.TryCreate();
            process.Start();
            if (pending != null && !pending.TryAssign(process))
            {
                pending.Dispose();
                pending = null;
            }
            job = pending;
            dsh = process;
            if (pending == null)
                AppendTrayNote("job object unavailable; falling back to taskkill for shutdown");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendTrayNote("starting " + packageSpec + " web --no-open");
            SetTrayText(Program.ProductName + " - Starting...");
            StartProbe();
        }
        catch (Exception ex)
        {
            dsh = null;
            DisposeJob();
            SetTrayText(Program.ProductName + " - Start failed");
            MessageBox.Show(
                "DSH could not be started. Make sure Node.js/npm is installed and npx is available.\r\n\r\n" + ex.Message,
                Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool EnsurePortIsFree()
    {
        if (!NetworkPorts.IsListening(DefaultPort)) return true;

        int pid = NetworkPorts.FindListenerPid(DefaultPort);
        if (pid <= 0)
        {
            ReportPortConflict("an unidentified process");
            return false;
        }

        string name = ProcessFacts.NameOf(pid) ?? "unknown";
        string label = name + " (PID " + pid + ")";
        string commandLine = ProcessFacts.CommandLineOf(pid);
        bool isDsh = commandLine != null &&
            commandLine.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0;
        bool mayBeDsh = commandLine == null && ProcessFacts.IsNodeHost(name);

        if (!isDsh && !mayBeDsh)
        {
            ReportPortConflict(label);
            return false;
        }
        if (mayBeDsh && MessageBox.Show(
                "Port " + DefaultPort + " is held by " + label + ", which looks like a leftover DSH server " +
                "but could not be identified for certain.\r\n\r\nTerminate it and start DSH?",
                Program.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return false;

        AppendTrayNote("reclaiming port " + DefaultPort + " from leftover " + label);
        KillTree(pid);
        if (NetworkPorts.WaitForRelease(DefaultPort, 10000)) return true;

        ReportPortConflict(label);
        return false;
    }

    private void ReportPortConflict(string owner)
    {
        AppendTrayNote("port " + DefaultPort + " is held by " + owner + "; DSH was not started");
        MessageBox.Show(
            "DSH cannot start because port " + DefaultPort + " is already in use by " + owner + ".\r\n\r\n" +
            "Stop that process, then choose Restart DSH from the tray menu.",
            Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void StartProbe()
    {
        StopProbe();
        probeElapsedMs = 0;
        int sourceGeneration = generation;
        probeTimer = new System.Windows.Forms.Timer { Interval = ProbeIntervalMs };
        probeTimer.Tick += delegate
        {
            probeElapsedMs += ProbeIntervalMs;
            if (sourceGeneration != generation || exiting || dsh == null || !string.IsNullOrEmpty(url))
            {
                StopProbe();
                return;
            }
            if (NetworkPorts.IsListening(DefaultPort))
            {
                AdoptUrl(DefaultUrl, sourceGeneration);
                return;
            }
            if (probeElapsedMs >= ProbeTimeoutMs) StopProbe();
        };
        probeTimer.Start();
    }

    private void StopProbe()
    {
        if (probeTimer == null) return;
        System.Windows.Forms.Timer stopping = probeTimer;
        probeTimer = null;
        stopping.Stop();
        stopping.Dispose();
    }

    private void HandleOutput(string line, string logPath, int sourceGeneration)
    {
        if (line == null) return;
        try
        {
            lock (logLock) File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }

        string candidate = ExtractUrl(line);
        if (candidate == null) return;

        try { ui.Post(delegate(object state) { AdoptUrl(candidate, sourceGeneration); }, null); }
        catch { }
    }

    private static string ExtractUrl(string line)
    {
        string clean = AnsiPattern.Replace(line, "");
        Match announced = AnnouncedUrlPattern.Match(clean);
        string raw = announced.Success
            ? announced.Groups[1].Value
            : LoopbackUrlPattern.Match(clean).Value;
        if (string.IsNullOrEmpty(raw)) return null;

        raw = raw.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '"', '\'');
        Uri parsed;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;
        return parsed.AbsoluteUri;
    }

    private void AdoptUrl(string candidate, int sourceGeneration)
    {
        if (sourceGeneration != generation || exiting || !string.IsNullOrEmpty(url)) return;
        url = candidate;
        try { File.WriteAllText(urlState, url, new UTF8Encoding(false)); } catch { }
        StopProbe();
        openItem.Enabled = true;
        RefreshRunningTrayText();
        TryCaptureRunningVersion();
        ScheduleBackgroundUpdateCheck();
        OpenDsh();
    }

    private void RefreshRunningTrayText()
    {
        if (exiting || string.IsNullOrEmpty(url)) return;
        SetTrayText(Program.ProductName + " - Running");
    }

    private void ScheduleBackgroundUpdateCheck()
    {
        DshPackageUpdater.ScheduleBackgroundCheck(
            ui,
            AppendTrayNote,
            delegate(string version)
            {
                if (exiting) return;
                tray.BalloonTipTitle = Program.ProductName + " update ready";
                tray.BalloonTipText = "DSH " + version +
                    " is available. Choose Restart DSH to download and switch to the latest version.";
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(8000);
            });
    }

    private void TryCaptureRunningVersion()
    {
        string version = ExtractDshVersion(ReadLog(stdoutLog));
        if (version == null) version = ExtractDshVersion(ReadLog(stderrLog));
        if (version == null && startedWithLatest)
            version = DshPackageUpdater.ReadPendingVersion();
        DshPackageUpdater.MarkRunningVersion(version);
        startedWithLatest = false;
    }

    private static string ExtractDshVersion(string log)
    {
        if (string.IsNullOrEmpty(log)) return null;
        Match match = Regex.Match(log, @"dsh(?:\s+web)?\s+v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private void SetTrayText(string tooltip)
    {
        tray.Text = tooltip.Length > MaxTooltipLength
            ? tooltip.Substring(0, MaxTooltipLength - 3) + "..."
            : tooltip;
    }

    private void HandleExit(Process process, int sourceGeneration)
    {
        if (sourceGeneration != generation) { process.Dispose(); return; }
        int exitCode = 0;
        try { exitCode = process.ExitCode; } catch { }
        process.Dispose();
        dsh = null;
        url = null;
        openItem.Enabled = false;
        StopProbe();
        DeleteUrlState();
        SetTrayText(Program.ProductName + " - Stopped");
        if (!exiting)
        {
            tray.BalloonTipTitle = Program.ProductName + " stopped";
            tray.BalloonTipText = DescribeExit(exitCode);
            tray.BalloonTipIcon = ToolTipIcon.Warning;
            tray.ShowBalloonTip(5000);
        }
    }

    private string DescribeExit(int exitCode)
    {
        string errors = ReadLog(stderrLog);
        if (errors != null && errors.IndexOf("EADDRINUSE", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Port " + DefaultPort + " was already in use, so DSH could not start. " +
                "Choose Restart DSH to reclaim the port.";
        return "DSH exited with code " + exitCode + ". See dsh-web-error.log in " + Program.AppDirectory + ".";
    }

    private string ReadLog(string path)
    {
        try
        {
            lock (logLock)
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private void AppendTrayNote(string text)
    {
        try
        {
            lock (logLock)
                File.AppendAllText(stdoutLog, "[tray] " + text + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    private void OpenDsh()
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            tray.BalloonTipTitle = Program.ProductName;
            tray.BalloonTipText = "DSH has not reported its web address yet.";
            tray.BalloonTipIcon = ToolTipIcon.Info;
            tray.ShowBalloonTip(3000);
            return;
        }
        try { DshBrowserGate.TryOpen(url); }
        catch { }
    }

    private void RestartDsh()
    {
        restartItem.Enabled = false;
        try
        {
            StopDsh();
            if (DshPackageUpdater.IsUpdatePending())
            {
                SetTrayText(Program.ProductName + " - Updating");
                DshPackageUpdater.PrefetchLatestBlocking(AppendTrayNote);
            }
            StartDsh(restart: true);
        }
        finally { restartItem.Enabled = true; }
    }

    private void StopDsh()
    {
        DeleteUrlState();
        url = null;
        openItem.Enabled = false;
        StopProbe();
        Process process = dsh;
        dsh = null;
        ++generation;

        // The job object owns every descendant, so it is the only reliable way to stop the
        // server: cmd.exe can exit before node does, which orphans node outside the tree.
        if (job == null && process != null)
        {
            try { if (!process.HasExited) KillTree(process.Id); }
            catch { }
        }
        DisposeJob();
        if (process == null) return;
        try { process.WaitForExit(5000); }
        catch { }
        finally { process.Dispose(); }
    }

    private void DisposeJob()
    {
        ProcessJob closing = job;
        job = null;
        if (closing == null) return;
        try { closing.Terminate(); }
        catch { }
        finally { closing.Dispose(); }
        NetworkPorts.WaitForRelease(DefaultPort, 5000);
    }

    private static void KillTree(int processId)
    {
        var killer = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
            Arguments = "/PID " + processId + " /T /F",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (killer != null) { killer.WaitForExit(5000); killer.Dispose(); }
    }

    private void ExitLauncher()
    {
        if (exiting) return;
        exiting = true;
        StopDsh();
        tray.Visible = false;
        ExitThread();
    }

    private void DeleteUrlState()
    {
        try { if (File.Exists(urlState)) File.Delete(urlState); } catch { }
        DshBrowserGate.ClearOpenStamp();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!exiting) { exiting = true; StopDsh(); }
            StopProbe();
            DisposeJob();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Windows job object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so the kernel
/// terminates the whole DSH process tree even if this launcher is force-killed or crashes.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const int ExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformationData
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    private IntPtr handle;

    private ProcessJob(IntPtr jobHandle)
    {
        handle = jobHandle;
    }

    internal static ProcessJob TryCreate()
    {
        IntPtr jobHandle = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) return null;

            var limits = new ExtendedLimitInformationData();
            limits.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;
            int size = Marshal.SizeOf(typeof(ExtendedLimitInformationData));
            buffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(jobHandle, ExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(jobHandle);
                return null;
            }
            ProcessJob created = new ProcessJob(jobHandle);
            jobHandle = IntPtr.Zero;
            return created;
        }
        catch
        {
            if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    internal bool TryAssign(Process process)
    {
        try { return handle != IntPtr.Zero && AssignProcessToJobObject(handle, process.Handle); }
        catch { return false; }
    }

    internal void Terminate()
    {
        if (handle != IntPtr.Zero) TerminateJobObject(handle, 0);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}

internal static class NetworkPorts
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool sort, int addressFamily, int tableClass, int reserved);

    private const int AddressFamilyInet = 2;
    private const int TableOwnerPidAll = 5;
    private const int StateListen = 2;
    private const int RowSize = 24;

    internal static bool IsListening(int port)
    {
        try
        {
            IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i].Port == port) return true;
        }
        catch { }
        return false;
    }

    internal static bool WaitForRelease(int port, int timeoutMs)
    {
        int waited = 0;
        while (IsListening(port))
        {
            if (waited >= timeoutMs) return false;
            Thread.Sleep(250);
            waited += 250;
        }
        return true;
    }

    internal static int FindListenerPid(int port)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyInet, TableOwnerPidAll, 0);
        if (size <= 0) return 0;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AddressFamilyInet, TableOwnerPidAll, 0) != 0)
                return 0;
            int count = Marshal.ReadInt32(buffer);
            IntPtr row = new IntPtr(buffer.ToInt64() + 4);
            for (int i = 0; i < count; i++)
            {
                int state = Marshal.ReadInt32(row, 0);
                int encodedPort = Marshal.ReadInt32(row, 8);
                int localPort = ((encodedPort & 0xFF) << 8) | ((encodedPort >> 8) & 0xFF);
                if (state == StateListen && localPort == port) return Marshal.ReadInt32(row, 20);
                row = new IntPtr(row.ToInt64() + RowSize);
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buffer); }
        return 0;
    }
}

internal static class ProcessFacts
{
    internal static string NameOf(int processId)
    {
        try
        {
            using (Process process = Process.GetProcessById(processId))
                return process.ProcessName + ".exe";
        }
        catch { return null; }
    }

    internal static string CommandLineOf(int processId)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject row in results)
                    using (row)
                        return row["CommandLine"] as string;
            }
        }
        catch { }
        return null;
    }

    internal static bool IsNodeHost(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return processName.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0 ||
            processName.IndexOf("npm", StringComparison.OrdinalIgnoreCase) >= 0 ||
            processName.IndexOf("npx", StringComparison.OrdinalIgnoreCase) >= 0 ||
            string.Equals(processName, "cmd.exe", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfo version);

    [StructLayout(LayoutKind.Sequential)]
    private struct OsVersionInfo
    {
        public int Size;
        public int MajorVersion;
        public int MinorVersion;
        public int BuildNumber;
        public int PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
    }

    private const int CornerPreference = 33;
    private const int BorderColorAttribute = 34;
    private const int RoundedCorners = 2;
    private const int FirstWindows11Build = 22000;

    /// <summary>
    /// True on Windows 11 and newer, where DWM can round a window and draw the large
    /// system shadow around it. Read through RtlGetVersion because Environment.OSVersion
    /// is shimmed to 6.2 for .NET Framework binaries without a supportedOS manifest.
    /// </summary>
    internal static readonly bool SupportsModernChrome = DetectModernChrome();

    private static bool DetectModernChrome()
    {
        try
        {
            var version = new OsVersionInfo();
            version.Size = Marshal.SizeOf(typeof(OsVersionInfo));
            if (RtlGetVersion(ref version) != 0) return false;
            if (version.MajorVersion > 10) return true;
            return version.MajorVersion == 10 && version.BuildNumber >= FirstWindows11Build;
        }
        catch { return false; }
    }

    internal static bool TryRoundCorners(IntPtr window)
    {
        if (!SupportsModernChrome) return false;
        int preference = RoundedCorners;
        try { return DwmSetWindowAttribute(window, CornerPreference, ref preference, sizeof(int)) == 0; }
        catch { return false; }
    }

    internal static void SetBorderColor(IntPtr window, Color color)
    {
        if (!SupportsModernChrome) return;
        int value = color.R | (color.G << 8) | (color.B << 16);
        try { DwmSetWindowAttribute(window, BorderColorAttribute, ref value, sizeof(int)); }
        catch { }
    }
}

/// <summary>
/// Borrows the technique Chromium uses for frameless windows: keep WS_THICKFRAME so DWM
/// draws its large soft window shadow, then remove the non-client area in WM_NCCALCSIZE
/// so none of that frame is painted. This is what gives Electron-based tray menus their
/// shadow, in place of the small hard CS_DROPSHADOW that WinForms popups use by default.
/// </summary>
internal static class FramelessChrome
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    internal const int WsThickFrame = 0x00040000;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HitTestClient = 1;
    private const int HitTestCaption = 2;
    private const int FirstResizeHitTest = 10;
    private const int LastResizeHitTest = 18;
    private const int MinTrackSizeOffset = 24;

    internal static readonly Color BorderColor = Color.FromArgb(218, 222, 228);

    /// <summary>Claims the whole window as client area. Returns true when the message is handled.</summary>
    internal static bool TrySuppressFrame(ref Message m)
    {
        if (!WindowChrome.SupportsModernChrome) return false;
        if (m.Msg != WmNcCalcSize || m.WParam == IntPtr.Zero) return false;
        m.Result = IntPtr.Zero;
        return true;
    }

    internal static void NormalizeFrameResults(ref Message m)
    {
        if (!WindowChrome.SupportsModernChrome) return;

        // WS_THICKFRAME also brings the system minimum tracking size, which would
        // otherwise stretch a narrow window such as the tray menu.
        if (m.Msg == WmGetMinMaxInfo)
        {
            Marshal.WriteInt32(m.LParam, MinTrackSizeOffset, 1);
            Marshal.WriteInt32(m.LParam, MinTrackSizeOffset + 4, 1);
            return;
        }

        // Without a frame there is nothing to resize, so the outer pixels must stay
        // ordinary content rather than reporting resize borders.
        if (m.Msg == WmNcHitTest)
        {
            int hit = m.Result.ToInt32();
            if (hit >= FirstResizeHitTest && hit <= LastResizeHitTest) m.Result = new IntPtr(HitTestClient);
        }
    }

    internal static void DragWindow(IntPtr window)
    {
        ReleaseCapture();
        SendMessage(window, WmNcLeftButtonDown, new IntPtr(HitTestCaption), IntPtr.Zero);
    }
}

internal sealed class ModernContextMenuStrip : ContextMenuStrip
{
    private bool nativeChrome;

    /// <summary>True when DWM is rounding this window, so nothing should clip or draw corners.</summary>
    internal bool NativeChrome { get { return nativeChrome; } }

    internal ModernContextMenuStrip()
    {
        DropShadowEnabled = !WindowChrome.SupportsModernChrome;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            if (WindowChrome.SupportsModernChrome) parameters.Style |= FramelessChrome.WsThickFrame;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        nativeChrome = WindowChrome.TryRoundCorners(Handle);
        if (nativeChrome) WindowChrome.SetBorderColor(Handle, FramelessChrome.BorderColor);
    }

    protected override void WndProc(ref Message m)
    {
        if (FramelessChrome.TrySuppressFrame(ref m)) return;
        base.WndProc(ref m);
        FramelessChrome.NormalizeFrameResults(ref m);
    }
}

internal enum DialogTone
{
    Success,
    Question,
    Failure
}

/// <summary>Flat button that paints its own antialiased rounded shape and hover states.</summary>
internal sealed class PillButton : Button
{
    private readonly bool primary;
    private bool hovered;
    private bool pressed;

    internal PillButton(string text, bool isPrimary)
    {
        primary = isPrimary;
        Text = text;
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color fill;
        Color text;
        Color edge;
        if (primary)
        {
            fill = pressed ? Color.FromArgb(60, 66, 73)
                : hovered ? Color.FromArgb(45, 51, 57)
                : Color.FromArgb(31, 35, 40);
            text = Color.White;
            edge = fill;
        }
        else
        {
            fill = pressed ? Color.FromArgb(233, 236, 239)
                : hovered ? Color.FromArgb(243, 244, 246)
                : Color.White;
            text = Color.FromArgb(31, 35, 40);
            edge = Color.FromArgb(208, 213, 218);
        }

        e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = UiGeometry.RoundedRectangle(bounds, Height / 4))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(edge))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues)
        {
            using (var pen = new Pen(primary ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(31, 35, 40)))
            using (GraphicsPath path = UiGeometry.RoundedRectangle(
                Rectangle.Inflate(bounds, -3, -3), Math.Max(2, Height / 5)))
                e.Graphics.DrawPath(pen, path);
        }
    }
}

/// <summary>
/// Replaces the stock MessageBox for install and uninstall feedback: a frameless card with
/// the product icon, a heading, a wrapped explanation, and a footer with real actions.
/// </summary>
internal sealed class ModernDialog : Form
{
    private static readonly Color HeadingColor = Color.FromArgb(31, 35, 40);
    private static readonly Color BodyColor = Color.FromArgb(87, 96, 106);
    private static readonly Color FooterColor = Color.FromArgb(247, 248, 249);

    private readonly int footerTop;

    internal ModernDialog(string heading, string body, string primaryText, string secondaryText, DialogTone tone)
    {
        SuspendLayout();
        Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Text = Program.ProductName;
        Icon appIcon = TryLoadAppIcon();
        if (appIcon != null) Icon = appIcon;

        // Every measurement derives from the font height so the card scales with the display.
        int unit = Font.Height;
        int margin = (int)(unit * 1.4f);
        int glyph = unit * 2;
        int width = unit * 26;
        int textLeft = margin + glyph + margin;

        var symbol = new PictureBox
        {
            Bounds = new Rectangle(margin, margin, glyph, glyph),
            Image = LoadGlyph(tone, glyph),
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.Transparent
        };

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = HeadingColor,
            Location = new Point(textLeft, margin - (int)(unit * 0.15f)),
            MaximumSize = new Size(width - textLeft - margin, 0),
            Text = heading
        };

        var detail = new Label
        {
            AutoSize = true,
            ForeColor = BodyColor,
            MaximumSize = new Size(width - textLeft - margin, 0),
            Text = body
        };

        Controls.Add(symbol);
        Controls.Add(title);
        Controls.Add(detail);
        title.PerformLayout();
        detail.Location = new Point(textLeft, title.Bottom + (int)(unit * 0.5f));
        detail.PerformLayout();

        int contentBottom = Math.Max(detail.Bottom, symbol.Bottom);
        footerTop = contentBottom + margin;
        int buttonHeight = (int)(unit * 1.85f);
        int buttonWidth = (int)(unit * 5.6f);
        int footerHeight = buttonHeight + margin;
        ClientSize = new Size(width, footerTop + footerHeight);

        int right = width - margin;
        var primary = new PillButton(primaryText, true)
        {
            Bounds = new Rectangle(0, 0, MeasureButton(primaryText, buttonWidth), buttonHeight),
            DialogResult = DialogResult.OK,
            Font = Font,
            TabIndex = 0
        };
        primary.Left = right - primary.Width;
        primary.Top = footerTop + (footerHeight - buttonHeight) / 2;
        Controls.Add(primary);
        AcceptButton = primary;

        if (!string.IsNullOrEmpty(secondaryText))
        {
            var secondary = new PillButton(secondaryText, false)
            {
                Bounds = new Rectangle(0, 0, MeasureButton(secondaryText, buttonWidth), buttonHeight),
                DialogResult = DialogResult.Cancel,
                Font = Font,
                TabIndex = 1
            };
            secondary.Left = primary.Left - secondary.Width - (int)(unit * 0.5f);
            secondary.Top = primary.Top;
            Controls.Add(secondary);
            CancelButton = secondary;
        }
        else
        {
            CancelButton = primary;
        }

        foreach (Control control in new Control[] { symbol, title, detail })
            control.MouseDown += BeginDrag;
        MouseDown += BeginDrag;

        ResumeLayout(true);
        primary.Select();
    }

    private int MeasureButton(string text, int minimum)
    {
        int measured = TextRenderer.MeasureText(text, Font).Width + Font.Height * 2;
        return Math.Max(minimum, measured);
    }

    private static Icon TryLoadAppIcon()
    {
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
        catch { return null; }
    }

    private static Image LoadGlyph(DialogTone tone, int size)
    {
        Icon source = tone == DialogTone.Failure ? SystemIcons.Error : TryLoadAppIcon();
        if (source == null) source = SystemIcons.Application;
        using (var sized = new Icon(source, size, size))
            return sized.ToBitmap();
    }

    private void BeginDrag(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) FramelessChrome.DragWindow(Handle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            if (WindowChrome.SupportsModernChrome) parameters.Style |= FramelessChrome.WsThickFrame;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (WindowChrome.TryRoundCorners(Handle))
            WindowChrome.SetBorderColor(Handle, FramelessChrome.BorderColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var brush = new SolidBrush(FooterColor))
            e.Graphics.FillRectangle(brush, 0, footerTop, ClientSize.Width, ClientSize.Height - footerTop);
        using (var pen = new Pen(Color.FromArgb(234, 237, 240)))
            e.Graphics.DrawLine(pen, 0, footerTop, ClientSize.Width, footerTop);
    }

    protected override void WndProc(ref Message m)
    {
        if (FramelessChrome.TrySuppressFrame(ref m)) return;
        base.WndProc(ref m);
        FramelessChrome.NormalizeFrameResults(ref m);
    }

    internal static bool Confirm(string heading, string body, string primaryText, string secondaryText, DialogTone tone)
    {
        using (var dialog = new ModernDialog(heading, body, primaryText, secondaryText, tone))
            return dialog.ShowDialog() == DialogResult.OK;
    }

    internal static void Inform(string heading, string body, DialogTone tone)
    {
        Confirm(heading, body, "Close", null, tone);
    }
}

internal static class UiGeometry
{
    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        return RoundedRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
    }

    internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModernMenuItem : ToolStripMenuItem
{
    internal int TextLeftInset;
    internal int TextRightInset;

    internal ModernMenuItem(string text, EventHandler handler) : base(text, null, handler)
    {
    }
}

internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    internal ModernMenuRenderer() : base(new ModernMenuColorTable())
    {
        RoundedEdges = false;
    }

    private static bool HasNativeChrome(ToolStrip toolStrip)
    {
        var modern = toolStrip as ModernContextMenuStrip;
        return modern != null && modern.NativeChrome;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (HasNativeChrome(e.ToolStrip))
        {
            e.Graphics.Clear(Color.White);
            return;
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(Color.White))
        using (GraphicsPath path = UiGeometry.RoundedRectangle(
            new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 12))
            e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (HasNativeChrome(e.ToolStrip)) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(218, 222, 228)))
        using (GraphicsPath path = UiGeometry.RoundedRectangle(
            new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 12))
            e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(Color.FromArgb(243, 244, 246)))
        using (GraphicsPath path = UiGeometry.RoundedRectangle(
            new Rectangle(0, 0, e.Item.Width, e.Item.Height), 7))
            e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        e.TextColor = e.Item.Enabled
            ? Color.FromArgb(31, 35, 40)
            : Color.FromArgb(156, 163, 175);

        var modern = e.Item as ModernMenuItem;
        var textRect = e.TextRectangle;
        if (modern != null)
        {
            textRect.X = modern.TextLeftInset;
            textRect.Width = e.Item.Width - modern.TextLeftInset - modern.TextRightInset;
        }
        textRect.Y = 0;
        textRect.Height = e.Item.Height;
        TextFormatFlags flags = TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis;
        if ((e.TextFormat & TextFormatFlags.RightToLeft) == TextFormatFlags.RightToLeft)
            flags |= TextFormatFlags.RightToLeft;

        TextRenderer.DrawText(
            e.Graphics,
            e.Text,
            e.TextFont,
            textRect,
            e.TextColor,
            flags);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var pen = new Pen(Color.FromArgb(229, 231, 235)))
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }
}

internal sealed class ModernMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground { get { return Color.White; } }
    public override Color ImageMarginGradientBegin { get { return Color.White; } }
    public override Color ImageMarginGradientMiddle { get { return Color.White; } }
    public override Color ImageMarginGradientEnd { get { return Color.White; } }
    public override Color MenuBorder { get { return Color.Transparent; } }
    public override Color MenuItemBorder { get { return Color.Transparent; } }
    public override Color MenuItemSelected { get { return Color.FromArgb(243, 244, 246); } }
}

internal static class IconFactory
{
    internal static void WriteIcon(string outputPath, string svgPath)
    {
        string svg = File.ReadAllText(svgPath);
        Match pathMatch = Regex.Match(svg, "<path\\s+[^>]*d=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (!pathMatch.Success) throw new InvalidDataException("The official DSH SVG path was not found.");
        using (GraphicsPath whale = ParseSvgPath(pathMatch.Groups[1].Value))
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            byte[][] frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                frames[i] = RenderPng(whale, sizes[i]);

            using (var stream = File.Create(outputPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)sizes.Length);
                int offset = 6 + (16 * sizes.Length);
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)frames[i].Length);
                    writer.Write((uint)offset);
                    offset += frames[i].Length;
                }
                for (int i = 0; i < frames.Length; i++) writer.Write(frames[i]);
            }
        }
    }

    private static byte[] RenderPng(GraphicsPath path, int size)
    {
        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Black))
        using (var memory = new MemoryStream())
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size / 50f, size / 50f);
            graphics.FillPath(brush, path);
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
    }

    private static GraphicsPath ParseSvgPath(string data)
    {
        MatchCollection tokens = Regex.Matches(
            data, @"[A-Za-z]|[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?");
        var path = new GraphicsPath(FillMode.Winding);
        int index = 0;
        char command = '\0';
        float currentX = 0;
        float currentY = 0;

        while (index < tokens.Count)
        {
            string token = tokens[index].Value;
            if (char.IsLetter(token[0]))
            {
                command = token[0];
                index++;
            }

            if (command == 'M')
            {
                currentX = Number(tokens[index++]);
                currentY = Number(tokens[index++]);
                path.StartFigure();
                command = 'L';
            }
            else if (command == 'L')
            {
                float x = Number(tokens[index++]);
                float y = Number(tokens[index++]);
                path.AddLine(currentX, currentY, x, y);
                currentX = x;
                currentY = y;
            }
            else if (command == 'C')
            {
                float x1 = Number(tokens[index++]);
                float y1 = Number(tokens[index++]);
                float x2 = Number(tokens[index++]);
                float y2 = Number(tokens[index++]);
                float x = Number(tokens[index++]);
                float y = Number(tokens[index++]);
                path.AddBezier(currentX, currentY, x1, y1, x2, y2, x, y);
                currentX = x;
                currentY = y;
            }
            else if (command == 'Z' || command == 'z')
            {
                path.CloseFigure();
                command = '\0';
            }
            else
            {
                throw new InvalidDataException("Unsupported command in the DSH SVG: " + command);
            }
        }
        return path;
    }

    private static float Number(Match token)
    {
        return float.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
