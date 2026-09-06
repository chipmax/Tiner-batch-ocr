using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace MinerU25Tool
{
    public partial class MainWindow : Window
    {
        // --- Duong dan mineru ---
        private string _mineruPath;
        private const int MaxStemBytes = 200;
        private const int MaxLogLines = 500;

        // --- Trang thai chay ---
        private bool _running = false;
        private bool _cancelRequested = false;
        private bool _closing = false;

        // --- Lua chon duoc cache khi bat dau batch ---
        private bool _detailLog = false;
        private bool _retry = true;
        private string _cpuThreads = "2";
        private int _pageFrom = 0;
        private int _pageTo = 0;
        private string _serverUrl = "";
        private readonly SemaphoreSlim _apiRestartLock = new SemaphoreSlim(1, 1);
        private int _apiRestarts = 0;
        private volatile bool _batchAbort = false;
        private volatile bool _paused = false;
        private volatile bool _forceApiRestart = false;
        private int _consecFails = 0;
        private readonly List<KeyValuePair<DateTime, long>> _pageSamples = new List<KeyValuePair<DateTime, long>>();
        private readonly object _sampleLock = new object();
        private int _apiFilesSinceStart = 0;
        private int _activeFiles = 0;
        private const int ApiRestartEveryFiles = 25;
        private string _apiKey = "";
        private int _timeoutMinutes = 30;
        private bool _retryHigh = true;

        private readonly List<string> _logLines = new List<string>();
        private readonly object _logLock = new object();
        private readonly List<string> _pendingLog = new List<string>();
        private string _logFilePath = null;
        private DispatcherTimer _logTick;
        private Stopwatch _sw;
        private DateTime _startedTime;
        private string _lastProgress = "";
        private string _curFileName = "";
        private string _curFileFull = "";

        // --- Du lieu hien thi trang thai (file) ---
        private int _stTotal = 0;
        private int _stDone = 0;
        private int _stErrors = 0;
        private int _stSkipped = 0;
        private string _stLabel = "Cho bat dau";

        // --- Du lieu hien thi trang thai (trang) ---
        private long _totalPages = 0;
        private long _pagesDone = 0;
        private int _curPageInFile = 0;
        private int _curFilePages = 0;
        private bool _curFilePagesFromMineru = false;

        // --- Page-counts map ---
        private Dictionary<string, int> _pageMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _pageMapByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // --- mineru-api thường trú (Đợt 2 - B2) ---
        private CancellationTokenSource _batchCts = null;
        private Process _apiProc = null;
        private int _apiPort = 0;
        private string _apiUrl = null;
        private readonly ConcurrentDictionary<int, Process> _runningProcs = new ConcurrentDictionary<int, Process>();

        // --- Thread-safety locks (Đợt 2) ---
        private readonly object _stateLock = new object();   // ghi batch-state.json + counters đọc
        private readonly object _progLock = new object();    // CheckProgressLine / SetCurFile
        private readonly object _suspLock = new object();    // _suspicious
        private readonly object _reportLock = new object();  // _reportRecords
        private readonly object _errLock = new object();     // _errList

        // --- Kết quả batch (thread-safe) ---
        private readonly List<string> _suspicious = new List<string>();
        private readonly List<ReportRecord> _reportRecords = new List<ReportRecord>();
        private readonly List<ErrorEntry> _errList = new List<ErrorEntry>();

        // --- Timer cap nhat ETA ---
        private DispatcherTimer _tick;

        private static readonly Regex _rxPage = new Regex(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

        // --- Bo loc nhieu (tqdm/progress) khoi nhat ky ---
        private static readonly Regex NoiseRx = new Regex(@"(%\||\|\s*\d+\s*/\s*\d+\s*\[|it/s|s/it|Predict:|Fetching\s+\d+\s+files|^\s*$)", RegexOptions.Compiled);
        private static bool IsKeepKeyword(string line) =>
            line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("traceback", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0;

        // --- Ngan may ngu khi dang chay batch ---
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // --- MinerU 4.x (mineru-kit trong venv rieng) ---
        private string _mineru4xKit = null;
        private bool _forceFileMode = false;
        private string _curBackend = "";
        private volatile bool _noProgressParse = false;
        private string _apiServerBackend = null;
        private bool _crossSkipLogged = false;
        // P1 Fluent UI: nav / toast / accent / dock
        private DispatcherTimer _toastTimer;

        // P3 tray (WinForms NotifyIcon) + cleanup presets
        private WinForms.NotifyIcon _tray = null;
        private bool _joinLines = false;
        private bool _squeezeBlanks = false;
        // C1 watch folder
        private FileSystemWatcher _watcher = null;
        private DispatcherTimer _watchTick = null;
        private readonly object _watchLock = new object();
        private Dictionary<string, WatchEntry> _watchPending = new Dictionary<string, WatchEntry>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _watchDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private class WatchEntry { public long Size; public DateTime Seen; }
        // C4 tim kiem
        private List<string> _resultPaths = new List<string>();

        public MainWindow()
        {
            InitializeComponent();

            // Hieu ung Enable/Disable effort theo engine
            SyncEffortState();

            // Mac dinh thu muc theo noi exe dang nam (khong phu thuoc duong dan may)
            srcBox.Text = Path.Combine(AppContext.BaseDirectory, "downloads");
            outBox.Text = Path.Combine(AppContext.BaseDirectory, "mineru25-out");

            // Cho phep ghi de thu muc qua tham so dong lenh: --src "..." --out "..."
            // (hoac khong can dau nhay: gop cac token cho den flag -- ke tiep)
            var cliArgs = Environment.GetCommandLineArgs();
            string cliEngine = null;
            for (int i = 1; i < cliArgs.Length; i++)
            {
                bool isSrc = string.Equals(cliArgs[i], "--src", StringComparison.OrdinalIgnoreCase);
                bool isOut = string.Equals(cliArgs[i], "--out", StringComparison.OrdinalIgnoreCase);
                bool isEngine = string.Equals(cliArgs[i], "--engine", StringComparison.OrdinalIgnoreCase);
                if (!isSrc && !isOut && !isEngine) continue;

                if (isEngine)
                {
                    // --engine <tag>: chi lay mot token ke tiep
                    if (i + 1 < cliArgs.Length) cliEngine = cliArgs[i + 1];
                    i++;
                    continue;
                }

                var parts = new List<string>();
                int j = i + 1;
                while (j < cliArgs.Length && !cliArgs[j].StartsWith("--", StringComparison.Ordinal))
                {
                    parts.Add(cliArgs[j]);
                    j++;
                }
                if (parts.Count > 0)
                {
                    string val = string.Join(" ", parts);
                    if (isSrc) srcBox.Text = val; else outBox.Text = val;
                }
                i = j - 1;
            }

            // CLI --engine: chon engine tuong ung (phai sau khi UI da san sang).
            // Phai chay TRUOC luong autostart ben duoi.
            if (!string.IsNullOrEmpty(cliEngine))
            {
                bool engineMatched = false;
                foreach (ComboBoxItem it in engineBox.Items)
                {
                    if (string.Equals(it.Tag as string, cliEngine, StringComparison.OrdinalIgnoreCase))
                    {
                        engineBox.SelectedItem = it;
                        engineMatched = true;
                        break;
                    }
                }
                if (!engineMatched)
                    AppendLog("[CLI] bo qua --engine khong khop voi item nao: " + cliEngine);
                SyncEffortState();
            }

            // Timer cap nhat trang thai moi giay
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (s, e) => { if (_running) RefreshStatus(); };

            // Timer flush nhat ky dinh ky (giam tai UI)
            _logTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _logTick.Tick += (s, e) => FlushLog();
            _logTick.Start();

            // Trang thai ban dau + ap theme/ngon ngu da luu
            try { InitDictRefs(); } catch { }
            try { LoadUiPrefs(); } catch { }
            try { ApplyTheme(_theme, false); } catch { }
            try { ApplyLang(_lang, false); } catch { }
            try { ApplyDisplayPrefs(); } catch { }
            SetPill("ready");
            try { SetupTray(); } catch { }
            try { GotoTab("cardConfig"); } catch { }

            // Tim mineru.exe (co the hoi chon neu chua thay)
            EnsureMineru(interactive: true);

            // Tu dong bat dau neu co tham so --autostart VA da tim thay mineru
            var args = cliArgs;
            if (args.Contains("--autostart", StringComparer.OrdinalIgnoreCase))
            {
                this.Loaded += (s, e) =>
                {
                    if (_mineruPath != null && File.Exists(_mineruPath))
                        TryAutoStart();
                    else
                        AppendLog("Khong the autostart vi chua tim thay mineru.exe. Hay bam BAT DAU.");
                };
            }
        }

        // =========================================================
        //  Tim va dam bao mineru.exe
        // =========================================================
        private string ResolveMineruPath()
        {
            // 1. File mineru-path.txt cung thu muc exe
            try
            {
                string txt = Path.Combine(AppContext.BaseDirectory, "mineru-path.txt");
                if (File.Exists(txt))
                {
                    string line = (File.ReadAllLines(txt).FirstOrDefault() ?? "").Trim();
                    if (line.Length > 0 && File.Exists(line))
                        return line;
                }
            }
            catch { }

            // 2. Quet bien PATH
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string cand = Path.Combine(dir.Trim(), "mineru.exe");
                        if (File.Exists(cand)) return cand;
                    }
                    catch { }
                }
            }
            catch { }

            // 3. Cac vi tri pho bien
            var found = new List<string>();
            void ScanPythonBase(string baseDir)
            {
                try
                {
                    if (!Directory.Exists(baseDir)) return;
                    foreach (var d in Directory.GetDirectories(baseDir, "Python*"))
                    {
                        string cand = Path.Combine(d, "Scripts", "mineru.exe");
                        if (File.Exists(cand)) found.Add(cand);
                    }
                }
                catch { }
            }

            try { ScanPythonBase(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python")); } catch { }
            try { ScanPythonBase(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Python")); } catch { }
            try { ScanPythonBase(@"C:\"); } catch { }

            var fixedPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3", "Scripts", "mineru.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3", "Scripts", "mineru.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "miniconda3", "Scripts", "mineru.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "anaconda3", "Scripts", "mineru.exe"),
            };
            foreach (var p in fixedPaths)
            {
                try { if (File.Exists(p)) return p; } catch { }
            }

            if (found.Count > 0) return found[0];

            return null;
        }

        // =========================================================
        //  Tim paddlex.exe cho engine paddleocr (thu tu uu tien)
        // =========================================================
        private string ResolvePaddleExe()
        {
            // 1. File paddle-path.txt canh exe tool — dong dau tien la duong dan day du toi paddlex.exe
            try
            {
                string txt = Path.Combine(AppContext.BaseDirectory, "paddle-path.txt");
                if (File.Exists(txt))
                {
                    string line = (File.ReadAllLines(txt).FirstOrDefault() ?? "").Trim();
                    if (line.Length > 0 && File.Exists(line))
                        return line;
                }
            }
            catch { }

            // 2. venv dat canh tool: <BaseDirectory>\paddle-env\Scripts\paddlex.exe
            try
            {
                string venv = Path.Combine(AppContext.BaseDirectory, "paddle-env", "Scripts", "paddlex.exe");
                if (File.Exists(venv)) return venv;
            }
            catch { }

            // 3. thu muc cung mineru.exe (hanh vi cu)
            try
            {
                string legacy = Path.Combine(Path.GetDirectoryName(_mineruPath) ?? "", "paddlex.exe");
                if (File.Exists(legacy)) return legacy;
            }
            catch { }

            return null;
        }

        // =========================================================
        //  Tim paddle_run.py (wrapper chi luu markdown gop). Tim theo thu tu:
        //  canh exe tool -> duyet nguoc tu thu muc exe (cho chay debug)
        // =========================================================
        private string ResolvePaddleWrapper()
        {
            try
            {
                string w = Path.Combine(AppContext.BaseDirectory, "paddle_run.py");
                if (File.Exists(w)) return w;
            }
            catch { }
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 5 && dir != null; i++)
                {
                    string cand = Path.Combine(dir.FullName, "paddle_run.py");
                    if (File.Exists(cand)) return cand;
                    cand = Path.Combine(dir.FullName, "mineru-tool", "paddle_run.py");
                    if (File.Exists(cand)) return cand;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        // =========================================================
        //  Tim mineru-kit.exe cua MinerU 4.x (venv rieng, thu tu uu tien)
        // =========================================================
        private string ResolveMinerU4Kit()
        {
            // 1. File mineru4x-path.txt canh exe tool — dong dau tien la duong dan day du toi mineru-kit.exe
            try
            {
                string txt = Path.Combine(AppContext.BaseDirectory, "mineru4x-path.txt");
                if (File.Exists(txt))
                {
                    string line = (File.ReadAllLines(txt).FirstOrDefault() ?? "").Trim();
                    if (line.Length > 0 && File.Exists(line))
                        return line;
                }
            }
            catch { }

            // 2. venv dat canh tool: <BaseDirectory>\mineru4x-venv\Scripts\mineru-kit.exe
            try
            {
                string venv = Path.Combine(AppContext.BaseDirectory, "mineru4x-venv", "Scripts", "mineru-kit.exe");
                if (File.Exists(venv)) return venv;
            }
            catch { }

            // 3. venv nam tren 1 cap (portable nam trong mineru-tool-portable, venv nam canh project)
            try
            {
                string up = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mineru4x-venv", "Scripts", "mineru-kit.exe"));
                if (File.Exists(up)) return up;
            }
            catch { }

            return null;
        }

        // Thu muc model goc cho MinerU 4.x: lay tu mineru.json (models-dir.vlm -> thu muc cha)
        // de tai su dung model local, tranh tai lai ~2GB
        private string ResolveMinerU4ModelBase()
        {
            try
            {
                string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mineru.json");
                if (!File.Exists(cfg)) return null;
                string json = File.ReadAllText(cfg, Encoding.UTF8);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("models-dir", out var md) &&
                        md.TryGetProperty("vlm", out var vlmEl))
                    {
                        string vlm = vlmEl.GetString();
                        if (!string.IsNullOrWhiteSpace(vlm))
                        {
                            string parent = Path.GetDirectoryName(vlm);
                            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                                return parent;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private bool EnsureMineru(bool interactive)
        {
            if (!string.IsNullOrEmpty(_mineruPath) && File.Exists(_mineruPath))
                return true;

            string found = ResolveMineruPath();
            if (!string.IsNullOrEmpty(found))
            {
                _mineruPath = found;
                return true;
            }

            if (!interactive)
                return false;

            var result = MessageBox.Show(
                "Khong tim thay mineru.exe tren may nay.\n\n" +
                "MinerU2.5 can duoc cai dat (pip install mineru[core]) va nam trong PATH.\n" +
                "Ban co muon chon thu cong file mineru.exe khong?",
                "Thieu mineru.exe", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return false;

            var dlg = new OpenFileDialog
            {
                Title = "Chon mineru.exe",
                Filter = "mineru.exe|mineru.exe|Executable files|*.exe",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
            {
                _mineruPath = dlg.FileName;
                try
                {
                    // Ghi vao mineru-path.txt cung exe (UTF8 khong BOM)
                    string txt = Path.Combine(AppContext.BaseDirectory, "mineru-path.txt");
                    File.WriteAllText(txt, _mineruPath, new UTF8Encoding(false));
                }
                catch (Exception ex) { AppendLog("ERR ghi mineru-path.txt: " + ex.Message); }
                return true;
            }

            return false;
        }

        // =========================================================
        //  Chon thu muc
        // =========================================================
        private void SrcBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { InitialDirectory = srcBox.Text };
            if (dlg.ShowDialog() == true) srcBox.Text = dlg.FolderName;
        }

        private void OutBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { InitialDirectory = outBox.Text };
            if (dlg.ShowDialog() == true) outBox.Text = dlg.FolderName;
        }

        private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncEffortState();
        }

        private bool IsEffortEnabledFor(string tag)
        {
            if (tag == null) return false;
            if (tag == "hybrid-engine" || tag == "hybrid-http-client") return true;
            if (tag.EndsWith("-http-client")) return true;
            return false;
        }

        private void SyncEffortState()
        {
            if (effortBox == null || engineBox == null) return; // guard: event chay trong InitializeComponent
            if (engineBox.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag as string;
                if (engineBadge != null) engineBadge.Text = ShortEngineName(tag);
                effortBox.IsEnabled = IsEffortEnabledFor(tag);
                bool isHttp = tag != null && tag.EndsWith("-http-client");
                if (urlRow != null)
                    urlRow.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
                if (apiModeChk != null && concBox != null)
                {
                    bool apiOn = apiModeChk.IsChecked == true;
                    // Chỉ cho chạy song song khi bật api-mode (local backend) hoặc backend là http-client
                    concBox.IsEnabled = apiOn || isHttp;
                }
                // Reset các khóa theo engine trước khi áp dụng (tránh kẹt disable khi đổi engine)
                if (!_running)
                {
                    if (apiModeChk != null) apiModeChk.IsEnabled = true;
                    if (engineBox != null) engineBox.IsEnabled = true;
                }

                // --- Goi y theo engine (PaddleOCR / http-client / pipeline) ---
                string hint = "";
                if (tag == "paddleocr")
                {
                    hint = L("S_HintPaddle");
                    // PaddleOCR: vo hieu hoa cac tuy chon khong ap dung
                    effortBox.IsEnabled = false;
                    if (apiModeChk != null) apiModeChk.IsEnabled = false;
                    if (concBox != null) concBox.IsEnabled = false;
                    if (urlRow != null) urlRow.Visibility = Visibility.Collapsed;
                }
                else if (tag == "mineru4x")
                {
                    hint = L("S_HintMineru4x");
                    effortBox.IsEnabled = false;
                    if (apiModeChk != null) apiModeChk.IsEnabled = false;
                    if (urlRow != null) urlRow.Visibility = Visibility.Collapsed;
                }
                else if (tag == "windows-ocr")
                {
                    hint = L("S_HintWinOcr");
                    effortBox.IsEnabled = false;
                    if (apiModeChk != null) apiModeChk.IsEnabled = false;
                    if (urlRow != null) urlRow.Visibility = Visibility.Collapsed;
                }
                else if (isHttp)
                {
                    hint = L("S_HintHttp");
                }
                else if (tag == "pipeline")
                {
                    hint = L("S_HintPipeline");
                }
                if (smartRoutingChk != null && smartRoutingChk.IsChecked == true)
                {
                    if (engineBox != null) engineBox.IsEnabled = false;
                    hint = L("S_HintSmart");
                }
                if (engineHint != null)
                {
                    engineHint.Text = hint;
                    engineHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        private static string ShortEngineName(string tag)
        {
            switch (tag)
            {
                case "hybrid-engine": return "hybrid-engine";
                case "vlm-engine": return "vlm-engine";
                case "pipeline": return "pipeline";
                case "vlm-http-client": return "vlm-http-client";
                case "hybrid-http-client": return "hybrid-http-client";
                case "paddleocr": return "PaddleOCR";
                case "mineru4x": return "MinerU 4.x basic";
                case "windows-ocr": return "Windows OCR";
                default: return tag ?? "";
            }
        }

        private void ApiModeChk_Changed(object sender, RoutedEventArgs e)
        {
            SyncEffortState();
        }

        private void SmartRoutingChk_Changed(object sender, RoutedEventArgs e)
        {
            SyncEffortState();
        }

        private void WatchChk_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (watchChk.IsChecked == true)
                    SetupWatcher();
                else
                    TeardownWatcher();
            }
            catch (Exception ex) { AppendLog("ERR watch: " + ex.Message); }
        }

        // =========================================================
        //  C1 watch folder: theo doi thu muc nguon, file moi on dinh -> tu chay batch
        // =========================================================
        private void SetupWatcher()
        {
            TeardownWatcher();
            string dir = "";
            try { dir = srcBox.Text.Trim(); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                AppendLog("[WATCH] thu muc nguon khong ton tai — tat theo doi.");
                try { watchChk.IsChecked = false; } catch { }
                return;
            }
            bool sub = false;
            try { sub = subDirsChk.IsChecked == true; } catch { }
            try
            {
                _watcher = new FileSystemWatcher(dir, "*.pdf");
                _watcher.IncludeSubdirectories = sub;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite;
                _watcher.Created += Watch_OnFile;
                _watcher.Renamed += Watch_OnRenamed;
                _watcher.EnableRaisingEvents = true;
                if (_watchTick == null)
                {
                    _watchTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    _watchTick.Tick += WatchTick_Tick;
                }
                _watchTick.Start();
                AppendLog("[WATCH] dang theo doi: " + dir + (sub ? " (gom thu muc con)" : "") + " — file moi on dinh 10s se tu chay.");
            }
            catch (Exception ex)
            {
                AppendLog("[WATCH] loi bat theo doi: " + ex.Message);
                try { watchChk.IsChecked = false; } catch { }
            }
        }

        private void TeardownWatcher()
        {
            try { if (_watchTick != null) _watchTick.Stop(); } catch { }
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= Watch_OnFile;
                    _watcher.Renamed -= Watch_OnRenamed;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch { }
            try { lock (_watchLock) _watchPending.Clear(); } catch { }
            AppendLog("[WATCH] da tat theo doi.");
        }

        private void Watch_OnFile(object sender, FileSystemEventArgs e)
        {
            try { EnqueueWatch(e.FullPath); } catch { }
        }

        private void Watch_OnRenamed(object sender, RenamedEventArgs e)
        {
            try { EnqueueWatch(e.FullPath); } catch { }
        }

        private void EnqueueWatch(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;
                lock (_watchLock)
                {
                    if (_watchDone.Contains(fullPath)) return;
                    long sz = 0;
                    try { sz = new FileInfo(fullPath).Length; } catch { return; }
                    _watchPending[fullPath] = new WatchEntry { Size = sz, Seen = DateTime.Now };
                }
            }
            catch { }
        }

        private async void WatchTick_Tick(object sender, EventArgs e)
        {
            List<string> keys;
            lock (_watchLock) keys = new List<string>(_watchPending.Keys);
            if (keys.Count == 0) return;
            foreach (var k in keys)
            {
                WatchEntry en;
                lock (_watchLock)
                {
                    if (!_watchPending.TryGetValue(k, out en)) continue;
                    if (_watchDone.Contains(k)) { _watchPending.Remove(k); continue; }
                }
                bool gone = false;
                long sz = -1;
                try
                {
                    if (!File.Exists(k)) gone = true;
                    else sz = new FileInfo(k).Length;
                }
                catch { gone = true; }
                if (gone) { lock (_watchLock) _watchPending.Remove(k); continue; }
                if (sz != en.Size)
                {
                    lock (_watchLock)
                    {
                        if (_watchPending.TryGetValue(k, out WatchEntry cur)) { cur.Size = sz; cur.Seen = DateTime.Now; }
                    }
                    continue;
                }
                if ((DateTime.Now - en.Seen).TotalSeconds < 10) continue;
                lock (_watchLock) _watchPending.Remove(k);
                if (_running || _installing) continue;
                bool wantWatch = false;
                try { wantWatch = watchChk.IsChecked == true; } catch { }
                if (!wantWatch) continue;
                lock (_watchLock) _watchDone.Add(k);
                AppendLog("[WATCH] file moi on dinh: " + Path.GetFileName(k) + " — tu chay batch...");
                await RunBatchAsync();
                return;
            }
        }

        // =========================================================
        //  C4 thu vien ket qua: tim kiem toan van trong .md
        // =========================================================
        private static string CollapseWs(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return "";
                return string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            }
            catch { return s ?? ""; }
        }

        private async void SearchBtn_Click(object sender, RoutedEventArgs e)
        {
            await RunSearchAsync();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SearchBtn_Click(sender, e);
            }
        }

        private async Task RunSearchAsync()
        {
            string q = "";
            try { q = (searchBox.Text ?? "").Trim(); } catch { }
            if (q.Length < 2) { try { resultInfo.Text = L("S_SearchNeed2"); } catch { } return; }
            string dir = "";
            try { dir = (outBox.Text ?? "").Trim(); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { try { resultInfo.Text = L("S_OutDirMissing"); } catch { } return; }
            try { searchBtn.IsEnabled = false; resultInfo.Text = L("S_Searching"); } catch { }
            var res = await Task.Run(() =>
            {
                var list = new List<Tuple<string, string, int, string>>();
                string[] mds;
                try { mds = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories); }
                catch { return list; }
                foreach (var f in mds)
                {
                    string text;
                    try { text = File.ReadAllText(f, Encoding.UTF8); }
                    catch { continue; }
                    int hits = 0, idx = 0, first = -1;
                    while (true)
                    {
                        idx = text.IndexOf(q, idx, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) break;
                        hits++;
                        if (first < 0) first = idx;
                        idx += q.Length;
                        if (hits > 10000) break;
                    }
                    if (hits <= 0) continue;
                    int s = Math.Max(0, first - 60);
                    int len = Math.Min(text.Length - s, 120 + q.Length);
                    string snip = CollapseWs(text.Substring(s, len));
                    string label = "";
                    try { label = Path.GetFileName(Path.GetDirectoryName(f)) + " / " + Path.GetFileName(f); } catch { label = f; }
                    list.Add(Tuple.Create(f, label, hits, snip));
                }
                list.Sort((a, b) => b.Item3.CompareTo(a.Item3));
                if (list.Count > 50) list = list.GetRange(0, 50);
                return list;
            });
            try
            {
                _resultPaths = res.Select(r => r.Item1).ToList();
                resultsBox.ItemsSource = res.Select(r => r.Item3 + "×  " + r.Item2 + "  —  " + r.Item4).ToList();
                resultInfo.Text = res.Count == 0 ? L("S_SearchNone") : (L("S_SearchFoundA") + res.Count + L("S_SearchFoundB"));
            }
            catch { }
            try { searchBtn.IsEnabled = true; } catch { }
        }

        private void OpenResultBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedResult();
        }

        private void ResultsBox_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedResult();
        }

        private void OpenSelectedResult()
        {
            try
            {
                int i = resultsBox.SelectedIndex;
                if (i >= 0 && i < _resultPaths.Count)
                    Process.Start(new ProcessStartInfo { FileName = _resultPaths[i], UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog("ERR mo file: " + ex.Message); }
        }

        // =========================================================
        //  Nut tien ich (mo thu muc KQ / bao cao / xoa log)
        // =========================================================
        private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
        {
            try { logBox.Clear(); } catch { }
        }

        private void OpenOutBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = (outBox != null) ? outBox.Text.Trim() : "";
                if (dir.Length == 0 || !Directory.Exists(dir))
                {
                    AppendLog("Thu muc ket qua chua ton tai: " + dir);
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog("ERR mo thu muc: " + ex.Message); }
        }

        private void OpenReportBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = (outBox != null) ? outBox.Text.Trim() : "";
                string rep = Path.Combine(dir, "batch-report.html");
                if (!File.Exists(rep))
                {
                    AppendLog("Chua co batch-report.html (chay xong batch moi co).");
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = rep, UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog("ERR mo bao cao: " + ex.Message); }
        }

        // =========================================================
        //  P1 Fluent UI: sidebar nav / accent / dock / toast / F1
        // =========================================================
        // Chuyen tab theo ten card (TabControl tu ve, khong can to mau tay)
        private void GotoTab(string name)
        {
            try
            {
                if (mainTabs == null) return;
                int idx = 0;
                if (name == "cardProgress") idx = 1;
                else if (name == "cardLibrary") idx = 2;
                else if (name == "cardSystem") idx = 3;
                else if (name == "cardSettings") idx = 4;
                if (idx >= 0 && idx < mainTabs.Items.Count)
                    mainTabs.SelectedIndex = idx;
            }
            catch { }
        }

        private void AccentBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string tag = (sender as Button)?.Tag as string ?? "";
                var parts = tag.Split(';');
                if (parts.Length == 2) ApplyAccent(parts[0], parts[1], true);
            }
            catch { }
        }

        private void ApplyAccent(string hex1, string hex2, bool save)
        {
            try
            {
                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0)
                };
                Color c1 = (Color)ColorConverter.ConvertFromString(hex1);
                Color c2 = (Color)ColorConverter.ConvertFromString(hex2);
                brush.GradientStops.Add(new GradientStop(c1, 0));
                brush.GradientStops.Add(new GradientStop(c2, 1));
                brush.Freeze();
                Resources["AccentGradient"] = brush;
                // Anh huong ro: nut BAT DAU + vien toast (Theme.Accent)
                try { if (startBtn != null) startBtn.Background = new SolidColorBrush(c1); } catch { }
                try { if (_themeDict != null) _themeDict["Theme.Accent"] = new SolidColorBrush(c1); } catch { }
                _accent = hex1 + ";" + hex2;
                if (save) SaveUiPrefs();
            }
            catch { }
        }

        // =========================================================
        //  Theme Sang/Toi + Ngon ngu VI/EN (luu settings.txt canh exe)
        // =========================================================
        private string _theme = "light";
        private string _lang = "vi";
        private double _fontPct = 100;
        private double _opacityPct = 100;
        private string _srcDir = "";
        private string _outDir = "";
        private bool _topmost = false;
        private bool _sound = true;
        private string _accent = "#3B82F6;#22D3EE";
        private bool _autostart = false;

        private string L(string key)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return key;
                if (app.Dispatcher.CheckAccess())
                    return (app.TryFindResource(key) as string) ?? key;
                return app.Dispatcher.Invoke(() => (app.TryFindResource(key) as string) ?? key);
            }
            catch { return key; }
        }

        private static Brush ThemeBrush(string key, string fallback)
        {
            try
            {
                var b = Application.Current.TryFindResource(key) as Brush;
                if (b != null) return b;
            }
            catch { }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        }

        // Giu reference dict theme/lang ngay tu dau (tim 1 lan duy nhat) — tranh
        // nham dict cua WPF-UI khi swap. Doi theme = copy tung key vao dung dict
        // cu (giu nguyen object) thay vi remove/add, DynamicResource tu cap nhat.
        private ResourceDictionary _themeDict, _langDict;

        private void InitDictRefs()
        {
            try
            {
                foreach (var d in Application.Current.Resources.MergedDictionaries)
                {
                    string s = "";
                    try { s = d.Source != null ? d.Source.OriginalString : ""; } catch { }
                    if (s.IndexOf("wpf", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (s.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                        s.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
                        _themeDict = d;
                    else if (s.EndsWith("Lang/vi.xaml", StringComparison.OrdinalIgnoreCase) ||
                             s.EndsWith("Lang/en.xaml", StringComparison.OrdinalIgnoreCase))
                        _langDict = d;
                }
            }
            catch { }
        }

        private static void CopyInto(ResourceDictionary target, string fileNew)
        {
            try
            {
                var nd = new ResourceDictionary { Source = new Uri(fileNew, UriKind.Relative) };
                if (target == null)
                {
                    Application.Current.Resources.MergedDictionaries.Add(nd);
                    return;
                }
                foreach (var k in nd.Keys.Cast<object>().ToList())
                {
                    try { target[k] = nd[k]; } catch { }
                }
            }
            catch { }
        }

        private string PrefsPath()
        {
            try { return Path.Combine(AppContext.BaseDirectory, "settings.txt"); }
            catch { return "settings.txt"; }
        }

        private void LoadUiPrefs()
        {
            try
            {
                if (!File.Exists(PrefsPath())) return;
                foreach (var line in File.ReadAllLines(PrefsPath()))
                {
                    var t = (line ?? "").Trim();
                    if (t.StartsWith("theme=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(6).Trim().ToLowerInvariant();
                        if (v == "dark" || v == "light") _theme = v;
                    }
                    else if (t.StartsWith("lang=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(5).Trim().ToLowerInvariant();
                        if (v == "en" || v == "vi") _lang = v;
                    }
                    else if (t.StartsWith("fontpct=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(t.Substring(8).Trim(), out double fp) && fp >= 80 && fp <= 150) _fontPct = fp;
                    }
                    else if (t.StartsWith("opacity=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(t.Substring(8).Trim(), out double op) && op >= 40 && op <= 100) _opacityPct = op;
                    }
                    else if (t.StartsWith("topmost=", StringComparison.OrdinalIgnoreCase))
                    {
                        _topmost = t.Substring(8).Trim() == "1";
                    }
                    else if (t.StartsWith("sound=", StringComparison.OrdinalIgnoreCase))
                    {
                        _sound = t.Substring(6).Trim() != "0";
                    }
                    else if (t.StartsWith("autostart=", StringComparison.OrdinalIgnoreCase))
                    {
                        _autostart = t.Substring(10).Trim() == "1";
                    }
                    else if (t.StartsWith("accent=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(7).Trim();
                        var ps = v.Split(';');
                        if (ps.Length == 2 && ps[0].Trim().Length > 0 && ps[1].Trim().Length > 0)
                            _accent = ps[0].Trim() + ";" + ps[1].Trim();
                    }
                    else if (t.StartsWith("srcdir=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(7).Trim();
                        if (v.Length > 0) _srcDir = v;
                    }
                    else if (t.StartsWith("outdir=", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(7).Trim();
                        if (v.Length > 0) _outDir = v;
                    }
                }
                try { if (srcBox != null && srcBox.Text.Trim().Length == 0 && _srcDir.Length > 0 && Directory.Exists(_srcDir)) srcBox.Text = _srcDir; } catch { }
                try { if (outBox != null && outBox.Text.Trim().Length == 0 && _outDir.Length > 0) outBox.Text = _outDir; } catch { }
            }
            catch { }
        }

        private void SaveUiPrefs()
        {
            try
            {
                try { if (soundChk != null) _sound = soundChk.IsChecked != false; } catch { }
                try { if (srcBox != null && srcBox.Text.Trim().Length > 0) _srcDir = srcBox.Text.Trim(); } catch { }
                try { if (outBox != null && outBox.Text.Trim().Length > 0) _outDir = outBox.Text.Trim(); } catch { }
                File.WriteAllLines(PrefsPath(), new[]
                {
                    "theme=" + _theme, "lang=" + _lang,
                    "fontpct=" + _fontPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "opacity=" + _opacityPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "topmost=" + (_topmost ? "1" : "0"),
                    "sound=" + (_sound ? "1" : "0"),
                    "autostart=" + (_autostart ? "1" : "0"),
                    "accent=" + _accent,
                    "srcdir=" + _srcDir,
                    "outdir=" + _outDir,
                }, new UTF8Encoding(false));
            }
            catch { }
        }

        private void ApplyTheme(string t, bool save)
        {
            try
            {
                _theme = (t == "dark") ? "dark" : "light";
                CopyInto(_themeDict, "Themes/" + (_theme == "dark" ? "Dark.xaml" : "Light.xaml"));
                try { RememberBaseColors(); } catch { }
                try { ApplyWindowTransparency(); } catch { }
                if (themeBtn != null)
                {
                    themeBtn.Content = _theme == "dark" ? "🌙 " + L("S_ThemeNameDark") : "☀ " + L("S_ThemeNameLight");
                    try { themeBtn.ToolTip = L("S_ThemeTip"); } catch { }
                }
                try
                {
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                        _theme == "dark" ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                                         : Wpf.Ui.Appearance.ApplicationTheme.Light);
                }
                catch { }
                try { RefreshPillText(); } catch { }
                if (save) SaveUiPrefs();
            }
            catch { }
        }

        private void ApplyLang(string l, bool save)
        {
            try
            {
                _lang = (l == "en") ? "en" : "vi";
                CopyInto(_langDict, "Lang/" + (_lang == "en" ? "en.xaml" : "vi.xaml"));
                if (langBtn != null)
                {
                    langBtn.Content = _lang == "en" ? "🌐 EN" : "🌐 VI";
                    try { langBtn.ToolTip = L("S_LangTip"); } catch { }
                }
                try
                {
                    if (themeBtn != null)
                        themeBtn.Content = _theme == "dark" ? "🌙 " + L("S_ThemeNameDark") : "☀ " + L("S_ThemeNameLight");
                }
                catch { }
                try { RefreshPillText(); } catch { }
                try { SyncEffortState(); } catch { }
                if (save) SaveUiPrefs();
            }
            catch { }
        }

        private void ThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyTheme(_theme == "dark" ? "light" : "dark", true);
                try { RefreshPillText(); } catch { }
            }
            catch { }
        }

        private void LangBtn_Click(object sender, RoutedEventArgs e)
        {
            try { ApplyLang(_lang == "en" ? "vi" : "en", true); }
            catch { }
        }

        // =========================================================
        //  Tab Cai dat: co chu, trong suot, topmost, autostart
        // =========================================================
        private void ApplyDisplayPrefs()
        {
            try
            {
                try { this.FontSize = 13 * _fontPct / 100.0; } catch { }
                ApplyWindowTransparency();
                try { this.Topmost = _topmost; } catch { }
                try { if (fontSlider != null) fontSlider.Value = _fontPct; } catch { }
                try { if (fontVal != null) fontVal.Text = ((int)_fontPct) + "%"; } catch { }
                try { if (opacitySlider != null) opacitySlider.Value = _opacityPct; } catch { }
                try { if (opacityVal != null) opacityVal.Text = ((int)_opacityPct) + "%"; } catch { }
                try { if (topmostChk != null) topmostChk.IsChecked = _topmost; } catch { }
                try { if (soundChk != null) soundChk.IsChecked = _sound; } catch { }
                try { if (autostartChk != null) autostartChk.IsChecked = _autostart; } catch { }
                try { ApplyAutostart(_autostart); } catch { }
                try
                {
                    var ps = (_accent ?? "").Split(';');
                    if (ps.Length == 2) ApplyAccent(ps[0], ps[1], false);
                }
                catch { }
            }
            catch { }
        }

        private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (fontSlider == null) return; // guard: event chay trong InitializeComponent
                double v = Math.Round(fontSlider.Value);
                if (v < 80) v = 80; if (v > 150) v = 150;
                _fontPct = v;
                try { this.FontSize = 13 * v / 100.0; } catch { }
                try { if (fontVal != null) fontVal.Text = ((int)v) + "%"; } catch { }
                SaveUiPrefs();
            }
            catch { }
        }

        private static readonly string[] _alphaKeys = new[]
        {
            "Theme.AppBg", "Theme.CardBg", "Theme.BtnBg", "Theme.TrackBg",
            "Theme.SelBg", "Theme.HoverBg",
            "Theme.PillReadyBg", "Theme.PillRunBg", "Theme.PillStopBg", "Theme.PillDoneBg"
        };
        private readonly Dictionary<string, Color> _baseColors = new Dictionary<string, Color>();

        private void RememberBaseColors()
        {
            try
            {
                if (_themeDict == null) return;
                foreach (var k in _alphaKeys)
                {
                    try
                    {
                        if (_themeDict.Contains(k) && _themeDict[k] is SolidColorBrush b)
                            _baseColors[k] = Color.FromArgb(255, b.Color.R, b.Color.G, b.Color.B);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Xuyen nen that (Window AllowsTransparency): chu giu duc,
        // cac brush nen pha alpha theo slider — DynamicResource tu cap nhat.
        private void ApplyWindowTransparency()
        {
            try
            {
                try { this.Opacity = 1.0; } catch { }
                if (_themeDict == null) return;
                byte a = (byte)Math.Max(0, Math.Min(255, (int)(255 * _opacityPct / 100.0)));
                foreach (var k in _alphaKeys)
                {
                    try
                    {
                        if (!_baseColors.TryGetValue(k, out Color c)) continue;
                        _themeDict[k] = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
                    }
                    catch { }
                }
                // Xoa nen cuc bo cu (magenta test truoc) de an theo DynamicResource
                try { if (contentGrid != null) contentGrid.ClearValue(BackgroundProperty); } catch { }
                // Khong de khe trong suot khi dang dac (click se xuyen xuong app khac)
                try
                {
                    if (_opacityPct >= 100 && _baseColors.TryGetValue("Theme.AppBg", out Color bg0))
                        this.Background = new SolidColorBrush(Color.FromArgb(255, bg0.R, bg0.G, bg0.B));
                    else
                        this.Background = Brushes.Transparent;
                }
                catch { }
            }
            catch { }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (opacitySlider == null) return; // guard: event chay trong InitializeComponent
                double v = Math.Round(opacitySlider.Value);
                if (v < 40) v = 40; if (v > 100) v = 100;
                _opacityPct = v;
                ApplyWindowTransparency();
                try { if (opacityVal != null) opacityVal.Text = ((int)v) + "%"; } catch { }
                SaveUiPrefs();
            }
            catch { }
        }

        private void TopmostChk_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (topmostChk == null) return;
                _topmost = topmostChk.IsChecked == true;
                try { this.Topmost = _topmost; } catch { }
                SaveUiPrefs();
            }
            catch { }
        }

        private void AutostartChk_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (autostartChk == null) return;
                _autostart = autostartChk.IsChecked == true;
                try { ApplyAutostart(_autostart); } catch { }
                SaveUiPrefs();
            }
            catch { }
        }

        private static void ApplyAutostart(bool on)
        {
            try
            {
                using (var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk == null) return;
                    if (on)
                    {
                        string exe = Process.GetCurrentProcess().MainModule.FileName;
                        rk.SetValue("MinerU25Tool", "\"" + exe + "\"");
                    }
                    else
                    {
                        try { rk.DeleteValue("MinerU25Tool", false); } catch { }
                    }
                }
            }
            catch { }
        }

        private void PauseBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_running) return;
                _paused = !_paused;
                if (pauseBtn != null) pauseBtn.Content = L(_paused ? "S_Continue" : "S_Pause");
                SetPill(_paused ? "paused" : "running");
                AppendLog(_paused ? "[PAUSE] tam dung — khong nhan file moi, file dang chay van xong." : "[PAUSE] tiep tuc.");
            }
            catch { }
        }


        // Titlebar tu ve (Window thuong, khong FluentWindow)
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount < 2) this.DragMove(); } catch { }
        }

        private void MinBtn_Click(object sender, RoutedEventArgs e)
        {
            try { this.WindowState = WindowState.Minimized; } catch { }
        }

        private void MaxBtn_Click(object sender, RoutedEventArgs e)
        {
            try { this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            try { this.Close(); } catch { }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            }
            catch { }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (_running) return;
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                var paths = (e.Data.GetData(DataFormats.FileDrop) as string[]) ?? new string[0];
                string target = null;
                int n = 0;
                foreach (var p in paths)
                {
                    try
                    {
                        if (Directory.Exists(p))
                        {
                            if (target == null) target = p;
                            try { foreach (var f in Directory.EnumerateFiles(p, "*.pdf", SearchOption.AllDirectories)) n++; } catch { }
                        }
                        else if (File.Exists(p) && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            n++;
                            if (target == null) { try { target = Path.GetDirectoryName(p); } catch { } }
                        }
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(target) && srcBox != null)
                {
                    srcBox.Text = target;
                    string msg = string.Format(L("S_DropFiles"), n);
                    AppendLog("[DROP] " + msg + " Tu: " + target);
                    try { ShowToast(msg); } catch { }
                }
            }
            catch { }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.F1)
                {
                    e.Handled = true;
                    var w = new HelpWindow { Owner = this };
                    w.ShowDialog();
                }
            }
            catch { }
        }

        private void ShowToast(string text)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        toastText.Text = text ?? "";
                        toastBorder.Visibility = Visibility.Visible;
                        if (_toastTimer == null)
                        {
                            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                            _toastTimer.Tick += (s, e) =>
                            {
                                try { toastBorder.Visibility = Visibility.Collapsed; } catch { }
                                try { _toastTimer.Stop(); } catch { }
                            };
                        }
                        _toastTimer.Stop();
                        _toastTimer.Start();
                    }
                    catch { }
                });
            }
            catch { }
        }

        // =========================================================
        //  P3 tray (WinForms NotifyIcon) + balloon
        // =========================================================
        private void SetupTray()
        {
            try
            {
                if (_tray != null) return;
                _tray = new WinForms.NotifyIcon();
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                        _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                }
                catch { }
                _tray.Text = "Batch OCR";
                _tray.Visible = true;
                _tray.DoubleClick += (s, e) =>
                {
                    try
                    {
                        if (Visibility == Visibility.Visible) Hide();
                        else { Show(); WindowState = WindowState.Normal; Activate(); }
                    }
                    catch { }
                };
                var menu = new WinForms.ContextMenuStrip();
                var miShow = new WinForms.ToolStripMenuItem("Hiện/Ẩn cửa sổ", null, (s, e) =>
                {
                    try
                    {
                        if (Visibility == Visibility.Visible) Hide();
                        else { Show(); WindowState = WindowState.Normal; Activate(); }
                    }
                    catch { }
                });
                var miExit = new WinForms.ToolStripMenuItem("Thoát", null, (s, e) => { try { Close(); } catch { } });
                menu.Items.Add(miShow);
                menu.Items.Add(miExit);
                _tray.ContextMenuStrip = menu;
            }
            catch { }
        }

        private void DisposeTray()
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
        }

        private void ShowTrayBalloon(string title, string text)
        {
            try { _tray?.ShowBalloonTip(5000, title ?? "Batch OCR", text ?? "", WinForms.ToolTipIcon.Info); } catch { }
        }

        // =========================================================
        //  BAT DAU
        // =========================================================
        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _installing) return;
            string tag0 = (engineBox.SelectedItem as ComboBoxItem)?.Tag as string;
            // PaddleOCR / MinerU 4.x / Windows OCR co exe rieng hoac chay in-process
            // (kiem tra chi tiet trong RunBatchAsync) -> khong bat buoc mineru.exe
            // Smart routing tu resolve engine trong batch -> cung khong bat buoc
            if (tag0 != "paddleocr" && tag0 != "mineru4x" && tag0 != "windows-ocr" && !(smartRoutingChk.IsChecked == true))
            {
                if (!EnsureMineru(true)) return;
            }
            await RunBatchAsync();
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_running) return;
            _cancelRequested = true;
            EmergencyKillAll();
            AppendLog("Da dung theo yeu cau...");
            // Không gọi SetRunning(false) ở đây — vòng lặp batch sẽ tự dọn dẹp và gọi SetRunning(false).
        }

        // Huỷ token chung và kill tất cả process đang chạy (mineru + mineru-api)
        private void EmergencyKillAll()
        {
            try { _batchCts?.Cancel(); } catch { }
            foreach (var kv in _runningProcs.ToArray())
            {
                try { KillProcessTree(kv.Value); } catch { }
            }
            try { if (_apiProc != null) KillProcessTree(_apiProc); } catch { }
        }

        // =========================================================
        //  Vong lap batch
        // =========================================================
        private async Task RunBatchAsync()
        {
            string src = srcBox.Text.Trim();
            string outdir = outBox.Text.Trim();

            if (!Directory.Exists(src))
            {
                MessageBox.Show(L("M_SrcMissing") + src, L("M_TitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!Directory.Exists(outdir))
            {
                try { Directory.CreateDirectory(outdir); }
                catch (Exception ex)
                {
                    MessageBox.Show(L("M_OutCreateFail") + ex.Message, L("M_TitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // Chan src trung outdir (C3): tranh output lan vao nguon
            try
            {
                string sFull = Path.GetFullPath(src).TrimEnd('\\', '/');
                string oFull = Path.GetFullPath(outdir).TrimEnd('\\', '/');
                if (string.Equals(sFull, oFull, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("LOI: thu muc nguon trung thu muc ket qua — hay chon 2 thu muc khac nhau.");
                    MessageBox.Show(L("M_SrcEqOut"), L("M_TitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            catch { }

            string backend = (engineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "hybrid-engine";
            bool isHybrid = backend == "hybrid-engine";
            string effort = (effortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "high";
            bool resume = resumeChk.IsChecked == true;

            // --- PaddleOCR: engine thu 2 chay tuyen tinh qua paddlex.exe ---
            string exePath = _mineruPath;
            if (backend == "paddleocr")
            {
                string paddleExe = ResolvePaddleExe();
                if (paddleExe == null)
                {
                    AppendLog("LOI: khong tim thay paddlex.exe. Da tim tai: (1) paddle-path.txt canh tool, (2) paddle-env\\Scripts\\paddlex.exe canh tool, (3) thu muc cung mineru.exe. Hay cai paddleocr vao venv 'paddle-env' dat canh tool, hoac tao paddle-path.txt.");
                    MessageBox.Show(L("M_MissingPaddle"), L("M_MissingPaddleTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                exePath = paddleExe;
                AppendLog("[PADDLE] dung paddlex: " + paddleExe);
                isHybrid = false; // PaddleOCR khong co effort/retry-high/API
            }
            bool smallFirst = smallFirstChk.IsChecked == true;
            bool subDirs = subDirsChk.IsChecked == true;
            bool retry = retryChk.IsChecked == true;
            _retry = retry;
            bool apiModeOn = apiModeChk.IsChecked == true;
            bool isHttpClient = backend.EndsWith("-http-client");
            // --- MinerU 4.x: chay qua mineru-kit.exe trong venv rieng (tier basic ~ medium) ---
            if (backend == "mineru4x")
            {
                string kitExe = ResolveMinerU4Kit();
                if (kitExe == null)
                {
                    AppendLog("LOI: khong tim thay mineru-kit.exe (MinerU 4.x). Da tim tai: (1) mineru4x-path.txt canh tool, (2) mineru4x-venv\\Scripts\\mineru-kit.exe canh tool, (3) ..\\mineru4x-venv\\Scripts. Hay tao venv mineru4x-venv hoac file mineru4x-path.txt.");
                    MessageBox.Show(L("M_MissingKit"), L("M_MissingKitTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                exePath = kitExe;
                _mineru4xKit = kitExe;
                AppendLog("[MINERU4] dung mineru-kit: " + kitExe);
                isHybrid = false; // 4.x khong co effort/retry-high
                apiModeOn = true; // bat buoc server (local parse nap lai model moi file, rat cham)
                AppendLog("[MINERU4] bat buoc API mode (tier basic ~ medium).");
            }
            // --- Windows OCR: chay in-process qua WinRT, khong can exe/server ---
            if (backend == "windows-ocr")
            {
                AppendLog("[WINOCR] dung Windows OCR API (WinRT) — mien phi, CPU, khong can cai dat.");
                isHybrid = false; // khong co effort/retry-high/API
            }
            _curBackend = backend;
            _forceFileMode = false; // tinh lai sau smart routing
            int conc = int.TryParse((concBox.SelectedItem as ComboBoxItem)?.Tag as string, out int c) ? c : 2;
            if (conc < 1) conc = 1;

            // Log ra file trong thu muc ket qua (unattended + chan doan)
            _logFilePath = Path.Combine(outdir, "batch-log.txt");
            try
            {
                // Xoay log khi file qua lon (B4): giu ban cu thanh batch-log-prev.txt
                try
                {
                    var lfi = new FileInfo(_logFilePath);
                    if (lfi.Exists && lfi.Length > 20L * 1024 * 1024)
                    {
                        string prev = Path.Combine(outdir, "batch-log-prev.txt");
                        try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                        File.Move(_logFilePath, prev);
                    }
                }
                catch { }
                File.AppendAllText(_logFilePath,
                    "=== SESSION " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }

            // Cache cac tuy chon (tranh doc control tu thread khac)
            _detailLog = detailLogChk.IsChecked == true;
            _cpuThreads = (cpuBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "2";
            _serverUrl = urlBox.Text.Trim();
            _apiKey = apiKeyBox.Text.Trim();
            _timeoutMinutes = int.TryParse((timeoutBox.SelectedItem as ComboBoxItem)?.Tag as string, out int tm) ? tm : 30;
            _retryHigh = retryHighChk.IsChecked == true;
            bool crossCheck = crossCheckChk.IsChecked == true;
            _joinLines = joinLinesChk.IsChecked == true;
            _squeezeBlanks = squeezeBlanksChk.IsChecked == true;
            // Khoang trang (1-based, 0 = khong gioi han)
            _pageFrom = 0; _pageTo = 0;
            try
            {
                if (pageFromBox != null && int.TryParse(pageFromBox.Text.Trim(), out int pf) && pf >= 1) _pageFrom = pf;
                if (pageToBox != null && int.TryParse(pageToBox.Text.Trim(), out int pt) && pt >= 1) _pageTo = pt;
                if (_pageFrom > 0 && _pageTo > 0 && _pageTo < _pageFrom) { int t = _pageFrom; _pageFrom = _pageTo; _pageTo = t; }
                if (_pageFrom > 0 || _pageTo > 0)
                    AppendLog("[TRANG] chi quet" + (_pageFrom > 0 ? " tu trang " + _pageFrom : "") + (_pageTo > 0 ? " den trang " + _pageTo : "") + " (MinerU 3.x).");
            }
            catch { _pageFrom = 0; _pageTo = 0; }

            // Liet ke file PDF
            List<FileInfo> files = new List<FileInfo>();
            try
            {
                var searchOpt = subDirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(src, "*.pdf", searchOpt))
                    files.Add(new FileInfo(f));
            }
            catch (Exception ex) { AppendLog("ERR liet ke: " + ex.Message); return; }

            if (files.Count == 0)
            {
                AppendLog("Khong tim thay file PDF nao trong: " + src);
                MessageBox.Show(L("M_NoPdf"), L("M_TitleNotice"));
                return;
            }

            if (smallFirst)
                files.Sort((a, b) => a.Length.CompareTo(b.Length));
            else
                files.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));

            // A2 smart routing: probe text/scan roi dinh backend tung file
            bool smartOn = smartRoutingChk.IsChecked == true;
            var backendMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string fastBe = null, ocrBe = null;
            string paddleExe0 = null, kitExe0 = null;
            bool mineruAvail = false;
            if (smartOn)
            {
                try { if (string.IsNullOrEmpty(_mineruPath) || !File.Exists(_mineruPath)) { var f0 = ResolveMineruPath(); if (!string.IsNullOrEmpty(f0)) _mineruPath = f0; } } catch { }
                mineruAvail = !string.IsNullOrEmpty(_mineruPath) && File.Exists(_mineruPath);
                try { paddleExe0 = ResolvePaddleExe(); } catch { }
                try { kitExe0 = ResolveMinerU4Kit(); if (!string.IsNullOrEmpty(kitExe0)) _mineru4xKit = kitExe0; } catch { }
                fastBe = !string.IsNullOrEmpty(kitExe0) ? "mineru4x" : (mineruAvail ? "hybrid-engine" : null);
                ocrBe = !string.IsNullOrEmpty(paddleExe0) ? "paddleocr" : (mineruAvail ? "pipeline" : null);
                if (fastBe == null || ocrBe == null)
                {
                    AppendLog("[SMART] thieu engine cho routing (fast=" + (fastBe ?? "?") + ", ocr=" + (ocrBe ?? "?") + ") — tat smart routing, dung engine da chon.");
                    smartOn = false;
                }
                else
                {
                    string probePy = null, probeScript = null;
                    try
                    {
                        probeScript = EnsureProbeScript();
                        foreach (var cand in new[] { PythonForExe(_mineruPath), PythonForExe(paddleExe0), PythonForExe(kitExe0) })
                        {
                            if (string.IsNullOrEmpty(cand) || probeScript == null) continue;
                            try { if (await HasModuleAsync(cand, "pypdf", 30)) { probePy = cand; break; } } catch { }
                        }
                    }
                    catch { }
                    if (probePy == null || probeScript == null)
                    {
                        AppendLog("[SMART] khong co python+pypdf de probe — tat smart routing, dung engine da chon.");
                        smartOn = false;
                    }
                    else
                    {
                        int nFast = 0, nOcr = 0;
                        for (int i = 0; i < files.Count; i++)
                        {
                            int avg = await ProbePdfTextAsync(probePy, probeScript, files[i].FullName, 30);
                            string rb = (avg >= SmartTextThreshold) ? fastBe : ocrBe;
                            backendMap[files[i].FullName] = rb;
                            if (rb == fastBe) nFast++; else nOcr++;
                            if ((i + 1) % 25 == 0 || i == files.Count - 1)
                                AppendLog("[SMART] probe " + (i + 1) + "/" + files.Count + " (text:" + nFast + ", scan:" + nOcr + ")...");
                        }
                        bool has4 = fastBe == "mineru4x" || ocrBe == "mineru4x";
                        bool has3 = fastBe == "hybrid-engine" || fastBe == "pipeline" || ocrBe == "hybrid-engine" || ocrBe == "pipeline";
                        if (has4 && has3)
                        {
                            string alt = mineruAvail ? "hybrid-engine" : (!string.IsNullOrEmpty(paddleExe0) ? "paddleocr" : null);
                            if (alt != null && alt != "mineru4x")
                            {
                                int moved = 0;
                                foreach (var k in backendMap.Keys.ToList())
                                    if (backendMap[k] == "mineru4x") { backendMap[k] = alt; moved++; }
                                if (fastBe == "mineru4x") fastBe = alt; else ocrBe = alt;
                                AppendLog("[SMART] tranh xung dot VRAM (2 server): chuyen " + moved + " file mineru4x -> " + alt + ".");
                            }
                        }
                        AppendLog("[SMART] dinh tuyen xong: pool fast=" + ShortEngineName(fastBe) + ", ocr=" + ShortEngineName(ocrBe) + ".");
                    }
                }
            }
            _crossSkipLogged = false;
            _forceFileMode = smartOn || (backend == "mineru4x" || backend == "paddleocr");

            // Dinh san thu muc output cho tung file: <stem>__<code>, them hash khi trung ten (A1/B3)
            // (on dinh giua cac lan chay vi phu thuoc full path, khong phu thuoc thu tu)
            var outMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string bb0 = backend;
                    if (smartOn && backendMap.TryGetValue(f.FullName, out string rb0) && !string.IsNullOrEmpty(rb0)) bb0 = rb0;
                    string code = EngineCode(bb0);
                    string stem0 = Path.GetFileNameWithoutExtension(f.Name);
                    string name = TruncateStemUtf8(stem0, MaxStemBytes) + "__" + code;
                    if (used.Contains(name))
                    {
                        string h = Hash6(f.FullName);
                        name = TruncateStemUtf8(stem0, MaxStemBytes) + "__" + code + "_" + h;
                        int k = 2;
                        while (used.Contains(name)) { name = TruncateStemUtf8(stem0, MaxStemBytes) + "__" + code + "_" + h + k; k++; }
                    }
                    used.Add(name);
                    outMap[f.FullName] = name;
                }
            }
            catch (Exception ex) { AppendLog("ERR (outmap): " + ex.Message); }

            // Tai page-counts (neu co)
            LoadPageCounts(src);
            _totalPages = 0;
            foreach (var f in files)
            {
                int p = PagesFor(f, src);
                if (p > 0) _totalPages += p;
            }
            _pagesDone = 0;

            // Reset cac danh sach thread-safe
            lock (_suspLock) _suspicious.Clear();
            lock (_reportLock) _reportRecords.Clear();
            lock (_errLock) _errList.Clear();
            _runningProcs.Clear();

            // Bat dau
            _running = true;
            _cancelRequested = false;
            _batchCts = new CancellationTokenSource();
            _lastProgress = "";
            _curFileName = "";
            _curFileFull = "";
            _curPageInFile = 0;
            _curFilePages = 0;
            _curFilePagesFromMineru = false;
            _sw = Stopwatch.StartNew();
            _startedTime = DateTime.Now;
            SetRunning(true);
            _apiRestarts = 0; _batchAbort = false; _consecFails = 0; _apiFilesSinceStart = 0; _activeFiles = 0; _forceApiRestart = false; _apiServerBackend = null; _noProgressParse = false;
            lock (_sampleLock) _pageSamples.Clear();

            bool noSleep = noSleepChk.IsChecked == true;
            if (noSleep) SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

            bool apiReady = false;
            string apiUrlForFiles = null;
            try
            {
                // --- B2: Khoi dong server thuong tru (smart routing toi da 1 loai server) ---
                if (!smartOn && apiModeOn && !isHttpClient && backend == "vlm-engine")
                {
                    AppendLog("[API] luu y: --api-url khong ho tro vlm-engine — chay tuan tu khong API");
                }
                string serverBackend = null;
                if (smartOn)
                {
                    if (backendMap.Values.Any(v => v == "mineru4x")) serverBackend = "mineru4x";
                    else if (apiModeOn && backendMap.Values.Any(v => v == "hybrid-engine" || v == "pipeline")) serverBackend = "hybrid-engine";
                }
                else if (!isHttpClient)
                {
                    if (backend == "mineru4x") serverBackend = "mineru4x";
                    else if (apiModeOn && (backend == "hybrid-engine" || backend == "pipeline")) serverBackend = backend;
                    else if (backend == "paddleocr" && crossCheck)
                    {
                        string kx = null;
                        try { kx = ResolveMinerU4Kit(); } catch { }
                        if (!string.IsNullOrEmpty(kx)) { _mineru4xKit = kx; serverBackend = "mineru4x"; AppendLog("[CHEO] khoi dong server 4.x de cheo kiem..."); }
                    }
                }
                if (serverBackend != null)
                {
                    AppendLog("[API] thu khoi dong server thuong tru (" + serverBackend + ")...");
                    apiReady = await StartMineruApiAsync(serverBackend, conc, _batchCts.Token);
                    if (apiReady)
                    {
                        apiUrlForFiles = _apiUrl;
                        _apiServerBackend = serverBackend;
                    }
                    else
                    {
                        AppendLog("[API] khong khoi dong duoc server — chay tuan tu nhu cu");
                        if (serverBackend == "mineru4x")
                            AppendLog("[MINERU4] CANH BAO: chay local se nap lai model MOI FILE (~7x cham). Hay kiem tra venv/server, hoac doi engine 3.x.");
                    }
                }

                bool useParallel = (apiReady || isHttpClient) && backend != "paddleocr" && !smartOn;
                if (smartOn && apiReady) AppendLog("[SMART] chay tuan tu de tranh tran VRAM (4GB) khi tron engine.");

                var ctx = new BatchCtx
                {
                    outdir = outdir,
                    backend = backend,
                    isHybrid = isHybrid,
                    effort = effort,
                    url = _serverUrl,
                    src = src,
                    resume = resume,
                    outMap = outMap,
                    backendMap = smartOn ? backendMap : null,
                    fastBe = fastBe,
                    ocrBe = ocrBe,
                    paddleExe = paddleExe0 ?? (backend == "paddleocr" ? exePath : null),
                    kitExe = kitExe0 ?? _mineru4xKit,
                    crossCheck = crossCheck,
                    apiUrl = apiUrlForFiles,
                    conc = conc,
                    exePath = exePath
                };

                int total = files.Count;
                _stTotal = total; _stDone = 0; _stErrors = 0; _stSkipped = 0; _stLabel = "DANG CHAY";
                RefreshStatus();

                AppendLog("=== BAT DAU " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                AppendLog("Tong so file: " + total + " | backend: " + backend + (isHybrid ? " | effort: " + effort : ""));
                AppendLog("Luồng CPU cho OCR: " + (_cpuThreads == "0" ? "tự động" : _cpuThreads));
                AppendLog("[MODE] " + (useParallel ? ("chay song song toi da " + conc + " file" + (apiReady ? (backend == "mineru4x" ? " (qua mineru-kit api-server tier basic)" : " (qua mineru-api thuong tru)") : " (http-client)")) : "chay tuan tu"));
                if (_totalPages > 0) AppendLog("Tong so trang (uoc tinh): " + _totalPages);

                if (useParallel)
                {
                    using var sem = new SemaphoreSlim(conc, conc);
                    var tasks = new List<Task<FileResult>>(total);
                    for (int i = 0; i < total; i++)
                    {
                        while (_paused && !_cancelRequested && !_batchAbort) await Task.Delay(500);
                        if (_cancelRequested || _batchAbort) break;
                        int idx = i;
                        bool got = false;
                        try { await sem.WaitAsync(_batchCts.Token); got = true; }
                        catch (OperationCanceledException) { break; }
                        if (!got) break;
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                if (_cancelRequested)
                                    return new FileResult { file = files[idx].Name, status = "SKIP", note = "bi dung", seconds = 0 };
                                return await RunFileAsync(idx, files[idx], ctx, _batchCts.Token);
                            }
                            finally
                            {
                                sem.Release();
                            }
                        }));
                    }
                    try { await Task.WhenAll(tasks); }
                    catch (Exception ex) { AppendLog("ERR song song: " + ex.Message); }
                }
                else
                {
                    for (int i = 0; i < total; i++)
                    {
                        while (_paused && !_cancelRequested && !_batchAbort) await Task.Delay(500);
                        if (_cancelRequested || _batchAbort) break;
                        await RunFileAsync(i, files[i], ctx, _batchCts.Token);
                        if (_cancelRequested)
                        {
                            AppendLog("Da dung giua chung trinh.");
                            break;
                        }
                    }
                }

                _sw.Stop();
                double batchSec = _sw.Elapsed.TotalSeconds;
                _running = false;
                SetRunning(false);

                // --- C2: Bao cao HTML ---
                WriteReport(outdir, _cancelRequested, batchSec);

                int doneCount = _stDone, errCount = _stErrors, skipCount = _stSkipped;
                string finalMsg = string.Format(L("F_DoneLine"), doneCount, errCount, skipCount);
                if (_cancelRequested) finalMsg = L("F_Stopped") + finalMsg;
                else if (_batchAbort) finalMsg = L("F_Aborted") + finalMsg;

                AppendLog("=== " + finalMsg + " ===");
                _stLabel = finalMsg;
                RefreshStatus();

                // Tat may sau khi quet xong (chi khi hoan tat tu nhien, khong phai do bam DUNG)
                bool shutdown = shutdownChk.IsChecked == true && !_cancelRequested;
                if (shutdown)
                {
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 120 /c \"MinerU25Tool: OCR hoan tat - go shutdown /a de huy\"") { UseShellExecute = false, CreateNoWindow = true });
                    AppendLog("Da len lich tat may sau 2 phut (huy: shutdown /a)");
                }
                else
                {
                    if (!_closing)
                        MessageBox.Show(finalMsg, L("M_TitleResult"));
                }
                try { ShowTrayBalloon("Batch OCR", finalMsg); } catch { }
                try { ShowToast(finalMsg); } catch { }
                try { if (soundChk != null && soundChk.IsChecked == true) System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
            catch (Exception ex)
            {
                AppendLog("ERR batch: " + ex.Message);
                try { _sw?.Stop(); } catch { }
                _running = false;
                SetRunning(false);
                try { WriteReport(outdir, _cancelRequested, _sw != null ? _sw.Elapsed.TotalSeconds : 0); } catch { }
            }
            finally
            {
                // --- Dung mineru-api (neu da khoi dong) ---
                if (_apiProc != null)
                {
                    try { KillProcessTree(_apiProc); } catch { }
                    AppendLog("[API] da dung mineru-api");
                    _apiProc = null; _apiUrl = null; _apiPort = 0; _apiServerBackend = null;
                }
                _runningProcs.Clear();
                try { _batchCts?.Cancel(); } catch { }
                try { _batchCts?.Dispose(); } catch { }
                _batchCts = null;
                if (noSleep) SetThreadExecutionState(ES_CONTINUOUS);
            }
        }

        // =========================================================
        //  Chay mot tien trinh mineru
        // =========================================================
        private async Task<(int exit, string errTail)> RunOneAsync(string file, string args, string exePath, CancellationToken ct, int procKey, int timeoutMin)
        {
            Process proc = null;
            try
            {
                proc = new Process();
                proc.StartInfo.FileName = exePath;
                proc.StartInfo.Arguments = args;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
                proc.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
                var errCap = new StringBuilder();
                Func<string> snapTail = () => { try { lock (errCap) return errCap.ToString(); } catch { return ""; } };

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        CheckProgressLine(e.Data);
                        if (_detailLog || !NoiseRx.IsMatch(e.Data) || IsKeepKeyword(e.Data))
                            AppendLog(e.Data);
                    }
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        CheckProgressLine(e.Data);
                        lock (errCap) { errCap.AppendLine(e.Data); if (errCap.Length > 8000) errCap.Remove(0, errCap.Length - 8000); }
                        if (_detailLog || !NoiseRx.IsMatch(e.Data) || IsKeepKeyword(e.Data))
                            AppendLog(IsRealError(e.Data) ? ("ERR: " + e.Data) : e.Data);
                    }
                };

                if (_cpuThreads != "0")
                {
                    foreach (var k in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS" })
                        proc.StartInfo.EnvironmentVariables[k] = _cpuThreads;
                    proc.StartInfo.EnvironmentVariables["MINERU_INTRA_OP_NUM_THREADS"] = _cpuThreads;
                    proc.StartInfo.EnvironmentVariables["MINERU_INTER_OP_NUM_THREADS"] = _cpuThreads;
                    proc.StartInfo.EnvironmentVariables["MINERU_PDF_RENDER_THREADS"] = _cpuThreads;
                }
                proc.StartInfo.EnvironmentVariables["TOKENIZERS_PARALLELISM"] = "false";
                proc.StartInfo.EnvironmentVariables["MINERU_VIRTUAL_VRAM_SIZE"] = "3";
                proc.StartInfo.EnvironmentVariables["MINERU_PROCESSING_WINDOW_SIZE"] = "8";
                if (!string.IsNullOrWhiteSpace(_apiKey))
                    proc.StartInfo.EnvironmentVariables["MINERU_VL_API_KEY"] = _apiKey;

                proc.Start();
                _runningProcs[procKey] = proc;

                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                Task exitTask = proc.WaitForExitAsync(ct);
                if (timeoutMin > 0)
                {
                    using var delayCts = new CancellationTokenSource();
                    Task delayTask = Task.Delay(TimeSpan.FromMinutes(timeoutMin), delayCts.Token);
                    Task first = await Task.WhenAny(exitTask, delayTask);
                    if (first == delayTask && !proc.HasExited)
                    {
                        try { KillProcessTree(proc); } catch { }
                        AppendLog("[TIMEOUT] " + Path.GetFileName(file) + " — qua " + timeoutMin + " phut, da huy");
                        _runningProcs.TryRemove(procKey, out _);
                        return (-999, snapTail());
                    }
                    delayCts.Cancel();
                }
                else
                {
                    await exitTask;
                }

                if (!proc.HasExited)
                {
                    // Đã bị huỷ qua token nhưng process vẫn sống -> kill luôn cho chắc
                    try { KillProcessTree(proc); } catch { }
                    _runningProcs.TryRemove(procKey, out _);
                    return (-999, snapTail());
                }

                int code;
                try { code = proc.ExitCode; }
                catch { code = -1; }
                _runningProcs.TryRemove(procKey, out _);
                return (code, snapTail());
            }
            catch (OperationCanceledException)
            {
                try { if (proc != null) KillProcessTree(proc); } catch { }
                if (proc != null) _runningProcs.TryRemove(procKey, out _);
                return (-999, "");
            }
            catch (Exception ex)
            {
                AppendLog("ERR: " + ex.Message);
                if (proc != null) _runningProcs.TryRemove(procKey, out _);
                return (-1, "");
            }
        }

        private void KillProcessTree(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { }
        }

        // =========================================================
        //  Build args
        // =========================================================
        private string BuildArgs(string file, string outdir, string outName, string backend, bool isHybrid, string effort, string url, string apiUrl)
        {
            var sb = new StringBuilder();

            // --- PaddleOCR: chay qua paddlex.exe (PP-StructureV3) ---
            if (backend == "paddleocr")
            {
                // Uu tien wrapper paddle_run.py (chi luu markdown gop,
                // tat seal/formula/chart): 1 PDF -> 1 .md + imgs/, khong
                // docx/tex/json/PNG nhu CLI mac dinh.
                try
                {
                    string wrap = ResolvePaddleWrapper();
                    string pExe = null;
                    try { pExe = ResolvePaddleExe(); } catch { }
                    string py = pExe == null ? null : PythonForExe(pExe);
                    if (wrap != null && py != null && File.Exists(py))
                        return "\"" + wrap + "\" --input \"" + file
                            + "\" --save_path \"" + Path.Combine(outdir, outName)
                            + "\" --device gpu";
                }
                catch { }
                sb.Append(" --pipeline PP-StructureV3");
                sb.Append(" --input \"").Append(file).Append("\"");
                sb.Append(" --save_path \"").Append(Path.Combine(outdir, outName)).Append("\"");
                sb.Append(" --device gpu");
                sb.Append(" --use_doc_orientation_classify False");
                sb.Append(" --use_doc_unwarping False");
                sb.Append(" --use_textline_orientation False");
                sb.Append(" --use_seal_recognition False");
                sb.Append(" --use_formula_recognition False");
                return sb.ToString();
            }

            // --- MinerU 4.x: chay qua mineru-kit parse --remote-url ---
            if (backend == "mineru4x")
            {
                sb.Append("parse \"").Append(file).Append("\"");
                sb.Append(" -o \"").Append(Path.Combine(outdir, outName)).Append("\"");
                sb.Append(" -f markdown");
                if (!string.IsNullOrWhiteSpace(apiUrl))
                    sb.Append(" --remote-url \"").Append(apiUrl).Append("\"");
                return sb.ToString();
            }

            sb.Append(" -p \"").Append(file).Append("\"");
            sb.Append(" -o \"").Append(Path.Combine(outdir, outName)).Append("\"");
            sb.Append(" -b ").Append(backend);
            if (backend == "pipeline")
            {
                // pipeline: khong co --effort / --image-analysis
            }
            else if (isHybrid)
                sb.Append(" --effort ").Append(effort);
            else
                sb.Append(" --image-analysis false");
            if (backend.EndsWith("-http-client") && !string.IsNullOrWhiteSpace(url))
                sb.Append(" -u \"").Append(url).Append("\"");
            if (!string.IsNullOrWhiteSpace(apiUrl))
                sb.Append(" --api-url \"").Append(apiUrl).Append("\"");
            // Gioi han trang (MinerU 3.x local/API, 0-based): bo qua neu khong dat
            if (!backend.EndsWith("-http-client") && (_pageFrom > 0 || _pageTo > 0))
            {
                if (_pageFrom > 0) sb.Append(" -s ").Append(_pageFrom - 1);
                if (_pageTo > 0) sb.Append(" -e ").Append(_pageTo - 1);
            }
            return sb.ToString();
        }

        // =========================================================
        //  B2: Khoi dong mineru-api thuong tru (giu model trong VRAM)
        // =========================================================
        private async Task<bool> StartMineruApiAsync(string backend, int conc, CancellationToken ct)
        {
            try
            {
                bool is4x = (backend == "mineru4x");
                string apiExe;
                if (is4x)
                {
                    apiExe = _mineru4xKit;
                    if (string.IsNullOrEmpty(apiExe) || !File.Exists(apiExe))
                    {
                        AppendLog("[API] khong tim thay mineru-kit.exe cho MinerU 4.x");
                        return false;
                    }
                }
                else
                {
                    apiExe = Path.Combine(Path.GetDirectoryName(_mineruPath) ?? "", "mineru-api.exe");
                    if (!File.Exists(apiExe))
                    {
                        AppendLog("[API] khong tim thay mineru-api.exe tai: " + apiExe);
                        return false;
                    }
                }

                // Chon port trang
                int port = 0;
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                }
                catch (Exception ex)
                {
                    AppendLog("[API] khong chon duoc port trang: " + ex.Message);
                    return false;
                }

                // Truong hop restart: don du cu
                try { if (_apiProc != null && !_apiProc.HasExited) KillProcessTree(_apiProc); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = apiExe,
                    Arguments = is4x
                        ? ("api-server --host 127.0.0.1 --port " + port + " --tier basic --preload-models --concurrency 1 --allow-local-source")
                        : ("--host 127.0.0.1 --port " + port + " --enable-vlm-preload true"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };

                // Copy cac bien CPU dang set cho mineru.exe + them MAX_CONCURRENT
                if (_cpuThreads != "0")
                {
                    foreach (var k in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS",
                                               "MINERU_INTRA_OP_NUM_THREADS", "MINERU_INTER_OP_NUM_THREADS", "MINERU_PDF_RENDER_THREADS" })
                        psi.EnvironmentVariables[k] = _cpuThreads;
                }
                psi.EnvironmentVariables["TOKENIZERS_PARALLELISM"] = "false";
                if (is4x)
                {
                    // Tai su dung model local, tranh tai lai ~2GB
                    string modelBase = ResolveMinerU4ModelBase();
                    if (!string.IsNullOrEmpty(modelBase))
                    {
                        psi.EnvironmentVariables["MINERU_MODEL_BASE_DIR"] = modelBase;
                        AppendLog("[MINERU4] tai su dung model local: " + modelBase);
                    }
                    else
                        AppendLog("[MINERU4] canh bao: khong tim thay models-dir.vlm trong mineru.json — server se tu tai model.");
                }
                else
                {
                    psi.EnvironmentVariables["MINERU_API_MAX_CONCURRENT_REQUESTS"] = "1";
                    psi.EnvironmentVariables["MINERU_VIRTUAL_VRAM_SIZE"] = "3";
                    psi.EnvironmentVariables["MINERU_PROCESSING_WINDOW_SIZE"] = "8";
                }
                if (!string.IsNullOrWhiteSpace(_apiKey))
                    psi.EnvironmentVariables["MINERU_VL_API_KEY"] = _apiKey;

                var proc = new Process { StartInfo = psi };
                var stderrSb = new StringBuilder();
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (stderrSb) stderrSb.AppendLine(e.Data); } };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null && _detailLog) AppendLog("[API] " + e.Data); };

                proc.Start();
                _apiProc = proc;
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                string url = "http://127.0.0.1:" + port + "/openapi.json";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var sw = Stopwatch.StartNew();
                bool ready = false;
                int lastLog = 0;
                while (sw.Elapsed.TotalMinutes < 8)
                {
                    if (ct.IsCancellationRequested) break;
                    if (proc.HasExited)
                    {
                        AppendLog("[API] mineru-api thoat som (exit=" + proc.ExitCode + ")");
                        string err = "";
                        lock (stderrSb) err = stderrSb.ToString();
                        if (!string.IsNullOrWhiteSpace(err)) AppendLog("[API] stderr: " + err.Trim());
                        _apiProc = null;
                        return false;
                    }
                    try
                    {
                        using var resp = await http.GetAsync(url, ct);
                        if (resp.IsSuccessStatusCode) { ready = true; break; }
                    }
                    catch { }
                    if (sw.Elapsed.TotalSeconds - lastLog >= 15)
                    {
                        lastLog = (int)sw.Elapsed.TotalSeconds;
                        AppendLog("[API] dang khoi dong va nap model VLM vao VRAM... (" + (int)sw.Elapsed.TotalSeconds + "s)");
                    }
                    await Task.Delay(2000, ct);
                }

                if (ready)
                {
                    _apiPort = port;
                    _apiUrl = "http://127.0.0.1:" + port;
                    AppendLog(is4x ? ("[MINERU4] api-server san sang tai " + _apiUrl) : ("[API] mineru-api san sang tai " + _apiUrl));
                    return true;
                }

                AppendLog("[API] khong san sang sau 8 phut — chay tuan tu nhu cu");
                try { KillProcessTree(proc); } catch { }
                _apiProc = null;
                return false;
            }
            catch (Exception ex)
            {
                AppendLog("[API] loi khoi dong: " + ex.Message);
                _apiProc = null;
                return false;
            }
        }

        // =========================================================
        //  B3/A4: Chay 1 file (dung cho ca tuan tu va song song)
        // =========================================================
        private async Task<FileResult> RunFileAsync(int index, FileInfo fi, BatchCtx ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var result = new FileResult { file = fi.Name, status = "LOI", seconds = 0 };
            try
            {
                string outName = OutNameFor(ctx, fi);
                string fileOutDir = Path.Combine(ctx.outdir, outName);
                string be = BackendFor(ctx, fi);
                bool isHyb = be == "hybrid-engine";
                string fileExe = ExeForBackend(ctx, be);
                string fileApiUrl = ApiForBackend(ctx, be);

                // Bo qua neu da co ket qua (resume): marker .done, hoac adopt output cu con khoe
                if (ctx.resume)
                {
                    if (IsDoneMarker(fileOutDir))
                    {
                        result.status = "SKIP";
                        result.note = "da co ket qua";
                        int pg = PagesFor(fi, ctx.src);
                        result.pages = pg;
                        Interlocked.Increment(ref _stSkipped);
                        Interlocked.Add(ref _pagesDone, (long)pg);
                        sw.Stop(); result.seconds = sw.Elapsed.TotalSeconds;
                        AddReport(new ReportRecord { file = fi.Name, status = "SKIP", pages = pg, seconds = result.seconds, note = result.note, engine = be, type = "" });
                        lock (_stateLock) WriteState(ctx.outdir, _startedTime, _stTotal, _stDone, _stSkipped, _stErrors, _errList, fi.Name);
                        RefreshStatus();
                        return result;
                    }
                    string stem0 = Path.GetFileNameWithoutExtension(fi.Name);
                    string adoptDir = FindResultDir(ctx.outdir, outName, TruncateStemUtf8(stem0, MaxStemBytes));
                    if (adoptDir != null)
                    {
                        string amd = FirstMdIn(adoptDir);
                        long alen = 0;
                        try { if (amd != null) alen = new FileInfo(amd).Length; } catch { }
                        if (alen >= 20)
                        {
                            WriteDoneMarker(adoptDir, be + "|adopted");
                            result.status = "SKIP";
                            result.note = "da co ket qua (adopt: " + Path.GetFileName(adoptDir) + ")";
                            int pgAdopt = PagesFor(fi, ctx.src);
                            result.pages = pgAdopt;
                            Interlocked.Increment(ref _stSkipped);
                            Interlocked.Add(ref _pagesDone, (long)pgAdopt);
                            sw.Stop(); result.seconds = sw.Elapsed.TotalSeconds;
                            AddReport(new ReportRecord { file = fi.Name, status = "SKIP", pages = pgAdopt, seconds = result.seconds, note = result.note, engine = be, type = "" });
                            lock (_stateLock) WriteState(ctx.outdir, _startedTime, _stTotal, _stDone, _stSkipped, _stErrors, _errList, fi.Name);
                            RefreshStatus();
                            return result;
                        }
                    }
                }

                SetCurFile(fi.Name, fi.FullName, PagesFor(fi, ctx.src));

                if (_cancelRequested || _batchAbort)
                {
                    result.status = "SKIP"; result.note = _cancelRequested ? "bi dung" : "bo qua — batch dung do loi lien tiep";
                    sw.Stop(); result.seconds = sw.Elapsed.TotalSeconds;
                    AddReport(new ReportRecord { file = fi.Name, status = "SKIP", pages = 0, seconds = result.seconds, note = result.note, engine = be, type = "" });
                    return result;
                }

                AppendLog("[FILE " + (index + 1) + "/" + _stTotal + "] " + fi.Name + (ctx.backendMap != null ? " [" + ShortEngineName(be) + "]" : ""));
                Interlocked.Increment(ref _activeFiles);
                Interlocked.Increment(ref _apiFilesSinceStart);

                // API song khong? Chet thi restart (toi da 2 lan), khong cuu duoc thi bo qua file
                // (chi kiem tra khi file can server; paddle khong can)
                if (fileApiUrl != null && !await EnsureApiAliveAsync(ctx, ct))
                {
                    result.status = "SKIP"; result.note = "bo qua — API chet, batch da dung (chay lai lan sau)";
                    sw.Stop(); result.seconds = sw.Elapsed.TotalSeconds;
                    AddReport(new ReportRecord { file = fi.Name, status = "SKIP", pages = 0, seconds = result.seconds, note = result.note, engine = be, type = "" });
                    return result;
                }

                // Timeout ti le so trang (B1): toi thieu theo cau hinh, 1 phut co so + 1 phut/trang
                int pgEarly = PagesFor(fi, ctx.src);
                int tmo = _timeoutMinutes;
                if (tmo > 0 && pgEarly > 0) tmo = Math.Max(tmo, 1 + pgEarly);

                int exit; string errTail;
                if (be == "windows-ocr")
                {
                    // P2: chay in-process qua WinRT, khong can exe/server
                    var rw = await RunWindowsOcrAsync(fi, fileOutDir, ct, tmo);
                    exit = rw.exit; errTail = rw.errTail;
                    if (exit != 0 && !_cancelRequested && _retry)
                    {
                        AppendLog("[CHẠY LẠI] " + fi.Name + " (Windows OCR)");
                        var rw2 = await RunWindowsOcrAsync(fi, fileOutDir, ct, tmo);
                        exit = rw2.exit;
                        if (!string.IsNullOrEmpty(rw2.errTail)) errTail = rw2.errTail;
                    }
                }
                else
                {
                string args = BuildArgs(fi.FullName, ctx.outdir, outName, be, isHyb, ctx.effort, ctx.url, fileApiUrl);
                var r1 = await RunOneAsync(fi.FullName, args, fileExe, ct, index, tmo);
                int exit0 = r1.exit;
                string errTail0 = r1.errTail;
                exit = exit0; errTail = errTail0;

                // Tu chay lai 1 lan khi loi (neu duoc chon va chua bi dung)
                if (exit != 0 && !_cancelRequested && _retry && (fileApiUrl == null || await EnsureApiAliveAsync(ctx, ct)))
                {
                    string retryArgs = args;
                    if (isHyb && _retryHigh && ctx.effort != "high")
                    {
                        AppendLog("[RETRY] " + fi.Name + " — chay lai voi effort=high");
                        retryArgs = BuildArgs(fi.FullName, ctx.outdir, outName, be, isHyb, "high", ctx.url, fileApiUrl);
                    }
                    else
                    {
                        AppendLog("[CHẠY LẠI] " + fi.Name);
                    }
                    var r2 = await RunOneAsync(fi.FullName, retryArgs, fileExe, ct, index, tmo);
                    exit = r2.exit;
                    if (!string.IsNullOrEmpty(r2.errTail)) errTail = r2.errTail;
                }
                }

                int pg2 = PagesFor(fi, ctx.src);
                result.pages = pg2;

                if (exit == 0)
                {
                    Interlocked.Exchange(ref _consecFails, 0);

                    // P3: hau xu ly lam sach .md (gop dong gay / xoa dong trong thua) — truoc quality gate
                    if (_joinLines || _squeezeBlanks)
                    {
                        try
                        {
                            string stemC = Path.GetFileNameWithoutExtension(fi.Name);
                            string cleanDir = FindResultDir(ctx.outdir, outName, TruncateStemUtf8(stemC, MaxStemBytes));
                            string cleanMd = cleanDir == null ? null : FirstMdIn(cleanDir);
                            if (!string.IsNullOrEmpty(cleanMd) && File.Exists(cleanMd))
                            {
                                string orig = File.ReadAllText(cleanMd, Encoding.UTF8);
                                string fixed_ = PostCleanText(orig, _joinLines, _squeezeBlanks);
                                if (!string.Equals(orig, fixed_))
                                {
                                    File.WriteAllText(cleanMd, fixed_, new UTF8Encoding(false));
                                    AppendLog("[CLEAN] " + fi.Name + ": " + orig.Length + " -> " + fixed_.Length + " ky tu");
                                }
                            }
                        }
                        catch (Exception ex) { AppendLog("ERR lam sach " + fi.Name + ": " + ex.Message); }
                    }

                    // TXT: xuat them ban text thuan (strip markdown) canh file .md
                    try
                    {
                        string stemT = Path.GetFileNameWithoutExtension(fi.Name);
                        string txtDir = FindResultDir(ctx.outdir, outName, TruncateStemUtf8(stemT, MaxStemBytes));
                        string txtMd = txtDir == null ? null : FirstMdIn(txtDir);
                        if (!string.IsNullOrEmpty(txtMd) && File.Exists(txtMd))
                        {
                            string plain = StripMdToText(File.ReadAllText(txtMd, Encoding.UTF8));
                            if (plain.Trim().Length > 0)
                                File.WriteAllText(Path.Combine(Path.GetDirectoryName(txtMd) ?? txtDir, Path.GetFileNameWithoutExtension(txtMd) + ".txt"), plain, new UTF8Encoding(false));
                        }
                    }
                    catch (Exception ex) { AppendLog("ERR xuat txt " + fi.Name + ": " + ex.Message); }

                    // PaddleOCR: xoa cac file PNG visualization o root save_path (khong de quy, giu nguyen imgs\)
                    if (be == "paddleocr")
                    {
                        try
                        {
                            string pngDir = fileOutDir;
                            if (Directory.Exists(pngDir))
                            {
                                int nDel = 0;
                                foreach (var png in Directory.GetFiles(pngDir, "*.png"))
                                {
                                    try { File.Delete(png); nDel++; }
                                    catch { }
                                }
                                if (nDel >= 1)
                                    AppendLog("[PADDLE] da xoa " + nDel + " file PNG visualization");
                            }
                        }
                        catch { }
                    }

                    // A4: quality gate
                    var q = QualityGate(fi, ctx.outdir, ctx.src, outName);
                    if (q.reason != null)
                    {
                        result.status = "KHA NGHI";
                        result.note = q.reason;
                        AddSuspicious(fi.Name + " — " + q.reason);
                        AppendLog("[KTRA] " + fi.Name + ": KHA NGHI — " + q.reason);
                    }
                    else
                    {
                        result.status = "OK";
                        AppendLog("[KTRA] " + fi.Name + ": OK (" + q.chars + " ky tu)");
                    }
                    // A1 cheo kiem: file KHA NGHI chay them engine 2, giu ban tot hon
                    if (result.status == "KHA NGHI" && ctx.crossCheck)
                    {
                        string vbe = null;
                        if (ctx.backendMap != null && ctx.fastBe != null && ctx.ocrBe != null)
                            vbe = (be == ctx.fastBe) ? ctx.ocrBe : ctx.fastBe;
                        else if (be != "paddleocr" && !string.IsNullOrEmpty(ctx.paddleExe))
                            vbe = "paddleocr";
                        else if (be == "paddleocr" && !string.IsNullOrEmpty(ctx.kitExe) && !string.IsNullOrEmpty(ctx.apiUrl))
                            vbe = "mineru4x";
                        if (vbe == null)
                        {
                            if (!_crossSkipLogged) { _crossSkipLogged = true; AppendLog("[CHEO] khong co engine phu kha dung — bo qua cheo kiem."); }
                        }
                        else
                        {
                            string vExe = ExeForBackend(ctx, vbe);
                            string vApi = ApiForBackend(ctx, vbe);
                            if (string.IsNullOrEmpty(vExe))
                            {
                                AppendLog("[CHEO] khong tim thay exe cho " + ShortEngineName(vbe) + " — bo qua.");
                            }
                            else
                            {
                                string stemV = Path.GetFileNameWithoutExtension(fi.Name);
                                string outName2 = TruncateStemUtf8(stemV, MaxStemBytes) + "__" + EngineCode(vbe) + "__v";
                                string vDir = Path.Combine(ctx.outdir, outName2);
                                AppendLog("[CHEO] " + fi.Name + " — chay them " + ShortEngineName(vbe) + "...");
                                string vArgs = BuildArgs(fi.FullName, ctx.outdir, outName2, vbe, vbe == "hybrid-engine", ctx.effort, ctx.url, vApi);
                                var swV = Stopwatch.StartNew();
                                _noProgressParse = true;
                                var rv = (exit: -1, errTail: "");
                                try { rv = await RunOneAsync(fi.FullName, vArgs, vExe, ct, index, tmo); }
                                finally { _noProgressParse = false; }
                                swV.Stop();
                                if (_cancelRequested || _batchAbort) { try { if (Directory.Exists(vDir)) Directory.Delete(vDir, true); } catch { } }
                                else if (rv.exit == 0)
                                {
                                    var q2 = QualityGate(fi, ctx.outdir, ctx.src, outName2);
                                    bool sPass = q2.reason == null;
                                    bool takeSecondary = sPass || q2.chars > q.chars;
                                    AppendLog("[CHEO] " + fi.Name + ": " + ShortEngineName(be) + " (" + q.chars + ") vs " + ShortEngineName(vbe) + " (" + q2.chars + ") → giu " + (takeSecondary ? ShortEngineName(vbe) : ShortEngineName(be)));
                                    if (takeSecondary)
                                    {
                                        try { foreach (var f in Directory.GetFiles(fileOutDir, "*.md", SearchOption.AllDirectories)) File.Delete(f); } catch { }
                                        WriteDoneMarker(vDir, vbe + "|OK|xcheo");
                                        result.status = sPass ? "OK" : "KHA NGHI";
                                        result.note = (sPass ? "OK" : result.note) + " (xcheo " + ShortEngineName(vbe) + ": " + q2.chars + " ky tu)";
                                        result.seconds += swV.Elapsed.TotalSeconds;
                                    }
                                    else
                                    {
                                        try { if (Directory.Exists(vDir)) Directory.Delete(vDir, true); } catch { }
                                    }
                                }
                                else
                                {
                                    AppendLog("[CHEO] " + fi.Name + " — engine phu loi (exit " + rv.exit + "), giu ban goc.");
                                    try { if (Directory.Exists(vDir)) Directory.Delete(vDir, true); } catch { }
                                }
                            }
                        }
                    }
                    // Ghi marker hoan thanh (ca OK va KHA NGHI) de resume tin duoc
                    WriteDoneMarker(fileOutDir, be + "|" + result.status);
                }
                else
                {
                    string errType = ClassifyError(exit, errTail);
                    result.status = "LOI";
                    result.note = "exit " + exit + " [" + errType + "]";
                    lock (_errLock) _errList.Add(new ErrorEntry { file = fi.Name, exit = exit, type = errType });
                    AppendLog("  -> LOI (exit " + exit + " [" + errType + "])");
                    if (errType == "OOM" && !string.IsNullOrEmpty(fileApiUrl))
                    {
                        _forceApiRestart = true;
                        AppendLog("[API] se khoi dong lai API truoc file tiep theo (giai phong VRAM sau OOM)");
                    }
                    int cf = Interlocked.Increment(ref _consecFails);
                    if (cf >= 5)
                    {
                        _batchAbort = true;
                        AppendLog("[BATCH] " + cf + " file loi lien tiep — DUNG batch de bao toan ket qua (thuong do VRAM/API). File chua lam se tiep tuc o lan sau.");
                    }
                }

                Interlocked.Add(ref _pagesDone, (long)pg2);
                if (result.status == "OK" || result.status == "KHA NGHI") Interlocked.Increment(ref _stDone);
                else if (result.status == "LOI") Interlocked.Increment(ref _stErrors);

                sw.Stop(); result.seconds = sw.Elapsed.TotalSeconds;

                AddReport(new ReportRecord { file = fi.Name, status = result.status, pages = result.pages, seconds = result.seconds, note = result.note ?? "", engine = be, type = "" });
                lock (_stateLock) WriteState(ctx.outdir, _startedTime, _stTotal, _stDone, _stSkipped, _stErrors, _errList, fi.Name);
                RefreshStatus();
                return result;
            }
            catch (Exception ex)
            {
                AppendLog("ERR xu ly " + fi.Name + ": " + ex.Message);
                result.status = "LOI";
                result.note = "exception: " + ex.Message;
                try { Interlocked.Increment(ref _stErrors); } catch { }
                sw.Stop(); result.seconds = sw.Elapsed.TotalSeconds;
                try { AddReport(new ReportRecord { file = fi.Name, status = "LOI", pages = 0, seconds = result.seconds, note = result.note, engine = BackendFor(ctx, fi), type = "EXC" }); } catch { }
                try { lock (_stateLock) WriteState(ctx.outdir, _startedTime, _stTotal, _stDone, _stSkipped, _stErrors, _errList, fi.Name); } catch { }
                return result;
            }
            finally { Interlocked.Decrement(ref _activeFiles); }
        }

        private void AddSuspicious(string entry)
        {
            lock (_suspLock) _suspicious.Add(entry);
        }

        // Kiem tra mineru-api con song khong (health endpoint + process)
        private async Task<bool> ApiHealthyAsync()
        {
            try
            {
                if (_apiProc == null || _apiProc.HasExited || _apiPort == 0) return false;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var resp = await http.GetAsync("http://127.0.0.1:" + _apiPort + "/openapi.json");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // Dam bao API con song truoc khi chay file; tu restart toi da 2 lan, khong duoc thi huy batch
        private async Task<bool> EnsureApiAliveAsync(BatchCtx ctx, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ctx.apiUrl)) return true;
            if (_batchAbort) return false;
            if (_forceApiRestart)
            {
                _forceApiRestart = false;
                return await ForceRestartApiAsync(ctx, ct, "file truoc bi OOM — khoi dong lai API de giai phong VRAM...");
            }

            // Restart dinh ky: VRAM khong tu giai phong sau moi task (issue #3399/#3243)
            // -> nap lai model moi N file de tranh tich tu. Chi restart khi khong file nao dang chay.
            if (_apiFilesSinceStart >= ApiRestartEveryFiles && _activeFiles <= 1)
            {
                await _apiRestartLock.WaitAsync(ct);
                try
                {
                    if (_apiFilesSinceStart >= ApiRestartEveryFiles && _activeFiles <= 1 && !_batchAbort)
                    {
                        AppendLog("[API] da xu ly " + _apiFilesSinceStart + " file — khoi dong lai mineru-api de giai phong VRAM...");
                        try { if (_apiProc != null && !_apiProc.HasExited) KillProcessTree(_apiProc); } catch { }
                        _apiProc = null; _apiUrl = null; _apiPort = 0;
                        _apiFilesSinceStart = 0;
                        _apiRestarts = 0;
                        bool ok2 = await StartMineruApiAsync(_apiServerBackend ?? ctx.backend, ctx.conc, ct);
                        if (ok2)
                        {
                            ctx.apiUrl = _apiUrl;
                            AppendLog("[API] nap lai model xong — tiep tuc batch");
                            return true;
                        }
                        _batchAbort = true;
                        AppendLog("[API] restart dinh ky that bai — DUNG batch");
                        return false;
                    }
                }
                finally { _apiRestartLock.Release(); }
            }

            if (await ApiHealthyAsync()) return true;
            await _apiRestartLock.WaitAsync(ct);
            try
            {
                if (await ApiHealthyAsync()) return true;
                if (_apiRestarts >= 2)
                {
                    AppendLog("[API] mineru-api da chet nhieu lan — DUNG batch de bao toan ket qua (file chua lam se tiep tuc o lan sau)");
                    _batchAbort = true;
                    return false;
                }
                _apiRestarts++;
                AppendLog("[API] PHAT HIEN mineru-api DA CHET — khoi dong lai (lan " + _apiRestarts + "/2)...");
                try { if (_apiProc != null && !_apiProc.HasExited) KillProcessTree(_apiProc); } catch { }
                _apiProc = null; _apiUrl = null; _apiPort = 0;
                bool ok = await StartMineruApiAsync(_apiServerBackend ?? ctx.backend, ctx.conc, ct);
                if (ok)
                {
                    ctx.apiUrl = _apiUrl;
                    AppendLog("[API] da khoi phuc mineru-api — tiep tuc batch");
                }
                else
                {
                    _batchAbort = true;
                    AppendLog("[API] khong khoi phuc duoc mineru-api — DUNG batch");
                }
                return ok;
            }
            finally { _apiRestartLock.Release(); }
        }

        // Khoi dong lai API bat buoc (sau OOM) — dung chung lock voi EnsureApiAliveAsync
        private async Task<bool> ForceRestartApiAsync(BatchCtx ctx, CancellationToken ct, string reason)
        {
            await _apiRestartLock.WaitAsync(ct);
            try
            {
                if (_batchAbort) return false;
                AppendLog("[API] " + reason);
                try { if (_apiProc != null && !_apiProc.HasExited) KillProcessTree(_apiProc); } catch { }
                _apiProc = null; _apiUrl = null; _apiPort = 0;
                bool ok = await StartMineruApiAsync(_apiServerBackend ?? ctx.backend, ctx.conc, ct);
                if (ok)
                {
                    ctx.apiUrl = _apiUrl;
                    AppendLog("[API] nap lai model xong — tiep tuc batch");
                    return true;
                }
                _batchAbort = true;
                AppendLog("[API] restart that bai — DUNG batch");
                return false;
            }
            finally { _apiRestartLock.Release(); }
        }

        private void AddReport(ReportRecord r)
        {
            lock (_reportLock) _reportRecords.Add(r);
        }

        // =========================================================
        //  A4: Quality gate sau moi file (exit 0)
        // =========================================================
        // =========================================================
        //  P2 engine Windows OCR (WinRT, in-process, CPU, mien phi)
        // =========================================================
        private static Windows.Globalization.Language WinOcrPickLanguage()
        {
            try
            {
                var langs = OcrEngine.AvailableRecognizerLanguages;
                if (langs == null) return null;
                foreach (var l in langs)
                {
                    try { if (l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return l; } catch { }
                }
                foreach (var l in langs) return l;
            }
            catch { }
            return null;
        }

        private async Task<(int exit, string errTail)> RunWindowsOcrAsync(FileInfo fi, string fileOutDir, CancellationToken ct, int timeoutMin)
        {
            try
            {
                var lang = WinOcrPickLanguage();
                if (lang == null)
                    return (-1, "Windows OCR: may chua cai goi ngon ngu OCR (Settings > Time & language > Language).");
                string langTag = "?";
                try { langTag = lang.LanguageTag; } catch { }
                OcrEngine engine = null;
                try { engine = OcrEngine.TryCreateFromLanguage(lang); } catch (Exception ex) { return (-1, "Windows OCR: khong tao duoc engine (" + langTag + "): " + ex.Message); }
                if (engine == null)
                    return (-1, "Windows OCR: khong tao duoc engine (" + langTag + ").");
                AppendLog("[WINOCR] ngon ngu: " + langTag);

                CancellationTokenSource timeoutCts = null;
                CancellationTokenSource linked = null;
                try
                {
                    if (timeoutMin > 0) timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin));
                    CancellationToken tok = ct;
                    if (timeoutCts != null) { linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token); tok = linked.Token; }

                    StorageFile sf = await StorageFile.GetFileFromPathAsync(fi.FullName).AsTask(tok);
                    PdfDocument doc = null;
                    try
                    {
                        doc = await PdfDocument.LoadFromFileAsync(sf).AsTask(tok);
                        uint n = 0;
                        try { n = doc.PageCount; } catch { }
                        if (n == 0) return (-1, "PDF khong co trang nao (co the ma hoa/hong).");
                        try { Directory.CreateDirectory(fileOutDir); } catch { }
                        string mdPath = Path.Combine(fileOutDir, Path.GetFileNameWithoutExtension(fi.Name) + ".md");
                        var sb = new StringBuilder();
                        const double scale = 2.0;
                        for (uint i = 0; i < n; i++)
                        {
                            tok.ThrowIfCancellationRequested();
                            using (PdfPage page = doc.GetPage(i))
                            {
                                var opt = new PdfPageRenderOptions();
                                try
                                {
                                    opt.DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale);
                                    opt.DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale);
                                }
                                catch { }
                                opt.BitmapEncoderId = BitmapEncoder.PngEncoderId;
                                using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                                {
                                    await page.RenderToStreamAsync(stream, opt).AsTask(tok);
                                    stream.Seek(0);
                                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(tok);
                                    SoftwareBitmap bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(tok);
                                    try
                                    {
                                        var res = await engine.RecognizeAsync(bmp).AsTask(tok);
                                        if (res != null && res.Lines != null)
                                        {
                                            foreach (var ln in res.Lines)
                                            {
                                                try { if (ln != null && !string.IsNullOrWhiteSpace(ln.Text)) sb.AppendLine(ln.Text.TrimEnd()); } catch { }
                                            }
                                        }
                                    }
                                    finally { try { bmp.Dispose(); } catch { } }
                                }
                            }
                            sb.AppendLine();
                            lock (_progLock) { _curPageInFile = (int)Math.Min(i + 1, int.MaxValue); _curFilePages = (int)Math.Min(n, int.MaxValue); }
                            RefreshStatus();
                        }
                        File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(false));
                        AppendLog("[WINOCR] xong " + n + " trang (" + langTag + "): " + Path.GetFileName(mdPath));
                        return (0, "");
                    }
                    finally { /* PdfDocument khong co Dispose trong projection nay; page da dispose tung trang */ }
                }
                catch (OperationCanceledException)
                {
                    if (timeoutCts != null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        return (-999, "Windows OCR timeout qua " + timeoutMin + " phut");
                    return (-999, "bi huy");
                }
                catch (Exception ex) { return (-1, "Windows OCR loi: " + ex.Message); }
                finally
                {
                    try { if (linked != null) linked.Dispose(); } catch { }
                    try { if (timeoutCts != null) timeoutCts.Dispose(); } catch { }
                }
            }
            catch (Exception ex) { return (-1, "Windows OCR loi: " + ex.Message); }
        } // het RunWindowsOcrAsync

        private static bool IsMdBlockStart(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            char c = t[0];
            if (c == '#' || c == '|' || c == '>' || c == '`' || c == '-' || c == '*' || c == '+') return true;
            int i = 0;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i > 0 && i < t.Length && (t[i] == '.' || t[i] == ')') && (i + 1 >= t.Length || t[i + 1] == ' ')) return true;
            return false;
        }

        private static bool EndsSentence(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            char c = t[t.Length - 1];
            return c == '.' || c == '。' || c == ':' || c == '：' || c == '?' || c == '!' || c == '？' || c == '！';
        }

        // P3: hau xu ly lam sach .md (gop dong gay / xoa dong trong thua)
        private static string PostCleanText(string text, bool joinLines, bool squeezeBlanks)
        {
            if (string.IsNullOrEmpty(text) || (!joinLines && !squeezeBlanks)) return text;
            try
            {
                var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                var buf = new List<string>(lines.Length);
                foreach (var raw in lines)
                {
                    string ln = raw.TrimEnd();
                    if (string.IsNullOrWhiteSpace(ln))
                    {
                        if (squeezeBlanks && buf.Count > 0 && buf[buf.Count - 1] == "") continue;
                        buf.Add("");
                        continue;
                    }
                    if (joinLines && buf.Count > 0 && buf[buf.Count - 1] != ""
                        && !IsMdBlockStart(ln.TrimStart()) && !IsMdBlockStart(buf[buf.Count - 1].TrimStart())
                        && !EndsSentence(buf[buf.Count - 1]))
                        buf[buf.Count - 1] = buf[buf.Count - 1] + " " + ln.Trim();
                    else
                        buf.Add(ln);
                }
                string joined = string.Join("\n", buf);
                return squeezeBlanks ? joined.Trim('\n') : joined;
            }
            catch { return text; }
        }

        private (string reason, int chars) QualityGate(FileInfo fi, string outdir, string src, string outName)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(fi.Name);
                string dir = FindResultDir(outdir, outName ?? TruncateStemUtf8(stem, MaxStemBytes), TruncateStemUtf8(stem, MaxStemBytes));
                string mdFile = dir == null ? null : FirstMdIn(dir);
                if (mdFile == null)
                    return ("khong tim thay file .md", 0);

                string text = File.ReadAllText(mdFile, Encoding.UTF8);
                int pages = PagesFor(fi, src);
                if (pages <= 0) pages = 1;
                int charsPerPage = text.Length / pages;

                int fffd = 0;
                foreach (char c in text) if (c == '\uFFFD') fffd++;
                double ratio = fffd / (double)Math.Max(text.Length, 1);

                if (text.Length < 20) return ("van ban qua ngan (<20 ky tu)", text.Length);
                if (charsPerPage < 50) return ("it hon 50 ky tu/trang (" + charsPerPage + ")", text.Length);
                if (ratio > 0.05) return ("nhieu ky tu loi U+FFFD: " + (ratio * 100.0).ToString("0.0") + "%", text.Length);
                return (null, text.Length);
            }
            catch (Exception ex)
            {
                // Loi doc/kiem tra -> khong danh gia la kha nghi, chi log
                AppendLog("[KTRA] loi kiem tra " + fi.Name + ": " + ex.Message);
                return (null, 0);
            }
        }

        // =========================================================
        //  Strip markdown -> text thuan (xuat .txt canh .md)
        // =========================================================
        private static string StripMdToText(string md)
        {
            try
            {
                if (string.IsNullOrEmpty(md)) return "";
                string t = md.Replace("\r\n", "\n");
                // Bo code fence giu noi dung
                t = Regex.Replace(t, "```[a-zA-Z]*\n", "");
                t = t.Replace("```", "");
                var lines = t.Split('\n');
                var sb = new StringBuilder();
                foreach (var raw in lines)
                {
                    string ln = raw;
                    // Bang: | -> khoang trang, bo dong ke ---/|---|
                    string nospace = ln.Replace(" ", "");
                    if (nospace.Length > 0 && nospace.Trim('|', '-', ':', '+').Length == 0) continue;
                    ln = ln.Replace("|", " ");
                    // Hinh anh ![a](u) -> a ; lien ket [t](u) -> t
                    ln = Regex.Replace(ln, @"!\[([^\]]*)\]\([^)]*\)", "$1");
                    ln = Regex.Replace(ln, @"\[([^\]]*)\]\([^)]*\)", "$1");
                    // Tieu de, quote, list markers dau dong
                    ln = Regex.Replace(ln, @"^\s{0,3}#{1,6}\s+", "");
                    ln = Regex.Replace(ln, @"^\s*>\s?", "");
                    ln = Regex.Replace(ln, @"^\s*([-*+]\s+|\d+[.)]\s+)", "");
                    // Inline: `code`, **bold**, *it*, __, ~~
                    ln = Regex.Replace(ln, @"`([^`]*)`", "$1");
                    ln = ln.Replace("**", "").Replace("__", "");
                    ln = Regex.Replace(ln, @"(^|\W)\*(\S[^*]*\S|\S)\*(\W|$)", "$1$2$3");
                    ln = Regex.Replace(ln, @"(^|\W)_(\S[^_]*\S|\S)_(\W|$)", "$1$2$3");
                    ln = ln.Replace("~~", "");
                    // HTML tags
                    ln = Regex.Replace(ln, @"<[^>]+>", "");
                    // Footnote refs [^1]
                    ln = Regex.Replace(ln, @"\[\^[^\]]*\]", "");
                    // Gop khoang trang, bo dong trang (giu 1 dong)
                    ln = Regex.Replace(ln, @"[ \t]{2,}", " ").TrimEnd();
                    if (ln.Trim().Length == 0)
                    {
                        if (sb.Length > 0 && !sb.ToString(sb.Length - 1, 1).Equals("\n\n"))
                            sb.Append("\n");
                        continue;
                    }
                    sb.Append(ln.Trim()).Append("\n");
                }
                string out_ = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim() + "\n";
                return out_;
            }
            catch { return md ?? ""; }
        }

        // =========================================================
        //  C2: Bao cao HTML khi ket thuc batch
        // =========================================================
        private void WriteReport(string outdir, bool stopped, double batchSec)
        {
            try
            {
                List<ReportRecord> recs;
                lock (_reportLock) recs = new List<ReportRecord>(_reportRecords);
                int total = recs.Count;
                int ok = recs.Count(r => r.status == "OK");
                int loi = recs.Count(r => r.status == "LOI");
                int skip = recs.Count(r => r.status == "SKIP");
                int nghi = recs.Count(r => r.status == "KHA NGHI");
                int oom = recs.Count(r => r.type == "OOM");

                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html lang=\"vi\"><head><meta charset=\"utf-8\">");
                sb.AppendLine("<title>MinerU 2.5 — Bao cao batch</title>");
                sb.AppendLine("<style>");
                sb.AppendLine("body{background:#17181D;color:#E8EAF0;font-family:'Segoe UI',Arial,sans-serif;margin:24px;}");
                sb.AppendLine("h1{color:#fff;font-size:22px;margin:0 0 12px;}");
                sb.AppendLine(".sum{background:#23242B;border:1px solid #34363F;border-radius:10px;padding:14px;margin:0 0 16px;}");
                sb.AppendLine(".sum b{color:#fff;} .sum span{margin-right:18px;font-size:14px;}");
                sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px;}");
                sb.AppendLine("th,td{border:1px solid #34363F;padding:8px 10px;text-align:left;vertical-align:top;}");
                sb.AppendLine("th{background:#23242B;color:#9CA0AB;}");
                sb.AppendLine("tr:nth-child(even){background:#1d1e24;}");
                sb.AppendLine(".OK{color:#4ADE80;} .LOI{color:#F87171;} .SKIP{color:#9CA0AB;} .KHANGHI{color:#FBBF24;}");
                sb.AppendLine("</style></head><body>");
                sb.AppendLine("<h1>MinerU 2.5 — Báo cáo batch</h1>");
                sb.AppendLine("<div class=\"sum\">");
                sb.AppendLine("<span>Tổng file: <b>" + total + "</b></span>");
                sb.AppendLine("<span>Thành công: <b>" + ok + "</b></span>");
                sb.AppendLine("<span>Lỗi: <b>" + loi + "</b></span>");
                sb.AppendLine("<span>Bỏ qua: <b>" + skip + "</b></span>");
                sb.AppendLine("<span>Khả nghi: <b>" + nghi + "</b></span>");
                if (oom > 0) sb.AppendLine("<span>Lỗi OOM: <b>" + oom + "</b></span>");
                sb.AppendLine("<span>Tổng thời gian: <b>" + FmtLong((long)batchSec) + "</b></span>");
                if (stopped) sb.AppendLine("<span style=\"color:#FBBF24;\">(batch dừng giữa chừng)</span>");
                sb.AppendLine("</div>");
                sb.AppendLine("<table><thead><tr><th>#</th><th>File</th><th>Trạng thái</th><th>Engine</th><th>Loại lỗi</th><th>Số trang</th><th>Thời gian (s)</th><th>Ghi chú</th></tr></thead><tbody>");
                int idx = 1;
                foreach (var r in recs)
                {
                    string cls = r.status == "OK" ? "OK" : r.status == "LOI" ? "LOI" : r.status == "SKIP" ? "SKIP" : "KHANGHI";
                    sb.AppendLine("<tr><td>" + idx + "</td><td>" + HtmlEscape(r.file) + "</td><td class=\"" + cls + "\">" + HtmlEscape(r.status) + "</td><td>" + HtmlEscape(r.engine ?? "") + "</td><td>" + HtmlEscape(r.type ?? "") + "</td><td>" + r.pages + "</td><td>" + r.seconds.ToString("0.0") + "</td><td>" + HtmlEscape(r.note ?? "") + "</td></tr>");
                    idx++;
                }
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</body></html>");

                File.WriteAllText(Path.Combine(outdir, "batch-report.html"), sb.ToString(), new UTF8Encoding(false));
                AppendLog("[BAOCAO] Da ghi batch-report.html");
            }
            catch (Exception ex) { AppendLog("ERR ghi bao cao: " + ex.Message); }
        }

        private static string HtmlEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        // =========================================================
        //  Output layout v2: <stem>__<code> + marker .done (A1/A2/B3)
        // =========================================================
        private static string EngineCode(string backend)
        {
            if (backend == "hybrid-engine") return "hyb";
            if (backend == "vlm-engine") return "vlm";
            if (backend == "pipeline") return "pipe";
            if (backend == "paddleocr") return "pad";
            if (backend == "mineru4x") return "m4x";
            if (backend == "windows-ocr") return "win";
            if (backend != null && backend.EndsWith("-http-client")) return "http";
            return "eng";
        }

        private static string Hash6(string s)
        {
            try
            {
                using (var sha = SHA1.Create())
                {
                    byte[] hb = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                    return BitConverter.ToString(hb).Replace("-", "").Substring(0, 6).ToLowerInvariant();
                }
            }
            catch { return "000000"; }
        }

        // Ten thu muc output da dinh cho 1 file (on dinh giua cac lan chay)
        private static string OutNameFor(BatchCtx ctx, FileInfo fi)
        {
            try
            {
                if (ctx.outMap != null && ctx.outMap.TryGetValue(fi.FullName, out string n) && !string.IsNullOrEmpty(n))
                    return n;
            }
            catch { }
            string stem = Path.GetFileNameWithoutExtension(fi.Name);
            return TruncateStemUtf8(stem, MaxStemBytes) + "__" + EngineCode(BackendFor(ctx, fi));
        }

        private const int SmartTextThreshold = 150; // ky tu/trang binh quan (>= thi coi la PDF text)

        // Backend thuc te cua 1 file (smart routing) — mac dinh la backend cua batch
        private static string BackendFor(BatchCtx ctx, FileInfo fi)
        {
            try
            {
                if (ctx.backendMap != null && ctx.backendMap.TryGetValue(fi.FullName, out string b) && !string.IsNullOrEmpty(b))
                    return b;
            }
            catch { }
            return ctx.backend;
        }

        private static bool NeedsApi(string be)
        {
            return be == "hybrid-engine" || be == "pipeline" || be == "mineru4x";
        }

        // exePath theo backend (smart routing / cheo kiem)
        private string ExeForBackend(BatchCtx ctx, string be)
        {
            if (be == "paddleocr" && !string.IsNullOrEmpty(ctx.paddleExe))
            {
                // Wrapper paddle_run.py (neu co): chay bang python cua venv
                // paddle thay vi paddlex.exe de chi luu markdown gop.
                try
                {
                    string wrap = ResolvePaddleWrapper();
                    string py = PythonForExe(ctx.paddleExe);
                    if (wrap != null && py != null && File.Exists(py)) return py;
                }
                catch { }
                return ctx.paddleExe;
            }
            if (be == "mineru4x" && !string.IsNullOrEmpty(ctx.kitExe)) return ctx.kitExe;
            if (!string.IsNullOrEmpty(ctx.exePath)) return ctx.exePath;
            try { if (!string.IsNullOrEmpty(_mineruPath) && File.Exists(_mineruPath)) return _mineruPath; } catch { }
            return ctx.exePath;
        }

        // apiUrl cho 1 file cu the (null = khong can server)
        private static string ApiForBackend(BatchCtx ctx, string be)
        {
            if (!NeedsApi(be)) return null;
            return ctx.apiUrl;
        }

        // python.exe cua 1 exe (Scripts\xxx.exe -> ..\python.exe)
        private static string PythonForExe(string exePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(exePath ?? "");
                if (string.IsNullOrEmpty(dir)) return null;
                string py = Path.Combine(dir, "python.exe");
                if (File.Exists(py)) return py;
            }
            catch { }
            return null;
        }

        private static async Task<bool> HasModuleAsync(string pyExe, string module, int timeoutSec)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    Arguments = "-c \"import " + module + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                    try { await proc.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return false; }
                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static string EnsureProbeScript()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "opencode");
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "pdfprobe.py");
                if (!File.Exists(p))
                {
                    File.WriteAllText(p,
                        "import sys\n" +
                        "def main():\n" +
                        "    try:\n" +
                        "        import pypdf\n" +
                        "        r = pypdf.PdfReader(sys.argv[1])\n" +
                        "        n = min(5, len(r.pages))\n" +
                        "        t = 0\n" +
                        "        for i in range(n):\n" +
                        "            try:\n" +
                        "                t += len(r.pages[i].extract_text() or '')\n" +
                        "            except Exception:\n" +
                        "                pass\n" +
                        "        print(t // max(1, n))\n" +
                        "    except Exception:\n" +
                        "        print(-1)\n" +
                        "main()\n", new UTF8Encoding(false));
                }
                return p;
            }
            catch { return null; }
        }

        // Probe text PDF: trung binh ky tu/trang tren 5 trang dau. -1 = loi.
        private static async Task<int> ProbePdfTextAsync(string pyExe, string script, string pdfPath, int timeoutSec)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    Arguments = "\"" + script + "\" \"" + pdfPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                using (var proc = new Process { StartInfo = psi })
                {
                    var sb = new StringBuilder();
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                    try { await proc.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return -1; }
                    string o = sb.ToString().Trim();
                    string last = o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                    if (int.TryParse(last.Trim(), out int v)) return v;
                    return -1;
                }
            }
            catch { return -1; }
        }

        private static string FirstMdIn(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { return null; }
        }

        // Tim thu muc ket qua: thu muc da dinh truoc, roi legacy <stem>, roi fuzzy (bo qua thu muc __code de khong nham engine khac)
        private static string FindResultDir(string outdir, string assignedName, string stem)
        {
            try
            {
                string primary = Path.Combine(outdir, assignedName);
                if (FirstMdIn(primary) != null) return primary;
                string legacy = Path.Combine(outdir, stem);
                if (!string.Equals(legacy, primary, StringComparison.OrdinalIgnoreCase) && FirstMdIn(legacy) != null)
                    return legacy;
                if (Directory.Exists(outdir))
                {
                    foreach (var d in Directory.EnumerateDirectories(outdir))
                    {
                        string dn = Path.GetFileName(d);
                        if (dn.IndexOf("__") >= 0) continue;
                        if (dn.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && FirstMdIn(d) != null)
                            return d;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool IsDoneMarker(string dir)
        {
            try { return Directory.Exists(dir) && File.Exists(Path.Combine(dir, ".done")); }
            catch { return false; }
        }

        private static void WriteDoneMarker(string dir, string content)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                File.WriteAllText(Path.Combine(dir, ".done"), content ?? "", new UTF8Encoding(false));
            }
            catch { }
        }

        // Chi gan nhan ERR: cho dong that su bao loi (C1)
        private static bool IsRealError(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("traceback", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("out of memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("cuda error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("cublas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("killed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Phan loai loi de retry/bao cao (B2)
        private static string ClassifyError(int exit, string errTail)
        {
            if (exit == -999) return "TIMEOUT";
            string t = errTail ?? "";
            if (t.IndexOf("out of memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("OutOfMemoryError", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("CUBLAS_STATUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("CUDA error", StringComparison.OrdinalIgnoreCase) >= 0)
                return "OOM";
            if (t.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("xref", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("damaged", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("invalid pdf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("PDFSyntax", StringComparison.Ordinal) >= 0)
                return "PDF";
            return "RUN";
        }

        // =========================================================
        //  Truncate stem giong mineru (UTF-8 toi da 200 bytes)
        // =========================================================
        private static string TruncateStemUtf8(string s, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= maxBytes) return s;

            int len = maxBytes;
            // Lui ve boundary UTF-8 hop le (bo qua cac byte tiep noi 10xxxxxx)
            while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;

            return Encoding.UTF8.GetString(bytes, 0, len);
        }

        // =========================================================
        //  Load page-counts.json
        // =========================================================
        private void LoadPageCounts(string src)
        {
            _pageMap.Clear();
            _pageMapByName.Clear();
            try
            {
                string cand = Path.Combine(src, "page-counts.json");
                if (!File.Exists(cand)) cand = Path.Combine(AppContext.BaseDirectory, "page-counts.json");
                if (!File.Exists(cand)) return;

                string json = File.ReadAllText(cand);
                var obj = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (obj == null) return;

                foreach (var kv in obj)
                {
                    if (kv.Key == null) continue;
                    string rel = kv.Key.Replace('\\', '/').ToLowerInvariant();
                    _pageMap[rel] = kv.Value;
                    int slash = rel.LastIndexOf('/');
                    string name = (slash >= 0 ? rel.Substring(slash + 1) : rel);
                    _pageMapByName[name] = kv.Value;
                }
            }
            catch { }
        }

        private int PagesFor(FileInfo fi, string src)
        {
            try
            {
                string full = fi.FullName;
                string rel = full;
                if (src != null && full.Length > src.Length &&
                    full.StartsWith(src, StringComparison.OrdinalIgnoreCase) &&
                    (full[src.Length] == '\\' || full[src.Length] == '/'))
                {
                    rel = full.Substring(src.Length + 1);
                }
                rel = rel.Replace('\\', '/').ToLowerInvariant();
                if (_pageMap.TryGetValue(rel, out int p) && p > 0) return p;
            }
            catch { }
            if (_pageMapByName.TryGetValue(fi.Name.ToLowerInvariant(), out int p2) && p2 > 0) return p2;
            return 0;
        }

        // =========================================================
        //  Cap nhat trang thai phu (Predict / window / page)
        // =========================================================
        private void CheckProgressLine(string line)
        {
            if (_curBackend == "mineru4x" || _curBackend == "paddleocr" || _noProgressParse) return; // 4.x/paddle/verify khong co tien do theo trang
            // Bọc lock để tránh race khi nhiều file chạy song song (Đợt 2 - B3)
            lock (_progLock)
            {
                bool updated = false;

                if (line.Contains("Predict:") || line.Contains("window"))
                {
                    _lastProgress = line.Length > 120 ? line.Substring(0, 120) : line;
                    updated = true;
                }

                if (line.Contains("Predict") || line.Contains("window") || line.Contains("page") || line.Contains("%"))
                {
                    var matches = _rxPage.Matches(line);
                    if (matches.Count > 0)
                    {
                        var m = matches[matches.Count - 1];
                        if (int.TryParse(m.Groups[1].Value, out int cur) && int.TryParse(m.Groups[2].Value, out int tot))
                        {
                            if (tot > 0 && tot < 100000 && cur <= tot + 1)
                            {
                                _curPageInFile = Math.Min(cur, tot);
                                if (_curFilePages <= 0)
                                {
                                    _curFilePages = tot;
                                    _curFilePagesFromMineru = true;
                                }
                                updated = true;
                            }
                        }
                    }
                }

                if (updated) RefreshStatus();
            }
        }

        // Cập nhật thông tin file đang chạy (thread-safe)
        private void SetCurFile(string name, string full, int pages)
        {
            lock (_progLock)
            {
                _curFileName = name;
                _curFileFull = full;
                _curPageInFile = 0;
                _curFilePages = pages;
                _curFilePagesFromMineru = (pages <= 0);
            }
        }

        // =========================================================
        //  Ghi batch-state.json
        // =========================================================
        private void WriteState(string outdir, DateTime started, int total, int done, int skipped, int errors,
                                List<ErrorEntry> errList, string lastFile)
        {
            try
            {
                List<string> susp;
                lock (_suspLock) susp = new List<string>(_suspicious);
                List<ErrorEntry> errSnap;
                lock (_errLock) errSnap = new List<ErrorEntry>(errList);
                var state = new
                {
                    started = started.ToString("o"),
                    updated = DateTime.Now.ToString("o"),
                    total = total,
                    done = done,
                    skipped = skipped,
                    errors = errors,
                    pages_done = _pagesDone,
                    total_pages = _totalPages,
                    error_list = errSnap,
                    suspicious = susp,
                    last_file = lastFile,
                    elapsed_sec = (int)_sw.Elapsed.TotalSeconds
                };
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                // Ghi atomic (B4): ghi file tam roi rename de khong mat tien do neu mat dien giua chung
                string tmpPath = Path.Combine(outdir, "batch-state.json.tmp");
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                File.Move(tmpPath, Path.Combine(outdir, "batch-state.json"), true);
            }
            catch (Exception ex) { AppendLog("ERR ghi state: " + ex.Message); }
        }

        // =========================================================
        //  UI helpers
        // =========================================================
        private void RefreshStatus()
        {
            Dispatcher.InvokeAsync(() =>
            {
                int total = _stTotal;
                int done = _stDone, errors = _stErrors, skipped = _stSkipped;
                int processed = done + errors + skipped;
                int remainingFiles = (total > 0) ? Math.Max(0, total - processed) : 0;
                double elapsed = (_sw != null) ? _sw.Elapsed.TotalSeconds : 0;
                bool pageMode = _totalPages > 0 && !_forceFileMode;

                long pagesNow = 0;
                double pct = 0;
                if (pageMode)
                {
                    pagesNow = Math.Min(_pagesDone + _curPageInFile, _totalPages);
                    pct = _totalPages > 0 ? (pagesNow * 100.0 / _totalPages) : 0;
                }
                else
                {
                    pct = (total > 0) ? (processed * 100.0 / total) : 0;
                }
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;

                // Cac kho thong ke
                stDoneText.Text = done.ToString();
                stRemainText.Text = remainingFiles.ToString();
                stPagesText.Text = pageMode ? (pagesNow.ToString() + "/" + _totalPages.ToString()) : "—";
                stErrText.Text = errors.ToString();
                stSkipText.Text = skipped.ToString();
                int suspCount = 0;
                try { lock (_suspLock) suspCount = _suspicious.Count; } catch { }
                stSuspText.Text = suspCount.ToString();

                // File dang chay
                curFileText.Text = _curFileName;
                curFileText.ToolTip = _curFileFull;
                curPageText.Text = (pageMode && _curFilePages > 0) ? ("trang " + _curPageInFile + "/" + _curFilePages) : "";

                // Toc do / ETA
                string speedPart, etaPart;
                string elapsedPart = FmtLong((long)elapsed);

                if (pageMode)
                {
                    // Lay mau (datetime, pages) de tinh toc do theo cua so gan day
                    lock (_sampleLock)
                    {
                        if (_pageSamples.Count == 0 || (DateTime.Now - _pageSamples[_pageSamples.Count - 1].Key).TotalSeconds >= 2)
                            _pageSamples.Add(new KeyValuePair<DateTime, long>(DateTime.Now, pagesNow));
                        while (_pageSamples.Count > 2 && (DateTime.Now - _pageSamples[0].Key).TotalMinutes > 10)
                            _pageSamples.RemoveAt(0);
                    }

                    if (elapsed > 30 && pagesNow > 0)
                    {
                        long remainingPages = Math.Max(0, _totalPages - pagesNow);
                        // Toc do trung binh tu dau batch (tham khao)
                        double avgSpeed = pagesNow / (elapsed / 60.0);
                        // ETA dua vao toc do 10 phut gan day -> khong bi leo khi den doan file lon
                        double speed = avgSpeed;
                        string etaBase = "tổng";
                        DateTime winStart; long winPages;
                        lock (_sampleLock) { winStart = _pageSamples[0].Key; winPages = pagesNow - _pageSamples[0].Value; }
                        double winMin = (DateTime.Now - winStart).TotalMinutes;
                        if (winMin >= 1.0 && winPages > 0)
                        {
                            double recent = winPages / winMin;
                            if (recent > 0.01) { speed = recent; etaBase = "10 phút gần nhất"; }
                        }
                        etaPart = (remainingPages <= 0) ? "xong" : FmtEtaLong((long)(remainingPages * 60.0 / speed));
                        speedPart = speed.ToString("0.0") + " trang/phút (" + etaBase + ") · TB từ đầu: " + avgSpeed.ToString("0.0");
                    }
                    else
                    {
                        speedPart = "—";
                        etaPart = "—";
                    }
                }
                else
                {
                    if (total > 0 && processed > 0 && elapsed > 0)
                    {
                        double avg = elapsed / (double)(done + errors);
                        double etaSec = avg * remainingFiles;
                        etaPart = (remainingFiles <= 0) ? "xong" : FmtEtaLong((long)etaSec);
                        speedPart = "—";
                    }
                    else
                    {
                        speedPart = "—";
                        etaPart = "—";
                    }
                }

                etaText.Text = "Tốc độ: " + speedPart + " · Dự kiến còn lại: " + etaPart + " · Đã chạy: " + elapsedPart;
                pctText.Text = pct.ToString("0.0") + "%";
                progressBar.Value = pct;
            });
        }

        private void AppendLog(string line)
        {
            if (line == null) return;
            lock (_logLock)
            {
                _pendingLog.Add(line);
            }
        }

        private void FlushLog()
        {
            bool hasNew = false;
            List<string> flushed = null;
            lock (_logLock)
            {
                if (_pendingLog.Count > 0)
                {
                    flushed = new List<string>(_pendingLog);
                    foreach (var l in _pendingLog) _logLines.Add(l);
                    _pendingLog.Clear();
                    hasNew = true;
                }
            }
            if (!hasNew) return;

            // Ghi log ra file de theo doi unattended + chan doan loi
            if (_logFilePath != null && flushed != null)
            {
                try { File.AppendAllLines(_logFilePath, flushed, new UTF8Encoding(false)); } catch { }
            }

            if (_logLines.Count > MaxLogLines)
                _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);

            try { logBox.Text = string.Join(Environment.NewLine, _logLines); } catch { }
            // logBox co the chua render (tab Tien do chua mo) -> LineCount=0, ScrollToLine se throw
            try { if (_logLines.Count > 0 && logBox.LineCount > 0) logBox.ScrollToLine(Math.Min(_logLines.Count - 1, logBox.LineCount - 1)); } catch { }
        }

        private void SetRunning(bool running)
        {
            Dispatcher.InvokeAsync(() =>
            {
                srcBox.IsEnabled = !running;
                srcBtn.IsEnabled = !running;
                outBox.IsEnabled = !running;
                outBtn.IsEnabled = !running;
                engineBox.IsEnabled = !running;
                effortBox.IsEnabled = !running && IsEffortEnabledFor((engineBox.SelectedItem as ComboBoxItem)?.Tag as string);
                resumeChk.IsEnabled = !running;
                smallFirstChk.IsEnabled = !running;
                subDirsChk.IsEnabled = !running;
                retryChk.IsEnabled = !running;
                noSleepChk.IsEnabled = !running;
                shutdownChk.IsEnabled = !running;
                detailLogChk.IsEnabled = !running;
                cpuBox.IsEnabled = !running;
                apiModeChk.IsEnabled = !running;
                concBox.IsEnabled = !running;
                smartRoutingChk.IsEnabled = !running;
                crossCheckChk.IsEnabled = !running;
                joinLinesChk.IsEnabled = !running;
                squeezeBlanksChk.IsEnabled = !running;
                startBtn.IsEnabled = !running;
                stopBtn.IsEnabled = running;
                if (pauseBtn != null) pauseBtn.IsEnabled = running;
                if (stopBtn2 != null) stopBtn2.IsEnabled = running;
                if (!running)
                {
                    _paused = false;
                    if (pauseBtn != null) pauseBtn.Content = L("S_Pause");
                }
                try { installBtn.IsEnabled = !running && !_installing && HasInstallableMissing(); } catch { }

                // Khi dung xong: tinh lai trang thai Enable cua concBox theo backend/apiMode
                if (!running && apiModeChk != null && concBox != null)
                    SyncEffortState();

                // Flush nhat ky ngay de khong mat dong cuoi khi ket thuc/stop
                FlushLog();

                if (running)
                {
                    SetPill("running");
                    _tick.Start();
                    try { GotoTab("cardProgress"); } catch { }
                }
                else
                {
                    _tick.Stop();
                    SetPill(_cancelRequested ? "stopped" : "done");
                }
            });
        }

        private string _pillState = "ready";

        private void SetPill(string state)
        {
            try
            {
                _pillState = state;
                RefreshPillText();
            }
            catch { }
        }

        private void RefreshPillText()
        {
            try
            {
                if (pillBorder == null || pillText == null) return;
                string bgKey = "Theme.PillReadyBg", fgKey = "Theme.PillReadyFg", txtKey = "S_PillReady";
                if (_pillState == "running") { bgKey = "Theme.PillRunBg"; fgKey = "Theme.PillRunFg"; txtKey = "S_PillRunning"; }
                else if (_pillState == "paused") { bgKey = "Theme.PillStopBg"; fgKey = "Theme.PillStopFg"; txtKey = "S_StPaused"; }
                else if (_pillState == "stopped") { bgKey = "Theme.PillStopBg"; fgKey = "Theme.PillStopFg"; txtKey = "S_PillStopped"; }
                else if (_pillState == "done") { bgKey = "Theme.PillDoneBg"; fgKey = "Theme.PillDoneFg"; txtKey = "S_PillDone"; }
                pillBorder.Background = ThemeBrush(bgKey, "#E5E5E5");
                pillText.Foreground = ThemeBrush(fgKey, "#605E5C");
                pillText.Text = L(txtKey);
            }
            catch { }
        }

        private static string FmtLong(long sec)
        {
            if (sec < 0) sec = 0;
            long d = sec / 86400;
            long h = (sec % 86400) / 3600;
            long m = (sec % 3600) / 60;
            long s = sec % 60;
            if (d > 0) return d + " ngày " + h + " giờ " + m + " phút";
            if (h > 0) return h + " giờ " + m + " phút";
            if (m > 0) return m + " phút " + s + " giây";
            return s + " giây";
        }

        private static string FmtEtaLong(long sec)
        {
            if (sec <= 0 || !double.IsFinite(sec)) return "—";
            long d = sec / 86400;
            long h = (sec % 86400) / 3600;
            long m = (sec % 3600) / 60;
            if (d > 0) return d + " ngày " + h + " giờ";
            if (h > 0) return h + " giờ " + m + " phút";
            if (m > 0) return m + " phút";
            return "< 1 phút";
        }

        // =========================================================
        //  Dong cua so khi dang chay
        // =========================================================
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_running)
            {
                var r = MessageBox.Show(L("M_CloseConfirm"),
                                        L("M_TitleConfirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes)
                {
                    _closing = true;
                    _cancelRequested = true;
                    EmergencyKillAll();
                }
                else
                {
                    e.Cancel = true;
                }
            }
            try { if (!e.Cancel) DisposeTray(); } catch { }
            base.OnClosing(e);
        }

        // =========================================================
        //  Kiem tra he thong + tu cai dat (may moi / portable)
        // =========================================================
        private volatile bool _installing = false;
        private CancellationTokenSource _installCts;
        private List<SysItem> _lastSysItems = new List<SysItem>();

        private class SysItem
        {
            public string Name;
            public bool Ok;
            public string Detail;
            public Func<CancellationToken, Task<bool>> Install; // null = khong tu cai duoc
            public string NameKey;   // key ten da ngon ngu (S_SysN_*)
            public string PurpKey;   // key muc dich (S_SysP_*)
            public bool Required = true; // false = thieu thi vang (tuy chon), true = thieu thi do
        }

        private static (int exit, string output) RunProbe(string exe, string args, int timeoutSec)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return (-1, "");
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutSec * 1000))
                    {
                        try { p.Kill(true); } catch { }
                        return (-999, "timeout");
                    }
                    return (p.ExitCode, sb.ToString());
                }
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        private static string ParseVersion(string s)
        {
            try
            {
                var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
                if (m.Success) return m.Groups[1].Value + "." + m.Groups[2].Value + (m.Groups[3].Success ? "." + m.Groups[3].Value : "");
            }
            catch { }
            return "";
        }

        private static string FindPython()
        {
            try
            {
                var (e, o) = RunProbe("py", "-3 -c \"import sys;print(sys.executable)\"", 30);
                if (e == 0)
                {
                    string p = o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                    if (p.Length > 0 && File.Exists(p)) return p;
                }
            }
            catch { }
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';'))
                {
                    try
                    {
                        string cand = Path.Combine(dir.Trim(), "python.exe");
                        if (File.Exists(cand)) return cand;
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                var bases = new List<string>();
                try { bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python")); } catch { }
                bases.Add(@"C:\Python313"); bases.Add(@"C:\Python312"); bases.Add(@"C:\Python311");
                try { bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python313")); } catch { }
                foreach (var b in bases)
                {
                    try
                    {
                        if (!Directory.Exists(b)) continue;
                        foreach (var d in Directory.GetDirectories(b, "Python*"))
                        {
                            string cand = Path.Combine(d, "python.exe");
                            if (File.Exists(cand)) return cand;
                        }
                        string direct = Path.Combine(b, "python.exe");
                        if (File.Exists(direct)) return direct;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string VenvPython(string venvDir)
        {
            try
            {
                string p = Path.Combine(venvDir, "Scripts", "python.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        private List<SysItem> CollectSysChecks(string outDirPref)
        {
            var items = new List<SysItem>();
            string py = null;
            try { py = FindPython(); } catch { }

            // 1. Python
            if (py == null)
            {
                items.Add(new SysItem
                {
                    Name = "Python 3.10+",
                    NameKey = "S_SysN_Py",
                    PurpKey = "S_SysP_Py",
                    Required = true,
                    Ok = false,
                    Detail = L("S_SysD_PyNf"),
                    Install = ct =>
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = "https://www.python.org/downloads/", UseShellExecute = true }); } catch { }
                        AppendLog("[CAI] Hay cai Python 3.11+ (tick 'Add python.exe to PATH') roi bam Kiem tra lai. Hoac cmd: winget install Python.Python.3.13");
                        return Task.FromResult(true);
                    }
                });
            }
            else
            {
                string v = "";
                bool ok = false;
                try
                {
                    var (e, o) = RunProbe(py, "--version", 30);
                    v = ParseVersion(o);
                    var parts = v.Split('.');
                    ok = e == 0 && parts.Length >= 2 && int.Parse(parts[0]) == 3 && int.Parse(parts[1]) >= 10;
                }
                catch { }
                items.Add(new SysItem { Name = "Python 3.10+", NameKey = "S_SysN_Py", PurpKey = "S_SysP_Py", Required = true, Ok = ok, Detail = (ok ? v + " " + L("S_SysD_At").Trim() + " " : L("S_SysD_BadVer").Trim() + " ") + py });
            }

            // 2. pip
            if (py == null)
                items.Add(new SysItem { Name = "pip", NameKey = "S_SysN_Pip", PurpKey = "S_SysP_Pip", Required = true, Ok = false, Detail = L("S_SysD_NoPy") });
            else
            {
                try
                {
                    var (e, o) = RunProbe(py, "-m pip --version", 60);
                    string line = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                    items.Add(new SysItem { Name = "pip", NameKey = "S_SysN_Pip", PurpKey = "S_SysP_Pip", Required = true, Ok = e == 0, Detail = e == 0 ? line : L("S_SysD_Err1") });
                }
                catch (Exception ex) { items.Add(new SysItem { Name = "pip", NameKey = "S_SysN_Pip", PurpKey = "S_SysP_Pip", Required = true, Ok = false, Detail = L("S_SysD_Err").Trim() + " " + ex.Message }); }
            }

            // 3. MinerU 3.x
            string mineruExe = null;
            try { mineruExe = ResolveMineruPath(); } catch { }
            if (mineruExe == null)
            {
                string pyCap = py;
                items.Add(new SysItem
                {
                    Name = "MinerU 3.x (mineru.exe)",
                    NameKey = "S_SysN_Mineru",
                    PurpKey = "S_SysP_Mineru",
                    Required = true,
                    Ok = false,
                    Detail = L("S_SysD_Nf"),
                    Install = ct => InstallMinerUPipAsync(pyCap, ct)
                });
            }
            else
            {
                try
                {
                    var (e, o) = RunProbe(mineruExe, "--version", 60);
                    string line = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                    items.Add(new SysItem { Name = "MinerU 3.x (mineru.exe)", NameKey = "S_SysN_Mineru", PurpKey = "S_SysP_Mineru", Required = true, Ok = e == 0, Detail = (e == 0 ? line + " " : L("S_SysD_Err1") + " ") + L("S_SysD_At").Trim() + " " + mineruExe });
                }
                catch (Exception ex) { items.Add(new SysItem { Name = "MinerU 3.x (mineru.exe)", NameKey = "S_SysN_Mineru", PurpKey = "S_SysP_Mineru", Required = true, Ok = false, Detail = L("S_SysD_Err").Trim() + " " + ex.Message }); }
            }

            // 4. mineru.json (thieu thi lan chay dau se tu tai model — can mang)
            try
            {
                string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mineru.json");
                if (!File.Exists(cfg))
                {
                    items.Add(new SysItem
                    {
                        Name = "mineru.json",
                        NameKey = "S_SysN_Json",
                        PurpKey = "S_SysP_Json",
                        Required = true,
                        Ok = false,
                        Detail = L("S_SysD_JsonNf"),
                        Install = ct =>
                        {
                            try
                            {
                                File.WriteAllText(cfg, "{\n  \"model-source\": \"huggingface\"\n}\n", new UTF8Encoding(false));
                                AppendLog("[CAI] da tao mineru.json toi thieu.");
                                return Task.FromResult(true);
                            }
                            catch (Exception ex) { AppendLog("[CAI] loi tao mineru.json: " + ex.Message); return Task.FromResult(false); }
                        }
                    });
                }
                else
                {
                    string detail = L("S_SysD_Have");
                    bool ok = true;
                    try
                    {
                        string json = File.ReadAllText(cfg, Encoding.UTF8);
                        using (var doc = JsonDocument.Parse(json)) { }
                        try
                        {
                            string baseDir = ResolveMinerU4ModelBase();
                            detail += string.IsNullOrEmpty(baseDir) ? L("S_SysD_AutoDl").Trim() : L("S_SysD_Local").Trim() + " " + baseDir;
                        }
                        catch { }
                    }
                    catch (Exception ex) { ok = false; detail = L("S_SysD_BadJson").Trim() + " " + ex.Message; }
                    items.Add(new SysItem { Name = "mineru.json", NameKey = "S_SysN_Json", PurpKey = "S_SysP_Json", Required = true, Ok = ok, Detail = detail });
                }
            }
            catch (Exception ex) { items.Add(new SysItem { Name = "mineru.json", NameKey = "S_SysN_Json", PurpKey = "S_SysP_Json", Required = true, Ok = false, Detail = L("S_SysD_Err").Trim() + " " + ex.Message }); }

            // 5. GPU NVIDIA
            try
            {
                var (e, o) = RunProbe("nvidia-smi", "-L", 30);
                string line = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                items.Add(new SysItem
                {
                    Name = "GPU NVIDIA (driver)",
                    NameKey = "S_SysN_Gpu",
                    PurpKey = "S_SysP_Gpu",
                    Required = false,
                    Ok = e == 0,
                    Detail = e == 0 ? line : L("S_SysD_GpuNf")
                });
            }
            catch (Exception ex) { items.Add(new SysItem { Name = "GPU NVIDIA (driver)", NameKey = "S_SysN_Gpu", PurpKey = "S_SysP_Gpu", Required = false, Ok = false, Detail = L("S_SysD_Err").Trim() + " " + ex.Message }); }

            // 6. torch CUDA (env chinh)
            if (py == null)
                items.Add(new SysItem { Name = "torch CUDA (env chinh)", NameKey = "S_SysN_Torch", PurpKey = "S_SysP_Torch", Required = false, Ok = false, Detail = L("S_SysD_NoPy") });
            else
            {
                string pyCap6 = py;
                bool ok = false;
                string detail = "";
                try
                {
                    var (e, o) = RunProbe(py, "-c \"import torch;print(torch.__version__+'|'+str(torch.cuda.is_available()))\"", 120);
                    string line = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                    var parts = line.Split('|');
                    if (e == 0 && parts.Length == 2)
                    {
                        ok = parts[1].Trim() == "True";
                        detail = "torch " + parts[0].Trim() + ", CUDA=" + parts[1].Trim();
                    }
                    else detail = L("S_SysD_TorchFail");
                }
                catch (Exception ex) { detail = L("S_SysD_Err").Trim() + " " + ex.Message; }
                items.Add(new SysItem
                {
                    Name = "torch CUDA (env chinh)",
                    NameKey = "S_SysN_Torch",
                    PurpKey = "S_SysP_Torch",
                    Required = false,
                    Ok = ok,
                    Detail = detail,
                    Install = ok ? null : (Func<CancellationToken, Task<bool>>)(ct => RunPipAsync(pyCap6, "install --timeout 120 --retries 5 torch torchvision", ct))
                });
            }

            // 7. PaddleOCR (venv paddle-env)
            string px = null;
            try { px = ResolvePaddleExe(); } catch { }
            if (px == null)
            {
                items.Add(new SysItem
                {
                    Name = "PaddleOCR (paddlex.exe)",
                    NameKey = "S_SysN_Paddle",
                    PurpKey = "S_SysP_Paddle",
                    Required = false,
                    Ok = false,
                    Detail = L("S_SysD_PaddleNf"),
                    Install = ct => InstallPaddleVenvAsync(ct)
                });
            }
            else
            {
                bool ok = false;
                string detail = "";
                try
                {
                    string vpy = null;
                    try { vpy = Path.Combine(Path.GetDirectoryName(px) ?? "", "python.exe"); } catch { }
                    if (vpy != null && File.Exists(vpy))
                    {
                        var (e, o) = RunProbe(vpy, "-c \"import paddle;print(paddle.__version__+'|'+str(paddle.device.is_compiled_with_cuda()))\"", 120);
                        string line = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                        var parts = line.Split('|');
                        if (e == 0 && parts.Length == 2)
                        {
                            ok = parts[1].Trim() == "True";
                            detail = "paddle " + parts[0].Trim() + ", CUDA=" + parts[1].Trim() + " " + L("S_SysD_At").Trim() + " " + px;
                        }
                        else detail = L("S_SysD_PaddleFail").Trim() + " " + px;
                    }
                    else detail = L("S_SysD_NoVenv").Trim() + " " + px;
                }
                catch (Exception ex) { detail = L("S_SysD_Err").Trim() + " " + ex.Message; }
                items.Add(new SysItem
                {
                    Name = "PaddleOCR (paddlex.exe)",
                    NameKey = "S_SysN_Paddle",
                    PurpKey = "S_SysP_Paddle",
                    Required = false,
                    Ok = ok,
                    Detail = detail,
                    Install = ok ? null : (Func<CancellationToken, Task<bool>>)(ct => InstallPaddleVenvAsync(ct))
                });
            }

            // 8. MinerU 4.x (venv mineru4x-venv)
            string kx = null;
            try { kx = ResolveMinerU4Kit(); } catch { }
            if (kx == null)
            {
                items.Add(new SysItem
                {
                    Name = "MinerU 4.x (mineru-kit.exe)",
                    NameKey = "S_SysN_Kit",
                    PurpKey = "S_SysP_Kit",
                    Required = false,
                    Ok = false,
                    Detail = L("S_SysD_KitNf"),
                    Install = ct => InstallMinerU4xVenvAsync(ct)
                });
            }
            else
            {
                bool ok = false;
                string detail = "";
                try
                {
                    string vpy = null;
                    try { vpy = Path.Combine(Path.GetDirectoryName(kx) ?? "", "python.exe"); } catch { }
                    if (vpy != null && File.Exists(vpy))
                    {
                        var (e, o) = RunProbe(vpy, "-c \"import torch;print(torch.__version__+'|'+str(torch.cuda.is_available()))\"", 120);
                        string line = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                        var parts = line.Split('|');
                        if (e == 0 && parts.Length == 2)
                        {
                            ok = parts[1].Trim() == "True";
                            detail = "torch " + parts[0].Trim() + ", CUDA=" + parts[1].Trim() + " " + L("S_SysD_At").Trim() + " " + kx;
                        }
                        else detail = L("S_SysD_TorchFailAt").Trim() + " " + kx;
                    }
                    else detail = L("S_SysD_NoVenv").Trim() + " " + kx;
                }
                catch (Exception ex) { detail = L("S_SysD_Err").Trim() + " " + ex.Message; }
                items.Add(new SysItem
                {
                    Name = "MinerU 4.x (mineru-kit.exe)",
                    NameKey = "S_SysN_Kit",
                    PurpKey = "S_SysP_Kit",
                    Required = false,
                    Ok = ok,
                    Detail = detail,
                    Install = ok ? null : (Func<CancellationToken, Task<bool>>)(ct => InstallMinerU4xVenvAsync(ct))
                });
            }

            // 8b. Windows OCR (WinRT, khong can cai dat)
            try
            {
                var wlangs = OcrEngine.AvailableRecognizerLanguages;
                int wcount = 0;
                string wfirst = "";
                try
                {
                    foreach (var wl in wlangs)
                    {
                        wcount++;
                        if (wfirst.Length == 0) { try { wfirst = wl.LanguageTag; } catch { } }
                    }
                }
                catch { }
                items.Add(new SysItem
                {
                    Name = "Windows OCR (WinRT)",
                    NameKey = "S_SysN_WinOcr",
                    PurpKey = "S_SysP_WinOcr",
                    Required = false,
                    Ok = wcount > 0,
                    Detail = wcount > 0 ? (wcount + " " + (wcount == 1 ? L("S_SysD_WinLang1") : L("S_SysD_WinLangs")).Trim() + " " + wfirst) : L("S_SysD_WinNf")
                });
            }
            catch (Exception ex) { items.Add(new SysItem { Name = "Windows OCR (WinRT)", NameKey = "S_SysN_WinOcr", PurpKey = "S_SysP_WinOcr", Required = false, Ok = false, Detail = L("S_SysD_Err").Trim() + " " + ex.Message }); }

            // 9. Dia trong (o dia thu muc ket qua) — dir truyen tu UI thread
            try
            {
                string dir = outDirPref ?? "";
                if (dir.Length == 0) dir = AppContext.BaseDirectory;
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                var drv = new DriveInfo(root);
                double gb = drv.AvailableFreeSpace / 1073741824.0;
                items.Add(new SysItem { Name = "Dia trong (" + root + ")", NameKey = "S_SysN_Disk", PurpKey = "S_SysP_Disk", Required = true, Ok = gb >= 10, Detail = L("S_SysD_Free").Trim() + " " + gb.ToString("0.0") + " GB" });
            }
            catch (Exception ex) { items.Add(new SysItem { Name = "Dia trong", NameKey = "S_SysN_Disk", PurpKey = "S_SysP_Disk", Required = true, Ok = true, Detail = L("S_SysD_Unknown").Trim() + " " + ex.Message }); }

            return items;
        }

        private async void CheckBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            try { if (sysSummary != null) sysSummary.Text = L("S_SysChecking"); } catch { }
            try { if (sysList != null) sysList.ItemsSource = null; } catch { }
            string outPref = "";
            try { if (outBox != null) outPref = outBox.Text.Trim(); } catch { }
            List<SysItem> items = await Task.Run(() =>
            {
                try { return CollectSysChecks(outPref); }
                catch (Exception ex) { return new List<SysItem> { new SysItem { Name = "Kiem tra", Required = true, Ok = false, Detail = "loi: " + ex.Message } }; }
            });
            try
            {
                _lastSysItems = items;
                RefreshSysList();
                installBtn.IsEnabled = !_running && !_installing && HasInstallableMissing();
                AppendLog("[SYS] kiem tra xong: " + items.Count + " muc.");
            }
            catch { }
        }

        private void RefreshSysList()
        {
            try
            {
                var items = _lastSysItems ?? new System.Collections.Generic.List<SysItem>();
                var rows = new System.Collections.Generic.List<object>();
                int reqMiss = 0, optMiss = 0, canFix = 0;
                bool dark = _theme == "dark";
                foreach (var it in items)
                {
                    string nm = !string.IsNullOrEmpty(it.NameKey) ? L(it.NameKey) : it.Name;
                    if (nm == it.NameKey) nm = it.Name;
                    string pu = !string.IsNullOrEmpty(it.PurpKey) ? L(it.PurpKey) : "";
                    if (pu == it.PurpKey) pu = "";
                    string tech = it.Detail ?? "";
                    string mau, tt;
                    if (it.Ok) { mau = dark ? "#4ADE80" : "#107C10"; tt = "✓ OK"; }
                    else if (it.Required) { mau = dark ? "#F87171" : "#D13438"; tt = "✗ " + L("S_SysReq"); reqMiss++; }
                    else { mau = dark ? "#FBBF24" : "#B45309"; tt = "! " + L("S_SysOpt"); optMiss++; }
                    if (!it.Ok && it.Install != null) canFix++;
                    rows.Add(new
                    {
                        TT = tt,
                        Mau = mau,
                        Muc = nm,
                        ChiTiet = (pu.Length > 0 ? pu + " — " : "") + tech,
                        CoTheCai = !it.Ok && it.Install != null && !_installing && !_running,
                        Item = it
                    });
                }
                if (sysList != null) sysList.ItemsSource = rows;
                if (sysSummary != null)
                    sysSummary.Text = items.Count + " " + L("S_SysSumItems") + " — " + L("S_SysReqMiss") + ": " + reqMiss + ", " + L("S_SysOptMiss") + ": " + optMiss + " (" + L("S_InstallOne") + ": " + canFix + ").";
            }
            catch { }
        }

        private async void SysInstallOne_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_installing || _running) return;
                var item = (sender as Button)?.Tag as SysItem;
                if (item == null || item.Ok || item.Install == null) return;
                _installing = true;
                try { installBtn.IsEnabled = false; } catch { }
                RefreshSysList();
                AppendLog("[SYS] dang cai: " + item.Name + " ...");
                bool ok = false;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
                        ok = await Task.Run(() => item.Install(cts.Token));
                }
                catch (Exception ex) { AppendLog("[SYS] loi cai " + item.Name + ": " + ex.Message); }
                item.Ok = ok;
                if (ok) { try { item.Detail = L("S_SysD_Installed"); } catch { } }
                AppendLog("[SYS] " + (ok ? "cai XONG: " : "cai THAT BAI: ") + item.Name);
                _installing = false;
                RefreshSysList();
                try { installBtn.IsEnabled = HasInstallableMissing(); } catch { }
            }
            catch { try { _installing = false; } catch { } }
        }

        private bool HasInstallableMissing()
        {
            try
            {
                var list = _lastSysItems;
                if (list == null) return false;
                foreach (var it in list)
                    if (!it.Ok && it.Install != null) return true;
            }
            catch { }
            return false;
        }

        private async void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _installing) return;
            var targets = new List<SysItem>();
            try
            {
                foreach (var it in (_lastSysItems ?? new List<SysItem>()))
                    if (!it.Ok && it.Install != null) targets.Add(it);
            }
            catch { }
            if (targets.Count == 0) { AppendLog("[CAI] khong co muc nao can cai (bam Kiem tra truoc)."); return; }
            _installing = true;
            installBtn.IsEnabled = false;
            try { startBtn.IsEnabled = false; } catch { }
            _installCts = new CancellationTokenSource();
            try
            {
                AppendLog("[CAI] bat dau cai " + targets.Count + " muc (co the mat 10-20 phut, can mang)...");
                foreach (var t in targets)
                {
                    if (_installCts.IsCancellationRequested) { AppendLog("[CAI] da huy."); break; }
                    AppendLog("[CAI] " + t.Name + "...");
                    bool ok = false;
                    try { ok = await Task.Run(async () => await t.Install(_installCts.Token)); }
                    catch (Exception ex) { AppendLog("[CAI] loi: " + ex.Message); }
                    t.Ok = ok;
                    if (ok) { try { t.Detail = L("S_SysD_Installed"); } catch { } }
                    AppendLog(ok ? ("[CAI] xong: " + t.Name) : ("[CAI] THAT BAI: " + t.Name + " — xem log tren."));
                }
                AppendLog("[CAI] hoan tat.");
                RefreshSysList();
            }
            finally
            {
                try { if (_installCts != null) _installCts.Dispose(); } catch { }
                _installCts = null;
                _installing = false;
                try { startBtn.IsEnabled = !_running; } catch { }
                try { installBtn.IsEnabled = false; } catch { }
            }
        }

        private async Task<bool> RunPipAsync(string pyExe, string args, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(pyExe) || !File.Exists(pyExe)) { AppendLog("[pip] khong tim thay python."); return false; }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    Arguments = "-m pip " + args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLog("[pip] " + e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog("[pip] " + e.Data); };
                    proc.Start();
                    try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    try { await proc.WaitForExitAsync(ct); }
                    catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return false; }
                    return proc.ExitCode == 0 && !ct.IsCancellationRequested;
                }
            }
            catch (Exception ex) { AppendLog("[pip] loi: " + ex.Message); return false; }
        }

        private async Task<bool> InstallMinerUPipAsync(string pyExe, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(pyExe)) { AppendLog("[CAI] thieu Python — cai Python truoc."); return false; }
            return await RunPipAsync(pyExe, "install --timeout 90 --retries 5 \"mineru[core]==3.4.5\"", ct);
        }

        private async Task<bool> InstallPaddleVenvAsync(CancellationToken ct)
        {
            string py = null;
            try { py = FindPython(); } catch { }
            if (py == null) { AppendLog("[CAI] thieu Python — cai Python truoc."); return false; }
            string venv = Path.Combine(AppContext.BaseDirectory, "paddle-env");
            string venvPy = Path.Combine(venv, "Scripts", "python.exe");
            if (!File.Exists(venvPy))
            {
                AppendLog("[CAI] tao venv paddle-env...");
                var (e0, o0) = await Task.Run(() => RunProbe(py, "-m venv \"" + venv + "\"", 300));
                if (!File.Exists(venvPy)) { AppendLog("[CAI] tao venv that bai: " + (o0 ?? "").Trim()); return false; }
            }
            if (!await RunPipAsync(venvPy, "install --timeout 90 --retries 5 paddlepaddle-gpu==3.3.1 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            if (!await RunPipAsync(venvPy, "install --timeout 90 --retries 5 \"paddleocr==3.7.0\"", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            if (!await RunPipAsync(venvPy, "install --timeout 90 --retries 5 \"paddlex[ocr]==3.7.2\"", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            if (!await RunPipAsync(venvPy, "install --timeout 90 --retries 5 python-docx", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            try
            {
                string px = Path.Combine(venv, "Scripts", "paddlex.exe");
                if (File.Exists(px))
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "paddle-path.txt"), px, new UTF8Encoding(false));
                    AppendLog("[CAI] da ghi paddle-path.txt.");
                    return true;
                }
                AppendLog("[CAI] khong thay paddlex.exe sau khi cai.");
                return false;
            }
            catch (Exception ex) { AppendLog("[CAI] loi: " + ex.Message); return false; }
        }

        private async Task<bool> InstallMinerU4xVenvAsync(CancellationToken ct)
        {
            string py = null;
            try { py = FindPython(); } catch { }
            if (py == null) { AppendLog("[CAI] thieu Python — cai Python truoc."); return false; }
            string venv = Path.Combine(AppContext.BaseDirectory, "mineru4x-venv");
            string venvPy = Path.Combine(venv, "Scripts", "python.exe");
            if (!File.Exists(venvPy))
            {
                AppendLog("[CAI] tao venv mineru4x-venv...");
                var (e0, o0) = await Task.Run(() => RunProbe(py, "-m venv \"" + venv + "\"", 300));
                if (!File.Exists(venvPy)) { AppendLog("[CAI] tao venv that bai: " + (o0 ?? "").Trim()); return false; }
            }
            if (!await RunPipAsync(venvPy, "install --timeout 120 --retries 5 torch torchvision --index-url https://download.pytorch.org/whl/cu130", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            if (!await RunPipAsync(venvPy, "install --timeout 90 --retries 5 \"mineru==4.0.0a6\"", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            if (!await RunPipAsync(venvPy, "install --timeout 90 --retries 5 \"mineru[torch]==4.0.0a6\"", ct)) return false;
            if (ct.IsCancellationRequested) return false;
            try
            {
                string kx = Path.Combine(venv, "Scripts", "mineru-kit.exe");
                if (File.Exists(kx))
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mineru4x-path.txt"), kx, new UTF8Encoding(false));
                    AppendLog("[CAI] da ghi mineru4x-path.txt.");
                    return true;
                }
                AppendLog("[CAI] khong thay mineru-kit.exe sau khi cai.");
                return false;
            }
            catch (Exception ex) { AppendLog("[CAI] loi: " + ex.Message); return false; }
        }

        // =========================================================
        //  Auto start
        // =========================================================
        public void TryAutoStart()
        {
            if (!_running) _ = RunBatchAsync();
        }
    }

    public class ErrorEntry
    {
        public string file { get; set; }
        public int exit { get; set; }
        public string type { get; set; }
    }

    // Kết quả chạy 1 file (Đợt 2 - B3/A4/C2)
    public class FileResult
    {
        public string file;
        public string status;   // OK / LOI / SKIP / KHA NGHI
        public int pages;
        public double seconds;
        public string note;
    }

    // Bản ghi cho báo cáo HTML (Đợt 2 - C2)
    public class ReportRecord
    {
        public string file;
        public string status;   // OK / LOI / SKIP / KHA NGHI
        public int pages;
        public double seconds;
        public string note;
        public string engine;
        public string type;     // LOI: TIMEOUT / OOM / PDF / RUN / EXC
    }

    // Ngữ cảnh chung của batch (truyền vào RunFileAsync)
    public class BatchCtx
    {
        public string outdir, backend, effort, url, src;
        public bool isHybrid, resume;
        public Dictionary<string, string> outMap;
        public Dictionary<string, string> backendMap; // smart routing: fullpath -> backend
        public string fastBe, ocrBe;                  // pool smart routing
        public string paddleExe, kitExe;              // exe da resolve
        public bool crossCheck;
        public string apiUrl;
        public int conc;
        public string exePath;
    }
}
