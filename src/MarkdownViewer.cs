// Markdown Viewer: renders Markdown files in its own window.
//
// The window hosts md.html in WebView2 (the Edge engine built into Windows). The page asks for files
// at https://mdview.example/C:/path/file.md; this process answers those requests itself, straight
// from disk, so there is no web server, no open port and no network traffic. A FileSystemWatcher
// tells the page when the file changes, so nothing polls. Closing the window ends everything.
//
//   MarkdownViewer.exe [file-or-folder]    open a window (no argument: the home page)
//   MarkdownViewer.exe --install           Start menu entry, "Open with" for .md, Settings > Apps entry
//   MarkdownViewer.exe --uninstall         undo --install and delete the app folder
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Markdown Viewer")]
[assembly: AssemblyProduct("Markdown Viewer")]
[assembly: AssemblyDescription("View Markdown files")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    public const string Name = "Markdown Viewer";
    public const string Version = "1.0";
    public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--install") return Setup.Install(args.Contains("--quiet"));
        if (args.Length > 0 && args[0] == "--uninstall") return Setup.Uninstall(args.Contains("--quiet"));
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }  // per-monitor DPI: sharp text
        Application.EnableVisualStyles();
        Application.Run(new Viewer(args.Length > 0 ? args[0] : null));
        return 0;
    }
}

// The page names files "C:/a/b.md" or "UNC/server/share/b.md".
static class Paths
{
    public static string ToPage(string disk)
    {
        string full = Path.GetFullPath(disk);
        return full.StartsWith(@"\\") ? "UNC/" + full.Substring(2).Replace('\\', '/') : full.Replace('\\', '/');
    }

    public static string ToDisk(string page)
    {
        page = page.TrimStart('/');
        try
        {
            if (page.StartsWith("UNC/")) return Path.GetFullPath(@"\\" + page.Substring(4).Replace('/', '\\'));
            if (Regex.IsMatch(page, "^[A-Za-z]:(/|$)"))
                return Path.GetFullPath(page.Substring(0, 2) + "\\" + (page.Length > 3 ? page.Substring(3) : "").Replace('/', '\\'));
        }
        catch (Exception) { }
        return null;
    }

    // md.html?C:/a/b%20c.md : percent-encoded UTF-8, keeping / and : readable
    public static string PageFor(string target)
    {
        if (target == null) return "md.html";
        string full = Path.GetFullPath(target.Trim('"'));
        string p = ToPage(full);
        if (Directory.Exists(full)) p = p.TrimEnd('/') + "/";
        var url = new StringBuilder("md.html?");
        foreach (byte b in Encoding.UTF8.GetBytes(p))
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || "_.-~/:".IndexOf(c) >= 0) url.Append(c);
            else url.Append('%').Append(b.ToString("X2"));
        }
        return url.ToString();
    }

    public static bool IsMarkdown(string path)
    {
        return path != null && Regex.IsMatch(path, @"\.(md|markdown|mdown|mkd)$", RegexOptions.IgnoreCase);
    }
}

class Viewer : Form
{
    const string Host = "mdview.example";
    const string Origin = "https://" + Host + "/";
    const string Text8 = "text/plain; charset=utf-8";
    // Linked files the app opens in their usual program; anything else is shown in Explorer instead of run.
    static readonly HashSet<string> Openable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".txt", ".csv", ".tex", ".doc", ".docx",
        ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".mp3", ".mp4", ".wav", ".webm", ".html", ".htm" };
    static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { ".md", Text8 }, { ".markdown", Text8 }, { ".mdown", Text8 }, { ".mkd", Text8 }, { ".txt", Text8 },
        { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" },
        { ".webp", "image/webp" }, { ".avif", "image/avif" }, { ".svg", "image/svg+xml" }, { ".bmp", "image/bmp" },
        { ".ico", "image/x-icon" }, { ".pdf", "application/pdf" }, { ".mp4", "video/mp4" }, { ".webm", "video/webm" },
        { ".mp3", "audio/mpeg" }, { ".wav", "audio/wav" }, { ".ogg", "audio/ogg" },
        { ".css", "text/css" }, { ".js", "text/javascript" }, { ".json", "application/json" },
        { ".woff2", "font/woff2" }, { ".woff", "font/woff" }, { ".ttf", "font/ttf" } };

    readonly WebView2 web = new WebView2();
    readonly string target;
    readonly Timer debounce = new Timer { Interval = 150 };
    CoreWebView2Environment env;
    FileSystemWatcher watcher;
    string watchedFile;  // null while watching a whole folder

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public Viewer(string target)
    {
        this.target = target;
        Text = Program.Name;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        bool dark = SystemUsesDarkMode();
        BackColor = dark ? Color.FromArgb(18, 20, 22) : Color.FromArgb(252, 252, 251);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
        debounce.Tick += (s, e) =>
        {
            debounce.Stop();
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString("changed");
        };
        HandleCreated += (s, e) => DarkTitleBar(dark);
        Load += async (s, e) => { RestoreSize(); await Start(); };
        FormClosing += (s, e) => SaveSize();
        FormClosed += (s, e) => Unwatch();
    }

    async Task Start()
    {
        try
        {
            // Everything shown is local, so skip the engine's background downloads and update checks.
            var options = new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update " +
                (Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? ""));
            env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.Dir, "data"), options);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Markdown Viewer needs the Microsoft Edge WebView2 Runtime, which isn't installed.\n\n" +
                "Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        CoreWebView2 core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsReputationCheckingRequired = false;  // no SmartScreen lookups: web links open in your browser instead
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Serve;
        core.NavigationStarting += Navigating;
        core.NewWindowRequested += (s, e) => { e.Handled = true; OpenOutside(e.Uri); };
        core.DocumentTitleChanged += (s, e) => { Text = string.IsNullOrEmpty(core.DocumentTitle) ? Program.Name : core.DocumentTitle; };
        core.WebMessageReceived += Message;
        core.Navigate(Origin + Paths.PageFor(target));
    }

    // ---------- answering the page's requests from disk ----------

    void Serve(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        Uri uri = new Uri(e.Request.Uri);
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path == "/md.html")
        {
            byte[] page = File.ReadAllBytes(Path.Combine(Program.Dir, "md.html"));
            Reply(e, 200, page, "text/html; charset=utf-8", PagePolicy(page));
        }
        else if (path.StartsWith("/katex/"))
        {
            string file = Path.GetFullPath(Path.Combine(Program.Dir, "katex", path.Substring(7).Replace('/', '\\')));
            if (file.StartsWith(Path.Combine(Program.Dir, "katex") + "\\") && File.Exists(file)) Reply(e, 200, File.ReadAllBytes(file), TypeOf(file), null);
            else Reply(e, 404, null, Text8, null);
        }
        else if (path == "/__md/id")
        {
            Reply(e, 200, Encoding.UTF8.GetBytes(Json.Places()), "application/json", null);
        }
        else if (path == "/__md/list")
        {
            string dir = Paths.ToDisk(QueryValue(uri.Query, "dir") ?? "");
            CoreWebView2Deferral deferral = e.GetDeferral();
            Task.Run(() => dir != null && Directory.Exists(dir) ? Json.Listing(dir) : null).ContinueWith(t =>
            {
                if (t.Result == null) Reply(e, 404, null, Text8, null);
                else Reply(e, 200, Encoding.UTF8.GetBytes(t.Result), "application/json", null);
                deferral.Complete();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            string disk = Paths.ToDisk(path);
            byte[] body = disk != null && File.Exists(disk) ? ReadShared(disk) : null;
            if (body == null) Reply(e, 404, null, Text8, null);
            // Files other than the viewer run sandboxed: an HTML or SVG opened here can't reach other files.
            else Reply(e, 200, body, TypeOf(disk), disk.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? null : "sandbox");
        }
    }

    void Reply(CoreWebView2WebResourceRequestedEventArgs e, int status, byte[] body, string type, string policy)
    {
        string headers = "Content-Type: " + type + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-cache";
        if (policy != null) headers += "\r\nContent-Security-Policy: " + policy;
        e.Response = env.CreateWebResourceResponse(new MemoryStream(body ?? new byte[0]), status, status == 200 ? "OK" : "Not Found", headers);
    }

    // md.html may run only its own scripts (by hash) and the bundled KaTeX, and may only fetch from this app,
    // so a hostile .md file can't run code that reads other files.
    static string PagePolicy(byte[] page)
    {
        var hashes = new StringBuilder();
        using (var sha = SHA256.Create())
            foreach (Match m in Regex.Matches(Encoding.UTF8.GetString(page), @"<script>([\s\S]*?)</script>"))
                hashes.Append(" 'sha256-").Append(Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(m.Groups[1].Value.Replace("\r\n", "\n"))))).Append('\'');
        return "default-src 'none'; script-src 'self'" + hashes + "; style-src 'self' 'unsafe-inline'; font-src 'self'; " +
               "img-src * data: blob:; media-src * data: blob:; connect-src 'self'; base-uri 'none'; form-action 'none'";
    }

    static byte[] ReadShared(string path)
    {
        try
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var m = new MemoryStream())
            {
                f.CopyTo(m);
                return m.ToArray();
            }
        }
        catch (Exception) { return null; }
    }

    static string TypeOf(string path)
    {
        string type;
        return Types.TryGetValue(Path.GetExtension(path), out type) ? type : "application/octet-stream";
    }

    static string QueryValue(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&'))
            if (pair.StartsWith(name + "=")) return Uri.UnescapeDataString(pair.Substring(name.Length + 1));
        return null;
    }

    // ---------- links and navigation ----------

    void Navigating(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Uri u;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out u)) { e.Cancel = true; return; }
        if (u.Host != Host) { e.Cancel = true; OpenOutside(e.Uri); return; }
        string path = Uri.UnescapeDataString(u.AbsolutePath);
        if (path == "/md.html" || u.Query == "?raw") { Unwatch(); return; }
        e.Cancel = true;
        string disk = Paths.ToDisk(path);
        if (disk == null) return;
        if (Directory.Exists(disk) || Paths.IsMarkdown(disk)) BeginInvoke((Action)(() => web.CoreWebView2.Navigate(Origin + Paths.PageFor(disk))));
        else if (File.Exists(disk)) OpenFile(disk);
    }

    static void OpenOutside(string address)
    {
        Uri u;
        if (Uri.TryCreate(address, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto"))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    static void OpenFile(string disk)
    {
        if (Openable.Contains(Path.GetExtension(disk))) Process.Start(new ProcessStartInfo(disk) { UseShellExecute = true });
        else Process.Start("explorer.exe", "/select,\"" + disk + "\"");
    }

    // ---------- messages from the page ----------

    void Message(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin)) return;
        string m;
        try { m = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
        if (m.StartsWith("watch:")) Watch(Paths.ToDisk(m.Substring(6)), false);
        else if (m.StartsWith("watchdir:")) Watch(Paths.ToDisk(m.Substring(9)), true);
        else if (m.StartsWith("theme:")) DarkTitleBar(m == "theme:dark");
        else if (m == "pick") Pick();
        else if (m == "close") Close();
        else if (m == "open" && e.AdditionalObjects != null && e.AdditionalObjects.Count > 0)
        {
            var file = e.AdditionalObjects[0] as CoreWebView2File;
            if (file != null && !string.IsNullOrEmpty(file.Path)) web.CoreWebView2.Navigate(Origin + Paths.PageFor(file.Path));
        }
    }

    void Pick()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "Open a Markdown file";
            dialog.Filter = "Markdown files (*.md, *.markdown)|*.md;*.markdown;*.mdown;*.mkd|All files (*.*)|*.*";
            if (dialog.ShowDialog(this) == DialogResult.OK) web.CoreWebView2.Navigate(Origin + Paths.PageFor(dialog.FileName));
        }
    }

    // ---------- change notifications (Windows tells us; nothing polls) ----------

    void Watch(string path, bool folder)
    {
        Unwatch();
        if (path == null) return;
        try
        {
            string dir = folder ? path : Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) return;
            watchedFile = folder ? null : path;
            watcher = new FileSystemWatcher(dir);
            watcher.IncludeSubdirectories = folder;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                                   (folder ? NotifyFilters.DirectoryName : 0);
            watcher.Changed += (s, e) => Changed(e.FullPath, null);
            watcher.Created += (s, e) => Changed(e.FullPath, null);
            watcher.Deleted += (s, e) => Changed(e.FullPath, null);
            watcher.Renamed += (s, e) => Changed(e.FullPath, e.OldFullPath);  // editors often save by renaming a temp file
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { Unwatch(); }
    }

    void Changed(string path, string oldPath)
    {
        bool relevant = watchedFile != null
            ? string.Equals(path, watchedFile, StringComparison.OrdinalIgnoreCase) || string.Equals(oldPath, watchedFile, StringComparison.OrdinalIgnoreCase)
            : Paths.IsMarkdown(path) || Paths.IsMarkdown(oldPath) || !Path.HasExtension(path);
        if (!relevant) return;
        try { BeginInvoke((Action)(() => { debounce.Stop(); debounce.Start(); })); } catch (InvalidOperationException) { }
    }

    void Unwatch()
    {
        if (watcher == null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        watcher = null;
    }

    // ---------- window chrome ----------

    static bool SystemUsesDarkMode()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            return k != null && Equals(k.GetValue("AppsUseLightTheme"), 0);
    }

    void DarkTitleBar(bool dark)
    {
        if (!IsHandleCreated) return;
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);  // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    void RestoreSize()
    {
        float scale = DeviceDpi / 96f;
        int w = 1120, h = 860, max = 0;
        using (var k = Registry.CurrentUser.OpenSubKey(Setup.AppKey))
            if (k != null)
            {
                w = (int)k.GetValue("WindowWidth", w);
                h = (int)k.GetValue("WindowHeight", h);
                max = (int)k.GetValue("WindowMaximized", 0);
            }
        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min((int)(w * scale), area.Width), Math.Min((int)(h * scale), area.Height));
        MinimumSize = new Size((int)(360 * scale), (int)(300 * scale));
        if (max == 1) WindowState = FormWindowState.Maximized;
    }

    void SaveSize()
    {
        float scale = DeviceDpi / 96f;
        Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        using (var k = Registry.CurrentUser.CreateSubKey(Setup.AppKey))
        {
            k.SetValue("WindowWidth", (int)(r.Width / scale), RegistryValueKind.DWord);
            k.SetValue("WindowHeight", (int)(r.Height / scale), RegistryValueKind.DWord);
            k.SetValue("WindowMaximized", WindowState == FormWindowState.Maximized ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}

static class Json
{
    static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // Desktop, Documents and Downloads, wherever Windows keeps them (they may be on OneDrive).
    public static string Places()
    {
        var items = new List<string>();
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
            if (k != null)
                foreach (var place in new[] { new[] { "Desktop", "Desktop" }, new[] { "Documents", "Personal" },
                                              new[] { "Downloads", "{374DE290-123F-4565-9164-39C4925E467B}" } })
                {
                    string path = Environment.ExpandEnvironmentVariables(k.GetValue(place[1], "") as string ?? "");
                    if (path.Length > 0 && Directory.Exists(path))
                        items.Add("{\"name\":" + Str(place[0]) + ",\"p\":" + Str(Paths.ToPage(path).TrimEnd('/') + "/") + "}");
                }
        return "{\"places\":[" + string.Join(",", items) + "]}";
    }

    static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "node_modules", "__pycache__", "venv", "site-packages", "AppData", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "System Volume Information" };

    // Markdown files under a folder, breadth first, within a time and size budget.
    public static string Listing(string folder)
    {
        var clock = Stopwatch.StartNew();
        var files = new List<string>();
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(new DirectoryInfo(folder));
        int dirs = 0;
        bool truncated = false;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        while (queue.Count > 0)
        {
            if (dirs >= 5000 || files.Count >= 3000 || clock.ElapsedMilliseconds > 2000) { truncated = true; break; }
            dirs++;
            try
            {
                foreach (FileSystemInfo entry in queue.Dequeue().EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        bool skip = entry.Name.StartsWith(".") || entry.Name.StartsWith("$") || Skip.Contains(entry.Name) ||
                                    (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (!skip) queue.Enqueue((DirectoryInfo)entry);
                    }
                    else if (Paths.IsMarkdown(entry.Name))
                    {
                        double t = (entry.LastWriteTimeUtc - epoch).TotalSeconds;
                        files.Add("{\"p\":" + Str(Paths.ToPage(entry.FullName)) + ",\"t\":" + t.ToString("0.###", CultureInfo.InvariantCulture) + "}");
                    }
                }
            }
            catch (Exception) { }  // unreadable folder
        }
        return "{\"files\":[" + string.Join(",", files) + "],\"truncated\":" + (truncated ? "true" : "false") +
               ",\"ms\":" + clock.ElapsedMilliseconds + "}";
    }
}

// Per-user registration; nothing here needs administrator rights.
static class Setup
{
    public const string AppKey = @"Software\MarkdownViewer";
    const string Classes = @"Software\Classes\";
    const string ProgId = "MarkdownViewer.md";
    const string Capabilities = AppKey + @"\Capabilities";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MarkdownViewer";
    static readonly string[] Extensions = { ".md", ".markdown" };
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.Name + ".lnk");

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool DeleteFile(string path);

    static void Put(string key, string name, object value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(key))
            k.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MarkdownViewer");

    public static int Install(bool quiet)
    {
        if (!string.Equals(Program.Dir, Home, StringComparison.OrdinalIgnoreCase))
        {
            // Run from a download: copy the app to its per-user home first, then register that copy.
            try
            {
                foreach (string file in Directory.GetFiles(Program.Dir, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(Program.Dir.Length + 1);
                    if (relative.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) continue;
                    string destination = Path.Combine(Home, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination, true);
                    DeleteFile(destination + ":Zone.Identifier");  // drop the "downloaded" mark, or Windows warns on every launch
                }
            }
            catch (IOException)
            {
                MessageBox.Show("Close all Markdown Viewer windows, then run the installer again.", Program.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            Process.Start(Path.Combine(Home, "MarkdownViewer.exe"), "--install" + (quiet ? " --quiet" : "")).WaitForExit();
            return 0;
        }
        Register();
        if (!quiet)
            MessageBox.Show("Markdown Viewer is installed. Find it in Start, or double-click any .md file.\n\n" +
                "To open .md files with it by default: right-click a .md file > Open with > Choose another app > " +
                "Markdown Viewer > Always.", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void Register()
    {
        string exe = Application.ExecutablePath;
        string open = "\"" + exe + "\" \"%1\"";
        Put(Classes + ProgId, "", "Markdown document");
        Put(Classes + ProgId, "FriendlyTypeName", "Markdown document");
        Put(Classes + ProgId + @"\DefaultIcon", "", Path.Combine(Program.Dir, "file.ico"));
        Put(Classes + ProgId + @"\shell\open", "FriendlyAppName", Program.Name);
        Put(Classes + ProgId + @"\shell\open\command", "", open);
        Put(Classes + @"Applications\MarkdownViewer.exe", "FriendlyAppName", Program.Name);
        Put(Classes + @"Applications\MarkdownViewer.exe\DefaultIcon", "", exe + ",0");
        Put(Classes + @"Applications\MarkdownViewer.exe\shell\open\command", "", open);
        foreach (string ext in Extensions)
        {
            Put(Classes + @"Applications\MarkdownViewer.exe\SupportedTypes", ext, "");
            Put(Classes + ext + @"\OpenWithProgids", ProgId, "");
            Put(Capabilities + @"\FileAssociations", ext, ProgId);
        }
        // Windows lets only the user choose default apps; this lists us under Settings > Apps > Default apps.
        Put(Capabilities, "ApplicationName", Program.Name);
        Put(Capabilities, "ApplicationDescription", "View Markdown files");
        Put(@"Software\RegisteredApplications", Program.Name, Capabilities);
        long bytes = new DirectoryInfo(Program.Dir).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !f.FullName.StartsWith(Path.Combine(Program.Dir, "data"))).Sum(f => f.Length);
        Put(UninstallKey, "DisplayName", Program.Name);
        Put(UninstallKey, "DisplayVersion", Program.Version);
        Put(UninstallKey, "DisplayIcon", exe + ",0");
        Put(UninstallKey, "Publisher", Program.Name);
        Put(UninstallKey, "InstallLocation", Program.Dir);
        Put(UninstallKey, "UninstallString", "\"" + exe + "\" --uninstall");
        Put(UninstallKey, "NoModify", 1);
        Put(UninstallKey, "NoRepair", 1);
        Put(UninstallKey, "EstimatedSize", (int)(bytes / 1024));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link = shell.CreateShortcut(Shortcut);
        link.TargetPath = exe;
        link.WorkingDirectory = Program.Dir;
        link.IconLocation = exe + ",0";
        link.Description = "View Markdown files";
        link.Save();
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);  // SHCNE_ASSOCCHANGED
    }

    public static int Uninstall(bool quiet)
    {
        foreach (string key in new[] { Classes + ProgId, Classes + @"Applications\MarkdownViewer.exe", UninstallKey, AppKey })
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
            if (k != null) k.DeleteValue(Program.Name, false);
        foreach (string ext in Extensions)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Classes + ext + @"\OpenWithProgids", true))
                if (k != null) k.DeleteValue(ProgId, false);
            using (var k = Registry.CurrentUser.OpenSubKey(Classes + ext, true))
                if (k != null && Equals(k.GetValue(""), ProgId)) k.DeleteValue("", false);
        }
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        if (!quiet) MessageBox.Show(Program.Name + " has been removed.", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        // Delete the app folder once this process has exited, but only if it really is our install folder.
        if (string.Equals(Program.Dir, Home, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Program.Dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return 0;
    }
}
