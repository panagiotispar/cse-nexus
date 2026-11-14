using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;

namespace StudyHub.Desktop
{
    public partial class MainWindow : Window
    {
        // ==========================================
        // SYSTEM PATHS & FLAGS
        // ==========================================
        private string MemoryFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "MemoryBuffer.txt");
        private string VaultFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "vault.dat");
        private string DataFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "my_grades.json");

        private bool _hasSavedCredentials = false;
        private string _savedUser = "";
        private string _savedPass = "";
        private bool _hasAutoExtracted = false;

        private bool isBaseCampLoaded = false;
        private bool isAnalyticsLoaded = false;
        private bool isSpaceLoaded = false;
        private bool isSimulatorLoaded = false;
        private bool isCoopLoaded = false;
        private bool isHubLoaded = false;
        private bool isScheduleLoaded = false;

        // ==========================================
        // LIVE SCHEDULES (crawler προγράμματος ΤΜΗΥΠ)
        // ==========================================
        public class ScheduleDoc
        {
            public string Kind { get; set; } = "";      // lectures | labs | exams
            public string Title { get; set; } = "";     // όπως το γράφει η σελίδα
            public string Url { get; set; } = "";
            public string FileName { get; set; } = "";
            public string Hash { get; set; } = "";
            public string ChangedAt { get; set; } = ""; // πότε άλλαξε τελευταία φορά το περιεχόμενο
        }

        public class ScheduleMeta
        {
            public string SourcePage { get; set; } = "";
            public string CheckedAt { get; set; } = "";
            public List<ScheduleDoc> Docs { get; set; } = new List<ScheduleDoc>();
        }

        // Μόνο αυτές οι δύο διευθύνσεις είναι σταθερές. Τα PDF ανακαλύπτονται δυναμικά.
        private const string ScheduleHubUrl = "https://www.cse.uoi.gr/programmata-didaskalias/";
        private const string SiteHomeUrl = "https://www.cse.uoi.gr/";

        private static readonly System.Net.Http.HttpClient _http = CreateHttp();
        private static System.Net.Http.HttpClient CreateHttp()
        {
            var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) UoiStudyHub/1.0");
            return h;
        }

        private ScheduleMeta _schedMeta = new ScheduleMeta();
        private string _schedCurrentKind = "lectures";
        private bool _schedBusy = false;

        private string ScheduleDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "schedules");
        private string ScheduleMetaPath => Path.Combine(ScheduleDir, "meta.json");

        private static string KindLabel(string kind) =>
            kind == "labs" ? "Εργαστήρια" : kind == "exams" ? "Εξεταστική" : "Μαθήματα";

        // Ο τύπος του PDF βγαίνει από το κείμενο του λινκ (χωρίς τόνους, κεφαλαία)
        private static string KindOf(string text)
        {
            string t = NormalizeKey(text);
            if (t.Contains("ΕΡΓΑΣΤΗΡ")) return "labs";
            if (t.Contains("ΕΞΕΤΑΣΤΙΚ")) return "exams";
            if (t.Contains("ΜΑΘΗΜΑΤ")) return "lectures";
            return null;
        }

        private ScheduleMeta LoadScheduleMeta()
        {
            try
            {
                if (File.Exists(ScheduleMetaPath))
                    return JsonSerializer.Deserialize<ScheduleMeta>(File.ReadAllText(ScheduleMetaPath)) ?? new ScheduleMeta();
            }
            catch (Exception ex) { LogError("LoadScheduleMeta", ex); }
            return new ScheduleMeta();
        }

        private void SaveScheduleMeta(ScheduleMeta m)
        {
            Directory.CreateDirectory(ScheduleDir);
            File.WriteAllText(ScheduleMetaPath, JsonSerializer.Serialize(m));
        }

        // Διαβάζει μια σελίδα και επιστρέφει τα PDF προγραμμάτων των ΠΡΟΠΤΥΧΙΑΚΩΝ
        private async Task<List<(string Kind, string Title, string Url)>> FindScheduleLinksAsync(string pageUrl, bool strict)
        {
            var result = new List<(string Kind, string Title, string Url)>();

            string html = await _http.GetStringAsync(pageUrl);
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            // Επικεφαλίδες και links με τη σειρά που εμφανίζονται στη σελίδα
            var nodes = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6|//a[@href]");
            if (nodes == null) return result;

            var baseUri = new Uri(pageUrl);
            string section = "";

            foreach (var n in nodes)
            {
                if (n.Name.Length == 2 && n.Name[0] == 'h')
                {
                    string h = NormalizeKey(HtmlAgilityPack.HtmlEntity.DeEntitize(n.InnerText));
                    if (h.Contains("ΜΕΤΑΠΤΥΧΙΑΚ")) section = "post";
                    else if (h.Contains("ΠΡΟΠΤΥΧΙΑΚ")) section = "under";
                    continue;
                }

                if (strict && section != "under") continue;

                string href = n.GetAttributeValue("href", "");
                if (!Uri.TryCreate(baseUri, href, out Uri abs)) continue;
                if (!abs.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

                string title = System.Text.RegularExpressions.Regex.Replace(HtmlAgilityPack.HtmlEntity.DeEntitize(n.InnerText), @"\s+", " ").Trim();
                string decodedPath = Uri.UnescapeDataString(abs.AbsolutePath);

                // Ποτέ μεταπτυχιακά
                if (NormalizeKey(title).Contains("ΜΕΤΑΠΤΥΧΙΑΚ") || NormalizeKey(decodedPath).Contains("ΜΕΤΑΠΤΥΧΙΑΚ")) continue;

                string kind = KindOf(title) ?? KindOf(decodedPath);
                if (kind == null) continue;
                if (result.Any(r => r.Kind == kind)) continue;   // κρατάμε το πρώτο (πιο πρόσφατο) ανά τύπο

                result.Add((kind, title, abs.AbsoluteUri));
            }
            return result;
        }

        // Βρίσκει τα PDF: πρώτα στη σελίδα "Προγράμματα Διδασκαλίας", αλλιώς ψάχνει το σωστό λινκ στην αρχική
        private async Task<(string Page, List<(string Kind, string Title, string Url)> Links)> DiscoverSchedulesAsync()
        {
            // 1) Η σταθερή σελίδα του Τμήματος
            try
            {
                var links = await FindScheduleLinksAsync(ScheduleHubUrl, true);
                if (links.Count == 0) links = await FindScheduleLinksAsync(ScheduleHubUrl, false);
                if (links.Count > 0) return (ScheduleHubUrl, links);
            }
            catch (Exception ex) { LogError("Schedule: κύρια σελίδα", ex); }

            // 2) Εφεδρικά: το URL της σελίδας μπορεί να άλλαξε, οπότε ψάχνουμε το λινκ στο μενού της αρχικής
            try
            {
                string home = await _http.GetStringAsync(SiteHomeUrl);
                var d = new HtmlAgilityPack.HtmlDocument();
                d.LoadHtml(home);
                var baseUri = new Uri(SiteHomeUrl);

                var candidates = (d.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlAgilityPack.HtmlNodeCollection(null))
                    .Select(a => new
                    {
                        Text = NormalizeKey(HtmlAgilityPack.HtmlEntity.DeEntitize(a.InnerText)),
                        Href = a.GetAttributeValue("href", "")
                    })
                    .Where(x => x.Text.Contains("ΠΡΟΓΡΑΜΜΑ") && x.Text.Contains("ΜΑΘΗΜΑΤ") && !x.Href.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    .Select(x => Uri.TryCreate(baseUri, x.Href, out Uri u) ? u.AbsoluteUri : null)
                    .Where(u => u != null && u != ScheduleHubUrl)
                    .Distinct()
                    .Take(3)
                    .ToList();

                foreach (var url in candidates)
                {
                    var links = await FindScheduleLinksAsync(url, true);
                    if (links.Count == 0) links = await FindScheduleLinksAsync(url, false);
                    if (links.Count > 0) return (url, links);
                }
            }
            catch (Exception ex) { LogError("Schedule: εφεδρική αναζήτηση", ex); }

            return (ScheduleHubUrl, new List<(string Kind, string Title, string Url)>());
        }

        // Κατεβάζει τα PDF και ανιχνεύει αλλαγές
        private async Task<ScheduleMeta> SyncSchedulesAsync()
        {
            Directory.CreateDirectory(ScheduleDir);
            var meta = LoadScheduleMeta();

            var found = await DiscoverSchedulesAsync();
            if (found.Links.Count == 0) throw new Exception("Δεν βρέθηκαν PDF προγράμματος στο site του Τμήματος.");

            string now = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            int ok = 0;

            foreach (var l in found.Links)
            {
                try
                {
                    byte[] bytes = await _http.GetByteArrayAsync(l.Url);
                    if (bytes.Length < 1000) continue;   // δεν είναι έγκυρο PDF

                    string hash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");

                    // Το όνομα αρχείου έχει το hash: έτσι δεν "κλειδώνει" ένα PDF που βλέπεις ήδη
                    string file = $"{l.Kind}_{hash.Substring(0, 8)}.pdf";
                    string path = Path.Combine(ScheduleDir, file);

                    var old = meta.Docs.FirstOrDefault(d => d.Kind == l.Kind);
                    bool changed = old == null || old.Hash != hash || !File.Exists(path);
                    if (changed) File.WriteAllBytes(path, bytes);

                    // Καθαρισμός παλιών εκδόσεων του ίδιου τύπου
                    foreach (var f in Directory.GetFiles(ScheduleDir, l.Kind + "_*.pdf"))
                        if (!string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase))
                            try { File.Delete(f); } catch { }

                    meta.Docs.RemoveAll(d => d.Kind == l.Kind);
                    meta.Docs.Add(new ScheduleDoc
                    {
                        Kind = l.Kind,
                        Title = l.Title,
                        Url = l.Url,
                        FileName = file,
                        Hash = hash,
                        ChangedAt = changed ? now : old.ChangedAt
                    });
                    ok++;
                }
                catch (Exception ex) { LogError("Schedule download " + l.Kind, ex); }
            }

            if (ok == 0) throw new Exception("Βρέθηκαν σύνδεσμοι, αλλά η λήψη των PDF απέτυχε.");

            meta.SourcePage = found.Page;
            meta.CheckedAt = now;
            SaveScheduleMeta(meta);
            return meta;
        }

        private bool ScheduleIsStale()
        {
            if (_schedMeta.Docs.Count == 0) return true;
            if (!DateTime.TryParseExact(_schedMeta.CheckedAt, "dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime t)) return true;
            return (DateTime.Now - t).TotalMinutes > 60;
        }

        // ----- UI -----
        private async Task LoadSchedule()
        {
            try
            {
                await ScheduleBrowser.EnsureCoreWebView2Async(await GetEnvAsync());

                // 1) Αμέσως: ό,τι έχουμε ήδη στον δίσκο (δουλεύει και χωρίς internet)
                _schedMeta = LoadScheduleMeta();
                UpdateScheduleUi();
                ShowScheduleView();

                // 2) Έλεγχος για νεότερη έκδοση, μόνο αν έχει περάσει πάνω από 1 ώρα
                if (ScheduleIsStale()) await RefreshSchedulesAsync();
            }
            catch (Exception ex) { ShowErrorPage(ScheduleBrowser, "Schedule", ex); }
        }

        private async Task RefreshSchedulesAsync()
        {
            if (_schedBusy) return;
            _schedBusy = true;
            ScheduleStatus.Text = "🔄 Έλεγχος για νέα έκδοση στο site του Τμήματος...";

            try
            {
                var before = _schedMeta.Docs.ToDictionary(d => d.Kind, d => d.Hash);
                _schedMeta = await SyncSchedulesAsync();
                UpdateScheduleUi();

                var changed = _schedMeta.Docs.Where(d => !before.ContainsKey(d.Kind) || before[d.Kind] != d.Hash).ToList();
                if (changed.Count > 0)
                {
                    ShowScheduleView();
                    ScheduleStatus.Text += "  ·  🆕 Νέα έκδοση: " + string.Join(", ", changed.Select(d => KindLabel(d.Kind)));
                }
            }
            catch (Exception ex)
            {
                LogError("RefreshSchedules", ex);
                ScheduleStatus.Text = _schedMeta.Docs.Count > 0
                    ? "⚠️ Αδυναμία ελέγχου (offline;). Εμφανίζεται η τελευταία αποθηκευμένη έκδοση."
                    : "⚠️ " + ex.Message;
            }
            finally { _schedBusy = false; }
        }

        private void UpdateScheduleUi()
        {
            var cur = _schedMeta.Docs.FirstOrDefault(d => d.Kind == _schedCurrentKind);
            ScheduleTitle.Text = cur != null ? "📅 " + cur.Title : "📅 Live Πρόγραμμα Τμήματος";

            string checkedAt = string.IsNullOrEmpty(_schedMeta.CheckedAt) ? "-" : _schedMeta.CheckedAt;
            ScheduleStatus.Text = cur == null
                ? "Δεν έχει βρεθεί ακόμα αρχείο."
                : $"Τελευταίος έλεγχος: {checkedAt}  ·  Η έκδοση αυτή εντοπίστηκε: {cur.ChangedAt}";

            HighlightScheduleButtons();
        }

        private void HighlightScheduleButtons()
        {
            void Mark(Border b, string kind)
            {
                bool on = _schedCurrentKind == kind;
                b.Background = on ? Br("#f1c40f") : Br("#111b24");
                b.BorderBrush = on ? Br("#f1c40f") : Br("#2c3e50");
                if (b.Child is TextBlock t) t.Foreground = on ? Brushes.Black : Br("#bdc3c7");
            }
            Mark(BtnSchedLectures, "lectures");
            Mark(BtnSchedLabs, "labs");
            Mark(BtnSchedExams, "exams");
        }

        private void ShowSchedulePdf(string kind)
        {
            _schedCurrentKind = kind;
            if (ScheduleBrowser.CoreWebView2 == null) return;

            var doc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == kind);
            string path = doc == null ? null : Path.Combine(ScheduleDir, doc.FileName);

            if (path != null && File.Exists(path))
                ScheduleBrowser.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
            else
                ScheduleBrowser.NavigateToString("<html><body style='background:#05070a;color:#7f8c8d;font-family:Consolas,monospace;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;'>Δεν υπάρχει διαθέσιμο αρχείο για αυτή την κατηγορία ακόμα.</body></html>");
        }

        private void SchedKind_Click(object sender, MouseButtonEventArgs e)
        {
            _schedCurrentKind = (string)((FrameworkElement)sender).Tag;
            ShowScheduleView();
            UpdateScheduleUi();
        }

        private async void SchedRefresh_Click(object sender, MouseButtonEventArgs e) => await RefreshSchedulesAsync();

        private void SchedExternal_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var doc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == _schedCurrentKind);
                if (doc == null) return;
                string path = Path.Combine(ScheduleDir, doc.FileName);
                if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { LogError("SchedExternal", ex); }
        }

        public MainWindow()
        {
            EnsureStartupRequirements();
            LoadSave();                 // ΠΡΙΝ από το InitializeComponent
            InitializeComponent();
            LoadVault();
            ApplyThemeResources();
            RebuildThemeSwatches();
            RestoreUiState();
            InitWindowControls();
            InitUpdates();
            InitFocus();
            WireFullscreenHotkeys();    // ΠΡΙΝ από την InitializeBrowser
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            ApplyFullscreen(_save.Fullscreen);
            InitializeBrowser();
            UpdateHud();
            UpdateTimeMachineUi();
            _stateReady = true;
        }

        private Task<CoreWebView2Environment> _envTask;

        private Task<CoreWebView2Environment> GetEnvAsync() =>
            _envTask ??= CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub"));

        // ==========================================
        // BOOT SEQUENCE & BROWSERS INITIALIZATION
        // ==========================================
        private async void InitializeBrowser()
        {
            var env = await GetEnvAsync();

            // Αρχικοποίηση Browsers
            await ScannerBrowser.EnsureCoreWebView2Async(env);
            await SplashBrowser.EnsureCoreWebView2Async(env);
            await TypewriterBrowser.EnsureCoreWebView2Async(env);

            // Συνδέουμε το Scanner για το Auto-Login/Extraction
            ScannerBrowser.NavigationCompleted += ScannerBrowser_NavigationCompleted;
            ScannerBrowser.Source = new Uri("https://classweb.uoi.gr/");

            // 1. ΦΟΡΤΩΣΗ SPLASH SCREEN (ASCII ART)
            string splashHtml = @"
            <html><head><link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&display=swap' rel='stylesheet'>
            <style>
                body { background-color: #05070a; margin: 0; overflow: hidden; display: flex; justify-content: center; align-items: center; height: 100vh; font-family: 'Share Tech Mono', Consolas, monospace; color: #00ffcc; }
                .boot-ascii { font-size: 16px; line-height: 1.2; white-space: pre; text-align: center; text-shadow: 0 0 10px rgba(0, 255, 204, 0.8); margin-bottom: 25px; }
                .boot-terminal { width: 480px; font-size: 1.1rem; line-height: 1.6; text-shadow: 0 0 5px rgba(0, 255, 204, 0.5); text-align: left; margin: 0 auto; }
                .t-line { overflow: hidden; white-space: nowrap; opacity: 0; }
                .l1 { animation: type-line 0.4s steps(30, end) 0.5s forwards; }
                .l2 { animation: type-line 0.4s steps(30, end) 1.5s forwards; }
                .l3 { animation: type-line 0.4s steps(30, end) 2.5s forwards; }
                .l4 { animation: type-line 0.4s steps(30, end) 3.5s forwards; color: #2ecc71; text-shadow: 0 0 10px #2ecc71; }
                @keyframes type-line { 0% { width: 0; opacity: 1; } 100% { width: 100%; opacity: 1; } }
            </style></head>
            <body><div><div class='boot-ascii'>
 _    _   ____    _____ 
| |  | | / __ \  |_   _|
| |  | || |  | |   | |  
| |__| || |__| |  _| |_ 
 \____/  \____/  |_____|
--- MAINFRAME LINK ---
            </div><div class='boot-terminal'>
                <div class='t-line l1'>&gt; INITIALIZING SYSTEM...</div>
                <div class='t-line l2'>&gt; CONNECTING TO UOI NETWORK [||||||||||]</div>
                <div class='t-line l3'>&gt; DECRYPTING ACADEMIC RECORDS... OK.</div>
                <div class='t-line l4'>&gt; ACCESS GRANTED. WELCOME, MERCENARY.</div>
            </div></div></body></html>";

            SplashBrowser.NavigateToString(ApplyTheme(splashHtml));

            // 2. ΦΟΡΤΩΣΗ TYPEWRITER
            string typewriterHtml = @"
            <html><head><link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&display=swap' rel='stylesheet'>
            <style>
                body { background-color: #05070a; margin: 0; overflow: hidden; }
                .typewriter-text {
                    font-family: 'Share Tech Mono', Consolas, 'Courier New', monospace;
                    color: #00ffcc; text-shadow: 0px 0px 8px rgba(0, 255, 204, 0.6);
                    font-size: 2.2rem; font-weight: bold; white-space: nowrap; overflow: hidden;
                    border-right: 0.15em solid #00ffcc; 
                    animation: typing 2.5s steps(45, end), blink-caret 0.75s step-end infinite;
                    animation-delay: 4.5s; width: 0; animation-fill-mode: forwards;
                }
                @keyframes typing { from { width: 0; } to { width: 100%; } }
                @keyframes blink-caret { from, to { border-color: transparent; } 50% { border-color: #00ffcc; } }
            </style></head><body>
                <div class='typewriter-text'>root@cse-uoi:~$ ./cse-nexus.exe</div>
            </body></html>";

            _typewriterHtmlRaw = typewriterHtml;
            TypewriterBrowser.NavigateToString(ApplyTheme(typewriterHtml));

            // Το Base Camp φορτώνει εδώ μόνο αν είναι το ορατό tab.
            // Αλλιώς θα φορτώσει όταν το επισκεφτεί ο χρήστης (το WebView2 δεν αρχικοποιείται σε κρυφό tab).
            if (MainTabControl.SelectedIndex == 0)
            {
                isBaseCampLoaded = true;
                _ = LoadBaseCamp();
            }

            // 4. Κρύβουμε το Splash μετά από 4.5s
            await Task.Delay(4500);
            if (SplashScreenOverlay != null) SplashScreenOverlay.Visibility = Visibility.Collapsed;
        }

        private bool _bcHooked = false;
        private bool _simHooked = false;
        private string _coopUploadHtml = "";

        // ----- Memory Card: UI state -----
        private void RestoreUiState()
        {
            // Παράθυρο (μόνο αν είναι ακόμα ορατό σε κάποια οθόνη)
            if (_save.WinWidth > 200 && _save.WinHeight > 200)
            {
                double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;

                bool visible = _save.WinLeft < vr - 100 && _save.WinTop < vb - 100
                            && _save.WinLeft + _save.WinWidth > vl + 100 && _save.WinTop + _save.WinHeight > vt + 100;
                if (visible)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = _save.WinLeft;
                    Top = _save.WinTop;
                    Width = _save.WinWidth;
                    Height = _save.WinHeight;
                }
            }
            if (_save.WinMaximized) WindowState = WindowState.Maximized;

            // Control Center (ανοίγει αμέσως, χωρίς animation)
            if (_save.RailExpanded)
            {
                _railExpanded = true;
                RailExpandedView.Visibility = Visibility.Visible;
                RailCollapsedView.Visibility = Visibility.Collapsed;
                SidePanel.Width = 300;
            }

            // Tab
            if (_save.ActiveTab > 0 && _save.ActiveTab < MainTabControl.Items.Count)
                MainTabControl.SelectedIndex = _save.ActiveTab;
        }

        private void SaveUiState()
        {
            try
            {
                _save.WinMaximized = WindowState == WindowState.Maximized;
                Rect b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                _save.WinLeft = b.Left;
                _save.WinTop = b.Top;
                _save.WinWidth = b.Width;
                _save.WinHeight = b.Height;
                _save.ActiveTab = MainTabControl.SelectedIndex;
            }
            catch (Exception ex) { LogError("SaveUiState", ex); }
            SaveState();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveUiState();
            base.OnClosing(e);
        }

        private void BtnOpenSaveFolder_Click(object sender, MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(SavePath)); }
            catch (Exception ex) { LogError("OpenSaveFolder", ex); }
        }

        // ----- Memory Card: μηνύματα από τα WebViews -----
        private void BaseCampBrowser_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            HandleSaveMessage(e.TryGetWebMessageAsString());
        }

        private void HandleSaveMessage(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString();

                if (type == "boss")
                {
                    var arr = root.GetProperty("value").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
                    if (arr.Length == 4) { _save.Boss = arr; SaveState(); }
                }
                
                else if (type == "sim")
                {
                    var v = root.GetProperty("value");
                    _save.Sim = new SimState
                    {
                        CourseGoal = v.GetProperty("courseGoal").GetDouble(),
                        ThesisGoal = v.GetProperty("thesisGoal").GetDouble(),
                        Filter = v.GetProperty("filter").GetString() ?? "all",
                        Selected = v.GetProperty("selected").EnumerateArray().Select(x => x.GetString()).ToList(),
                        Targets = v.GetProperty("targets").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble())
                    };
                    SaveState();
                }

                else if (type == "sched")
                {
                    HandleSchedMessage(root);
                }
            }
            catch (Exception ex) { LogError("HandleSaveMessage", ex); }
        }

        private async Task RefreshActiveTabAndResetOthers()
        {
            UpdateHud();
            RebuildTimeline();
            // Μόνο τα tabs που εξαρτώνται από τους βαθμούς (το Hub δεν αγγίζεται)
            isBaseCampLoaded = false;
            isAnalyticsLoaded = false;
            isSpaceLoaded = false;
            isSimulatorLoaded = false;
            isCoopLoaded = false;

            int index = MainTabControl.SelectedIndex;

            if (index == 0) { isBaseCampLoaded = true; await LoadBaseCamp(); }
            else if (index == 1) { isAnalyticsLoaded = true; await LoadAnalytics(); }
            else if (index == 2) { isSpaceLoaded = true; await LoadSpace(); }
            else if (index == 3) { isSimulatorLoaded = true; await LoadSimulator(); }
            else if (index == 5) { isCoopLoaded = true; await LoadCoop(); }

            SchedRefreshView();
        }

        // ==========================================
        // THEME SYSTEM
        // ==========================================
        public class ThemeDef
        {
            public string Name { get; set; }
            public string Hex { get; set; }
            public bool Secret { get; set; }
        }

        

        private static readonly Dictionary<string, ThemeDef> AllThemes = new Dictionary<string, ThemeDef>
        {
            { "cyan",      new ThemeDef { Name = "Cyberpunk (Cyan)",               Hex = "#00ffcc" } },
            { "matrix",    new ThemeDef { Name = "Matrix (Green)",                 Hex = "#00ff00" } },
            { "sith",      new ThemeDef { Name = "Sith (Red)",                     Hex = "#ff003c" } },
            { "synthwave", new ThemeDef { Name = "Synthwave (Pink)",               Hex = "#ff007f" } },
            { "amber",     new ThemeDef { Name = "Hacker (Amber)",                 Hex = "#ffb000" } },
            { "metal",     new ThemeDef { Name = "Heavy Metal (Blood Red)",        Hex = "#9e0000", Secret = true } },
            { "arcade",    new ThemeDef { Name = "Retro 8-Bit (Arcade Orange)",    Hex = "#ff5500", Secret = true } },
            { "johto",     new ThemeDef { Name = "Johto Edition (Legendary Gold)", Hex = "#ffd700", Secret = true } }
        };

        private string _activeTheme = "cyan";
        private readonly HashSet<string> _unlockedThemes = new HashSet<string>();
        private string _typewriterHtmlRaw = "";
        private DispatcherTimer _statusTimer;

        private string ThemeHex => AllThemes[_activeTheme].Hex;

        private string ThemeRgb
        {
            get
            {
                var c = (Color)ColorConverter.ConvertFromString(ThemeHex);
                return $"{c.R},{c.G},{c.B}";
            }
        }

        private string SavePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "save.json");

        private bool IsThemeAvailable(string key) =>
            AllThemes.ContainsKey(key) && (!AllThemes[key].Secret || _unlockedThemes.Contains(key));

        // ==========================================
        // CONTROL CENTER + TIME MACHINE
        // ==========================================
        public class TimelinePoint
        {
            public string Year { get; set; }
            public int Order { get; set; }
            public string Month { get; set; }
            public string Label
            {
                get
                {
                    int n = int.Parse(Year);
                    return PeriodLabel(Month, Year);
                }
            }
        }

        private bool _tmOn = false;
        private string _tmYear = "";
        private int _tmOrder = 0;
        private string _tmLabel = "";
        private bool _railExpanded = false;

        private const string TimeMachineCss = @"<style>
body::after {
    content: ''; position: fixed; top: 0; left: 0; right: 0; bottom: 0; pointer-events: none; z-index: 999999;
    backdrop-filter: sepia(0.35) contrast(1.1) grayscale(0.4) hue-rotate(-10deg) saturate(0.8);
    background: linear-gradient(rgba(18,16,16,0) 50%, rgba(0,0,0,0.15) 50%), linear-gradient(90deg, rgba(255,0,0,0.04), rgba(0,255,0,0.015), rgba(0,0,255,0.04));
    background-size: 100% 4px, 3px 100%;
}
</style>";

        private static SolidColorBrush Br(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        // Εφέ CRT όταν το Time Machine είναι ενεργό
        private string ApplyFx(string html)
        {
            if (!_tmOn) return html;
            int i = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? html : html.Insert(i, TimeMachineCss);
        }

        // Κεντρικό σημείο φόρτωσης βαθμών (με φίλτρο Time Machine)
        private List<GradeRecord> GetPassedGrades(JsonSerializerOptions options)
        {
            var passed = JsonSerializer.Deserialize<List<GradeRecord>>(File.ReadAllText(DataFilePath), options)
                                       .Where(g => g.IsPassed && g.Grade.HasValue).ToList();
            if (!_tmOn) return passed;

            return passed.Where(g => int.TryParse(g.AcademicYear, out _) &&
                (string.CompareOrdinal(g.AcademicYear, _tmYear) < 0 ||
                 (g.AcademicYear == _tmYear && g.ExamOrder <= _tmOrder))).ToList();
        }

        private List<TimelinePoint> GetTimeline()
        {
            try
            {
                if (!File.Exists(DataFilePath)) return new List<TimelinePoint>();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var all = JsonSerializer.Deserialize<List<GradeRecord>>(File.ReadAllText(DataFilePath), options);

                return all.Where(g => g.IsPassed && g.Grade.HasValue && int.TryParse(g.AcademicYear, out _))
                          .Select(g => new { g.AcademicYear, g.ExamOrder, g.ExamMonth })
                          .Distinct()
                          .OrderBy(x => x.AcademicYear, StringComparer.Ordinal).ThenBy(x => x.ExamOrder)
                          .Select(x => new TimelinePoint { Year = x.AcademicYear, Order = x.ExamOrder, Month = x.ExamMonth == "Άγνωστο" ? "Άλλο" : x.ExamMonth })
                          .ToList();
            }
            catch (Exception ex) { LogError("GetTimeline", ex); return new List<TimelinePoint>(); }
        }

        // ----- Rail -----
        private void Rail_Toggle_Click(object sender, MouseButtonEventArgs e) => ToggleRail();

        private void ToggleRail()
        {
            _railExpanded = !_railExpanded;
            RailExpandedView.Visibility = _railExpanded ? Visibility.Visible : Visibility.Collapsed;
            RailCollapsedView.Visibility = _railExpanded ? Visibility.Collapsed : Visibility.Visible;

            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = _railExpanded ? 300 : 64,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            SidePanel.BeginAnimation(FrameworkElement.WidthProperty, anim);

            SaveState();
        }

        // ----- HUD -----
        private void UpdateHud()
        {
            try
            {
                if (!File.Exists(DataFilePath)) return;
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var p = CalculatePlayerProfile(GetPassedGrades(options));

                HudAvatar.Text = p.Avatar;
                RailAvatar.Text = p.Avatar;
                HudGpa.Text = p.Gpa.ToString("F2");
                HudRank.Text = p.Rank;
                HudLevel.Text = p.LevelLabel;
                HudXpText.Text = $"{p.XpInLevel:g}/30 XP";
                HudXpBar.Value = p.XpInLevel;
            }
            catch (Exception ex) { LogError("UpdateHud", ex); }
        }

        // ----- Time Machine -----
        private async void TmToggle_Click(object sender, MouseButtonEventArgs e) => await SetTimeMachineAsync(!_tmOn);
        private async void BtnTmReturn_Click(object sender, MouseButtonEventArgs e) => await SetTimeMachineAsync(false);

        private async Task SetTimeMachineAsync(bool on, TimelinePoint point = null)
        {
            bool wasOn = _tmOn;

            if (on)
            {
                var tl = GetTimeline();
                if (tl.Count == 0)
                {
                    ShowTerminalMessage("⏳ Δεν υπάρχουν δεδομένα για το Time Machine.", "#e74c3c");
                    return;
                }
                point = point ?? tl.Last();
                if (_tmOn && point.Year == _tmYear && point.Order == _tmOrder) return;

                _tmOn = true;
                _tmYear = point.Year;
                _tmOrder = point.Order;
                _tmLabel = point.Label;
            }
            else
            {
                if (!_tmOn) return;
                _tmOn = false;
            }

            UpdateTimeMachineUi();
            if (wasOn != _tmOn) ReloadTypewriterHeader();
            await RefreshActiveTabAndResetOthers();
        }

        private void ReloadTypewriterHeader()
        {
            if (!string.IsNullOrEmpty(_typewriterHtmlRaw) && TypewriterBrowser.CoreWebView2 != null)
                TypewriterBrowser.NavigateToString(ApplyTheme(_typewriterHtmlRaw).Replace("animation-delay: 4.5s;", "animation-delay: 0.2s;"));
        }

        private void UpdateTimeMachineUi()
        {
            TmToggleText.Text = _tmOn ? "ON" : "OFF";
            TmToggleText.Foreground = _tmOn ? Brushes.Black : Br("#7f8c8d");
            TmToggle.Background = _tmOn ? Br("#f1c40f") : Br("#1a2530");

            TimeMachineBanner.Visibility = _tmOn ? Visibility.Visible : Visibility.Collapsed;
            TimeMachineBannerText.Text = $"Προβολή προφίλ όπως ήταν: {_tmLabel}";
            RailTmIcon.BorderBrush = _tmOn ? Br("#f1c40f") : Br("#2c3e50");
            Title = _tmOn ? $"CSE Nexus - ⏳ TIME MACHINE [{_tmLabel}]" : "CSE Nexus";

            RebuildTimeline();
        }

        private void RebuildTimeline()
        {
            TimelinePanel.Children.Clear();

            foreach (var yearGroup in GetTimeline().GroupBy(t => t.Year))
            {
                int n = int.Parse(yearGroup.Key);
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };

                var label = new TextBlock
                {
                    Text = $"{n}-{(n + 1) % 100:00}",
                    Width = 62,
                    Foreground = Br("#7f8c8d"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);

                var chips = new WrapPanel();
                foreach (var pt in yearGroup)
                {
                    bool selected = _tmOn && pt.Year == _tmYear && pt.Order == _tmOrder;
                    var chip = new Border
                    {
                        Margin = new Thickness(0, 0, 5, 5),
                        Padding = new Thickness(9, 4, 9, 4),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(1),
                        BorderBrush = selected ? Br("#f1c40f") : Br("#2c3e50"),
                        Background = selected ? Br("#f1c40f") : Br("#111b24"),
                        Cursor = Cursors.Hand,
                        Tag = pt,
                        Child = new TextBlock
                        {
                            Text = pt.Month,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12,
                            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                            Foreground = selected ? Brushes.Black : Br("#bdc3c7")
                        }
                    };
                    chip.MouseLeftButtonUp += async (s, e) => await SetTimeMachineAsync(true, (TimelinePoint)((Border)s).Tag);
                    chips.Children.Add(chip);
                }
                row.Children.Add(chips);
                TimelinePanel.Children.Add(row);
            }
        }

        public class SaveData
        {
            public List<string> Themes { get; set; } = new List<string>();
            public string ActiveTheme { get; set; } = "cyan";
            public bool[] Boss { get; set; } = new bool[4];
            public bool RailExpanded { get; set; } = false;
            public int ActiveTab { get; set; } = 0;
            public double WinLeft { get; set; } = 0;
            public double WinTop { get; set; } = 0;
            public double WinWidth { get; set; } = 0;
            public double WinHeight { get; set; } = 0;
            public bool WinMaximized { get; set; } = false;
            public SimState Sim { get; set; } = new SimState();
            public string CoopPayload { get; set; } = "";
            public string CoopLoadedAt { get; set; } = "";
            public SchedState Sched { get; set; } = new SchedState();
            public bool Fullscreen { get; set; } = true;
        }

        public class SimState
        {
            public double CourseGoal { get; set; } = -1;   // -1 = δεν έχει οριστεί από τον χρήστη
            public double ThesisGoal { get; set; } = -1;
            public string Filter { get; set; } = "all";
            public List<string> Selected { get; set; } = new List<string>();
            public Dictionary<string, double> Targets { get; set; } = new Dictionary<string, double>();
        }

        private SaveData _save = new SaveData();
        private bool _stateReady = false;

        private void LoadSave()
        {
            try
            {
                if (!File.Exists(SavePath)) return;
                var data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(SavePath));
                if (data == null) return;

                if (_save.Sched == null) _save.Sched = new SchedState();

                _save = data;
                if (_save.Boss == null || _save.Boss.Length != 4) _save.Boss = new bool[4];
                if (_save.Sim == null) _save.Sim = new SimState();

                foreach (var t in _save.Themes ?? new List<string>())
                    if (AllThemes.ContainsKey(t) && AllThemes[t].Secret) _unlockedThemes.Add(t);

                if (!string.IsNullOrEmpty(_save.ActiveTheme) && IsThemeAvailable(_save.ActiveTheme))
                    _activeTheme = _save.ActiveTheme;
            }
            catch (Exception ex) { LogError("LoadSave", ex); }
        }

        private void SaveState()
        {
            try
            {
                _save.Themes = _unlockedThemes.ToList();
                _save.ActiveTheme = _activeTheme;
                _save.RailExpanded = _railExpanded;

                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));

                // Γράφουμε πρώτα σε προσωρινό αρχείο, ώστε ένα crash να μη χαλάσει το save
                string tmp = SavePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_save));
                File.Copy(tmp, SavePath, true);
                File.Delete(tmp);

                if (SaveStatusText != null) SaveStatusText.Text = $"Τελευταία αποθήκευση: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex) { LogError("SaveState", ex); }
        }

        // Αλλάζει το χρώμα της WPF διεπαφής (tabs, πλαίσια, terminal)
        private void ApplyThemeResources()
        {
            var color = (Color)ColorConverter.ConvertFromString(ThemeHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Resources["ThemeColor"] = color;
            Resources["ThemeBrush"] = brush;
        }

        // Αλλάζει το cyan μέσα στα HTML που φορτώνουν τα WebViews
        private string ApplyTheme(string html)
        {
            string rgb = ThemeRgb;
            html = html
                .Replace("#00ffcc", ThemeHex)
                .Replace("rgba(0, 255, 204,", $"rgba({rgb},")
                .Replace("rgba(0,255,204,", $"rgba({rgb},");
            return ApplyFx(html);
        }

        private void RebuildThemeSwatches()
        {
            ThemeSwatchPanel.Children.Clear();
            foreach (var kv in AllThemes)
            {
                if (!IsThemeAvailable(kv.Key)) continue;

                var swatch = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value.Hex)),
                    BorderBrush = kv.Key == _activeTheme ? Brushes.White : Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand,
                    ToolTip = kv.Value.Name,
                    Tag = kv.Key
                };
                swatch.MouseLeftButtonUp += async (s, e) => await SetThemeAsync((string)((Border)s).Tag);
                ThemeSwatchPanel.Children.Add(swatch);
            }
        }

        private async Task SetThemeAsync(string key)
        {
            if (!IsThemeAvailable(key) || key == _activeTheme) return;

            _activeTheme = key;
            SaveState();
            ApplyThemeResources();
            RebuildThemeSwatches();

            // Το header ξαναγράφεται (χωρίς τη χρονική καθυστέρηση της εκκίνησης)
            if (!string.IsNullOrEmpty(_typewriterHtmlRaw) && TypewriterBrowser.CoreWebView2 != null)
                TypewriterBrowser.NavigateToString(ApplyTheme(_typewriterHtmlRaw).Replace("animation-delay: 4.5s;", "animation-delay: 0.2s;"));

            await RefreshThemedTabs();
        }

        // Ξαναφορτώνει μόνο τα tabs που χρησιμοποιούν το theme (το Co-op και το Hub δεν αγγίζονται)
        private async Task RefreshThemedTabs()
        {
            isBaseCampLoaded = false;
            isAnalyticsLoaded = false;
            isSpaceLoaded = false;

            int index = MainTabControl.SelectedIndex;
            if (index == 0) { isBaseCampLoaded = true; await LoadBaseCamp(); }
            else if (index == 1) { isAnalyticsLoaded = true; await LoadAnalytics(); }
            else if (index == 2) { isSpaceLoaded = true; await LoadSpace(); }

            SchedRefreshView();
        }

        // ----- Admin Terminal -----
        private void ShowTerminalMessage(string msg, string colorHex)
        {
            TerminalStatus.Text = msg;
            TerminalStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));

            _statusTimer?.Stop();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (s, e) => { TerminalStatus.Text = ""; _statusTimer.Stop(); };
            _statusTimer.Start();
        }

        private void UnlockTheme(string key, string message)
        {
            if (_unlockedThemes.Contains(key))
            {
                ShowTerminalMessage($"{AllThemes[key].Name}: ήδη ξεκλειδωμένο!", "#bdc3c7");
                return;
            }
            _unlockedThemes.Add(key);
            SaveState();
            RebuildThemeSwatches();
            ShowTerminalMessage(message, AllThemes[key].Hex);
        }

        private void TerminalInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            TerminalHint.Visibility = string.IsNullOrEmpty(TerminalInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TerminalInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            string cmd = TerminalInput.Text.Trim().ToLowerInvariant();
            TerminalInput.Clear();
            if (cmd == "") return;

            switch (cmd)
            {
                case "iddqd": ShowTerminalMessage("🎮 GOD MODE ACTIVATED: Όλα τα bugs έγιναν features.", "#2ecc71"); break;
                case "matrix": ShowTerminalMessage("🐇 Wake up, Neo... Το ClassWeb σε έχει.", "#f1c40f"); break;
                case "order66": ShowTerminalMessage("⚔️ Executing Order 66... (Just kidding!)", "#e74c3c"); break;
                case "pantera": UnlockTheme("metal", "🤘 HEAVY METAL THEME unlocked!"); break;
                case "konami": UnlockTheme("arcade", "👾 RETRO 8-BIT THEME unlocked!"); break;
                case "johto": UnlockTheme("johto", "⚡ JOHTO EDITION THEME unlocked!"); break;
                default: ShowTerminalMessage("🔒 ACCESS DENIED: Unknown command.", "#e74c3c"); break;
            }
        }

        // ==========================================
        // DYNAMIC TAB LOADING (LAZY LOAD)
        // ==========================================
        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl)
            {
                int index = MainTabControl.SelectedIndex;
                if (_stateReady) { _save.ActiveTab = index; SaveState(); }

                Dispatcher.InvokeAsync(async () =>
                {
                    if (index == 0 && !isBaseCampLoaded)
                    {
                        isBaseCampLoaded = true;
                        await LoadBaseCamp();
                    }
                    else if (index == 1 && !isAnalyticsLoaded)
                    {
                        isAnalyticsLoaded = true;
                        await LoadAnalytics();
                    }
                    else if (index == 2 && !isSpaceLoaded)
                    {
                        isSpaceLoaded = true;
                        await LoadSpace();
                    }
                    else if (index == 3 && !isSimulatorLoaded)
                    {
                        isSimulatorLoaded = true;
                        await LoadSimulator();
                    }
                    else if (index == 4 && !isHubLoaded)
                    {
                        isHubLoaded = true;
                        await LoadDeepDiveHub();
                    }
                    else if (index == 5 && !isCoopLoaded)
                    {
                        isCoopLoaded = true;
                        await LoadCoop();
                    }
                    else if (index == 6)
                    {
                        if (!isScheduleLoaded)
                        {
                            isScheduleLoaded = true;
                            await LoadSchedule();
                        }
                        else
                        {
                            SchedTabActivated();
                        }
                    }
                }, DispatcherPriority.Background);
            }
        }

        // ==========================================
        // DATA EXTRACTION & CLASSWEB SYNC
        // ==========================================
        
        private async Task ExtractClassWebData()
        {
            try
            {
                await Task.Delay(1000);

                // --- 1. ΚΑΤΕΒΑΣΜΑ ΒΑΘΜΩΝ ---
                string jsFetchGrades = @"
                    window.hackerVaultGrades = 'WAITING';
                    function getCookie(name) { var match = document.cookie.match(new RegExp('(^| )' + name + '=([^;]+)')); if (match) return decodeURIComponent(match[2]); return ''; }
                    var csrfCookie = getCookie('XSRF-TOKEN'); var metaCsrf = document.querySelector('meta[name=""_csrf""]'); var finalCsrf = csrfCookie ? csrfCookie : (metaCsrf ? metaCsrf.content : '');
                    fetch('https://classweb.uoi.gr/feign/student/grades/diploma', { method: 'GET', headers: { 'Accept': 'application/json, text/plain, */*', 'X-Requested-With': 'XMLHttpRequest', 'X-XSRF-TOKEN': finalCsrf, 'X-CSRF-TOKEN': finalCsrf } })
                    .then(response => { if (!response.ok) throw new Error('HTTP ' + response.status); return response.text(); })
                    .then(data => { window.hackerVaultGrades = data; })
                    .catch(err => { window.hackerVaultGrades = 'ERROR: ' + err.message; });
                ";
                await ScannerBrowser.ExecuteScriptAsync(jsFetchGrades);

                string jsonGrades = "";
                bool gradesSuccess = false;
                for (int i = 0; i < 14; i++)
                {
                    await Task.Delay(500);
                    string raw = await ScannerBrowser.ExecuteScriptAsync("window.hackerVaultGrades");
                    if (string.IsNullOrEmpty(raw) || raw == "null" || raw.Contains("WAITING")) continue;
                    jsonGrades = System.Text.RegularExpressions.Regex.Unescape(raw);
                    if (jsonGrades.StartsWith("\"") && jsonGrades.EndsWith("\"")) jsonGrades = jsonGrades.Substring(1, jsonGrades.Length - 2);
                    if (jsonGrades.StartsWith("[") || jsonGrades.StartsWith("{")) { gradesSuccess = true; break; }
                }

                if (gradesSuccess)
                {
                    File.WriteAllText(DataFilePath, jsonGrades);

                    // Αντί να φορτώνουμε στα τυφλά το BaseCamp, καλούμε τον νέο μηχανισμό
                    Dispatcher.InvokeAsync(async () => {
                        await RefreshActiveTabAndResetOthers();
                    });
                }

                // --- 2. ΚΑΤΕΒΑΣΜΑ ΟΔΗΓΟΥ ΣΠΟΥΔΩΝ ---
                string jsFetchCurr = @"
                    window.hackerVaultCurr = 'WAITING';
                    var csrfCookie = getCookie('XSRF-TOKEN'); var metaCsrf = document.querySelector('meta[name=""_csrf""]'); var finalCsrf = csrfCookie ? csrfCookie : (metaCsrf ? metaCsrf.content : '');
                    fetch('https://classweb.uoi.gr/feign/student/program_courses', { method: 'GET', headers: { 'Accept': 'application/json, text/plain, */*', 'X-Requested-With': 'XMLHttpRequest', 'X-XSRF-TOKEN': finalCsrf, 'X-CSRF-TOKEN': finalCsrf } })
                    .then(response => { if (!response.ok) throw new Error('HTTP ' + response.status); return response.text(); })
                    .then(data => { window.hackerVaultCurr = data; })
                    .catch(err => { window.hackerVaultCurr = 'ERROR: ' + err.message; });
                ";
                await ScannerBrowser.ExecuteScriptAsync(jsFetchCurr);

                string jsonCurr = "";
                bool currSuccess = false;
                for (int i = 0; i < 14; i++)
                {
                    await Task.Delay(500);
                    string raw = await ScannerBrowser.ExecuteScriptAsync("window.hackerVaultCurr");
                    if (string.IsNullOrEmpty(raw) || raw == "null" || raw.Contains("WAITING")) continue;
                    jsonCurr = System.Text.RegularExpressions.Regex.Unescape(raw);
                    if (jsonCurr.StartsWith("\"") && jsonCurr.EndsWith("\"")) jsonCurr = jsonCurr.Substring(1, jsonCurr.Length - 2);
                    if (jsonCurr.StartsWith("[") || jsonCurr.StartsWith("{")) { currSuccess = true; break; }
                }

                if (currSuccess)
                {
                    string currFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "Curriculum_Dump.json");
                    File.WriteAllText(currFilePath, jsonCurr);
                }
            }
            catch (Exception ex) { LogError("ExtractClassWebData", ex); }
        }

        private string LogFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "error.log");

        private void LogError(string where, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath));
                File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"[{where}] {ex}");
        }

        private void ShowErrorPage(Microsoft.Web.WebView2.Wpf.WebView2 browser, string where, Exception ex)
        {
            LogError(where, ex);
            try
            {
                if (browser?.CoreWebView2 == null) return;
                string msg = System.Net.WebUtility.HtmlEncode(ex.Message);
                string path = System.Net.WebUtility.HtmlEncode(LogFilePath);
                browser.NavigateToString($@"<html><body style='background:#05070a; color:#e74c3c; font-family:Consolas,monospace; padding:40px;'>
        <h2>⚠️ SYSTEM ERROR: {where}</h2>
        <div style='color:#ecf0f1; margin-top:15px;'>{msg}</div>
        <div style='color:#7f8c8d; margin-top:25px; font-size:0.85rem;'>Πλήρης αναφορά: {path}</div>
        </body></html>");
            }
            catch { }
        }

        private static string NormalizeGreek(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            string decomposed = input.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (char c in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
        }
        private static string NormalizeKey(string s) =>
            System.Text.RegularExpressions.Regex.Replace(NormalizeGreek(s ?? ""), @"\s+", " ").Trim();

        // "2014" -> "14-15"
        private static string AcadYearShort(string academicYear)
        {
            if (int.TryParse(academicYear, out int n)) return $"{n % 100:00}-{(n + 1) % 100:00}";
            return academicYear;
        }

        // ("Φεβ", "2014") -> "Φεβ '14-15"
        private static string PeriodLabel(string month, string academicYear) => $"{month} '{AcadYearShort(academicYear)}";

        private static readonly (string Branch, string[] Keys)[] BranchRules =
        {
            ("Graphics & Vision", new[] { "ΓΡΑΦΙΚ", "ΟΡΑΣΗ", "ΠΟΛΥΜΕΣ", "ΑΛΛΗΛΕΠΙΔΡΑΣ", "ΕΙΚΟΝΑΣ", "ΗΧΟΥ", "3D" }),
            ("Cybersecurity", new[] { "ΑΣΦΑΛΕΙ", "ΚΡΥΠΤΟΓΡΑΦ", "ΚΑΚΟΒΟΥΛ", "ΑΜΥΝΑ", "ΙΟΥΣ", "ΕΠΙΘΕΣΕΙΣ" }),
            ("Networks & Comms", new[] { "ΔΙΚΤΥ", "ΤΗΛΕΠΙΚΟΙΝ", "ΣΗΜΑΤ", "ΑΣΥΡΜΑΤ", "ΔΙΑΔΙΚΤΥ", "ΖΕΥΞΕΙΣ" }),
            ("Hardware & Architecture", new[] { "ΚΥΚΛΩΜΑΤ", "ΨΗΦΙΑΚ", "ΑΡΧΙΤΕΚΤΟΝΙΚ", "ΗΛΕΚΤΡΟΝΙΚ", "ΜΙΚΡΟΕΠΕΞΕΡΓΑΣΤ", "VLSI", "ΦΥΣΙΚ", "ΑΞΙΟΠΙΣΤΙ" }),
            ("Math & Theory", new[] { "ΛΟΓΙΣΜΟΣ", "ΑΛΓΕΒΡΑ", "ΜΑΘΗΜΑΤ", "ΠΙΘΑΝΟΤΗΤ", "ΣΤΑΤΙΣΤΙΚ", "ΑΡΙΘΜΗΤΙΚ", "ΥΠΟΛΟΓΙΣΜΟΥ", "ΓΡΑΦΗΜΑΤ", "ΠΟΛΥΠΛΟΚΟΤΗΤ", "ΒΕΛΤΙΣΤΟΠΟΙΗΣ" }),
            ("Data & AI", new[] { "ΝΕΥΡΩΝ", "ΝΟΗΜΟΣΥΝ", "ΒΑΣΕΙΣ", "ΜΑΘΗΣΗ", "ΡΟΜΠΟΤΙΚ", "ΓΛΩΣΣΑΣ", "ΕΞΟΡΥΞ", "ΔΕΔΟΜΕΝ", "ΠΛΗΡΟΦΟΡΙΑ" }),
            ("Software & Systems", new[] { "ΠΡΟΓΡΑΜΜΑΤΙΣΜ", "ΛΟΓΙΣΜΙΚ", "ΑΛΓΟΡΙΘΜ", "ΔΟΜΕΣ", "ΜΕΤΑΦΡΑΣΤ", "ΛΕΙΤΟΥΡΓΙΚ", "ΚΑΤΑΝΕΜΗΜΕΝ", "ΠΑΡΑΛΛΗΛ", "ΣΥΣΤΗΜΑΤ", "ΑΝΤΙΚΕΙΜΕΝΟΣΤΡΕΦ" })
        };

        private static string GetBranch(string title)
        {
            string t = NormalizeGreek(title);
            foreach (var rule in BranchRules)
                if (rule.Keys.Any(k => t.Contains(k))) return rule.Branch;
            return "General / Core";
        }

        // ==========================================
        // TAB 1: BASE CAMP LOGIC
        // ==========================================
        private async Task LoadBaseCamp()
        {
            try
            {
                await BaseCampBrowser.EnsureCoreWebView2Async(await GetEnvAsync());
                if (!_bcHooked)
                {
                    BaseCampBrowser.WebMessageReceived += BaseCampBrowser_WebMessageReceived;
                    _bcHooked = true;
                }

                if (!File.Exists(DataFilePath)) return;

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var passedGrades = GetPassedGrades(options);

                // --- ΠΡΟΣΘΗΚΗ: Χρήση της Global Engine (CalculatePlayerProfile) ---
                var p = CalculatePlayerProfile(passedGrades);
                p.Badges += FocusAchievements().Count;

                int targetCourses = 47;
                double xpPercent = (p.Ects % 30 / 30.0) * 100.0;
                if (p.Ects >= 300) xpPercent = 100;

                // Status Message
                string statusText;
                int missingCoursesCount = Math.Max(0, targetCourses - p.Courses);
                if (missingCoursesCount == 0)
                {
                    statusText = !p.HasThesis ? "Μένει Διπλωματική!" : "Απόφοιτος!";
                }
                else
                {
                    string numColor = missingCoursesCount <= 5 ? "#4CAF50" : missingCoursesCount <= 15 ? "#00f2fe" : missingCoursesCount <= 30 ? "#ffa726" : "#ff4b4b";
                    string msgPrefix = missingCoursesCount <= 5 ? "Πολύ κοντά στην πηγή!" : missingCoursesCount <= 15 ? "Μπήκες στην τελική ευθεία!" : missingCoursesCount <= 30 ? "Έχουμε δρόμο ακόμα!" : "Δυνατά για τη συνέχεια!";
                    statusText = $"{msgPrefix}<br>Μένουν <span style='color: {numColor}; font-weight: bold;'>{missingCoursesCount}</span> μαθ.";
                }

                // --- Κλίμακα Πτυχίου & Πρακτική ---
                string degreeClass = "-";
                string targetMsg = "";
                if (p.Gpa >= 8.5)
                {
                    degreeClass = "Άριστα 🏆";
                    targetMsg = "Βρίσκεσαι στην υψηλότερη βαθμίδα! Συνέχισε την εξαιρετική δουλειά!";
                }
                else if (p.Gpa >= 6.5)
                {
                    degreeClass = "Λίαν Καλώς 🥈";
                    targetMsg = $"Απέχεις <b>{(8.5 - p.Gpa):F2}</b> μονάδες από το <b>Άριστα</b>!";
                }
                else if (p.Gpa >= 5.0)
                {
                    degreeClass = "Καλώς 🥉";
                    targetMsg = $"Απέχεις <b>{(6.5 - p.Gpa):F2}</b> μονάδες από το <b>Λίαν Καλώς</b>!";
                }

                string degreeHtml = "";
                if (p.Gpa >= 5.0)
                {
                    degreeHtml = $"<div style=\"background: rgba(46, 204, 113, 0.1); border-left: 4px solid #2ecc71; padding: 12px 15px; border-radius: 4px; color: #2ecc71; margin-top: 20px; margin-bottom: 10px; font-size: 1.05rem; box-shadow: 0 2px 5px rgba(0,0,0,0.2);\">🎯 <b>Κλίμακα Πτυχίου:</b> Η τρέχουσα βαθμολογία σου αντιστοιχεί στο <b>{degreeClass}</b>. {targetMsg}</div>";
                }

                string internshipHtml = "";
                if (p.HasInternship)
                {
                    var internshipCourses = passedGrades.Where(c => c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase)).ToList();
                    double internshipEcts = internshipCourses.Sum(c => c.Ects);
                    internshipHtml = $"<div style=\"background: rgba(52, 152, 219, 0.1); border-left: 4px solid #3498db; padding: 12px 15px; border-radius: 4px; color: #3498db; margin-bottom: 10px; font-size: 1rem; box-shadow: 0 2px 5px rgba(0,0,0,0.2);\">📌 Εντοπίστηκε Πρακτική Άσκηση. Προστέθηκαν τα ECTS ({internshipEcts:g}) στον Μ.Ο., αλλά εξαιρέθηκε από την καταμέτρηση των {targetCourses} μαθημάτων.</div>";
                }

                // --- BOSS ARENA (Διπλωματική & Δίκτυα Ι) ---
                var netCourse = passedGrades.FirstOrDefault(c => System.Text.RegularExpressions.Regex.IsMatch(c.CourseId.Title, @"Δ[ιί]κτυα Υπολογιστ[ωώ]ν\s*[ΙI1](?!\s*[ΙI1])", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                bool hasNet = netCourse != null;
                string netGrade = hasNet ? netCourse.FinalGrade.ToString("F1") : "";

                List<MissingCourse> missingBounties = GetMissingCoursesList(passedGrades.Select(g => NormalizeKey(g.CourseId.Title)).ToHashSet());
                var nextTarget = missingBounties.OrderByDescending(m => m.Ects).FirstOrDefault();

                // --- ΠΡΟΣΘΗΚΗ: DYNAMIC SKILL TREE (ECTS EXPANSION) ---
                var treeGrades = passedGrades.Where(g => g.FinalGrade >= 5.0)
                    .OrderBy(g => g.AcademicYear)
                    .ThenBy(g => g.ExamOrder)
                    .ToList();

                File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "branches.txt"),
                string.Join("\n", treeGrades.Select(g => $"{GetBranch(g.CourseId.Title),-26} {g.CourseId.Title}")));

                var branchYMap = new Dictionary<string, int> {
                    { "Cybersecurity", 4 }, { "Data & AI", 3 }, { "Software & Systems", 2 }, { "Graphics & Vision", 1 },
                    { "General / Core", 0 }, { "Hardware & Architecture", -1 }, { "Networks & Comms", -2 }, { "Math & Theory", -3 }
                };

                var bColors = new Dictionary<string, string> {
                    { "Cybersecurity", "'#00ff00'" }, { "Data & AI", "'#00e5b0'" }, { "Software & Systems", "'#3498db'" },
                    { "Graphics & Vision", "'#ff007f'" }, { "General / Core", "'#bdc3c7'" }, { "Hardware & Architecture", "'#e74c3c'" },
                    { "Networks & Comms", "'#9b59b6'" }, { "Math & Theory", "'#f1c40f'" }
                };

                double runningEcts = 0;
                var treeNodes = new List<dynamic>();
                foreach (var g in treeGrades)
                {
                    runningEcts += g.Ects;
                    string b = GetBranch(g.CourseId.Title);

                    treeNodes.Add(new { Course = g.CourseId.Title.Replace("'", "&apos;"), Grade = g.FinalGrade, Ects = g.Ects, CumEcts = runningEcts, Branch = b, YVal = branchYMap[b], Color = bColors[b] });
                }

                string skillTreeTraces = "";
                foreach (var branch in branchYMap.Keys)
                {
                    var branchNodes = treeNodes.Where(n => n.Branch == branch).ToList();
                    if (!branchNodes.Any()) continue;

                    string xVals = "[0," + string.Join(",", branchNodes.Select(n => n.CumEcts.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
                    string yVals = "[0," + string.Join(",", branchNodes.Select(n => n.YVal.ToString())) + "]";
                    skillTreeTraces += $@"{{ x: {xVals}, y: {yVals}, mode: 'lines', line: {{ color: {bColors[branch]}, width: 3, shape: 'spline', smoothing: 0 }}, hoverinfo: 'none', showlegend: false }},";
                }

                string nodeX = "[0," + string.Join(",", treeNodes.Select(n => n.CumEcts.ToString(System.Globalization.CultureInfo.InvariantCulture))) + (treeNodes.Any() ? $",{runningEcts.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "") + "]";
                string nodeY = "[0," + string.Join(",", treeNodes.Select(n => n.YVal.ToString())) + (treeNodes.Any() ? $",{treeNodes.Last().YVal}" : "") + "]";
                string nodeText = "['<b>ROOT_NODE</b><br>Σύστημα Ενεργό<br>0 ECTS'," + string.Join(",", treeNodes.Select(n => $"'<b>{n.Course}</b><br>Βαθμός: {n.Grade:F1}<br>ECTS: +{n.Ects}<br>Σύνολο ECTS: {n.CumEcts}'")) + (treeNodes.Any() ? $", '<b>CURRENT POSITION</b><br>Σύνολο: {runningEcts} ECTS'" : "") + "]";
                string nodeC = "['#ffffff'," + string.Join(",", treeNodes.Select(n => "'#0a0e17'")) + (treeNodes.Any() ? $", '{ThemeHex}'" : "") + "]";
                string nodeS = "[18," + string.Join(",", treeNodes.Select(n => "12")) + (treeNodes.Any() ? ", 22" : "") + "]";
                string nodeLc = $"['{ThemeHex}'," + string.Join(",", treeNodes.Select(n => n.Color)) + (treeNodes.Any() ? ", '#ffffff'" : "") + "]";

                skillTreeTraces += $@"{{ x: {nodeX}, y: {nodeY}, mode: 'markers', hoverinfo: 'text', text: {nodeText}, marker: {{ size: {nodeS}, color: {nodeC}, line: {{ color: {nodeLc}, width: 2 }}, symbol: 'hexagon' }}, showlegend: false }}";

                // Οι τίτλοι των Ranks με HTML <br> για τέλειο word-wrap (ώστε να είναι στενοί και να χωράνε)
                string[] ranksArray = {
                    "HELLO<br>WORLD",
                    "LOOP<br>SCRIPTER",
                    "BUG<br>HUNTER",
                    "OBJECT<br>ORIENTED",
                    "TREE<br>TRAVERSER",
                    "DATABASE<br>RANGER",
                    "MACHINE<br>LEARNING",
                    "8-BIT<br>LEGEND",
                    "3D RENDER<br>MAGE",
                    "SYSTEM<br>ARCHITECT",
                    "MASTER<br>OF CODE"
                };

                // Μετατρέπουμε το HEX χρώμα του παίκτη σε RGB (για να ρυθμίζουμε τη διαφάνεια)
                var wpfColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(p.ArchColor);
                string archRgb = $"{wpfColor.R},{wpfColor.G},{wpfColor.B}";

                int maxZones = (int)(Math.Max(300, runningEcts + 30) / 30) + 1;
                if (maxZones > 11) maxZones = 11; // Κλειδώνουμε το μέγιστο στα 11 Ranks

                string shapesJson = "[";
                string annotationsJson = "[";

                for (int i = 0; i < maxZones; i++)
                {
                    int levelNum = i + 1;
                    string rankName = i < ranksArray.Length ? ranksArray[i] : "UNKNOWN";
                    string lvlName = i < 10 ? $"LVL {levelNum}" : "MAX";
                    bool isDone = levelNum < p.Level || (p.IsMax && i < 10);
                    bool isCurrent = !isDone && (levelNum == p.Level || (p.IsMax && i >= 10));

                    string fillC, lineC, textColor, nodeStatusText;

                    if (isDone) // === ΠΕΡΑΣΜΕΝΑ LEVELS (Completed) ===
                    {
                        fillC = $"'rgba({archRgb}, 0.05)'";
                        lineC = $"'rgba({archRgb}, 0.25)'";
                        textColor = $"'rgba({archRgb}, 0.5)'"; // 50% opacity για τα περασμένα (αχνά)
                        nodeStatusText = $"✓ {lvlName}";
                    }
                    else if (isCurrent) // === ΤΡΕΧΟΝ LEVEL (Current) ===
                    {
                        fillC = $"'rgba({archRgb}, 0.15)'";
                        lineC = $"'{p.ArchColor}'";
                        textColor = $"'{p.ArchColor}'";        // 100% opacity (λαμπερά)
                        nodeStatusText = $"📍 {lvlName}";
                    }
                    else // === ΜΕΛΛΟΝΤΙΚΑ LEVELS (Locked) ===
                    {
                        fillC = "'rgba(255, 255, 255, 0.01)'";
                        lineC = "'rgba(127, 140, 141, 0.15)'";
                        textColor = "'rgba(127, 140, 141, 0.5)'"; // Γκριζαρισμένα
                        nodeStatusText = $"🔒 {lvlName}";
                    }

                    // Το Background του Κελιού
                    shapesJson += $"{{ type: 'rect', xref: 'x', yref: 'paper', x0: {i * 30}, y0: 0, x1: {(i + 1) * 30}, y1: 1, fillcolor: {fillC}, layer: 'below', line: {{ width: {(isCurrent ? 2 : 1)}, dash: '{(isCurrent ? "solid" : "dot")}', color: {lineC} }} }},";

                    // Ο Τίτλος: Προσθέσαμε ένα μικρό κενό (<br><br>) ανάμεσα στο LVL και τον τίτλο για να αναπνέει
                    string fullText = $"'<b>{nodeStatusText}</b><br><br><span style=\"font-size: 10px;\">{rankName}</span>'";

                    // XΡΗΣΗ yref: 'paper' και yanchor: 'top': Αυτό καρφώνει το κείμενο ψηλά ανεξάρτητα από τις γραμμές του γραφήματος!
                    annotationsJson += $"{{ x: {i * 30 + 15}, y: 0.98, xref: 'x', yref: 'paper', yanchor: 'top', text: {fullText}, showarrow: false, align: 'center', font: {{ color: {textColor}, size: 12, family: '\"Share Tech Mono\", monospace' }} }},";
                }
                shapesJson += "]"; annotationsJson += "]";

                string col1Html = "";
                if (p.HasThesis)
                {
                    col1Html = $@"
                    <div style='background: linear-gradient(145deg, #1a2a1a, #0e3a0e); border: 2px solid #2ecc71; border-radius: 10px; padding: 15px; margin-bottom: 0px; margin-top: 10px; box-shadow: 0 0 15px rgba(46, 204, 113, 0.4); text-align: center; animation: pulse-green 3s infinite; height: 130px; display: flex; flex-direction: column; justify-content: center;'>
                        <h3 style='color: #2ecc71; margin: 0; font-size: 1.3rem; text-shadow: 0 0 10px rgba(46,204,113,0.8);'>🐉 FINAL BOSS DEFEATED!</h3>
                        <div style='color: #a9dfbf; font-size: 1rem; margin-top: 5px;'>Διπλωματική ολοκληρώθηκε με: <strong>{p.ThesisGrade}</strong> 🏆</div>
                    </div>";
                }
                else
                {
                    col1Html = @"
                    <div id='boss-container' style='background: linear-gradient(145deg, #1a0f0f, #2a1111); border: 2px solid #e74c3c; border-radius: 10px; padding: 15px; margin-bottom: 10px; margin-top: 10px; box-shadow: 0 0 15px rgba(231,76,60,0.4); text-align: center; transition: all 0.4s ease;'>
                        <h3 id='boss-title' style='color: #e74c3c; margin: 0; font-size: 1.2rem; text-shadow: 0 0 10px rgba(231,76,60,0.4); transition: color 0.4s ease;'>⚠️ FINAL BOSS: LURKING</h3>
                        <div style='color: #f5b7b1; font-size: 1rem; margin-top: 5px;'>Διπλωματική Εργασία (30 ECTS)</div>
                        <div style='background: #0a0e17; border: 1px solid #34495e; border-radius: 8px; height: 16px; margin-top: 12px; position: relative; overflow: hidden; box-shadow: inset 0 0 5px #000;'>
                            <div id='boss-hp-bar' style='width: 100%; background: #e74c3c; height: 100%; transition: width 0.6s cubic-bezier(0.4, 0, 0.2, 1), background 0.4s ease;'></div>
                            <div id='boss-hp-text' style='position: absolute; top: 0; left: 0; width: 100%; font-family: ""Share Tech Mono"", monospace; font-size: 0.65rem; color: #fff; line-height: 16px; font-weight: bold; text-shadow: 1px 1px 2px #000;'>HP: 100 / 100</div>
                        </div>
                    </div>
                    <div style='font-size: 0.8rem; color: #7f8c8d; font-family: monospace; margin-bottom: 5px;'>// TACTICAL MILESTONES:</div>
                    <div style='display:flex; flex-direction:column; gap:8px; margin-left: 5px;'>
                        <label class='milestone-label'><input type='checkbox' class='boss-cb' onchange='updateBossHp()'> 📖 1. Έρευνα & Βιβλιογραφία</label>
                        <label class='milestone-label'><input type='checkbox' class='boss-cb' onchange='updateBossHp()'> 💻 2. Υλοποίηση</label>
                        <label class='milestone-label'><input type='checkbox' class='boss-cb' onchange='updateBossHp()'> 📝 3. Συγγραφή Κειμένου & Μετρήσεις</label>
                        <label class='milestone-label'><input type='checkbox' class='boss-cb' onchange='updateBossHp()'> 🎯 4. Ολοκλήρωση Παρουσίασης</label>
                    </div>";
                }

                string col2Html = "";
                if (hasNet)
                {
                    string radarHtml = nextTarget != null ?
                        $@"🎯 <b>NEXT MINI-BOSS:</b> {nextTarget.Title} <span style='color: #f39c12;'>[{nextTarget.Ects} ECTS]</span>" :
                        @"<span style='color: #2ecc71;'>📡 <b>RADAR:</b> Όλα τα Mini-Bosses έχουν εξολοθρευτεί!</span>";

                    col2Html = $@"
                    <div style='background: linear-gradient(145deg, #0f2027, #203a43); border: 2px solid #3498db; border-radius: 10px; padding: 15px; margin-bottom: 0px; margin-top: 10px; box-shadow: 0 0 15px rgba(52, 152, 219, 0.4); text-align: center; animation: pulse-blue 3s infinite; display: flex; flex-direction: column; justify-content: center;'>
                        <h3 style='color: #3498db; margin: 0; font-size: 1.2rem; text-shadow: 0 0 10px rgba(52,152,219,0.8);'>🛡️ NIGHTMARE CLEARED!</h3>
                        <div style='color: #aed6f1; font-size: 1rem; margin-top: 5px;'>Δίκτυα Υπολογιστών Ι: Επέζησες με <strong>{netGrade}</strong> ⚔️</div>
                    </div>
                    <div style='height: 12px;'></div>
                    <div class='loot-radar-container'>
                        <div class='loot-section'>
                            <div class='loot-header'>💎 LEGENDARY LOOT ACQUIRED</div>
                            <div class='loot-item'>✓ <b>Item:</b> Fiber Optic Cable</div>
                            <div class='loot-item'>✓ <b>Stats Gained:</b> +7 ECTS (Grade: {netGrade})</div>
                        </div>
                        <div class='radar-section'>
                            <div class='radar-scan-line'></div>
                            <div class='radar-text'>{radarHtml}</div>
                        </div>
                    </div>";
                }
                else
                {
                    col2Html = @"
                    <div style='background: linear-gradient(145deg, #2b1055, #4b1a7d); border: 2px solid #9b59b6; border-radius: 10px; padding: 15px; margin-bottom: 0px; margin-top: 10px; box-shadow: 0 0 15px rgba(155, 89, 182, 0.4); text-align: center; animation: pulse-purple 2s infinite; display: flex; flex-direction: column; justify-content: center;'>
                        <h3 style='color: #9b59b6; margin: 0; font-size: 1.2rem; text-shadow: 0 0 10px rgba(155,89,182,0.8);'>💀 HIDDEN BOSS: ALIVE</h3>
                        <div style='color: #d7bde2; font-size: 1rem; margin-top: 5px;'>Ο εφιάλτης ""Δίκτυα Υπολογιστών Ι"" παραμονεύει...</div>
                    </div>
                    <div style='height: 12px;'></div>
                    <div class='intel-container'>
                        <div class='intel-header'>⚠️ BOSS INTEL (CLASSIFIED)</div>
                        <div class='intel-item'><b>Threat Level:</b> VERY HIGH (7 ECTS)</div>
                        <div class='intel-item'><b>Recommended Level:</b> 7th Semester+</div>
                        <div class='intel-item'><b>Weakness:</b> Παλαιότερα θέματα, επίλυση ασκήσεων και αφομοίωση θεωρίας</div>
                        <div class='intel-item' style='color: #e74c3c; margin-top: 8px; font-weight: bold;'>Status: AWAITING EXTERMINATION</div>
                    </div>";
                }

                string bossArenaHtml = $@"
                <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-top: 20px; align-items: stretch;'>
                    <div style='display: flex; flex-direction: column;'>{col1Html}</div>
                    <div style='display: flex; flex-direction: column; justify-content: space-between;'>{col2Html}</div>
                </div>";

                // --- LIVE TERMINAL SYSTEM LOG ---
                var recentCourses = passedGrades
                    .OrderByDescending(g => g.AcademicYear)
                    .ThenByDescending(g => g.ExamOrder)
                    .Take(5)
                    .Reverse()
                    .ToList();

                string terminalLines = "";
                foreach (var g in recentCourses)
                {
                    terminalLines += $"<span style='color: #00ffcc;'>[SYS_UPDATE]</span> &gt; Course '{g.CourseId.Title.Replace("'", "&apos;")}' cleared. Grade: <span style='color: #f1c40f;'>{g.FinalGrade:F1}</span><br>";
                    if (g.Ects > 0)
                    {
                        terminalLines += $"<span style='color: #2ecc71;'>[XP_GAIN]</span> &gt; +{g.Ects:g} ECTS acquired.<br>";
                    }
                }
                terminalLines += "<span style='color: #00ffcc;'>[STATUS]</span> &gt; Saving progress... OK.<br><span class='blink-cursor'>_</span>";

                string terminalHtml = $@"
                <div style='margin-top: 30px;'>
                    <h3 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; margin-top: 30px; font-size: 1.5rem;'>📟 Live System Log</h3>
                    <div style='background-color: #0a0e17; border: 1px solid #34495e; border-radius: 8px; padding: 15px; font-family: ""Share Tech Mono"", monospace; font-size: 0.95rem; color: #ecf0f1; box-shadow: inset 0 0 10px #000; overflow-x: auto; margin-bottom: 20px;'>
                        <div style='color: #7f8c8d; font-size: 0.8rem; margin-bottom: 10px; border-bottom: 1px solid #2c3e50; padding-bottom: 5px;'>root@cse-uoi:~/logs/recent_activity.log</div>
                        <div style='line-height: 1.5;'>
                            {terminalLines}
                        </div>
                    </div>
                </div>
                <style>
                .blink-cursor {{
                    animation: blinker 1s linear infinite;
                    color: #00ffcc;
                    font-weight: bold;
                }}
                @keyframes blinker {{
                    50% {{ opacity: 0; }}
                }}
                </style>";

                // --- ACTIVE QUEST BOARD (REMATCHES) ---
                var topQuests = missingBounties
                    .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(m.Title, @"Δ[ιί]κτυα Υπολογιστ[ωώ]ν\s*[ΙI1](?!\s*[ΙI1])", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .OrderByDescending(m => m.Ects)
                    .Take(3)
                    .ToList();

                string bountiesHtml = "";
                if (topQuests.Any())
                {
                    string questsCards = "";
                    foreach (var quest in topQuests)
                    {
                        questsCards += $@"
                        <div class='bounty-card'>
                            <div class='bounty-badge'>WANTED ALIVE</div>
                            <h4 class='bounty-title'>{quest.Title}</h4>
                            <div class='bounty-details'>
                                ⚔️ <strong>Type:</strong> Revenge Quest<br>
                                💰 <strong>Bounty:</strong> <span style='color: #2ecc71; font-weight: bold;'>{quest.Ects:g} ECTS / XP</span>
                            </div>
                        </div>";
                    }

                    bountiesHtml = $@"
                    <div style='margin-top: 30px;'>
                        <h3 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; margin-top: 30px; font-size: 1.5rem;'>📜 Active Bounties (Rematch Required)</h3>
                        <div style='display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 20px; margin-top: 25px;'>
                            {questsCards}
                        </div>
                    </div>
                    <style>
                    .bounty-card {{ background-color: #1a1a1a; border: 1px dashed #e74c3c; border-radius: 8px; padding: 20px 15px 15px 15px; position: relative; box-shadow: 2px 2px 10px rgba(231, 76, 60, 0.1); transition: transform 0.2s, box-shadow 0.2s; }}
                    .bounty-card:hover {{ transform: translateY(-5px); box-shadow: 0 5px 15px rgba(231, 76, 60, 0.3); }}
                    .bounty-badge {{ position: absolute; top: -12px; left: 15px; background: #e74c3c; color: #fff; font-weight: bold; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-family: monospace; }}
                    .bounty-title {{ color: #e74c3c; margin-top: 5px; margin-bottom: 10px; font-size: 1.1rem; text-shadow: 0 0 5px rgba(231, 76, 60, 0.3); }}
                    .bounty-details {{ color: #bdc3c7; font-size: 0.95rem; line-height: 1.6; }}
                    </style>";
                }

                // --- LEGENDARY ACHIEVEMENTS (LOOT) & HOLO-CARDS ---
                List<Dictionary<string, string>> badges = new List<Dictionary<string, string>>();
                var regularCourses = passedGrades.Where(c => !c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) && !c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase)).ToList();

                if (regularCourses.Any(g => g.FinalGrade == 10))
                    badges.Add(new Dictionary<string, string> { { "icon", "🎯" }, { "title", "Απόλυτο 10άρι" }, { "desc", "Πέτυχες 10 σε τουλάχιστον ένα μάθημα!" } });

                var maxEctsPeriod = regularCourses.GroupBy(g => new { g.AcademicYear, g.ExamMonth }).Select(g => g.Sum(c => c.Ects)).DefaultIfEmpty(0).Max();
                if (maxEctsPeriod >= 30)
                    badges.Add(new Dictionary<string, string> { { "icon", "🚂" }, { "title", "Μηχανή ECTS" }, { "desc", $"Συγκέντρωσες {maxEctsPeriod:g} ECTS σε μία εξεταστική!" } });

                var maxSeptCourses = regularCourses.Where(g => g.ExamMonth == "Σεπ").GroupBy(g => g.AcademicYear).Select(g => g.Count()).DefaultIfEmpty(0).Max();
                if (maxSeptCourses >= 3)
                    badges.Add(new Dictionary<string, string> { { "icon", "🛡️" }, { "title", "Αντεπίθεση Σεπτέμβρη" }, { "desc", "Έσωσες τη χρονιά περνώντας 3+ μαθήματα τον Σεπτέμβρη!" } });

                if (p.HasThesis)
                    badges.Add(new Dictionary<string, string> { { "icon", "🐉" }, { "title", "Boss Defeated" }, { "desc", "Ολοκλήρωσες τη Διπλωματική Εργασία!" } });

                if (p.HasInternship)
                    badges.Add(new Dictionary<string, string> { { "icon", "🌐" }, { "title", "Hello World!" }, { "desc", "Ολοκλήρωσες την Πρακτική Άσκηση!" } });

                if (p.Streak >= 3)
                    badges.Add(new Dictionary<string, string> { { "icon", "🔥" }, { "title", "On Fire" }, { "desc", $"Πέρασες 2+ μαθήματα για {p.Streak} σερί εξεταστικές!" } });

                badges.AddRange(FocusAchievements());

                string badgesHtml = "";
                if (badges.Any())
                {
                    string badgesCards = "";
                    foreach (var b in badges)
                    {
                        badgesCards += $@"
                        <div class='loot-badge holo-card'>
                            <div class='float-30' style='font-size: 2.5rem; margin-bottom: 10px; text-shadow: 0 2px 4px rgba(0,0,0,0.5);'>{b["icon"]}</div>
                            <div class='float-20' style='color: #f1c40f; font-weight: bold; font-size: 1.1rem; margin-bottom: 8px;'>{b["title"]}</div>
                            <div class='float-10' style='color: #ecf0f1; font-size: 0.85rem; line-height: 1.3;'>{b["desc"]}</div>
                        </div>";
                    }

                    badgesHtml = $@"
                    <div style='margin-top: 30px;'>
                        <h3 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; margin-top: 30px; font-size: 1.5rem;'>🏅 Legendary Achievements (Loot)</h3>
                        <div style='display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 15px; margin-top: 25px;'>
                            {badgesCards}
                        </div>
                    </div>
                    <style>
                    .holo-card {{ position: relative; overflow: hidden; transition: all 0.4s cubic-bezier(0.175, 0.885, 0.32, 1.275); transform-style: preserve-3d; }}
                    .holo-card::after {{ content: ''; position: absolute; top: 0; left: -150%; width: 50%; height: 100%; background: linear-gradient(to right, rgba(255,255,255,0) 0%, rgba(255,255,255,0.15) 50%, rgba(255,255,255,0) 100%); transform: skewX(-25deg); transition: left 0.6s ease-in-out; z-index: 1; pointer-events: none; }}
                    .holo-card:hover::after {{ left: 200%; }}
                    @keyframes pulse-gold {{ 0% {{ box-shadow: 0 0 5px #f1c40f, inset 0 0 2px #f1c40f; border-color: #f1c40f; }} 50% {{ box-shadow: 0 0 15px #f39c12, inset 0 0 5px #f39c12; border-color: #f39c12; }} 100% {{ box-shadow: 0 0 5px #f1c40f, inset 0 0 2px #f1c40f; border-color: #f1c40f; }} }}
                    .loot-badge {{ background: linear-gradient(145deg, #1a252f, #2c3e50); border: 2px solid #f1c40f; border-radius: 12px; padding: 15px; text-align: center; animation: pulse-gold 3s infinite ease-in-out; min-height: 140px; z-index: 2; }}
                    .loot-badge:hover {{ animation: none; transform: perspective(800px) scale(1.08) rotateX(-5deg) rotateY(5deg) translateZ(10px); box-shadow: 10px 15px 25px rgba(0,0,0,0.7), 0 0 25px rgba(241, 196, 15, 0.8); border-color: #fff; }}
                    .float-30 {{ transform: translateZ(30px); }} .float-20 {{ transform: translateZ(20px); }} .float-10 {{ transform: translateZ(10px); }}
                    </style>";
                }

                // --- HALL OF FAME, MILESTONES & SURVIVAL ---
                var semStats = regularCourses
                    .GroupBy(g => new { g.AcademicYear, g.ExamMonth })
                    .Select(group => new
                    {
                        Period = PeriodLabel(group.Key.ExamMonth, group.Key.AcademicYear),
                        Count = group.Count(),
                        GPA = group.Sum(c => c.Ects) > 0 ? group.Sum(c => c.FinalGrade * c.Ects) / group.Sum(c => c.Ects) : 0
                    })
                    .ToList();

                string hallOfFameHtml = "";
                if (semStats.Any())
                {
                    var golden = semStats.OrderByDescending(s => s.Count).ThenByDescending(s => s.GPA).First();
                    var dark = semStats.OrderBy(s => s.GPA).ThenBy(s => s.Count).First();

                    var top5 = regularCourses.OrderByDescending(c => c.FinalGrade).Take(5).ToList();
                    var survival = regularCourses.Where(c => c.FinalGrade == 5.0).Skip(Math.Max(0, regularCourses.Count(c => c.FinalGrade == 5.0) - 5)).ToList();

                    string trophiesHtml = "";
                    foreach (var c in top5)
                    {
                        string shortYear = AcadYearShort(c.AcademicYear);
                        trophiesHtml += $@"
                        <div class='trophy-card holo-card'>
                            <div class='card-icon float-30'>🥇</div>
                            <div class='card-details float-20' title='{c.CourseId.Title.Replace("'", "&apos;")}'>
                                <div class='course-title trophy-title'>{c.CourseId.Title}</div>
                                <div style='font-size: 0.75rem; color: #bdc3c7;'>📅 {c.ExamMonth} '{shortYear}</div>
                            </div>
                            <div class='grade-badge trophy-badge float-10'>{c.FinalGrade:F1}</div>
                        </div>";
                    }

                    string survivalHtml = "";
                    if (!survival.Any())
                    {
                        survivalHtml = "<div class='info-box' style='color:#bdc3c7; border-color:#bdc3c7; background: rgba(255,255,255,0.05);'>Δεν έχεις περάσει κανένα μάθημα με 5.0! 🦾</div>";
                    }
                    else
                    {
                        foreach (var c in survival)
                        {
                            string shortYear = AcadYearShort(c.AcademicYear); survivalHtml += $@"
                            <div class='survival-card holo-card'>
                                <div class='card-icon float-30'>🪖</div>
                                <div class='card-details float-20' title='{c.CourseId.Title.Replace("'", "&apos;")}'>
                                    <div class='course-title survival-title'>{c.CourseId.Title}</div>
                                    <div style='font-size: 0.75rem; color: #7f8c8d;'>⚠️ {c.ExamMonth} '{shortYear}</div>
                                </div>
                                <div class='grade-badge survival-badge float-10'>{c.FinalGrade:F1}</div>
                            </div>";
                        }
                    }

                    hallOfFameHtml = $@"
                    <div style='margin-top: 30px;'>
                        <h3 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; font-size: 1.5rem;'>🏛 Hall of Fame & Milestones</h3>
                        <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-top: 15px;'>
                            <div class='info-box'>🌟 <b>Χρυσή Εξεταστική:</b> <b>{golden.Period}</b> ({golden.Count} μαθ. | Μ.Ο. {golden.GPA:F2})</div>
                            <div class='info-box' style='border-color: #f39c12; color: #f39c12; background: rgba(243,156,18,0.1);'>💀 <b>Πιο Δύσκολη Εξεταστική:</b> <b>{dark.Period}</b> ({dark.Count} μαθ. | Μ.Ο. {dark.GPA:F2})</div>
                        </div>
                        
                        <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-top: 20px;'>
                            <div>
                                <h5 style='color: #f1c40f; margin-bottom: 10px; border-bottom: 1px solid #f1c40f; padding-bottom: 4px; font-size: 1.1rem;'>🏆 Top 5 Trophies</h5>
                                {trophiesHtml}
                            </div>
                            <div>
                                <h5 style='color: #7f8c8d; margin-bottom: 10px; border-bottom: 1px solid #7f8c8d; padding-bottom: 4px; font-size: 1.1rem;'>🛡️ Barely Survived (5.0)</h5>
                                {survivalHtml}
                            </div>
                        </div>
                    </div>
                    <style>
                    /* Trophies & Survival CSS */
                    .trophy-card {{ background: linear-gradient(145deg, #2a2000, #1a1000); border: 1px solid #f1c40f; border-radius: 6px; padding: 8px 12px; margin-bottom: 8px; display: flex; align-items: center; box-shadow: 0 0 10px rgba(241, 196, 15, 0.1); }}
                    .trophy-card:hover {{ transform: perspective(600px) scale(1.03) rotateX(3deg) rotateY(-3deg); box-shadow: -5px 8px 15px rgba(0,0,0,0.5), 0 0 15px rgba(241, 196, 15, 0.4); }}
                    .survival-card {{ background: #111; border: 1px solid #333; border-left: 3px dashed #e74c3c; border-radius: 4px; padding: 8px 12px; margin-bottom: 8px; display: flex; align-items: center; box-shadow: inset 0 0 10px rgba(0,0,0,0.8); background-image: repeating-linear-gradient(-45deg, transparent, transparent 5px, rgba(255,0,0,0.03) 5px, rgba(255,0,0,0.03) 10px); }}
                    .survival-card:hover {{ transform: perspective(600px) scale(1.03) rotateX(-3deg) rotateY(3deg); box-shadow: 5px 8px 15px rgba(0,0,0,0.5), 0 0 15px rgba(231, 76, 60, 0.3); border-left-style: solid; }}
                    .card-icon {{ font-size: 1.4rem; margin-right: 12px; transform: translateZ(20px); }}
                    .card-details {{ flex-grow: 1; overflow: hidden; transform: translateZ(10px); }}
                    .course-title {{ font-size: 0.85rem; font-weight: bold; margin-bottom: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }}
                    .trophy-title {{ color: #f1c40f; text-shadow: 0 0 3px rgba(241,196,15,0.4); }}
                    .survival-title {{ color: #bdc3c7; }}
                    .grade-badge {{ background: #000; padding: 4px 8px; border-radius: 4px; font-family: 'Share Tech Mono', monospace; font-size: 1rem; font-weight: bold; margin-left: 10px; transform: translateZ(15px); }}
                    .trophy-badge {{ border: 1px solid #f1c40f; color: #f1c40f; box-shadow: 0 0 5px rgba(241,196,15,0.2); }}
                    .survival-badge {{ border: 1px solid #7f8c8d; color: #e74c3c; }}
                    </style>";
                }

                // --- DATA EXTRACTION PROTOCOL ---
                int barLen = 20;
                int filledLen = (int)(Math.Min(p.Ects / 300.0, 1.0) * barLen);
                string progressBar = new string('█', filledLen) + new string('░', barLen - filledLen);

                string mdText = $@"## 🎓 Academic Profile (@panagiotispar)\n\n**University of Ioannina** | Computer Engineering and Informatics\n\n### 📊 Base Stats\n- ⚔️ **Rank:** {p.Rank}\n- 🎯 **GPA:** {p.Gpa:F2} / 10.0\n- 📚 **Courses:** {p.Courses} / {targetCourses}\n- 🎓 **Progress:** `[{progressBar}]` {(p.Ects / 300.0) * 100:F1}% ({p.Ects:g}/300 ECTS)\n\n### 🏅 Unlocked Achievements\n";
                foreach (var b in badges) { mdText += $"- **{b["icon"]} {b["title"]}**: {b["desc"]}\n"; }

                string csvText = "Μάθημα,Βαθμός,ECTS,Περίοδος,Ακαδ. Έτος\n";
                string tableHtml = "<table class='data-table'><tr><th>Μάθημα</th><th>Βαθμός</th><th>ECTS</th><th>Περίοδος</th></tr>";
                foreach (var g in passedGrades)
                {
                    string safeTitle = g.CourseId.Title.Replace("\"", "\"\"");
                    csvText += $"\"{safeTitle}\",{g.FinalGrade:F1},{g.Ects},{g.ExamMonth},{g.AcademicYear}\n";
                    tableHtml += $"<tr><td>{g.CourseId.Title}</td><td>{g.FinalGrade:F1}</td><td>{g.Ects}</td><td>{PeriodLabel(g.ExamMonth, g.AcademicYear)}</td></tr>";
                }
                tableHtml += "</table>";

                string jsMdText = mdText.Replace("\n", "\\n").Replace("'", "\\'").Replace("\"", "\\\"");
                string jsCsvText = csvText.Replace("\n", "\\n").Replace("'", "\\'").Replace("\"", "\\\"");

                string dataExtractionHtml = $@"
                <div style='margin-top: 30px;'>
                    <h3 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; font-size: 1.5rem;'>💾 Data Extraction Protocol</h3>
                    <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-top: 15px; align-items: start;'> <!-- Προσθήκη align-items: start; -->                        
                        <!-- Markdown Column -->
                        <div class='extraction-card'>
                            <details class='cyber-details'>
                                <summary>🐙 Εξαγωγή Προφίλ για GitHub (README.md)</summary>
                                <div class='details-content'>
                                    <pre class='md-preview'>{mdText.Replace("\n", "<br>")}</pre>
                                    <button class='cyber-btn' onclick=""downloadFile('progress.md', '{jsMdText}')"">💾 Κατέβασμα ως progress.md</button>
                                </div>
                            </details>
                        </div>

                        <!-- CSV Column -->
                        <div class='extraction-card'>
                            <details class='cyber-details'>
                                <summary>🗄️ Προεπισκόπηση Καθαρών Δεδομένων (Raw Data)</summary>
                                <div class='details-content'>
                                    <div class='table-container'>{tableHtml}</div>
                                    <button class='cyber-btn' onclick=""downloadFile('academic_database.csv', '{jsCsvText}')"">💾 Κατέβασμα Database (.csv)</button>
                                </div>
                            </details>
                        </div>

                    </div>
                </div>
                <style>
                <style>
                .extraction-card {{ background: #0a0e17; border: 1px solid #34495e; border-radius: 6px; padding: 15px; box-shadow: inset 0 0 10px rgba(0,0,0,0.5); align-self: start; }} /* Το align-self: start; αποτρέπει το τέντωμα */
                .cyber-details summary {{ cursor: pointer; color: #3498db; font-weight: bold; padding: 10px; background: rgba(52,152,219,0.1); border-radius: 4px; transition: background 0.3s; list-style: none; }}
                .cyber-details summary:hover {{ background: rgba(52,152,219,0.2); }}
                .cyber-details summary::-webkit-details-marker {{ display: none; }}
                .details-content {{ margin-top: 15px; display: none; }} /* Απόκρυψη από προεπιλογή */
                .cyber-details[open] .details-content {{ display: block; }} /* Εμφάνιση μόνο όταν είναι open */
                .md-preview {{ background: #111b24; padding: 15px; border-radius: 4px; color: #bdc3c7; font-family: monospace; font-size: 0.85rem; max-height: 250px; overflow-y: auto; border: 1px dashed #2c3e50; white-space: pre-wrap; }}
                .table-container {{ max-height: 250px; overflow-y: auto; border: 1px dashed #2c3e50; border-radius: 4px; margin-bottom: 15px; }}
                .data-table {{ width: 100%; border-collapse: collapse; color: #ecf0f1; font-size: 0.85rem; text-align: left; }}
                .data-table th {{ background: #111b24; position: sticky; top: 0; padding: 8px; color: #3498db; border-bottom: 1px solid #2c3e50; z-index: 2; }}
                .data-table td {{ padding: 6px 8px; border-bottom: 1px solid #1a252f; }}
                .cyber-btn {{ width: 100%; background: #111b24; color: #00ffcc; border: 1px solid #00ffcc; padding: 10px; border-radius: 4px; cursor: pointer; font-family: 'Share Tech Mono', monospace; font-size: 1rem; transition: all 0.3s; margin-top: 10px; }}
                .cyber-btn:hover {{ background: rgba(0,255,204,0.1); box-shadow: 0 0 10px rgba(0,255,204,0.4); }}
                </style>
                <script>
                function downloadFile(filename, content) {{
                    const blob = new Blob([content], {{ type: 'text/plain;charset=utf-8' }});
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement('a');
                    a.href = url;
                    a.download = filename;
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                    URL.revokeObjectURL(url);
                }}
                </script>";

                string bossJson = "[" + string.Join(",", _save.Boss.Select(b => b ? "true" : "false")) + "]";

                string html = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='UTF-8'>
                    <link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&family=Libre+Barcode+39+Text&display=swap' rel='stylesheet'>
                    <script src='https://cdn.plot.ly/plotly-2.35.2.min.js'></script>
                    <style>
                        body {{ margin: 0; padding: 30px; font-family: 'Share Tech Mono', monospace; color: #bdc3c7; overflow-x: hidden; }}
                        .bg-blueprint {{
                            position: fixed; top: 0; left: 0; width: 100vw; height: 100vh; z-index: -1; pointer-events: none;
                            background-color: #05070a;
                            background-image: linear-gradient(rgba(0, 255, 204, 0.03) 1px, transparent 1px), linear-gradient(90deg, rgba(0, 255, 204, 0.03) 1px, transparent 1px);
                            background-size: 40px 40px; background-position: center center;
                        }}
                        
                        /* ID CARD CSS */
                        .id-card-wrapper {{ display: flex; justify-content: center; margin-bottom: 30px; perspective: 1000px; }}
                        .id-card {{ background: linear-gradient(135deg, #0a0e17 0%, #111b24 100%); border: 2px solid {p.ArchColor}; border-radius: 15px; width: 100%; max-width: 680px; padding: 25px; box-shadow: 0 10px 30px {p.ArchColor}40, inset 0 0 20px rgba(0,0,0,0.8); display: flex; align-items: center; position: relative; overflow: hidden; transform: rotateX(2deg) rotateY(-2deg); transition: transform 0.3s ease; }}
                        .holo-glare {{ position: absolute; top: -50%; left: -50%; width: 200%; height: 200%; background: linear-gradient(45deg, rgba(255,255,255,0) 40%, rgba(255,255,255,0.1) 50%, rgba(255,255,255,0) 60%); transform: rotate(30deg); pointer-events: none; animation: holo-glare 5s infinite linear; }}
                        @keyframes holo-glare {{ 0% {{ transform: translateX(-120%) rotate(30deg); }} 100% {{ transform: translateX(200%) rotate(30deg); }} }}
                        .avatar-box {{ background: rgba(0,0,0,0.5); border: 2px solid {p.ArchColor}; border-radius: 12px; width: 130px; height: 130px; display: flex; justify-content: center; align-items: center; font-size: 5rem; text-shadow: 0 0 20px {p.ArchColor}; margin-right: 25px; flex-shrink: 0; position: relative; box-shadow: inset 0 0 15px {p.ArchColor}40; }}
                        .verified-tag {{ position: absolute; bottom: -12px; background: {p.ArchColor}; color: #000; font-size: 0.7rem; font-weight: bold; padding: 3px 10px; border-radius: 4px; text-transform: uppercase; box-shadow: 0 0 10px {p.ArchColor}; }}
                        
                        /* XP BAR CSS */
                        .xp-container {{ background-color: #111b24; padding: 15px; border-radius: 10px; border: 1px solid #2c3e50; box-shadow: inset 0 0 10px rgba(0,0,0,0.5); margin-bottom: 30px; }}
                        .xp-bar-bg {{ position: relative; width: 100%; background-color: #05070a; border-radius: 20px; height: 22px; border: 2px solid #34495e; overflow: visible; margin-top: 25px; }}
                        .xp-fill {{ width: {xpPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}%; background: linear-gradient(90deg, #f39c12 0%, #f1c40f 100%); height: 100%; border-radius: 20px; box-shadow: 0 0 10px #f1c40f; transition: width 1s ease-in-out; }}
                        .xp-runner {{ position: absolute; top: -35px; left: calc({xpPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}% - 25px); font-size: 35px; transform: scaleX(-1); text-shadow: 0 2px 4px rgba(0,0,0,0.5); z-index: 10; }}
                        
                        /* INVENTORY GRID */
                        .inventory-grid {{ display: grid; grid-template-columns: repeat(4, 1fr); gap: 15px; margin-top: 30px; }}
                        .inv-card {{ background-color: #16212b; border: 2px solid #2c3e50; border-radius: 12px; padding: 15px; text-align: center; transition: all 0.3s ease; box-shadow: 0 4px 6px rgba(0,0,0,0.3); }}
                        .inv-card:hover {{ border-color: #00ffcc; box-shadow: 0 0 15px rgba(0, 255, 204, 0.3); transform: translateY(-5px); }}
                        .mini-bar-bg {{ background-color: rgba(255,255,255,0.1); border-radius: 6px; height: 8px; width: 100%; overflow: hidden; margin-top: 10px; }}
                        .mini-bar-fill {{ background: linear-gradient(90deg, #2980b9, #3498db); height: 100%; }}

                        /* --- BOSS ARENA CSS --- */
                        .loot-radar-container {{ background: #0a0e17; border: 1px solid #34495e; border-radius: 6px; padding: 18px; box-shadow: inset 0 0 15px rgba(0,0,0,0.6); }}
                        .loot-header {{ color: #3498db; font-family: 'Share Tech Mono', monospace; font-weight: bold; font-size: 1.05rem; margin-bottom: 10px; text-shadow: 0 0 5px #3498db; }}
                        .loot-item {{ color: #bdc3c7; font-size: 0.9rem; margin-bottom: 6px; padding-left: 10px; border-left: 2px solid #3498db; }}
                        .radar-section {{ position: relative; margin-top: 15px; padding-top: 15px; border-top: 1px dashed #34495e; overflow: hidden; }}
                        .radar-text {{ color: #e74c3c; font-family: monospace; font-size: 0.9rem; letter-spacing: 0.5px; }}
                        .radar-scan-line {{ position: absolute; top: 0; left: 0; width: 100%; height: 1px; background: rgba(231,76,60,0.8); box-shadow: 0 0 15px 2px #e74c3c; animation: scan 2.5s linear infinite; }}
                        @keyframes scan {{ 0% {{ top: 0; opacity: 0; }} 10% {{ opacity: 1; }} 90% {{ opacity: 1; }} 100% {{ top: 100%; opacity: 0; }} }}
                        .intel-container {{ background: rgba(231, 76, 60, 0.05); border: 1px solid #e74c3c; border-left: 4px solid #e74c3c; border-radius: 4px; padding: 18px; box-shadow: inset 0 0 10px rgba(231,76,60,0.1); }}
                        .intel-header {{ color: #e74c3c; font-family: 'Share Tech Mono', monospace; font-weight: bold; font-size: 1.1rem; margin-bottom: 12px; letter-spacing: 1px; }}
                        .intel-item {{ color: #bdc3c7; font-size: 0.9rem; margin-bottom: 6px; }}
                        @keyframes pulse-red {{ 0% {{ box-shadow: 0 0 5px rgba(231,76,60,0.2); }} 50% {{ box-shadow: 0 0 20px rgba(231,76,60,0.6); }} 100% {{ box-shadow: 0 0 5px rgba(231,76,60,0.2); }} }}
                        @keyframes pulse-green {{ 0% {{ box-shadow: 0 0 5px rgba(46,204,113,0.2); }} 50% {{ box-shadow: 0 0 20px rgba(46,204,113,0.6); }} 100% {{ box-shadow: 0 0 5px rgba(46,204,113,0.2); }} }}
                        @keyframes pulse-purple {{ 0% {{ box-shadow: 0 0 5px rgba(155,89,182,0.2); }} 50% {{ box-shadow: 0 0 20px rgba(155,89,182,0.6); }} 100% {{ box-shadow: 0 0 5px rgba(155,89,182,0.2); }} }}
                        @keyframes pulse-blue {{ 0% {{ box-shadow: 0 0 5px rgba(52,152,219,0.2); }} 50% {{ box-shadow: 0 0 20px rgba(52,152,219,0.6); }} 100% {{ box-shadow: 0 0 5px rgba(52,152,219,0.2); }} }}
                        .milestone-label {{ display: block; color: #ecf0f1; font-size: 0.95rem; cursor: pointer; transition: color 0.2s; }}
                        .milestone-label:hover {{ color: #00ffcc; }}
                        input[type=checkbox].boss-cb {{ accent-color: #e74c3c; transform: scale(1.2); margin-right: 8px; cursor: pointer; }}
                    </style>
                </head>
                <body>
                    <div class='bg-blueprint'></div>

                    <!-- ID CARD -->
                    <div class='id-card-wrapper'>
                        <div class='id-card'>
                            <div class='holo-glare'></div>
                            <div class='avatar-box'>{p.Avatar}<div class='verified-tag'>VERIFIED</div></div>
                            <div style='flex-grow: 1; z-index: 2;'>
                                <div style='color: #7f8c8d; font-size: 0.75rem; letter-spacing: 3px; margin-bottom: 2px;'>MERCENARY ID // UOI-CSE</div>
                                <h2 style='color: #ecf0f1; margin: 0 0 10px 0; font-size: 2.2rem; letter-spacing: 1px;'>{p.Rank}</h2>
                                <div style='display: flex; gap: 15px; margin-bottom: 15px;'>
                                    <div style='background: rgba(255,255,255,0.05); padding: 8px 12px; border-radius: 6px; border-left: 3px solid {p.ArchColor}; flex: 1;'>
                                        <div style='color: #7f8c8d; font-size: 0.65rem; text-transform: uppercase;'>Class Profile</div>
                                        <div style='color: {p.ArchColor}; font-weight: bold; font-size: 1rem;'>{p.ArchTitle}</div>
                                    </div>
                                    <div style='background: rgba(255,255,255,0.05); padding: 8px 12px; border-radius: 6px; border-left: 3px solid #f1c40f; flex: 1;'>
                                        <div style='color: #7f8c8d; font-size: 0.65rem; text-transform: uppercase;'>Level</div>
                                        <div style='color: #f1c40f; font-weight: bold; font-size: 1rem;'>{p.LevelLabel}</div>
                                    </div>
                                    <div style='background: rgba(255,255,255,0.05); padding: 8px 12px; border-radius: 6px; border-left: 3px solid #3498db;'>
                                        <div style='color: #7f8c8d; font-size: 0.65rem; text-transform: uppercase;'>Overall GPA</div>
                                        <div style='color: #3498db; font-weight: bold; font-size: 1.1rem;'>{p.Gpa:F2}</div>
                                    </div>
                                </div>
                                <div style='display: flex; justify-content: space-between; align-items: flex-end; border-top: 1px dashed #34495e; padding-top: 10px;'>
                                    <div style='font-family: ""Libre Barcode 39 Text"", cursive; font-size: 3rem; color: #bdc3c7; line-height: 0.7;'>*UOI-{(int)p.Ects}*</div>
                                    <div style='text-align: right; color: #7f8c8d; font-size: 0.7rem; line-height: 1.4;'>
                                        STATUS: <span style='color: #2ecc71; font-weight: bold;'>ONLINE</span><br>
                                        ACHIEVEMENTS: <span style='color: #f1c40f; font-weight: bold;'>{p.Badges} UNLOCKED</span>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>

                    <!-- XP BAR -->
                    <div class='xp-container'>
                        <div style='display: flex; justify-content: space-between; color: white; font-weight: bold; font-size: 1.1rem;'>
                            <span>Level {(p.IsMax ? "MAX" : p.Level.ToString())}</span>
                            <span>{p.XpInLevel:g} / 30 XP</span>
                        </div>
                        <div class='xp-bar-bg'>
                            <div class='xp-fill'></div>
                            <div class='xp-runner'>🏃‍♀️</div>
                        </div>
                        <div style='text-align: right; color: #bdc3c7; font-size: 0.85rem; margin-top: 12px;'>Συνολικά ECTS: {p.Ects:g}</div>
                    </div>

                    <!-- DYNAMIC SKILL TREE -->
                    <details class='cyber-details' open style='margin-top: 30px; margin-bottom: 20px;'>
                        <summary style='font-size: 1.2rem; border-bottom: 1px solid #3498db; border-radius: 0; background: transparent; padding-left: 0; display: flex; justify-content: space-between; align-items: center;'>
                            <span>🌌 Neural Network Skill Tree (Dynamic Progression)</span>
                            <span class='tap-hint' style='font-size: 0.75rem; color: #7f8c8d; font-weight: normal; letter-spacing: 1px;'>[ tap to close ]</span>
                        </summary>
                        <div class='details-content'>
                            <div id='skill-tree-chart' style='background: #05070a; border: 1px solid #1a2536; border-radius: 8px; padding: 10px; height: 550px; box-shadow: inset 0 0 20px rgba(0,0,0,0.8);'></div>
                        </div>
                    </details>

                    <!-- INVENTORY STATS -->
                    <div style='color:#e74c3c; font-size:1.5rem; font-weight:bold; margin-top:30px; border-bottom:1px solid #2c3e50; padding-bottom:10px;'>🎒 Inventory Stats</div>
                    <div class='inventory-grid'>
                        <div class='inv-card'>
                            <div style='font-size:1.6rem; margin-bottom:8px;'>📚</div>
                            <div style='color:#7f8c8d; font-size:0.85rem; font-weight:bold;'>ΜΑΘΗΜΑΤΑ</div>
                            <div style='color:#ecf0f1; font-size:1.9rem;'>{p.Courses} / {targetCourses}</div>
                            <div class='mini-bar-bg'><div class='mini-bar-fill' style='width: {(Math.Min((double)p.Courses / targetCourses, 1.0) * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)}%;'></div></div>
                        </div>
                        <div class='inv-card'>
                            <div style='font-size:1.6rem; margin-bottom:8px;'>📜</div>
                            <div style='color:#7f8c8d; font-size:0.85rem; font-weight:bold;'>ΣΥΝΟΛΟ ECTS</div>
                            <div style='color:#ecf0f1; font-size:1.9rem;'>{p.Ects:g} / 300</div>
                            <div class='mini-bar-bg'><div class='mini-bar-fill' style='background: linear-gradient(90deg, #229954, #2ecc71); width: {(Math.Min(p.Ects / 300.0, 1.0) * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)}%;'></div></div>
                        </div>
                        <div class='inv-card'>
                            <div style='font-size:1.6rem; margin-bottom:8px;'>🎯</div>
                            <div style='color:#7f8c8d; font-size:0.85rem; font-weight:bold;'>ΤΡΕΧΩΝ Μ.Ο.</div>
                            <div style='color:#f1c40f; font-size:2.2rem;'>{p.Gpa:F2}</div>
                        </div>
                        <div class='inv-card'>
                            <div style='font-size:1.6rem; margin-bottom:8px;'>{(p.Courses >= targetCourses ? "🎓" : "🚀")}</div>
                            <div style='color:#7f8c8d; font-size:0.85rem; font-weight:bold;'>STATUS ΠΤΥΧΙΟΥ</div>
                            <div style='color:#ecf0f1; font-size:1.3rem; margin-top:10px;'>
                                {statusText}
                            </div>
                        </div>
                    </div>
                    <!-- MESSAGES & BOSS ARENA -->
                    {degreeHtml}
                    {internshipHtml}
                    {bossArenaHtml}
                    {terminalHtml}
                    {bountiesHtml}
                    {badgesHtml}
                    {hallOfFameHtml}
                    {dataExtractionHtml}

                    <script>
                        // Μηχανισμός Boss HP (Για τα Checkboxes της Διπλωματικής)
                        function updateBossHp() {{
                            let cbs = document.querySelectorAll('.boss-cb');
                            if(cbs.length === 0) return; 
                            
                            let dmg = 0;
                            cbs.forEach(cb => {{ if(cb.checked) dmg += 25; }});
                            let hp = 100 - dmg;
                            if (window.chrome && window.chrome.webview) {{
                                window.chrome.webview.postMessage(JSON.stringify({{ type: 'boss', value: Array.from(cbs).map(c => c.checked) }}));
                            }}
                            
                            let bar = document.getElementById('boss-hp-bar');
                            let text = document.getElementById('boss-hp-text');
                            let title = document.getElementById('boss-title');
                            let container = document.getElementById('boss-container');
                            
                            bar.style.width = hp + '%';
                            text.innerText = 'HP: ' + hp + ' / 100';
                            
                            if(hp > 50) {{
                                container.style.borderColor = '#e74c3c';
                                container.style.boxShadow = '0 0 15px rgba(231,76,60,0.4)';
                                title.style.color = '#e74c3c';
                                title.style.textShadow = '0 0 10px rgba(231,76,60,0.4)';
                                title.innerText = '⚠️ FINAL BOSS: LURKING';
                                bar.style.background = '#e74c3c';
                            }} else if(hp > 0) {{
                                container.style.borderColor = '#f39c12';
                                container.style.boxShadow = '0 0 15px rgba(243,156,18,0.5)';
                                title.style.color = '#f39c12';
                                title.style.textShadow = '0 0 10px rgba(243,156,18,0.5)';
                                title.innerText = '🔥 BOSS IS WEAKENED!';
                                bar.style.background = '#f39c12';
                            }} else {{
                                container.style.borderColor = '#2ecc71';
                                container.style.boxShadow = '0 0 15px rgba(46,204,113,0.6)';
                                title.style.color = '#2ecc71';
                                title.style.textShadow = '0 0 10px rgba(46,204,113,0.6)';
                                title.innerText = '💀 FINISHING BLOW READY!';
                                bar.style.background = '#2ecc71';
                            }}
                        }}

                        // Plotly Dynamic Skill Tree
                        Plotly.newPlot('skill-tree-chart', [{skillTreeTraces}], {{
                            title: {{ text: '<b>DYNAMIC SKILL TREE (ECTS EXPANSION)</b>', font: {{ color: '{ThemeHex}', family: '""Share Tech Mono"", monospace', size: 16 }} }},
                            paper_bgcolor: 'rgba(0,0,0,0)', plot_bgcolor: 'rgba(0,0,0,0)', margin: {{ t: 50, b: 30, l: 10, r: 20 }}, height: 550,
                            xaxis: {{ showgrid: false, zeroline: false, title: ""Αθροιστικά ECTS"", color: '#7f8c8d', tickvals: [0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300] }},
                            yaxis: {{ automargin: true, showgrid: false, zeroline: false, tickvals: [4, 3, 2, 1, 0, -1, -2, -3], ticktext: ['Cybersecurity', 'Data & AI', 'Software & Systems', 'Graphics & Vision', 'General / Core', 'Hardware & Architecture', 'Networks & Comms', 'Math & Theory'], tickfont: {{ color: '#bdc3c7', size: 11, family: '""Share Tech Mono"", monospace' }}, range: [-4.5, 6] }},
                            hoverlabel: {{ bgcolor: '#0a0e17', bordercolor: '{ThemeHex}', font: {{ family: '""Share Tech Mono"", monospace', color: '#ecf0f1', size: 13 }} }},
                            shapes: {shapesJson}, annotations: {annotationsJson}
                        }}, {{ displayModeBar: false, responsive: true }});
                        (function() {{
                            const det = document.querySelector('details.cyber-details');
                            const hint = det.querySelector('.tap-hint');
                            det.addEventListener('toggle', function() {{
                                hint.textContent = det.open ? '[ tap to close ]' : '[ tap to open ]';
                                if (det.open) Plotly.Plots.resize('skill-tree-chart');
                            }});
                        }})();
                        // Memory Card: επαναφορά Boss milestones
                        (function() {{
                            const saved = {bossJson};
                            document.querySelectorAll('.boss-cb').forEach((cb, i) => {{ cb.checked = !!saved[i]; }});
                            updateBossHp();
                        }})();
                    </script>
                </body>
                </html>";

                BaseCampBrowser.NavigateToString(ApplyTheme(html));
            }
            catch (Exception ex) { ShowErrorPage(BaseCampBrowser, "Base Camp", ex); }
        }

        private List<MissingCourse> GetMissingCoursesList(HashSet<string> passedCourses)
        {
            Dictionary<string, MissingCourse> allCurriculumCourses = new Dictionary<string, MissingCourse>();
            string currFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "Curriculum_Dump.json");

            if (File.Exists(currFilePath))
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(currFilePath)))
                {
                    if (doc.RootElement.TryGetProperty("programCourse", out JsonElement progCourses) && progCourses.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var courseNode in progCourses.EnumerateArray())
                        {
                            string title = "";
                            if (courseNode.TryGetProperty("courseId", out JsonElement cId) && cId.ValueKind == JsonValueKind.Object)
                                if (cId.TryGetProperty("title", out JsonElement t)) title = t.GetString();
                            if (string.IsNullOrEmpty(title) || title.Length <= 2) continue;

                            string code = "-"; if (courseNode.TryGetProperty("courseCode", out JsonElement cCode)) code = cCode.GetString();
                            double ects = 0; if (courseNode.TryGetProperty("ects", out JsonElement uNode) && uNode.ValueKind == JsonValueKind.Number) ects = uNode.GetDouble();

                            string category = "ΓΕΝΙΚΟ";
                            if (courseNode.TryGetProperty("categId", out JsonElement catObj) && catObj.ValueKind == JsonValueKind.Object)
                                if (catObj.TryGetProperty("title", out JsonElement catTitle)) category = catTitle.GetString();

                            string semester = "-";
                            int semesterOrder = 999;
                            if (courseNode.TryGetProperty("semesterId", out JsonElement semObj) && semObj.ValueKind == JsonValueKind.Object)
                            {
                                if (semObj.TryGetProperty("title", out JsonElement semTitle)) semester = semTitle.GetString();
                                if (semObj.TryGetProperty("sortOrder", out JsonElement semOrd) && semOrd.ValueKind == JsonValueKind.Number) semesterOrder = semOrd.GetInt32();
                            }

                            string period = "-";
                            if (courseNode.TryGetProperty("periodTitle", out JsonElement perTitle)) period = perTitle.GetString();

                            string upperTitle = NormalizeKey(title);
                            if (!allCurriculumCourses.ContainsKey(upperTitle))
                                allCurriculumCourses[upperTitle] = new MissingCourse { Code = code, Title = title.Trim(), Ects = ects, Category = category.ToUpper(), Semester = semester, SemesterOrder = semesterOrder, Period = period };
                        }
                    }
                }
            }

            List<MissingCourse> missingBounties = new List<MissingCourse>();
            foreach (var kvp in allCurriculumCourses)
                if (!passedCourses.Contains(kvp.Key) && !kvp.Key.Contains("ΠΡΑΚΤΙΚ") && !kvp.Key.Contains("ΔΙΠΛΩΜΑΤΙΚ"))
                    missingBounties.Add(kvp.Value);

            missingBounties = missingBounties
                .OrderBy(m => m.SemesterOrder)
                .ThenBy(m => m.Title, StringComparer.CurrentCulture)
                .ToList();
            return missingBounties;
        }

        // ==========================================
        // TAB 2: ANALYTICS ENGINE
        // ==========================================
        private async Task LoadAnalytics()
        {
            try
            {
                if (!File.Exists(DataFilePath)) return;
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var passedGrades = GetPassedGrades(options);
                if (passedGrades.Count == 0) return;

                var env = await GetEnvAsync();
                await ChartBrowser.EnsureCoreWebView2Async(env);

                var validGrades = passedGrades.Where(g => g.AcademicYear != "Άγνωστο" && g.ExamMonth != "Άγνωστο").ToList();
                int minYear = validGrades.Any() ? validGrades.Min(g => int.Parse(g.AcademicYear)) : DateTime.Now.Year;
                int maxYear = validGrades.Any() ? validGrades.Max(g => int.Parse(g.AcademicYear)) : DateTime.Now.Year;
                string[] periods = { "Φεβ", "Ιουν", "Σεπ" };

                var uniqueYears = validGrades.Select(g => g.AcademicYear).Distinct().OrderBy(y => y).ToList();
                var displayYears = uniqueYears.Select(y => { if (int.TryParse(y, out int yearNum)) return $"'{yearNum}-{(yearNum + 1) % 100:00}'"; return $"'{y}'"; }).ToList();

                string heatYears = "[" + string.Join(",", displayYears) + "]";

                // --- ΠΡΟΣΘΗΚΗ: PLAYER ARCHETYPE (ADVANCED SKILL PROFILING) ---
                var branchScores = new Dictionary<string, double> {
                    {"Software & Systems", 0}, {"Hardware & Architecture", 0}, {"Math & Theory", 0},
                    {"Data & AI", 0}, {"Networks & Comms", 0}, {"Graphics & Vision", 0}, {"Cybersecurity", 0}
                };

                

                foreach (var g in passedGrades)
                {
                    string branch = GetBranch(g.CourseId.Title);
                    if (branch != "General / Core")
                    {
                        double powerScore = 0.004 * (g.Ects * Math.Pow(g.FinalGrade, 4.5));
                        branchScores[branch] += powerScore;
                    }
                }

                string aColor = "#ffffff"; string aTitle = "Tech-Mercenary"; string aDesc = "Balanced Tech Generalist"; string aIcon = "⚔️"; string domCat = "None";
                var maxBranch = branchScores.OrderByDescending(x => x.Value).First();
                if (maxBranch.Value > 0)
                {
                    domCat = maxBranch.Key;
                    switch (domCat)
                    {
                        case "Software & Systems": aTitle = "Cyber-Mage"; aDesc = "Master of Software & Architectures"; aIcon = "🧙‍♂️"; aColor = "#3498db"; break;
                        case "Hardware & Architecture": aTitle = "Mecha-Paladin"; aDesc = "Hardware & Circuitry Specialist"; aIcon = "🦾"; aColor = "#e74c3c"; break;
                        case "Networks & Comms": aTitle = "Network Ninja"; aDesc = "Data Routing & Comms Agent"; aIcon = "🥷"; aColor = "#9b59b6"; break;
                        case "Math & Theory": aTitle = "Logic Oracle"; aDesc = "Math & Theoretical Foundations"; aIcon = "👁️"; aColor = "#f1c40f"; break;
                        case "Data & AI": aTitle = "AI-Netrunner"; aDesc = "Machine Learning & Data Analyst"; aIcon = "🧠"; aColor = "#00e5b0"; break;
                        case "Graphics & Vision": aTitle = "Holo-Artisan"; aDesc = "3D Graphics & Visual Computing"; aIcon = "🎨"; aColor = "#ff007f"; break;
                        case "Cybersecurity": aTitle = "Stealth Decker"; aDesc = "System Security & Defense"; aIcon = "🛡️"; aColor = "#00ff00"; break;
                    }
                }

                string radarBranches = "['Software & Systems', 'Hardware & Architecture', 'Math & Theory', 'Data & AI', 'Networks & Comms', 'Graphics & Vision', 'Cybersecurity']";
                string radarScores = "[" + string.Join(",", branchScores.Values.Select(v => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

                string heatPeriods = "['Φεβ', 'Ιουν', 'Σεπ']";
                List<string> zRows = new List<string>(); List<string> hoverRows = new List<string>(); double maxHeatEcts = 0;

                foreach (var p in periods)
                {
                    List<string> zCols = new List<string>(); List<string> hCols = new List<string>();
                    foreach (var y in uniqueYears)
                    {
                        var periodGrades = validGrades.Where(g => g.AcademicYear == y && g.ExamMonth == p);
                        double totalEcts = periodGrades.Sum(g => g.Ects);
                        if (totalEcts > maxHeatEcts) maxHeatEcts = totalEcts;

                        zCols.Add(totalEcts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        string status = totalEcts == 0 ? "OFFLINE (0 ECTS)" : totalEcts <= 15 ? "LOW POWER MODE" : totalEcts <= 30 ? "OPTIMAL YIELD" : "OVERCLOCK DETECTED ⚠";
                        string displayY = int.TryParse(y, out int yN) ? $"{yN}-{(yN + 1) % 100:00}" : y;
                        hCols.Add($"'<b>Έτος:</b> {displayY}<br><b>Περίοδος:</b> {p}<br><b>Απόδοση:</b> {totalEcts} ECTS<br><b>Status:</b> {status}'");
                    }
                    zRows.Add("[" + string.Join(",", zCols) + "]"); hoverRows.Add("[" + string.Join(",", hCols) + "]");
                }

                string heatZData = "[" + string.Join(",", zRows) + "]"; string heatHoverData = "[" + string.Join(",", hoverRows) + "]";
                string safeHeatZMax = Math.Max(40, maxHeatEcts).ToString(System.Globalization.CultureInfo.InvariantCulture);

                var gradeCounts = new Dictionary<double, int>();
                for (double i = 5.0; i <= 10.0; i += 0.5) gradeCounts[i] = 0;
                foreach (var g in passedGrades) { double rounded = Math.Round(g.FinalGrade * 2, MidpointRounding.AwayFromZero) / 2.0; if (gradeCounts.ContainsKey(rounded)) gradeCounts[rounded]++; }
                string distX = string.Join(",", gradeCounts.Keys.Select(k => $"'{k.ToString(System.Globalization.CultureInfo.InvariantCulture)}'"));
                string distY = string.Join(",", gradeCounts.Values);


                var catStats = passedGrades.GroupBy(g => g.CategId != null && !string.IsNullOrEmpty(g.CategId.Title) ? g.CategId.Title : "ΑΛΛΟ").Select(group => new { Category = group.Key, Ects = group.Sum(g => g.Ects) }).ToList();
                string pieLabels = string.Join(",", catStats.Select(c => $"'{c.Category}'")); string pieValues = string.Join(",", catStats.Select(c => c.Ects.ToString(System.Globalization.CultureInfo.InvariantCulture)));

                List<string> timeLabels = new List<string>(); List<int> courseCountPerPeriod = new List<int>(); List<int> cumulativeCourses = new List<int>();
                List<string> gpaEvolution = new List<string>(); List<string> barColors = new List<string>(); List<string> hoverTextsBar = new List<string>(); List<string> hoverTextsGpa = new List<string>();

                int cumCourses = 0; double cumPoints = 0; double cumEcts = 0; double currentGpa = 0;
                for (int y = minYear; y <= maxYear; y++)
                {
                    string yearStr = y.ToString(); string shortYear = AcadYearShort(yearStr);
                    foreach (string period in periods)
                    {
                        var coursesInPeriod = validGrades.Where(g => g.AcademicYear == yearStr && g.ExamMonth == period).ToList();
                        int countRegular = coursesInPeriod.Count(c => !c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) && !c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase));
                        cumCourses += countRegular;

                        foreach (var c in coursesInPeriod) { cumPoints += (c.FinalGrade * c.Ects); cumEcts += c.Ects; }
                        currentGpa = cumEcts > 0 ? Math.Round(cumPoints / cumEcts, 2) : 0;

                        timeLabels.Add($"\"{period}<br>'{shortYear}\""); courseCountPerPeriod.Add(countRegular); cumulativeCourses.Add(cumCourses);
                        gpaEvolution.Add(cumEcts > 0 ? currentGpa.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null");

                        if (coursesInPeriod.Count > 0)
                        {
                            barColors.Add(countRegular > 0 ? "'#3498db'" : "'#9b59b6'");
                            string text = $"'<b>📅 {period} \\'{shortYear} ({countRegular} μαθήματα)</b><br>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━<br>";
                            foreach (var c in coursesInPeriod)
                            {
                                string cleanTitle = c.CourseId.Title.Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
                                bool isSpecial = cleanTitle.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) || cleanTitle.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase);
                                if (isSpecial) text += $"▪ {cleanTitle} [{c.FinalGrade.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}] <i>({c.Ects} XP)</i> <b style=\"color:#f39c12;\">[SPECIAL]</b><br>";
                                else text += $"▪ {cleanTitle} [{c.FinalGrade.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}] <i>({c.Ects} XP)</i><br>";
                            }
                            hoverTextsBar.Add(text + "'");
                            hoverTextsGpa.Add($"'<b>📅 {period} \\'{shortYear}</b><br>━━━━━━━━━━━━━━━<br>Νέος Μ.Ο: <b>{currentGpa.ToString(System.Globalization.CultureInfo.InvariantCulture)}</b>'");
                        }
                        else
                        {
                            barColors.Add("'rgba(255,255,255,0.05)'");
                            hoverTextsBar.Add($"'<b>📅 {period} \\'{shortYear}</b><br>━━━━━━━━━━━━━━━<br>NO DATA (Σύστημα σε Αναμονή)'");
                            hoverTextsGpa.Add($"'<b>📅 {period} \\'{shortYear}</b><br>━━━━━━━━━━━━━━━<br>Μ.Ο: <b>{currentGpa.ToString(System.Globalization.CultureInfo.InvariantCulture)}</b> (Αμετάβλητος)'");
                        }
                    }
                }

                int timeCount = timeLabels.Count; string lastX = timeLabels.Count > 0 ? timeLabels.Last() : "''";
                int maxCumCourses = cumulativeCourses.Count > 0 ? cumulativeCourses.Last() : 0;
                string lastGpaStr = gpaEvolution.Count > 0 ? gpaEvolution.LastOrDefault(g => g != "null") ?? "0" : "0";

                List<string> staticTextsCum = new List<string>();
                for (int i = 0; i < cumulativeCourses.Count; i++)
                {
                    int prev = i > 0 ? cumulativeCourses[i - 1] : cumulativeCourses[0];
                    if (cumulativeCourses[i] > prev && i != cumulativeCourses.Count - 1) staticTextsCum.Add($"'<b>{cumulativeCourses[i]}</b>'");
                    else staticTextsCum.Add("''");
                }

                double minGpa = gpaEvolution.Where(g => g != "null").Select(g => double.Parse(g, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(5.0).Min() - 0.2;
                double maxGpa = gpaEvolution.Where(g => g != "null").Select(g => double.Parse(g, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(10.0).Max() + 0.2;
                string gpaRange = $"[{minGpa.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {maxGpa.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";

                string tLabels = string.Join(",", timeLabels); string tCourses = string.Join(",", courseCountPerPeriod); string tCumCourses = string.Join(",", cumulativeCourses);
                string tGpa = string.Join(",", gpaEvolution); string tBarColors = string.Join(",", barColors); string tHoverBar = string.Join(",", hoverTextsBar);
                string tHoverGpa = string.Join(",", hoverTextsGpa); string tStaticCum = string.Join(",", staticTextsCum);

                // --- ΠΡΟΣΘΗΚΗ: SPEEDRUN PREDICTOR & VELOCITY TRACKER ---
                var activePeriods = passedGrades.Select(g => new { g.AcademicYear, g.ExamMonth }).Distinct().ToList();
                int numPeriods = activePeriods.Count;
                string velocityHtml = "";

                double totalOverallEcts = passedGrades.Sum(g => g.Ects);

                if (totalOverallEcts >= 300)
                {
                    velocityHtml = "<div style='background: rgba(46, 204, 113, 0.1); border-left: 4px solid #2ecc71; padding: 12px 15px; border-radius: 4px; color: #2ecc71; margin-top: 25px; margin-bottom: 20px; font-size: 1.1rem;'>🎓 Έχεις ήδη τερματίσει το παιχνίδι! Το Velocity σου κλείδωσε.</div>";
                }
                else if (numPeriods > 0)
                {
                    double velocity = totalOverallEcts / numPeriods;
                    double remEcts = 300 - totalOverallEcts;

                    string vBadge, vColor, vDesc;
                    if (velocity >= 20) { vBadge = "Academic Weapon 🚀"; vColor = "#ff007f"; vDesc = "Ασύλληπτος ρυθμός. Το σύστημα δυσκολεύεται να σε ακολουθήσει!"; }
                    else if (velocity >= 17) { vBadge = "Speedrunner ⚡"; vColor = "#f1c40f"; vDesc = "Τρέχεις με ιλιγγιώδη ρυθμό. Προσοχή στο Overheating!"; }
                    else if (velocity >= 13) { vBadge = "Steady Grinder ⚙️"; vColor = "#3498db"; vDesc = "Σταθερός και αξιόπιστος ρυθμός. Βέλτιστη λειτουργία."; }
                    else if (velocity >= 8) { vBadge = "Tactical Survivor 🛡️"; vColor = "#e74c3c"; vDesc = "Προσεκτικά βήματα. Focus στην επιβίωση."; }
                    else { vBadge = "Zen Explorer 🧘"; vColor = "#9b59b6"; vDesc = "Απολαμβάνεις το side-content. Το πτυχίο μπορεί να περιμένει."; }

                    var now = DateTime.Now;
                    int currMonth = now.Month;
                    int currYear = now.Year;

                    string nextPeriod;
                    int baseYear;
                    if (currMonth >= 10 || currMonth <= 2) { nextPeriod = "Φεβ"; baseYear = currMonth >= 10 ? currYear + 1 : currYear; }
                    else if (currMonth >= 3 && currMonth <= 6) { nextPeriod = "Ιουν"; baseYear = currYear; }
                    else { nextPeriod = "Σεπ"; baseYear = currYear; }

                    string[] pOrder = { "Φεβ", "Ιουν", "Σεπ" };
                    int currIdx = Array.IndexOf(pOrder, nextPeriod);

                    bool passedThesis = passedGrades.Any(c => c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase));
                    int periodsNeeded;
                    string reqText;

                    if (!passedThesis)
                    {
                        double remCoursesEcts = Math.Max(0, remEcts - 30);
                        double threshold = velocity * (2.0 / 3.0);
                        if (remCoursesEcts <= threshold)
                        {
                            periodsNeeded = 1;
                            reqText = remCoursesEcts == 0 ? "Απαιτείται <b>1</b> εξεταστική (Μόνο Διπλωματική)." : $"Απαιτείται <b>1</b> εξεταστική (Διπλωματική & {remCoursesEcts:g} ECTS).";
                        }
                        else
                        {
                            int coursePeriods = Math.Max(1, (int)Math.Round(remCoursesEcts / velocity));
                            periodsNeeded = coursePeriods + 1;
                            reqText = $"Απαιτούνται <b>{periodsNeeded}</b> εξεταστικές (Διπλωματική & {remCoursesEcts:g} ECTS).";
                        }
                    }
                    else
                    {
                        periodsNeeded = velocity > 0 ? (int)((remEcts / velocity) + 0.99) : 99;
                        periodsNeeded = Math.Max(1, periodsNeeded);
                        reqText = $"Απαιτούνται <b>{periodsNeeded}</b> εξεταστικές ακόμα.";
                    }

                    int stepsToAdvance = periodsNeeded - 1;
                    for (int i = 0; i < stepsToAdvance; i++)
                    {
                        currIdx++;
                        if (currIdx > 2) { currIdx = 0; baseYear++; }
                    }
                    string gradPeriod = $"{pOrder[currIdx]} '{(baseYear - 1) % 100:00}-{baseYear % 100:00}";

                    velocityHtml = $@"
                    <div style='margin-top: 30px;'>
                        <h2 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; font-size: 1.5rem;'>⏱️ Velocity Tracker (Πρόβλεψη Πτυχίου)</h2>
                        <div style='display: flex; gap: 15px; margin-top: 20px; flex-wrap: wrap;'>
                            <div style='flex: 1; min-width: 200px; background: #0a0e17; border: 1px solid {vColor}; border-left: 4px solid {vColor}; border-radius: 8px; padding: 20px; box-shadow: 0 0 15px {vColor}20;'>
                                <div style='color: #7f8c8d; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 5px;'>Velocity Status</div>
                                <div style='color: {vColor}; font-size: 1.6rem; font-family: ""Share Tech Mono"", monospace; font-weight: bold; text-shadow: 0 0 10px {vColor}80;'>{vBadge}</div>
                                <div style='color: #bdc3c7; font-size: 0.85rem; margin-top: 5px;'>{vDesc}</div>
                            </div>
                            
                            <div style='flex: 1; min-width: 200px; background: #111b24; border: 1px dashed #3498db; border-radius: 8px; padding: 20px; text-align: center; box-shadow: inset 0 0 10px rgba(0,0,0,0.5);'>
                                <div style='color: #7f8c8d; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 5px;'>Average Speed</div>
                                <div style='color: #3498db; font-size: 2.2rem; font-family: ""Share Tech Mono"", monospace; font-weight: bold; text-shadow: 0 0 10px rgba(52,152,219,0.5);'>{velocity:F1} <span style='font-size: 1rem; color: #bdc3c7;'>ECTS/Εξεταστική</span></div>
                            </div>
                            
                            <div style='flex: 1; min-width: 200px; background: #0a0e17; border: 1px solid #2ecc71; border-right: 4px solid #2ecc71; border-radius: 8px; padding: 20px; text-align: right; box-shadow: 0 0 15px rgba(46,204,113,0.2);'>
                                <div style='color: #7f8c8d; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 5px;'>Est. Graduation</div>
                                <div style='color: #2ecc71; font-size: 1.8rem; font-family: ""Share Tech Mono"", monospace; font-weight: bold; text-shadow: 0 0 10px rgba(46,204,113,0.5);'>{gradPeriod}</div>
                                <div style='color: #bdc3c7; font-size: 0.85rem; margin-top: 5px;'>{reqText}</div>
                            </div>
                        </div>
                    </div>";
                }

                string html = $@"
                <html><head><script src='https://cdn.plot.ly/plotly-2.27.0.min.js'></script><link href='https://fonts.googleapis.com/css2?family=Roboto+Mono:wght@400;700&family=Share+Tech+Mono&display=swap' rel='stylesheet'>
                <style>
                body {{ margin: 0; padding: 20px; background-color: #05070a; font-family: 'Share Tech Mono', 'Roboto Mono', monospace; overflow-x: hidden; }} 
                
                /* Matrix Background CSS */
                .bg-matrix {{ position: fixed; top: 0; left: 0; width: 100vw; height: 100vh; z-index: -1; pointer-events: none; background-image: repeating-linear-gradient(90deg, transparent 0, transparent 60px, rgba(0, 255, 204, 0.03) 60px, rgba(0, 255, 204, 0.03) 62px), linear-gradient(to bottom, transparent 0%, rgba(0, 255, 204, 0.06) 50%, transparent 100%); background-size: 100% 100%, 100% 200vh; animation: data-fall 8s linear infinite; }}
                @keyframes data-fall {{ 0% {{ background-position: 0 0, 0 -200vh; }} 100% {{ background-position: 0 0, 0 200vh; }} }}
                
                .grid-container {{ display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-top: 20px; }} 
                .chart-box {{ background-color: #0b101a; border: 1px solid #1a2536; border-radius: 8px; padding: 10px; height: 420px; }} 
                .full-width {{ grid-column: span 2; height: 450px; }} 
                .heatmap-box {{ grid-column: span 2; height: 250px; margin-bottom: 20px; }}
                .scatter-box {{ grid-column: span 2; height: 600px; margin-top: 20px; }}
                
                /* Archetype Box CSS */
                .arch-grid {{ display: grid; grid-template-columns: 1fr 2.5fr; gap: 20px; margin-bottom: 30px; }}
                .arch-profile {{ background: #0b101a; border: 2px solid {aColor}; border-radius: 8px; padding: 25px 15px; text-align: center; height: 100%; box-shadow: 0 0 15px {aColor}44, inset 0 0 20px rgba(0,0,0,0.8); display: flex; flex-direction: column; justify-content: center; box-sizing: border-box; }}
                </style></head>
                <body>
                    <div class='bg-matrix'></div>
                    
                    <h2 style='color: #ecf0f1; margin-bottom: 20px; font-family: ""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; font-weight: bold;'>🧬 Player Archetype (Skill Profiling)</h2>
                    <div class='arch-grid'>
                        <div class='arch-profile'>
                            <div style='font-size: 4rem; text-shadow: 0 0 25px {aColor}; margin-bottom: 15px;'>{aIcon}</div>
                            <div style='color: #7f8c8d; font-size: 0.8rem; letter-spacing: 2px; text-transform: uppercase;'>Dominant Class</div>
                            <h3 style='color: {aColor}; margin: 5px 0 10px 0; font-family: monospace; font-size: 1.8rem; text-shadow: 0 0 8px {aColor}80;'>{aTitle}</h3>
                            <div style='color: #bdc3c7; font-size: 0.9rem;'>{aDesc}</div>
                            <div style='margin-top: 20px; font-size: 0.75rem; color: #7f8c8d; border-top: 1px dashed #34495e; padding-top: 15px;'>
                                Highest Power Branch: <strong style='color: #ecf0f1;'>{domCat}</strong>
                            </div>
                        </div>
                        <div id='chart-radar' class='chart-box' style='height: 380px; padding: 0;'></div>
                    </div>

                    {velocityHtml}

                    <div style='margin-top: 40px;'>
                        <h2 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; font-size: 1.5rem; margin-bottom: 20px;'>🟩 Cyber-Grid Activity (Server Heatmap)</h2>
                        <div style='color: #7f8c8d; font-size: 0.9rem; margin-bottom: 15px;'>Ανάλυση εποχικότητας: Δες σε ποιες περιόδους κάνεις το μεγαλύτερο Power-Leveling.</div>
                        <div id='chart0' class='chart-box heatmap-box'></div>
                    </div>

                    <div style='margin-top: 40px;'>
                        <h2 style='color: #bdc3c7; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; font-size: 1.5rem;'>📈 Γραμμική & Αθροιστική Ανάλυση</h2>
                        <div class='grid-container'>
                            <div id='chart4' class='chart-box full-width'></div>
                            <div id='chart3' class='chart-box full-width'></div>
                            <div id='chart1' class='chart-box'></div>
                            <div id='chart5' class='chart-box'></div>
                        </div>
                    </div>
                <script>
                    var cyberFont = '""Share Tech Mono"", ""Roboto Mono"", monospace'; 
                    var commonLayout = {{ paper_bgcolor: 'rgba(0,0,0,0)', plot_bgcolor: 'rgba(0,0,0,0)', margin: {{ t: 50, b: 50, l: 50, r: 30 }}, hoverlabel: {{ bgcolor: '#0a0e17', font: {{ family: cyberFont, color: '#ecf0f1' }} }} }}; 
                    var commonConfig = {{ displayModeBar: false, responsive: true }}; 
                    var gridStyle = {{ showgrid: true, gridcolor: 'rgba(0, 255, 204, 0.1)', zerolinecolor: 'rgba(0, 255, 204, 0.1)' }}; 
                    var noGridStyle = {{ showgrid: false, zeroline: false }}; 
                    var fontStyle = {{ family: cyberFont, color: '#bdc3c7', size: 12 }}; 
                    var titleFont = {{ family: cyberFont, color: '#00ffcc', size: 16 }};
                    
                    // Radar Chart
                    Plotly.newPlot('chart-radar', [{{
                        type: 'scatterpolar', r: {radarScores}, theta: {radarBranches},
                        fill: 'toself', name: 'Skill Power', line: {{ color: '{aColor}' }}, opacity: 0.8,
                        hovertemplate: '<b>%{{theta}}</b><br>Power Level: %{{r}}<extra></extra>'
                    }}], {{
                        polar: {{ radialaxis: {{ visible: true, showticklabels: false, gridcolor: 'rgba(255,255,255,0.1)' }}, angularaxis: {{ gridcolor: 'rgba(255,255,255,0.1)', tickfont: {{ size: 12, color: '#bdc3c7' }} }}, bgcolor: 'rgba(0,0,0,0)' }},
                        showlegend: false, margin: {{ t: 30, b: 30, l: 60, r: 60 }}, paper_bgcolor: 'rgba(0,0,0,0)', plot_bgcolor: 'rgba(0,0,0,0)'
                    }}, commonConfig);

                    // Heatmap
                    Plotly.newPlot('chart0', [{{ z: {heatZData}, x: {heatYears}, y: {heatPeriods}, type: 'heatmap', text: {heatHoverData}, hoverinfo: 'text', colorscale: [ [0.0, '#05070a'], [0.1, '#112233'], [0.5, '{aColor}'], [1.0, '#ffffff'] ], zmin: 0, zmax: {safeHeatZMax}, xgap: 6, ygap: 6, showscale: false }}], Object.assign({{}}, commonLayout, {{ xaxis: Object.assign({{type: 'category', tickfont: fontStyle}}, noGridStyle), yaxis: Object.assign({{autorange: 'reversed', tickfont: fontStyle}}, noGridStyle), hoverlabel: Object.assign({{}}, commonLayout.hoverlabel, {{ bordercolor: '{aColor}' }}), margin: {{ t: 20, b: 20, l: 40, r: 20 }} }}), commonConfig);

                    // Γραφήματα
                    Plotly.newPlot('chart1', [{{ x: [{distX}], y: [{distY}], type: 'bar', width: 0.6, marker: {{ color: '#ff007f', line: {{ color: '#ff007f', width: 1 }} }}, text: [{distY}].map(String), textposition: 'outside', textfont: {{ family: cyberFont, size: 12, color: '#ff007f' }}, hoverinfo: 'x+y' }}], Object.assign({{}}, commonLayout, {{ title: {{ text: '<b>GRADE_DISPERSION (Κατανομή Βαθμών)</b>', font: Object.assign({{}}, titleFont, {{color: '#ff007f'}}), x: 0.5 }}, xaxis: Object.assign({{tickangle: -90, type: 'category', tickfont: fontStyle}}, noGridStyle), yaxis: Object.assign({{title: {{ text: 'Πλήθος', font: fontStyle }}, tickfont: fontStyle}}, gridStyle), hoverlabel: Object.assign({{}}, commonLayout.hoverlabel, {{ bordercolor: '#ff007f' }}) }}), commonConfig);
                    
                    Plotly.newPlot('chart3', [{{ x: [{tLabels}], y: [{tGpa}], type: 'scatter', mode: 'lines+markers', connectgaps: true, line: {{ color: '#f1c40f', width: 4, shape: 'spline' }}, marker: {{ size: 10, color: '#0a0e17', line: {{ color: '#f1c40f', width: 2 }} }}, fill: 'tozeroy', fillcolor: 'rgba(241, 196, 15, 0.1)', hovertext: [{tHoverGpa}], hoverinfo: 'text' }}], Object.assign({{}}, commonLayout, {{ title: {{ text: '<b>PERFORMANCE_RATING (GPA Tracker)</b>', font: Object.assign({{}}, titleFont, {{color: '#f1c40f'}}), x: 0.5 }}, xaxis: Object.assign({{type: 'category', tickfont: fontStyle, range: [-0.5, {timeCount} - 0.5]}}, noGridStyle), yaxis: Object.assign({{title: {{ text: 'Σταθμικός Μ.Ο.', font: fontStyle }}, tickfont: fontStyle, range: {gpaRange}}}, gridStyle), hoverlabel: Object.assign({{}}, commonLayout.hoverlabel, {{ bordercolor: '#f1c40f' }}), annotations: [{{ x: {lastX}, y: {lastGpaStr}, text: '<b>GPA: {lastGpaStr}</b>', showarrow: true, arrowhead: 2, arrowsize: 1, arrowwidth: 2, arrowcolor: '#f1c40f', ax: -50, ay: -30, bgcolor: '#111b24', bordercolor: '#f1c40f', borderwidth: 1, borderpad: 4, font: {{ family: cyberFont, color: '#f1c40f' }} }}] }}), commonConfig);
                    
                    Plotly.newPlot('chart4', [{{ x: [{tLabels}], y: [{tCumCourses}], type: 'scatter', mode: 'lines+markers+text', text: [{tStaticCum}], textposition: 'top left', textfont: {{ family: cyberFont, color: '#00ffcc', size: 12 }}, line: {{ color: '#00ffcc', width: 3, shape: 'hv' }}, marker: {{ size: 8, color: '#0a0e17', line: {{ color: '#00ffcc', width: 2 }} }}, fill: 'tozeroy', fillcolor: 'rgba(0, 255, 204, 0.1)', hovertext: [{tHoverBar}], hoverinfo: 'text' }}], Object.assign({{}}, commonLayout, {{ title: {{ text: '<b>SYS.PROGRESS_TRACKER (Αθροιστικά Μαθήματα)</b>', font: titleFont, x: 0.5 }}, xaxis: Object.assign({{type: 'category', tickfont: fontStyle, range: [-0.5, {timeCount} - 0.5]}}, noGridStyle), yaxis: Object.assign({{title: {{ text: 'Σύνολο Περασμένων', font: fontStyle }}, tickfont: fontStyle}}, gridStyle), hoverlabel: Object.assign({{}}, commonLayout.hoverlabel, {{ bordercolor: '#00ffcc' }}), annotations: [{{ x: {lastX}, y: {maxCumCourses}, text: '<b>MAX: {maxCumCourses}</b>', showarrow: true, arrowhead: 2, arrowsize: 1, arrowwidth: 2, arrowcolor: '#00ffcc', ax: -60, ay: -40, bgcolor: '#0a0e17', bordercolor: '#00ffcc', borderwidth: 1, borderpad: 6, font: {{ family: cyberFont, size: 14, color: '#00ffcc' }} }}] }}), commonConfig);
                    
                    Plotly.newPlot('chart5', [{{ labels: [{pieLabels}], values: [{pieValues}], type: 'pie', hole: 0.5, textinfo: 'percent', textposition: 'inside', textfont: {{ family: cyberFont, color: '#ffffff' }}, marker: {{ colors: ['#3498db', '#e74c3c', '#f1c40f', '#2ecc71', '#9b59b6', '#00e5b0'], line: {{ color: '#0a0e17', width: 2 }} }}, hoverinfo: 'label+value+percent' }}], Object.assign({{}}, commonLayout, {{ title: {{ text: '<b>XP_DISTRIBUTION (Κατηγορίες)</b>', font: titleFont, x: 0.5 }}, showlegend: false, hoverlabel: Object.assign({{}}, commonLayout.hoverlabel, {{ bordercolor: '#bdc3c7' }}) }}), commonConfig);

                </script></body></html>";

                ChartBrowser.NavigateToString(ApplyTheme(html));
            }
            catch (Exception ex) { ShowErrorPage(ChartBrowser, "Analytics", ex); }
        }

        // ==========================================
        // TAB 3: 3D SPACE & TIMELINE
        // ==========================================
        private async Task LoadSpace()
        {
            try
            {
                if (!File.Exists(DataFilePath)) return;

                var env = await GetEnvAsync();
                await SpaceBrowser.EnsureCoreWebView2Async(env);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var passedGrades = GetPassedGrades(options);
                if (passedGrades.Count == 0) return;

                // Καθορισμός των ετών για τον Z άξονα ώστε να μπαίνουν με τη σωστή χρονολογική σειρά
                var uniqueYears = passedGrades.Where(g => g.AcademicYear != "Άγνωστο").Select(g => g.AcademicYear).Distinct().OrderBy(y => y).ToList();
                var displayYears = uniqueYears.Select(y => { if (int.TryParse(y, out int yearNum)) return $"'{yearNum}-{(yearNum + 1) % 100:00}'"; return $"'{y}'"; }).ToList();
                string yearsList = "[" + string.Join(",", displayYears) + "]";

                var scatterGroups = passedGrades
                    .Where(g => !g.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) && !g.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(g => new { g.Ects, g.FinalGrade, g.AcademicYear })
                    .Select(g => new
                    {
                        Ects = g.Key.Ects,
                        Grade = g.Key.FinalGrade,
                        Year = g.Key.AcademicYear,
                        Courses = string.Join("<br>▪ ", g.Select(c => c.CourseId.Title.Replace("'", "&apos;"))),
                        Count = g.Count()
                    }).ToList();

                string scatterX = "[" + string.Join(",", scatterGroups.Select(g => g.Ects.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
                string scatterY = "[" + string.Join(",", scatterGroups.Select(g => g.Grade.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
                string DisplayYear(string y) => int.TryParse(y, out int n) ? $"{n}-{(n + 1) % 100:00}" : y;
                string scatterZ = "[" + string.Join(",", scatterGroups.Select(g => $"'{DisplayYear(g.Year)}'")) + "]";
                string scatterSizes = "[" + string.Join(",", scatterGroups.Select(g => (10 + (g.Count * 5)).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
                string scatterText = "[" + string.Join(",", scatterGroups.Select(g => $"'▪ {g.Courses}'")) + "]";
                string scatterCustom = "[" + string.Join(",", scatterGroups.Select(g => g.Count.ToString())) + "]";

                string html = $@"
                <!DOCTYPE html>
                <html><head>
                <script src='https://cdn.plot.ly/plotly-2.27.0.min.js'></script>
                <link href='https://fonts.googleapis.com/css2?family=Roboto+Mono:wght@400;700&family=Share+Tech+Mono&display=swap' rel='stylesheet'>
                <style>
                body {{ margin: 0; padding: 20px; background-color: #05070a; font-family: 'Share Tech Mono', 'Roboto Mono', monospace; overflow: hidden; }} 
                .bg-matrix {{ position: fixed; top: 0; left: 0; width: 100vw; height: 100vh; z-index: -1; pointer-events: none; background-image: repeating-linear-gradient(90deg, transparent 0, transparent 60px, rgba(0, 255, 204, 0.03) 60px, rgba(0, 255, 204, 0.03) 62px), linear-gradient(to bottom, transparent 0%, rgba(0, 255, 204, 0.06) 50%, transparent 100%); background-size: 100% 100%, 100% 200vh; animation: data-fall 8s linear infinite; }}
                @keyframes data-fall {{ 0% {{ background-position: 0 0, 0 -200vh; }} 100% {{ background-position: 0 0, 0 200vh; }} }}
                .chart-box {{ background-color: #0b101a; border: 1px solid #1a2536; border-radius: 8px; padding: 10px; height: 80vh; width: 100%; }} 
                </style></head>
                <body>
                    <div class='bg-matrix'></div>
                    <div id='chart-scatter' class='chart-box'></div>
                <script>
                    var cyberFont = '""Share Tech Mono"", ""Roboto Mono"", monospace'; 
                    var commonLayout = {{ paper_bgcolor: 'rgba(0,0,0,0)', plot_bgcolor: 'rgba(0,0,0,0)', margin: {{ t: 50, b: 50, l: 50, r: 30 }}, hoverlabel: {{ bgcolor: '#0a0e17', font: {{ family: cyberFont, color: '#ecf0f1' }} }} }}; 
                    var commonConfig = {{ displayModeBar: false, responsive: true }}; 
                    var titleFont = {{ family: cyberFont, color: '#00ffcc', size: 16 }};
                    
                    Plotly.newPlot('chart-scatter', [{{
                        type: 'scatter3d', x: {scatterX}, y: {scatterY}, z: {scatterZ}, mode: 'markers',
                        marker: {{ size: {scatterSizes}, color: {scatterY}, colorscale: 'Electric', line: {{ width: 1, color: '#000' }}, opacity: 0.9 }},
                        text: {scatterText}, customdata: {scatterCustom},
                        hovertemplate: '<b>Year: %{{z}}</b><br>%{{customdata}} Data Nodes:<br>%{{text}}<br>━━━━━━━━━━━━━━━<br>Grade: %{{y}} | XP: %{{x}}<extra></extra>'
                    }}], Object.assign({{}}, commonLayout, {{
                        title: {{ text: '<b>SPATIAL_ANALYSIS (Δυσκολία vs Βαθμός vs Χρόνος)</b>', font: Object.assign({{}}, titleFont, {{size: 22}}), x: 0.5 }},
                        scene: {{
                            xaxis: {{ title: {{ text: 'XP (ECTS)', font: {{ color: '#bdc3c7' }} }}, tickfont: {{ color: '#7f8c8d' }}, showgrid: true, gridcolor: 'rgba(0,255,204,0.1)', backgroundcolor: '#0a0e17' }},
                            yaxis: {{ title: {{ text: 'Grade', font: {{ color: '#bdc3c7' }} }}, tickfont: {{ color: '#7f8c8d' }}, showgrid: true, gridcolor: 'rgba(0,255,204,0.1)', backgroundcolor: '#0a0e17', range: [4.5, 10.5] }},
                            zaxis: {{ title: {{ text: 'Year', font: {{ color: '#bdc3c7' }} }}, tickfont: {{ color: '#7f8c8d' }}, showgrid: true, gridcolor: 'rgba(0,255,204,0.1)', backgroundcolor: '#0a0e17', type: 'category', categoryorder: 'array', categoryarray: {yearsList} }},
                            camera: {{ eye: {{ x: 1.6, y: 1.6, z: 0.6 }} }}
                        }},
                        margin: {{ l: 0, r: 0, b: 0, t: 60 }},
                        hoverlabel: Object.assign({{}}, commonLayout.hoverlabel, {{ bordercolor: '#00ffcc' }})
                    }}), commonConfig);
                </script></body></html>";

                SpaceBrowser.NavigateToString(ApplyTheme(html));
            }
            catch (Exception ex) { ShowErrorPage(SpaceBrowser, "3D Space", ex); }
        }

        // ==========================================
        // TAB 4: SIMULATOR
        // ==========================================
        private async Task LoadSimulator()
        {
            try
            {
                if (!File.Exists(DataFilePath)) return;
                
                if (!_simHooked)
                {
                    SimulatorBrowser.WebMessageReceived += (s, e) => HandleSaveMessage(e.TryGetWebMessageAsString());
                    _simHooked = true;
                }

                var env = await GetEnvAsync();
                await SimulatorBrowser.EnsureCoreWebView2Async(env);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var passedGrades = GetPassedGrades(options);

                double totalEcts = passedGrades.Sum(g => g.Ects);
                double currentPoints = passedGrades.Sum(g => g.FinalGrade * g.Ects);
                int totalCourses = passedGrades.Count(c => !c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) && !c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase));
                bool hasThesis = passedGrades.Any(c => c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase));
                int targetCourses = 47;
                double aiGrade = 7.5;
                int missingCourseCount = Math.Max(0, targetCourses - totalCourses);
                if (missingCourseCount > 0)
                {
                    double missingTotalEcts = Math.Max(0, 300 - totalEcts);
                    double missingCourseEcts = missingTotalEcts - (hasThesis ? 0 : 30);
                    double neededEcts = missingCourseEcts / missingCourseCount;

                    var regularSorted = passedGrades
                        .Where(c => !c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) && !c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(g => g.AcademicYear).ThenBy(g => g.ExamOrder)
                        .ToList();

                    if (regularSorted.Count > 0)
                    {
                        int n = regularSorted.Count;
                        double pred = regularSorted
                            .Select((g, i) => new { g.FinalGrade, Dist = Math.Abs(g.Ects - neededEcts) + 1.5 * ((n - 1 - i) / (double)Math.Max(1, n - 1)) })
                            .OrderBy(x => x.Dist)
                            .Take(Math.Min(7, n))
                            .Average(x => x.FinalGrade);
                        aiGrade = Math.Max(5.0, Math.Min(10.0, Math.Round(pred, 1)));
                    }
                }
                string aiGradeStr = aiGrade.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

                List<MissingCourse> missingList = GetMissingCoursesList(passedGrades.Select(g => NormalizeKey(g.CourseId.Title)).ToHashSet());
                string missingJson = JsonSerializer.Serialize(missingList, options);

                string simJson = JsonSerializer.Serialize(_save.Sim);

                string simHtml = $@"
                <!DOCTYPE html>
                <html><head><meta charset='UTF-8'><script>
window.addEventListener('error', function (e) {{
    var d = document.createElement('div');
    d.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:99999;background:#3a0a0a;color:#ff6b6b;font:12px Consolas,monospace;padding:8px 12px;border-bottom:1px solid #e74c3c;';
    d.textContent = 'JS ERROR: ' + e.message + ' (γραμμή ' + e.lineno + ')';
    (document.body || document.documentElement).appendChild(d);
}});
</script><link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&display=swap' rel='stylesheet'>
                <style>                    
                    body {{ margin: 0; padding: 30px; font-family: 'Share Tech Mono', monospace; color: #bdc3c7; overflow-x: hidden; }}
                    .bg-hex {{ position: fixed; top: 0; left: 0; width: 100vw; height: 100vh; z-index: -1; pointer-events: none; background-color: #05070a; background-image: url(""data:image/svg+xml,%3Csvg width='60' height='103.923' viewBox='0 0 60 103.923' xmlns='http://www.w3.org/2000/svg'%3E%3Cpath d='M30 0l25.98 15v30l-25.98 15-25.98-15v-30zM30 51.961l25.98 15v30l-25.98 15-25.98-15v-30z' fill-opacity='0' stroke='%2300ffcc' stroke-width='1' stroke-opacity='0.1'/%3E%3C/svg%3E""); background-size: 60px 103.92px; animation: hex-pulse 5s ease-in-out infinite alternate; }}
                    @keyframes hex-pulse {{ 0% {{ opacity: 0.3; transform: scale(1); }} 100% {{ opacity: 1; transform: scale(1.05); }} }}
                    h2 {{ color: #00ffcc; border-bottom: 1px solid #2c3e50; padding-bottom: 10px; margin-top: 30px; }}
                    .panel {{ background: #0b101a; border: 1px solid #1a2536; border-radius: 8px; padding: 25px; margin-bottom: 20px; box-shadow: 0 4px 6px rgba(0,0,0,0.3); }}
                    .info-box {{ background: rgba(52, 152, 219, 0.1); border-left: 4px solid #3498db; padding: 12px 15px; border-radius: 4px; color: #3498db; margin-bottom: 15px; }}
                    .success-box {{ background: rgba(46, 204, 113, 0.1); border-left: 4px solid #2ecc71; padding: 12px 15px; border-radius: 4px; color: #2ecc71; margin-top: 25px; font-size: 1.1rem; }}
                    input[type=range] {{ -webkit-appearance: none; width: 100%; background: transparent; margin-bottom: 5px; }}
                    input[type=range]::-webkit-slider-thumb {{ -webkit-appearance: none; height: 18px; width: 18px; border-radius: 50%; background: #00ffcc; cursor: pointer; margin-top: -7px; box-shadow: 0 0 10px #00ffcc; }}
                    input[type=range]::-webkit-slider-runnable-track {{ width: 100%; height: 4px; background: #34495e; border-radius: 2px; }}
                    .btn {{ background: #111b24; color: #f1c40f; border: 1px solid #f1c40f; padding: 12px 20px; border-radius: 6px; cursor: pointer; font-family: inherit; font-size: 1rem; font-weight: bold; transition: all 0.3s; margin-bottom: 15px; }}
                    .btn:hover {{ background: rgba(241, 196, 15, 0.1); box-shadow: 0 0 15px rgba(241, 196, 15, 0.5); }}
                    .bounty-item input {{ cursor: pointer; accent-color: #e74c3c; transform: scale(1.2); }}
                    .selected-grid {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 15px; margin-bottom: 20px; }}
                    .mission-card {{ background: #111b24; border-left: 4px solid #f39c12; border-radius: 6px; padding: 15px; box-shadow: 0 2px 5px rgba(0,0,0,0.3); transition: all 0.3s; }}
                    .mission-title {{ font-weight: bold; color: #bdc3c7; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-bottom: 5px; font-size: 1rem; }}
                    .mission-ects {{ color: #2ecc71; font-size: 0.85rem; margin-bottom: 15px; font-family: monospace; }}
                    .val-label {{ float: right; color: #00ffcc; font-weight: bold; font-size: 1.1rem; }}
                    .overclocked-core-container {{ position: relative; background: rgba(20, 5, 5, 0.6); border: 3px solid #4a0000; border-radius: 12px; padding: 22px; margin-top: 15px; margin-bottom: 25px; overflow: hidden; box-shadow: 0 0 20px rgba(255, 0, 60, 0.3), inset 0 0 15px rgba(255, 255, 255, 0.05); animation: core-breathing 1.5s infinite alternate ease-in-out; backdrop-filter: blur(6px); }}
                    .metal-cage-overlay {{ position: absolute; top: 0; left: 0; width: 100%; height: 100%; background-image: repeating-linear-gradient(45deg, transparent, transparent 15px, rgba(0,0,0,0.7) 15px, rgba(0,0,0,0.7) 18px), repeating-linear-gradient(-45deg, transparent, transparent 15px, rgba(0,0,0,0.7) 15px, rgba(0,0,0,0.7) 18px); pointer-events: none; z-index: 1; }}
                    .rgb-led-core {{ position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); width: 180px; height: 180px; background: radial-gradient(circle, rgba(255,0,60,0.6) 0%, rgba(255,0,60,0) 70%); border-radius: 50%; z-index: 0; animation: led-pulse 0.4s infinite alternate; }}
                    .core-content {{ position: relative; z-index: 2; display: flex; align-items: center; }}
                    .core-icon {{ font-size: 3.2rem; margin-right: 20px; text-shadow: 0 0 25px #ff003c, 0 0 50px #ff003c; animation: hardware-shake 0.15s infinite; }}
                    .core-text {{ background: rgba(0,0,0,0.65); padding: 12px 18px; border-radius: 8px; border-left: 4px solid #ff003c; box-shadow: 0 4px 15px rgba(0,0,0,0.8); }}
                    .core-title {{ color: #ff003c; font-family: monospace; font-weight: bold; font-size: 1.4rem; letter-spacing: 2px; text-shadow: 0 0 12px #ff003c; }}
                    .core-subtitle {{ color: #fff; font-size: 0.95rem; font-weight: bold; margin-top: 4px; }}
                    @keyframes core-breathing {{ 0% {{ box-shadow: 0 0 15px rgba(255,0,60,0.2), inset 0 0 20px rgba(255,0,60,0.1); border-color: #4a0000; }} 100% {{ box-shadow: 0 0 40px rgba(255,0,60,0.6), inset 0 0 50px rgba(255,0,60,0.4); border-color: #ff003c; }} }}
                    @keyframes led-pulse {{ 0% {{ opacity: 0.7; transform: translate(-50%, -50%) scale(0.9); }} 100% {{ opacity: 1; transform: translate(-50%, -50%) scale(1.3); }} }}
                    @keyframes hardware-shake {{ 0% {{ transform: translate(1px, 1px) rotate(0deg); }} 25% {{ transform: translate(-2px, -1px) rotate(-1deg); }} 50% {{ transform: translate(-1px, 2px) rotate(1deg); }} 75% {{ transform: translate(2px, -2px) rotate(0deg); }} 100% {{ transform: translate(1px, -1px) rotate(1deg); }} }}
                    .standard-core {{ background: #0a0e17; border: 1px solid #3498db; border-left: 5px solid #3498db; border-radius: 4px; padding: 12px 15px; margin-top: 15px; margin-bottom: 25px; box-shadow: 0 0 15px rgba(52, 152, 219, 0.25); display: flex; align-items: center; }}
                    .standard-icon {{ font-size: 1.8rem; margin-right: 15px; text-shadow: 0 0 10px #3498db; }}
                    .stat-grid {{ display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 20px; }}
                    .loadout-stat {{ background: #0a0e17; border: 1px solid #34495e; border-radius: 8px; padding: 20px; text-align: center; box-shadow: inset 0 0 10px rgba(0,0,0,0.5); }}
                    .loadout-label {{ color: #7f8c8d; font-size: 0.85rem; text-transform: uppercase; letter-spacing: 1px; }}
                    .loadout-val {{ font-size: 2.5rem; margin: 10px 0; font-weight: bold; }}

                    .btn-ghost {{ color: #7f8c8d; border-color: #34495e; }}
                    .btn-ghost:hover {{ background: rgba(255,255,255,0.05); box-shadow: none; }}
                    .lo-toolbar {{ display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 8px; }}
                    .lo-toolbar .btn {{ margin-bottom: 0; }}
                    .lo-search {{ flex: 1; min-width: 220px; background: #0a0e17; border: 1px solid #1f2d3d; border-radius: 8px; padding: 11px 14px; color: #ecf0f1; font-family: inherit; font-size: 0.95rem; outline: none; transition: border-color .2s, box-shadow .2s; }}
                    .lo-search:focus {{ border-color: #00ffcc; box-shadow: 0 0 0 3px rgba(0,255,204,0.12); }}
                    .lo-note {{ font-size: 0.85rem; color: #7f8c8d; margin-bottom: 16px; }}
                    .lo-filters {{ display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 18px; }}
                    .chip {{ --gc: #00ffcc; background: #0d1520; color: #bdc3c7; border: 1px solid #1f2d3d; border-radius: 999px; padding: 7px 14px; font-family: inherit; font-size: 0.85rem; cursor: pointer; transition: all .2s; }}
                    .chip span {{ background: rgba(255,255,255,0.08); border-radius: 999px; padding: 1px 8px; margin-left: 6px; font-size: 0.78rem; }}
                    .chip:hover {{ border-color: var(--gc); color: #fff; }}
                    .chip.active {{ background: color-mix(in srgb, var(--gc) 18%, #0d1520); border-color: var(--gc); color: #fff; box-shadow: 0 0 14px color-mix(in srgb, var(--gc) 35%, transparent); }}
                    .lo-group {{ --gc: #7f8c8d; margin-bottom: 14px; border: 1px solid #1a2536; border-left: 4px solid var(--gc); border-radius: 10px; background: #0a0e17; overflow: hidden; }}
                    .lo-group > summary {{ list-style: none; cursor: pointer; display: flex; align-items: center; gap: 12px; padding: 14px 16px; user-select: none; }}
                    .lo-group > summary::-webkit-details-marker {{ display: none; }}
                    .lo-group > summary::after {{ content: '▾'; margin-left: auto; color: #7f8c8d; transition: transform .2s; }}
                    .lo-group:not([open]) > summary::after {{ transform: rotate(-90deg); }}
                    .lo-gicon {{ font-size: 1.3rem; }}
                    .lo-gtitle {{ color: var(--gc); font-weight: bold; letter-spacing: 1px; font-size: 0.95rem; }}
                    .lo-gcount {{ color: #7f8c8d; font-size: 0.8rem; }}
                    .lo-gsel {{ background: color-mix(in srgb, var(--gc) 20%, transparent); color: #fff; border: 1px solid var(--gc); border-radius: 999px; padding: 2px 10px; font-size: 0.78rem; }}
                    .lo-cards {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(290px, 1fr)); gap: 10px; padding: 4px 16px 16px; }}
                    .lo-card {{ display: flex; align-items: center; gap: 12px; background: #111b24; border: 1px solid #1f2d3d; border-radius: 10px; padding: 11px 12px; cursor: pointer; transition: all .18s; }}
                    .lo-card:hover {{ transform: translateY(-2px); border-color: var(--gc); box-shadow: 0 6px 16px rgba(0,0,0,0.4); }}
                    .lo-card.on {{ border-color: var(--gc); background: color-mix(in srgb, var(--gc) 14%, #111b24); box-shadow: 0 0 14px color-mix(in srgb, var(--gc) 30%, transparent); }}
                    .lo-check {{ width: 22px; height: 22px; border-radius: 6px; border: 2px solid #34495e; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: bold; color: #05070a; flex-shrink: 0; transition: all .18s; }}
                    .lo-card.on .lo-check {{ background: var(--gc); border-color: var(--gc); }}
                    .lo-info {{ flex: 1; min-width: 0; }}
                    .lo-name {{ color: #ecf0f1; font-size: 0.9rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }}
                    .lo-meta {{ color: #7f8c8d; font-size: 0.75rem; margin-top: 2px; }}
                    .lo-ects {{ color: var(--gc); font-weight: bold; font-size: 1.05rem; text-align: right; line-height: 1; }}
                    .lo-ects small {{ display: block; font-size: 0.6rem; color: #7f8c8d; margin-top: 3px; font-weight: normal; }}
                    .lo-empty {{ color: #7f8c8d; padding: 14px; text-align: center; grid-column: 1 / -1; }}
                    .lo-sumbar {{ margin: 22px 0 18px; padding: 16px; border: 1px dashed #34495e; border-radius: 10px; background: rgba(255,255,255,0.02); }}
                    .lo-sumtitle {{ color: #f1c40f; font-weight: bold; margin-bottom: 10px; letter-spacing: 1px; }}
                    .lo-summary {{ display: flex; flex-wrap: wrap; gap: 10px; }}
                    .lo-pill {{ --gc: #7f8c8d; border: 1px solid var(--gc); background: color-mix(in srgb, var(--gc) 16%, transparent); color: #ecf0f1; border-radius: 999px; padding: 6px 14px; font-size: 0.85rem; }}
                    .lo-hint {{ color: #7f8c8d; font-size: 0.9rem; }}
                    .mc-top {{ display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; }}
                    .mc-tag {{ --gc: #7f8c8d; font-size: 0.7rem; letter-spacing: 0.5px; color: var(--gc); border: 1px solid var(--gc); border-radius: 999px; padding: 2px 9px; }}
                    .mc-x {{ color: #7f8c8d; cursor: pointer; padding: 0 4px; transition: color .2s; }}
                    .mc-x:hover {{ color: #e74c3c; }}
                </style>
                </head>
                <body>
                    <div class='bg-hex'></div>
                    <h2>🔮 AI Προσομοιωτής Βαθμού</h2>
                    <div class='panel'>
                        <div id='ai-sim-info'></div>
                        <div style='display:flex; gap:30px; margin-top:15px;'>
                            <div style='flex:1;'>
                                <label style='font-size:1.1rem;'>Στόχος Μ.Ο. εναπομεινάντων: <span id='val-course' class='val-label'>{aiGradeStr}</span></label>
                                <input type='range' id='sim-course' min='5.0' max='10.0' step='0.1' value='{aiGradeStr}' oninput='updateAiSim()'>
                            </div>
                            <div style='flex:1;'>
                                <label style='font-size:1.1rem;'>Στόχος Διπλωματικής (30 ECTS): <span id='val-thesis' class='val-label'>9.0</span></label>
                                <input type='range' id='sim-thesis' min='5.0' max='10.0' step='0.1' value='9.0' oninput='updateAiSim()'>
                            </div>
                        </div>
                        <div id='ai-result' class='success-box'></div>
                    </div>
                    <h2>🎯 Mission Loadout (Στρατηγικό Πλάνο Εξεταστικής)</h2>
                    <div class='panel'>
                        <div class='lo-toolbar'>
                            <button class='btn' onclick='autoEquip()'>⚡ AUTO-EQUIP (AI Loadout)</button>
                            <button class='btn btn-ghost' onclick='clearLoadout()'>🗑️ Καθαρισμός</button>
                            <input id='lo-search' class='lo-search' type='text' placeholder='🔍 Αναζήτηση μαθήματος ή κωδικού...'>
                        </div>
                        <div class='lo-note'>
                            <b>Knapsack Alg:</b> Αυτόματη σύνθεση ιδανικού Loadout (~30 ECTS) με μίξη δύσκολων και εύκολων μαθημάτων.
                            Επιλέγει από την κατηγορία που έχεις φιλτράρει. <span style='color: #f1c40f;'>Κάνε κλικ ξανά για re-roll!</span>
                        </div>

                        <div id='lo-filters' class='lo-filters'></div>
                        <div id='lo-groups'></div>

                        <div class='lo-sumbar'>
                            <div class='lo-sumtitle'>🎒 Το Loadout σου</div>
                            <div id='lo-summary' class='lo-summary'></div>
                        </div>
                        <div id='selected-container' class='selected-grid'></div>
                        <div id='projection-container'></div>
                    </div>

                    <script>
                        const currentPoints = {currentPoints.ToString(System.Globalization.CultureInfo.InvariantCulture)};
                        const totalEcts = {totalEcts.ToString(System.Globalization.CultureInfo.InvariantCulture)};
                        const totalCourses = {totalCourses};
                        const targetCourses = 47;
                        const hasThesis = {hasThesis.ToString().ToLower()};
                        const missingCourses = {missingJson};
                        const simSaved = {simJson};
                        window.targetGrades = {{}};
                        
                        function initAiSim() {{
                            let missingTotalEcts = Math.max(0, 300 - totalEcts);
                            let missingCourseCount = Math.max(0, targetCourses - totalCourses);
                            let infoHtml = ''; let simThesis = document.getElementById('sim-thesis');
                            if(hasThesis) {{ simThesis.disabled = true; simThesis.style.opacity = '0.3'; }}
                            if (missingTotalEcts > 0 || missingCourseCount > 0) {{
                                if (missingCourseCount > 0) {{ infoHtml += `<div class='info-box'>🤖 <b>k-NN Πρόβλεψη:</b> Αναμένεται να γράψεις <b>~{aiGradeStr}</b> στα ${{missingCourseCount}} μαθήματα.</div>`; }}
                                else {{ infoHtml += `<div class='info-box'>Έχεις περάσει όλα τα απαιτούμενα μαθήματα!</div>`; document.getElementById('sim-course').disabled = true; document.getElementById('sim-course').style.opacity = '0.3'; }}
                                if(!hasThesis) infoHtml += `<div class='info-box' style='border-left-color:#f1c40f; color:#f1c40f;'>💡 <b>Tip:</b> Στη Διπλωματική ο στόχος ορίστηκε στο 9.0 από προεπιλογή.</div>`;
                            }} else {{
                                infoHtml = `<div class='info-box'>Έχεις συγκεντρώσει 300+ ECTS! Ο βαθμός σου έχει κλειδώσει.</div>`;
                                document.getElementById('sim-course').disabled = true; simThesis.disabled = true;
                            }}
                            document.getElementById('ai-sim-info').innerHTML = infoHtml; updateAiSim();
                        }}
                        function updateAiSim() {{
                            let missingTotalEcts = Math.max(0, 300 - totalEcts);
                            let missingCourseCount = Math.max(0, targetCourses - totalCourses);
                            if(missingTotalEcts === 0 && missingCourseCount === 0) return;
                            let cGrade = parseFloat(document.getElementById('sim-course').value); let tGrade = parseFloat(document.getElementById('sim-thesis').value);
                            document.getElementById('val-course').innerText = cGrade.toFixed(1); document.getElementById('val-thesis').innerText = tGrade.toFixed(1);
                            let missingCourseEcts = missingCourseCount === 0 ? 0 : missingTotalEcts - (hasThesis ? 0 : 30);
                            let futPoints = currentPoints + (missingCourseEcts * cGrade) + (hasThesis ? 0 : 30 * tGrade);
                            let futEcts = totalEcts + missingCourseEcts + (hasThesis ? 0 : 30);
                            let simGpa = futEcts > 0 ? (futPoints / futEcts) : 0;
                            let simClass = simGpa >= 8.5 ? 'Άριστα 🏆' : simGpa >= 6.5 ? 'Λίαν Καλώς 🥈' : 'Καλώς 🥉';
                            document.getElementById('ai-result').innerHTML = `✨ <b>Τελική Προβολή Πτυχίου:</b> Τελικός βαθμός <b>${{simGpa.toFixed(2)}} (${{simClass}})</b>!`;
                        }}
                        // ===== MISSION LOADOUT v2 =====
                        const norm = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toUpperCase();
                        const esc = s => String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/'/g, '&#39;').replace(/\u0022/g, '&quot;');
                        const fmt = n => Number.isInteger(n) ? String(n) : n.toFixed(1);

                        function styleFor(cat) {{
                            const c = norm(cat);
                            if (c.includes('ΕΠΙΛΟΓ')) return {{ rank: 2, icon: '🧩', color: '#f39c12' }};
                            if (c.includes('ΕΚΤΟΣ')) return {{ rank: 3, icon: '🌐', color: '#9b59b6' }};
                            if (c.includes('ΥΠΟΧΡΕΩΤ')) return {{ rank: 1, icon: '📘', color: '#3498db' }};
                            return {{ rank: 4, icon: '📦', color: '#1abc9c' }};
                        }}

                        missingCourses.sort((a, b) => (a.SemesterOrder - b.SemesterOrder) || String(a.Title).localeCompare(String(b.Title), 'el'));

                        // Οι ομάδες φτιάχνονται από τις πραγματικές κατηγορίες του ClassWeb
                        const GROUPS = [];
                        missingCourses.forEach((m, i) => {{
                            m._i = i;
                            const key = (norm(m.Category) || 'ΑΛΛΑ').replace(/[^0-9A-ZΑ-Ω]/g, '_');
                            let g = GROUPS.find(x => x.key === key);
                            if (!g) {{
                                const s = styleFor(m.Category);
                                g = {{ key: key, title: m.Category || 'ΑΛΛΑ', icon: s.icon, color: s.color, rank: s.rank }};
                                GROUPS.push(g);
                            }}
                            m._g = key;
                        }});
                        GROUPS.sort((a, b) => a.rank - b.rank || a.title.localeCompare(b.title, 'el'));

                        const selected = new Set();
                        const collapsedGroups = new Set();
                        let activeFilter = 'all';
                        let searchText = '';

                        function selectedList() {{ return missingCourses.filter(m => selected.has(m._i)); }}

                        function defaultTarget() {{
                            const v = parseFloat(document.getElementById('sim-course').value);
                            return Math.min(10, Math.max(5, Math.round((isNaN(v) ? 7.5 : v) * 2) / 2));
                        }}

                        function updateAll() {{ renderFilters(); renderCourses(); renderLoadout(); }}

                        function renderFilters() {{
                            const chip = (key, label, n, color) => `<button class='chip ${{activeFilter === key ? 'active' : ''}}' data-filter='${{key}}' style='--gc:${{color}}'>${{label}} <span>${{n}}</span></button>`;
                            let html = chip('all', 'Όλα', missingCourses.length, '#00ffcc');
                            GROUPS.forEach(g => {{
                                const n = missingCourses.filter(m => m._g === g.key).length;
                                if (n > 0) html += chip(g.key, `${{g.icon}} ${{g.title}}`, n, g.color);
                            }});
                            document.getElementById('lo-filters').innerHTML = html;
                        }}

                        function renderCourses() {{
                            const q = norm(searchText);
                            let html = '';
                            GROUPS.forEach(g => {{
                                if (activeFilter !== 'all' && activeFilter !== g.key) return;
                                const all = missingCourses.filter(m => m._g === g.key);
                                if (all.length === 0) return;

                                const list = all.filter(m => !q || norm(m.Title).includes(q) || norm(m.Code).includes(q));
                                const selItems = all.filter(m => selected.has(m._i));
                                const selE = selItems.reduce((s, m) => s + m.Ects, 0);

                                html += `<details class='lo-group' data-g='${{g.key}}' style='--gc:${{g.color}}' ${{collapsedGroups.has(g.key) ? '' : 'open'}}>`
                                      + `<summary><span class='lo-gicon'>${{g.icon}}</span><span class='lo-gtitle'>${{g.title}}</span>`
                                      + `<span class='lo-gcount'>${{list.length}} διαθέσιμα</span>`
                                      + (selItems.length > 0 ? `<span class='lo-gsel'>✓ ${{selItems.length}} επιλεγμένα · ${{fmt(selE)}} ECTS</span>` : '')
                                      + `</summary><div class='lo-cards'>`;

                                list.forEach(m => {{
                                    const on = selected.has(m._i);
                                    const meta = [m.SemesterOrder && m.SemesterOrder < 999 ? m.SemesterOrder + 'ο εξάμηνο' : '', m.Period && m.Period !== '-' ? esc(m.Period) : '', m.Code && m.Code !== '-' ? esc(m.Code) : ''].filter(Boolean).join(' · ');
                                    html += `<div class='lo-card ${{on ? 'on' : ''}}' data-i='${{m._i}}' title='${{esc(m.Title)}}'>`
                                          + `<div class='lo-check'>${{on ? '✓' : ''}}</div>`
                                          + `<div class='lo-info'><div class='lo-name'>${{esc(m.Title)}}</div><div class='lo-meta'>${{meta}}</div></div>`
                                          + `<div class='lo-ects'>${{fmt(m.Ects)}}<small>ECTS</small></div></div>`;
                                }});
                                if (list.length === 0) html += `<div class='lo-empty'>Κανένα αποτέλεσμα</div>`;
                                html += `</div></details>`;
                            }});
                            document.getElementById('lo-groups').innerHTML = html || `<div class='lo-empty'>Δεν υπάρχουν διαθέσιμα μαθήματα 🎉</div>`;
                        }}

                        function renderLoadout() {{
                            const sel = selectedList();
                            const preEcts = sel.reduce((s, m) => s + m.Ects, 0);
                            const isOverclocked = preEcts > 35;

                            let sum = '';
                            GROUPS.forEach(g => {{
                                const items = sel.filter(m => m._g === g.key);
                                if (items.length === 0) return;
                                sum += `<div class='lo-pill' style='--gc:${{g.color}}'>${{g.icon}} <b>${{items.length}}</b> ${{g.title}} · ${{fmt(items.reduce((s, m) => s + m.Ects, 0))}} ECTS</div>`;
                            }});
                            if (sel.length > 0) sum += `<div class='lo-pill' style='--gc:#f1c40f'>Σύνολο: <b>${{sel.length}}</b> μαθήματα · <b>${{fmt(preEcts)}}</b> ECTS</div>`;
                            document.getElementById('lo-summary').innerHTML = sum || `<div class='lo-hint'>Δεν έχεις επιλέξει μαθήματα ακόμα.</div>`;

                            let selHtml = '';
                            GROUPS.forEach(g => {{
                                sel.filter(m => m._g === g.key).forEach(m => {{
                                    if (!window.targetGrades[m.Title]) window.targetGrades[m.Title] = defaultTarget();
                                    const tg = window.targetGrades[m.Title];
                                    const bc = isOverclocked ? '#ff003c' : g.color;
                                    const bg = isOverclocked ? 'rgba(255,0,60,0.1)' : '#111b24';
                                    selHtml += `<div class='mission-card' style='border-left-color:${{bc}}; background:${{bg}};'>`
                                             + `<div class='mc-top'><span class='mc-tag' style='--gc:${{g.color}}'>${{g.icon}} ${{g.title}}</span><span class='mc-x' data-rm='${{m._i}}' title='Αφαίρεση'>✕</span></div>`
                                             + `<div class='mission-title' title='${{esc(m.Title)}}'>${{esc(m.Title)}}</div>`
                                             + `<div class='mission-ects'>Reward: ${{fmt(m.Ects)}} ECTS</div>`
                                             + `<label style='font-size:0.9rem;'>Target Grade: <span class='val-label' id='tg-${{m._i}}'>${{tg.toFixed(1)}}</span></label>`
                                             + `<input type='range' class='tg-range' data-i='${{m._i}}' min='5.0' max='10.0' step='0.5' value='${{tg}}'></div>`;
                                }});
                            }});
                            document.getElementById('selected-container').innerHTML = selHtml;
                            refreshProjection();
                        }}

                        function refreshProjection() {{
                            const sel = selectedList();
                            const preEcts = sel.reduce((s, m) => s + m.Ects, 0);
                            calculateProjection(sel, preEcts, preEcts > 35);
                        }}

                        function autoEquip() {{
                            const pool = missingCourses.filter(m => activeFilter === 'all' || m._g === activeFilter);
                            const heavy = pool.filter(c => c.Ects >= 6).sort(() => 0.5 - Math.random());
                            const light = pool.filter(c => c.Ects < 6).sort(() => 0.5 - Math.random());
                            const picked = []; let currEcts = 0;
                            for (let i = 0; i < Math.min(2, heavy.length); i++) {{ if (currEcts + heavy[i].Ects <= 34) {{ picked.push(heavy[i]); currEcts += heavy[i].Ects; }} }}
                            const rest = light.concat(heavy.slice(2));
                            for (let i = 0; i < rest.length; i++) {{ if (currEcts + rest[i].Ects <= 33) {{ picked.push(rest[i]); currEcts += rest[i].Ects; }} if (currEcts >= 28) break; }}
                            selected.clear(); picked.forEach(m => selected.add(m._i));
                            updateAll();
                        }}

                        function clearLoadout() {{ selected.clear(); updateAll(); }}

                        // ---- Events (delegation) ----
                        document.getElementById('lo-filters').addEventListener('click', e => {{
                            const b = e.target.closest('[data-filter]'); if (!b) return;
                            activeFilter = b.dataset.filter; updateAll();
                        }});
                        document.getElementById('lo-search').addEventListener('input', e => {{ searchText = e.target.value; renderCourses(); }});
                        document.getElementById('lo-groups').addEventListener('click', e => {{
                            const c = e.target.closest('.lo-card'); if (!c) return;
                            const i = parseInt(c.dataset.i, 10);
                            if (selected.has(i)) selected.delete(i); else selected.add(i);
                            updateAll();
                        }});
                        document.getElementById('lo-groups').addEventListener('toggle', e => {{
                            const k = e.target.dataset ? e.target.dataset.g : null; if (!k) return;
                            if (e.target.open) collapsedGroups.delete(k); else collapsedGroups.add(k);
                        }}, true);
                        document.getElementById('selected-container').addEventListener('input', e => {{
                            const r = e.target.closest('.tg-range'); if (!r) return;
                            const m = missingCourses[parseInt(r.dataset.i, 10)];
                            const v = parseFloat(r.value);
                            window.targetGrades[m.Title] = v;
                            document.getElementById('tg-' + m._i).innerText = v.toFixed(1);
                            refreshProjection();
                        }});
                        document.getElementById('selected-container').addEventListener('click', e => {{
                            const x = e.target.closest('[data-rm]'); if (!x) return;
                            selected.delete(parseInt(x.dataset.rm, 10)); updateAll();
                        }});
                        function calculateProjection(selectedMissions, preEcts, isOverclocked) {{
                            let pContainer = document.getElementById('projection-container');
                            if(selectedMissions.length === 0) {{ pContainer.innerHTML = `<div class='info-box' style='color:#bdc3c7; border-color:#bdc3c7;'>📡 Το Loadout είναι άδειο. Διάλεξε Bounties από τη λίστα ή πάτα το AUTO-EQUIP!</div>`; return; }}
                            let mPoints = 0; let mEcts = 0;
                            selectedMissions.forEach(m => {{ mPoints += window.targetGrades[m.Title] * m.Ects; mEcts += m.Ects; }});
                            let sessionGpa = mEcts > 0 ? (mPoints / mEcts) : 0; let projEcts = totalEcts + mEcts; let projGpa = projEcts > 0 ? (currentPoints + mPoints) / projEcts : 0; let curGpa = totalEcts > 0 ? currentPoints/totalEcts : 0;
                            let diff = projGpa - curGpa; let diffStr = diff > 0 ? `+${{diff.toFixed(2)}}` : diff.toFixed(2); let diffCol = diff > 0.001 ? '#2ecc71' : (diff < -0.001 ? '#e74c3c' : '#7f8c8d');
                            let bannerHtml = '';
                            if(isOverclocked) {{ bannerHtml = `<div class='overclocked-core-container'><div class='metal-cage-overlay'></div><div class='rgb-led-core'></div><div class='core-content'><div class='core-icon'>⚠️</div><div class='core-text'><div class='core-title'>CRITICAL OVERLOAD [ ${{preEcts}} ECTS ]</div><div class='core-subtitle'>${{selectedMissions.length}} Ενεργά Bounties — <span style='color:#f1c40f;'>SYSTEM INSTABILITY DETECTED. COOLING FAILURE IMMINENT.</span></div></div></div></div>`; }}
                            else {{ let dCol = preEcts <= 15 ? '#2ecc71' : '#3498db'; let dTit = preEcts <= 15 ? 'SYSTEM SECURE' : 'STANDARD PROTOCOL'; let dMsg = preEcts <= 15 ? 'Ελαφρύς φόρτος. Ιδανικό για High Grades.' : 'Κανονική εξεταστική. Απαιτείται Focus.'; let dIco = preEcts <= 15 ? '🛡️' : '⚙️'; bannerHtml = `<div class='standard-core' style='border-color: ${{dCol}}; border-left-color: ${{dCol}};'><div class='standard-icon' style='text-shadow: 0 0 10px ${{dCol}};'>${{dIco}}</div><div><div style='color: ${{dCol}}; font-family: monospace; font-weight: bold; font-size: 1.25rem; letter-spacing: 1px;'>${{dTit}} [ ${{preEcts}} ECTS ]</div><div style='color: #bdc3c7; font-size: 0.95rem; margin-top:5px;'>${{selectedMissions.length}} Ενεργά Bounties — <i>${{dMsg}}</i></div></div></div>`; }}
                            pContainer.innerHTML = `${{bannerHtml}}<h3 style='color: ${{isOverclocked ? '#ff003c' : '#f1c40f'}}; margin-top:20px; font-size:1.3rem;'>📊 Session Projection ${{isOverclocked ? '(WARNING!)' : ''}}</h3><div class='stat-grid'><div class='loadout-stat' style='border-color: ${{isOverclocked ? '#ff003c' : '#3498db'}};'><div class='loadout-label'>Session GPA</div><div class='loadout-val' style='color:#3498db; text-shadow: 0 0 10px rgba(52,152,219,0.5);'>${{sessionGpa.toFixed(2)}}</div></div><div class='loadout-stat' style='border-color: ${{isOverclocked ? '#ff003c' : '#2ecc71'}};'><div class='loadout-label'>New Total ECTS</div><div class='loadout-val' style='color:#2ecc71; text-shadow: 0 0 10px rgba(46,204,113,0.5);'>${{projEcts}}<span style='font-size:1.2rem; color:#7f8c8d;'> (+${{mEcts}})</span></div></div><div class='loadout-stat' style='border-color: ${{isOverclocked ? '#ff003c' : '#f1c40f'}};'><div class='loadout-label'>Projected Overall GPA</div><div class='loadout-val' style='color:#f1c40f; text-shadow: 0 0 10px rgba(241,196,15,0.5);'>${{projGpa.toFixed(2)}} <span style='font-size:1.2rem; color:${{diffCol}};'>(${{diffStr}})</span></div></div></div>`;
                        }}
                        // ===== MEMORY CARD: επαναφορά και αποθήκευση Simulator =====
                        let courseGoalTouched = false, thesisGoalTouched = false;

                        (function restoreSim() {{
                            try {{
                                if (simSaved.CourseGoal >= 5) {{ document.getElementById('sim-course').value = simSaved.CourseGoal; courseGoalTouched = true; }}
                                if (simSaved.ThesisGoal >= 5) {{ document.getElementById('sim-thesis').value = simSaved.ThesisGoal; thesisGoalTouched = true; }}
                                if (simSaved.Filter === 'all' || GROUPS.some(g => g.key === simSaved.Filter)) activeFilter = simSaved.Filter;
                                (simSaved.Selected || []).forEach(t => {{
                                    const m = missingCourses.find(x => x.Title === t);
                                    if (m) selected.add(m._i);
                                }});
                                Object.keys(simSaved.Targets || {{}}).forEach(t => {{ window.targetGrades[t] = simSaved.Targets[t]; }});
                            }} catch (e) {{ console.error(e); }}
                        }})();

                        let _simSaveTimer = null;
                        function saveSim() {{
                            clearTimeout(_simSaveTimer);
                            _simSaveTimer = setTimeout(() => {{
                                if (!(window.chrome && window.chrome.webview)) return;
                                const sel = selectedList();
                                const targets = {{}};
                                sel.forEach(m => {{ if (window.targetGrades[m.Title]) targets[m.Title] = window.targetGrades[m.Title]; }});
                                const cg = parseFloat(document.getElementById('sim-course').value);
                                const tg = parseFloat(document.getElementById('sim-thesis').value);
                                window.chrome.webview.postMessage(JSON.stringify({{
                                    type: 'sim',
                                    value: {{
                                        courseGoal: (courseGoalTouched && !isNaN(cg)) ? cg : -1,
                                        thesisGoal: (thesisGoalTouched && !isNaN(tg)) ? tg : -1,
                                        filter: activeFilter,
                                        selected: sel.map(m => m.Title),
                                        targets: targets
                                    }}
                                }}));
                            }}, 400);
                        }}

                        // Κάθε κλικ ή αλλαγή slider ενεργοποιεί (με καθυστέρηση 0.4s) την αποθήκευση
                        document.addEventListener('input', e => {{
                            if (e.target.id === 'sim-course') courseGoalTouched = true;
                            if (e.target.id === 'sim-thesis') thesisGoalTouched = true;
                            saveSim();
                        }}, true);
                        document.addEventListener('click', () => saveSim(), true);
                        initAiSim(); updateAll();
                    </script>
                </body>
                </html>";

                SimulatorBrowser.NavigateToString(ApplyFx(simHtml));
            }
            catch (Exception ex) { ShowErrorPage(SimulatorBrowser, "Simulator", ex); }
        }

        // ==========================================
        // TAB 5: DEEP DIVE STUDY HUB
        // ==========================================
        private void MemoryBufferText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MemoryPlaceholder != null) MemoryPlaceholder.Visibility = string.IsNullOrEmpty(MemoryBufferText.Text) ? Visibility.Visible : Visibility.Collapsed;
            try { File.WriteAllText(MemoryFilePath, MemoryBufferText.Text); } catch { }
        }

        private async Task LoadDeepDiveHub()
        {
            try
            {
                if (File.Exists(MemoryFilePath)) MemoryBufferText.Text = File.ReadAllText(MemoryFilePath);

                var env = await GetEnvAsync();

                await PomodoroBrowser.EnsureCoreWebView2Async(env);
                await RadioBrowser.EnsureCoreWebView2Async(env);

                string pomodoroHtml = @"
                <html><head><link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&display=swap' rel='stylesheet'>
                <style>
                body { margin: 0; padding: 20px; background: #0a0e17; font-family: 'Share Tech Mono', monospace; color: #00ffcc; display: flex; flex-direction: column; justify-content: space-evenly; height: 100vh; box-sizing: border-box; }
                .mission-input { background: #111b24; border: 1px dashed #34495e; color: #f1c40f; padding: 15px; width: 100%; box-sizing: border-box; border-radius: 6px; font-family: 'Share Tech Mono', monospace; font-size: 1.1rem; text-align: center; outline: none; transition: all 0.3s; margin-bottom: 25px; }
                .mission-input:focus { border-color: #f1c40f; box-shadow: 0 0 10px rgba(241, 196, 15, 0.3); }
                .timer-display { font-size: 6.5rem; text-align: center; text-shadow: 0 0 15px rgba(0, 255, 204, 0.8); letter-spacing: 2px; transition: all 0.3s; margin: 10px 0; }
                .mode-text { color: #f39c12; font-size: 1.2rem; text-transform: uppercase; letter-spacing: 1.5px; text-align: center; }
                .progress-bg { background: rgba(255,255,255,0.05); border-radius: 10px; height: 12px; width: 100%; border: 1px solid #1a252f; margin: 15px 0; overflow: hidden; }
                .progress-fill { background: #00ffcc; height: 100%; width: 100%; transition: width 1s linear, background 0.3s ease; box-shadow: 0 0 10px #00ffcc; }
                .btn-group { display: flex; justify-content: center; gap: 10px; margin-bottom: 10px; }
                .btn { background: #111b24; color: #ecf0f1; border: 1px solid #34495e; padding: 12px; border-radius: 6px; cursor: pointer; font-family: 'Share Tech Mono', monospace; font-size: 1rem; transition: all 0.2s; flex-grow: 1; }
                .btn:hover { border-color: #00ffcc; color: #00ffcc; box-shadow: 0 0 10px rgba(0, 255, 204, 0.4); }
                .stats { font-size: 1rem; color: #7f8c8d; border-top: 1px dashed #34495e; padding-top: 15px; text-align: center; }
                .stats span { color: #2ecc71; font-weight: bold; font-size: 1.3rem; }
                </style></head>
                <body>
                    <input type='text' class='mission-input' id='mission' placeholder='🎯 Input Active Mission...' />
                    <div class='mode-text' id='mode-text'>SYSTEM STANDBY</div>
                    <div class='timer-display' id='timer'>30:00</div>
                    <div class='progress-bg'><div class='progress-fill' id='progress'></div></div>
                    <div class='btn-group'>
                        <button class='btn' onclick='startTimer()'>START</button>
                        <button class='btn' onclick='pauseTimer()'>PAUSE</button>
                        <button class='btn' onclick='resetTimer()'>RESET</button>
                    </div>
                    <button class='btn' style='border-color: #e67e22; color: #e67e22; width: 100%;' onclick='toggleMode()' id='mode-btn'>SWITCH TO REST MODE</button>
                    <div class='stats'>Deep Dive Cycles: <span id='cycles'>0</span></div>
                <script>
                    const WORK_TIME = 30 * 60; const REST_TIME = 5 * 60;
                    let timeLeft = WORK_TIME; let totalTime = WORK_TIME;
                    let timerId = null; let isWorkMode = true; let cycles = 0;
                    const display = document.getElementById('timer'); const modeText = document.getElementById('mode-text');
                    const progressFill = document.getElementById('progress'); const alertSound = new Audio('https://assets.mixkit.co/active_storage/sfx/2869/2869-preview.mp3');
                    function updateDisplay() {
                        let m = Math.floor(timeLeft / 60); let s = timeLeft % 60;
                        display.textContent = (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
                        progressFill.style.width = ((timeLeft / totalTime) * 100) + '%';
                    }
                    function updateTheme() {
                        let c = isWorkMode ? '#e74c3c' : '#3498db';
                        if (timerId === null && timeLeft === totalTime) c = '#00ffcc';
                        display.style.color = c; display.style.textShadow = `0 0 15px ${c}`;
                        progressFill.style.background = c; progressFill.style.boxShadow = `0 0 10px ${c}`;
                    }
                    function startTimer() {
                        if (timerId !== null) return;
                        modeText.textContent = isWorkMode ? 'DEEP DIVE: ACTIVE' : 'COOLING PROTOCOL'; modeText.style.color = isWorkMode ? '#e74c3c' : '#3498db';
                        timerId = setInterval(() => {
                            timeLeft--; updateDisplay();
                            if (timeLeft <= 0) {
                                clearInterval(timerId); timerId = null; alertSound.play();
                                if (isWorkMode) { cycles++; document.getElementById('cycles').textContent = cycles; }
                                toggleMode();
                            }
                        }, 1000);
                        updateTheme(); 
                    }
                    function pauseTimer() { 
                        if (timerId === null) return; 
                        clearInterval(timerId); timerId = null; 
                        modeText.textContent = 'SYSTEM PAUSED'; modeText.style.color = '#f1c40f'; 
                        display.style.color = '#f1c40f'; display.style.textShadow = '0 0 15px rgba(241, 196, 15, 0.8)';
                        progressFill.style.background = '#f1c40f'; progressFill.style.boxShadow = '0 0 10px #f1c40f';
                    }
                    function resetTimer() { clearInterval(timerId); timerId = null; totalTime = isWorkMode ? WORK_TIME : REST_TIME; timeLeft = totalTime; modeText.textContent = 'SYSTEM STANDBY'; modeText.style.color = '#f39c12'; updateDisplay(); updateTheme(); }
                    function toggleMode() { isWorkMode = !isWorkMode; document.getElementById('mode-btn').textContent = isWorkMode ? 'SWITCH TO REST MODE' : 'SWITCH TO WORK MODE'; resetTimer(); }
                </script></body></html>";

                string radioHtml = @"
                <html><head><meta charset='UTF-8'><link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&display=swap' rel='stylesheet'>
                <style>
                body { margin: 0; padding: 15px; background: #0a0e17; font-family: 'Share Tech Mono', monospace; color: #ecf0f1; display: flex; flex-direction: column; height: 100vh; box-sizing: border-box; }
                .slider-container { margin-bottom: 15px; } .labels { display: flex; justify-content: space-between; font-size: 0.8rem; color: #7f8c8d; margin-bottom: 5px; }
                input[type=range] { -webkit-appearance: none; width: 100%; background: transparent; }
                input[type=range]::-webkit-slider-thumb { -webkit-appearance: none; height: 22px; width: 10px; border-radius: 3px; background: #e74c3c; cursor: pointer; box-shadow: 0 0 10px #e74c3c; border: 2px solid #fff; margin-top: -9px; }
                input[type=range]::-webkit-slider-runnable-track { width: 100%; height: 4px; background: #34495e; border-radius: 2px; }
                .led-screen { background: #05070a; border: 1px solid #f39c12; padding: 10px; border-radius: 6px; font-size: 1rem; margin-bottom: 15px; text-shadow: 0 0 8px #f39c12; color: #f39c12; box-shadow: inset 0 0 10px rgba(243,156,18,0.2); text-align: center;}
                .screen-container { background: #000; border-radius: 6px; overflow: hidden; flex-grow: 1; border: 1px solid #2c3e50; min-height: 150px; }
                iframe { width: 100%; height: 100%; border: none; }
                .offline-msg { display: flex; align-items: center; justify-content: center; height: 100%; color: #34495e; letter-spacing: 1px; }
                .aux-input { width: 100%; background: #111b24; border: 1px dashed #00ffcc; color: #00ffcc; padding: 8px; font-family: 'Share Tech Mono', monospace; font-size: 0.8rem; margin-top: 10px; border-radius: 4px; outline: none; display: none; text-align: center; box-sizing: border-box; }
                .aux-input:focus { border: 1px solid #00ffcc; box-shadow: 0 0 10px rgba(0,255,204,0.3); }
                .mixer-panel { background: #05070a; border: 1px dashed #34495e; border-radius: 6px; padding: 15px; margin-top: 15px; box-shadow: inset 0 0 10px rgba(0,0,0,0.5); }
                .mixer-title { color: #7f8c8d; font-size: 0.75rem; letter-spacing: 2px; margin-bottom: 12px; text-align: center; }
                .mixer-row { display: flex; align-items: center; justify-content: space-between; margin-bottom: 10px; }
                .mixer-label { font-size: 0.85rem; width: 35%; font-weight: bold; }
                .mixer-slider::-webkit-slider-thumb { height: 16px; width: 10px; border: 1px solid #fff; margin-top: -6px; }
                #vol-rain::-webkit-slider-thumb { background: #3498db; box-shadow: 0 0 8px #3498db; }
                #vol-server::-webkit-slider-thumb { background: #9b59b6; box-shadow: 0 0 8px #9b59b6; }
                #vol-key::-webkit-slider-thumb { background: #e74c3c; box-shadow: 0 0 8px #e74c3c; }
                </style></head>
                <body>
                    <div class='slider-container'>
                        <div class='labels'><span>OFF</span><span>98.5</span><span>101.2</span><span>104.4</span><span>107.8</span><span>AUX</span></div>
                        <input type='range' id='freq-slider' min='0' max='5' value='1'>
                    </div>
                    <div class='led-screen' id='led'>TUNED: 98.5 FM (LOFI)</div>
                    <div class='screen-container' id='screen'></div>
                    <input type='text' id='aux-input' class='aux-input' placeholder='🔗 Paste YouTube URL & Press Enter...'>
                    <div class='mixer-panel'>
                        <div class='mixer-title'>SYNTHETIC AMBIENCE</div>
                        <div class='mixer-row'><span class='mixer-label' style='color: #3498db;'>🌧️ RAIN</span><input type='range' id='vol-rain' class='mixer-slider' min='0' max='1' step='0.05' value='0'></div>
                        <div class='mixer-row'><span class='mixer-label' style='color: #9b59b6;'>🖥️ SERVER</span><input type='range' id='vol-server' class='mixer-slider' min='0' max='1' step='0.05' value='0'></div>
                        <div class='mixer-row'><span class='mixer-label' style='color: #e74c3c;'>📡 DATA</span><input type='range' id='vol-key' class='mixer-slider' min='0' max='1' step='0.05' value='0'></div>
                    </div>
                <script>
                    const slider = document.getElementById('freq-slider'); const led = document.getElementById('led'); 
                    const screen = document.getElementById('screen'); const auxInput = document.getElementById('aux-input');
                    const stations = [
                        { name: 'POWER OFF', color: '#34495e', url: null },
                        { name: 'TUNED: 98.5 FM (LOFI)', color: '#e67e22', url: 'https://www.youtube.com/embed/lTRiuFIWV54' },
                        { name: 'TUNED: 101.2 FM (JAZZ)', color: '#1abc9c', url: 'https://www.youtube.com/embed/MYPVQccHhAQ?autoplay=1' },
                        { name: 'TUNED: 104.4 FM (FANTASY)', color: '#6c5ce7', url: 'https://www.youtube.com/embed/mm0QSsRwzUo?autoplay=1' },
                        { name: 'TUNED: 107.8 FM (PIANO)', color: '#2980b9', url: 'https://www.youtube.com/embed/rZxbHDtlcPU?autoplay=1' },
                        { name: 'AUXILIARY LINK ACTIVE', color: '#00ffcc', url: 'aux' }
                    ];
                    function getEmbedUrl(url) {
                        let vid = '';
                        if (url.includes('v=')) vid = url.split('v=')[1].substring(0,11);
                        else if (url.includes('youtu.be/')) vid = url.split('youtu.be/')[1].substring(0,11);
                        return vid ? 'https://www.youtube.com/embed/' + vid + '?autoplay=1' : '';
                    }
                    function updateRadio() {
                        const st = stations[slider.value];
                        led.textContent = st.name; led.style.color = st.color; led.style.textShadow = `0 0 8px ${st.color}`; led.style.borderColor = st.color;
                        if (st.url === null) { screen.innerHTML = '<div class=""offline-msg"">[ ΣΥΣΤΗΜΑ ΑΝΕΝΕΡΓΟ ]</div>'; auxInput.style.display = 'none'; }
                        else if (st.url === 'aux') { screen.innerHTML = '<div class=""offline-msg"" style=""color:#00ffcc;"">[ ΑΝΑΜΟΝΗ ΣΗΜΑΤΟΣ AUX ]</div>'; auxInput.style.display = 'inline-block'; auxInput.value = ''; }
                        else { screen.innerHTML = `<iframe src='${st.url}' allow='autoplay; encrypted-media' allowfullscreen></iframe>`; auxInput.style.display = 'none'; }
                    }
                    auxInput.addEventListener('keypress', function (e) {
                        if (e.key === 'Enter') {
                            const embed = getEmbedUrl(this.value);
                            if (embed) screen.innerHTML = `<iframe src='${embed}' allow='autoplay; encrypted-media' allowfullscreen></iframe>`;
                            else screen.innerHTML = '<div class=""offline-msg"" style=\""color:#e74c3c;"">[ INVALID SIGNAL ]</div>';
                        }
                    });
                    slider.addEventListener('input', updateRadio); updateRadio();
                    let audioCtx; let rainGain, serverGain, dataGain;
                    function initAudio() {
                        if(audioCtx) return; audioCtx = new (window.AudioContext || window.webkitAudioContext)();
                        let bs = audioCtx.sampleRate * 2; let nBuf = audioCtx.createBuffer(1, bs, audioCtx.sampleRate); let out = nBuf.getChannelData(0);
                        for (let i = 0; i < bs; i++) out[i] = Math.random() * 2 - 1;
                        let rSrc = audioCtx.createBufferSource(); rSrc.buffer = nBuf; rSrc.loop = true;
                        let rFlt = audioCtx.createBiquadFilter(); rFlt.type = 'lowpass'; rFlt.frequency.value = 800;
                        rainGain = audioCtx.createGain(); rainGain.gain.value = 0; rSrc.connect(rFlt).connect(rainGain).connect(audioCtx.destination); rSrc.start();
                        let sSrc = audioCtx.createBufferSource(); sSrc.buffer = nBuf; sSrc.loop = true;
                        let sFlt = audioCtx.createBiquadFilter(); sFlt.type = 'lowpass'; sFlt.frequency.value = 120;
                        serverGain = audioCtx.createGain(); serverGain.gain.value = 0; sSrc.connect(sFlt).connect(serverGain).connect(audioCtx.destination); sSrc.start();
                        dataGain = audioCtx.createGain(); dataGain.gain.value = 0; dataGain.connect(audioCtx.destination);
                        function playDataBeep() {
                            if (dataGain.gain.value > 0) {
                                let osc = audioCtx.createOscillator(); osc.type = 'square'; osc.frequency.value = 400 + Math.random() * 1500;
                                let bGain = audioCtx.createGain(); bGain.gain.setValueAtTime(0.02, audioCtx.currentTime); bGain.gain.exponentialRampToValueAtTime(0.001, audioCtx.currentTime + 0.1);
                                osc.connect(bGain).connect(dataGain); osc.start(); osc.stop(audioCtx.currentTime + 0.1);
                            } setTimeout(playDataBeep, Math.random() * 400 + 50);
                        } playDataBeep();
                    }
                    document.getElementById('vol-rain').addEventListener('input', function() { initAudio(); if(audioCtx.state === 'suspended') audioCtx.resume(); rainGain.gain.value = this.value * 0.4; });
                    document.getElementById('vol-server').addEventListener('input', function() { initAudio(); if(audioCtx.state === 'suspended') audioCtx.resume(); serverGain.gain.value = this.value * 1.5; });
                    document.getElementById('vol-key').addEventListener('input', function() { initAudio(); if(audioCtx.state === 'suspended') audioCtx.resume(); dataGain.gain.value = this.value; });
                </script></body></html>";

                ShowFocusCore(pomodoroHtml);
                InitNotes();

                string radioFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "RadioFiles");
                Directory.CreateDirectory(radioFolder);
                string radioFile = Path.Combine(radioFolder, "radio.html");
                File.WriteAllText(radioFile, radioHtml);

                RadioBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping("cyber.radio", radioFolder, CoreWebView2HostResourceAccessKind.Allow);
                RadioBrowser.Source = new Uri("http://cyber.radio/radio.html");
            }
            catch (Exception ex) { LogError("LoadDeepDiveHub", ex); }
        }


        // ==========================================
        // TAB 6: CO-OP MODE (Split-Screen Multiplayer)
        // ==========================================
        private async Task LoadCoop()
        {
            try
            {
                // Αρχικοποίηση του WebView2 για το Co-op ΜΟΝΟ όταν ανοίξει το Tab 6
                var env = await GetEnvAsync();
                await CoopBrowser.EnsureCoreWebView2Async(env);
                // Φόρτωση οθόνης ανεβάσματος (Drag & Drop UI)
                string uploadHtml = @"
            <html><body style='background: #05070a; color: #bdc3c7; font-family: ""Share Tech Mono"", monospace; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0;'>
            <div style='text-align: center; background: #0b101a; padding: 50px; border-radius: 12px; border: 2px dashed #f1c40f; box-shadow: 0 0 20px rgba(241,196,15,0.2);'>
                <h1 style='color: #f1c40f; font-size: 2.5rem; text-shadow: 0 0 10px rgba(241,196,15,0.5);'>⚔️ Co-op Mode: Player 1 vs Player 2</h1>
                <p style='font-size: 1.1rem; margin-top: 10px; color: #ecf0f1;'>Ανέβασε το αρχείο <b>academic_database.csv</b> (ή .json) του συμφοιτητή σου.</p>
                <p style='font-size: 0.85rem; color: #7f8c8d; margin-bottom: 30px;'>Τα δεδομένα θα αναλυθούν για το Head-to-Head Arcade Matchup!</p>
                <label style='background: #f1c40f; color: #000; padding: 12px 25px; border-radius: 6px; font-weight: bold; font-size: 1.1rem; cursor: pointer; transition: all 0.3s; box-shadow: 0 0 15px rgba(241,196,15,0.4);'>
                    📂 ΕΠΙΛΟΓΗ ΑΡΧΕΙΟΥ P2
                    <input type='file' id='p2file' accept='.csv,.json' style='display: none;' />
                </label>
            </div>
            <script>
                document.getElementById('p2file').addEventListener('change', function(e) {
                    var file = e.target.files[0];
                    if (!file) return;
                    var reader = new FileReader();
                    reader.onload = function(e) { window.chrome.webview.postMessage(e.target.result); };
                    reader.readAsText(file);
                });
            </script>
            </body></html>";

                _coopUploadHtml = uploadHtml;
                CoopBrowser.WebMessageReceived -= CoopBrowser_WebMessageReceived;
                CoopBrowser.WebMessageReceived += CoopBrowser_WebMessageReceived;

                // Memory Card: αν υπάρχει αποθηκευμένος αντίπαλος, ανοίγει κατευθείαν το matchup
                if (!string.IsNullOrEmpty(_save.CoopPayload) && ShowCoopMatchup(_save.CoopPayload, _save.CoopLoadedAt, true))
                    return;

                CoopBrowser.NavigateToString(uploadHtml);
            }
            catch (Exception ex) { ShowErrorPage(CoopBrowser, "Co-op", ex); }
        }

        private void CoopBrowser_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string payload = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(payload)) return;
            if (IsHotkeyMessage(payload)) return;

            // Αποσύνδεση αντιπάλου (από το κουμπί της σελίδας αποτελεσμάτων)
            if (payload == "@@COOP_RESET@@")
            {
                _save.CoopPayload = "";
                _save.CoopLoadedAt = "";
                SaveState();
                CoopBrowser.NavigateToString(_coopUploadHtml);
                return;
            }

            // Νέο αρχείο αντιπάλου
            string stamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            if (ShowCoopMatchup(payload, stamp, false))
            {
                _save.CoopPayload = payload;
                _save.CoopLoadedAt = stamp;
                SaveState();
            }
        }

        private bool ShowCoopMatchup(string payload, string loadedAt, bool silent)
        {
            try
            {
                // Φόρτωση P1 (Εσύ)
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var p1Grades = GetPassedGrades(options);

                // Φόρτωση P2 (Αντίπαλος) από το Payload (JSON ή CSV)
                List<GradeRecord> p2Grades = new List<GradeRecord>();
                if (payload.TrimStart().StartsWith("["))
                {
                    p2Grades = JsonSerializer.Deserialize<List<GradeRecord>>(payload, options).Where(g => g.IsPassed && g.Grade.HasValue).ToList();
                }
                else
                {
                    var lines = payload.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Skip(1);
                    foreach (var line in lines)
                    {
                        try
                        {
                            int l1 = line.LastIndexOf(','); int l2 = line.LastIndexOf(',', l1 - 1);
                            int l3 = line.LastIndexOf(',', l2 - 1); int l4 = line.LastIndexOf(',', l3 - 1);
                            if (l4 > 0)
                            {
                                string title = line.Substring(1, l4 - 2).Replace("\"\"", "\"");
                                double grade = double.Parse(line.Substring(l4 + 1, l3 - l4 - 1), System.Globalization.CultureInfo.InvariantCulture) / 10.0;
                                double ects = double.Parse(line.Substring(l3 + 1, l2 - l3 - 1), System.Globalization.CultureInfo.InvariantCulture);
                                string month = line.Substring(l2 + 1, l1 - l2 - 1);
                                string year = line.Substring(l1 + 1);

                                p2Grades.Add(new GradeRecord { CourseId = new CourseInfo { Title = title }, Grade = grade, Ects = ects, ExamPeriodId = new ExamPeriodInfo { Abbr = month, Title = year + "-X" }, IsPassed = true });
                            }
                        }
                        catch { }
                    }
                }

                // Υπολογισμός Προφίλ
                var p1 = CalculatePlayerProfile(p1Grades);
                var p2 = CalculatePlayerProfile(p2Grades);

                // Αποτροπή ίδιου χρώματος
                if (p1.ArchColor == p2.ArchColor) p2.ArchColor = "#ff007f";

                // AI Matchup Verdict
                int score1 = 0, score2 = 0;
                if (p1.Gpa > p2.Gpa) score1++; else if (p2.Gpa > p1.Gpa) score2++;
                if (p1.Ects > p2.Ects) score1++; else if (p2.Ects > p1.Ects) score2++;
                if (p1.Streak > p2.Streak) score1++; else if (p2.Streak > p1.Streak) score2++;
                if (p1.Badges > p2.Badges) score1++; else if (p2.Badges > p1.Badges) score2++;

                string vTitle, vColor, vMsg;
                if (score1 > score2)
                {
                    vTitle = "🏆 PLAYER 1 WINS (FLAWLESS VICTORY)"; vColor = "#00ffcc";
                    vMsg = "Ο Player 1 κυριαρχεί στο ακαδημαϊκό πεδίο μάχης. Ο Player 2 πρέπει να επιστρέψει στο Base Camp για grinding.";
                }
                else if (score2 > score1)
                {
                    vTitle = "🏆 PLAYER 2 WINS (DOMINATION)"; vColor = "#ff007f";
                    vMsg = "Ο Player 2 έκανε speedrun το πτυχίο! Ο Player 1 έμεινε πίσω στο level scaling και χρειάζεται άμεσα energy drinks.";
                }
                else
                {
                    vTitle = "⚔️ ABSOLUTE TIE (SUDDEN DEATH REQUIRED)"; vColor = "#f1c40f";
                    vMsg = "Απόλυτη ισοπαλία! Και οι δύο παίκτες έχουν ακριβώς το ίδιο power level. Ένα επικό rematch στην επόμενη εξεταστική είναι μονόδρομος.";
                }

                string p1Prog = Math.Min((p1.Ects / 300.0) * 100, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string p2Prog = Math.Min((p2.Ects / 300.0) * 100, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Παραγωγή HTML
                string html = $@"
                <!DOCTYPE html>
                <html><head>
                <script src='https://cdn.plot.ly/plotly-2.27.0.min.js'></script>
                <link href='https://fonts.googleapis.com/css2?family=Share+Tech+Mono&family=Libre+Barcode+39+Text&display=swap' rel='stylesheet'>
                <style>
                body {{ margin: 0; padding: 30px; background: #05070a; font-family: 'Share Tech Mono', monospace; color: #bdc3c7; overflow-x: hidden; }}
                .id-box {{ background: linear-gradient(135deg, #0a0e17 0%, #111b24 100%); border-radius: 12px; padding: 20px; display: flex; align-items: center; box-shadow: 0 10px 20px rgba(0,0,0,0.5); }}
                .avatar {{ width: 80px; height: 80px; display: flex; justify-content: center; align-items: center; font-size: 3rem; background: rgba(0,0,0,0.5); border-radius: 8px; margin-right: 15px; position: relative; }}
                .tag {{ position: absolute; bottom: -8px; color: #000; font-size: 0.6rem; padding: 2px 5px; border-radius: 3px; font-weight: bold; text-transform: uppercase; }}
                .stat-box {{ background: rgba(255,255,255,0.05); padding: 5px 8px; border-radius: 4px; flex: 1; }}
                .tug-row {{ margin-bottom: 25px; }}
                .tug-bar {{ position: relative; width: 100%; height: 35px; background: #05070a; border-radius: 6px; display: flex; overflow: hidden; border: 1px solid #34495e; margin-top: 8px; box-shadow: 0 0 10px rgba(0,0,0,0.5); }}
                .spec-box {{ background: #0a0e17; border-radius: 8px; padding: 15px; flex: 1; }}
                .co-btn {{ background: #111b24; color: #f1c40f; border: 1px solid #f1c40f; padding: 8px 14px; border-radius: 6px; cursor: pointer; font-family: inherit; font-size: 0.85rem; transition: all .2s; }}
                .co-btn:hover {{ background: rgba(241,196,15,0.1); box-shadow: 0 0 12px rgba(241,196,15,0.4); }}
                .co-danger {{ color: #e74c3c; border-color: #e74c3c; }}
                .co-danger:hover {{ background: rgba(231,76,60,0.1); box-shadow: 0 0 12px rgba(231,76,60,0.4); }}                
                </style></head> 
                <body>
                    <!-- COOP TOOLBAR -->
                    <div style='display: flex; justify-content: space-between; align-items: center; gap: 15px; margin-bottom: 25px; padding: 12px 18px; background: #0a0e17; border: 1px solid #1f2d3d; border-radius: 10px;'>
                        <div style='font-size: 0.85rem; color: #7f8c8d;'><span style='color: #2ecc71;'>●</span> Αποθηκεύτηκε αυτόματα · Αντίπαλος φορτώθηκε: <b style='color: #bdc3c7;'>{(string.IsNullOrEmpty(loadedAt) ? "-" : loadedAt)}</b></div>
                        <div style='display: flex; gap: 10px;'>
                            <label class='co-btn'>🔄 Αλλαγή αντιπάλου<input type='file' id='p2change' accept='.csv,.json' style='display: none;'></label>
                            <button class='co-btn co-danger' onclick='resetRival()'>🗑️ Αποσύνδεση</button>
                        </div>
                    </div>
                    <!-- ID CARDS -->
                    <div style='display: grid; grid-template-columns: 5fr 1fr 5fr; gap: 15px; align-items: center;'>
                        <!-- P1 -->
                        <div>
                            <h3 style='text-align: center; color: {p1.ArchColor}; text-shadow: 0 0 10px {p1.ArchColor}80; margin-bottom: 10px;'>P1 (ΕΣΥ)</h3>
                            <div class='id-box' style='border: 2px solid {p1.ArchColor};'>
                                <div class='avatar' style='border: 2px solid {p1.ArchColor}; text-shadow: 0 0 15px {p1.ArchColor}; box-shadow: inset 0 0 15px {p1.ArchColor}40;'>{p1.Avatar}<div class='tag' style='background: {p1.ArchColor};'>VERIFIED</div></div>
                                <div style='flex-grow: 1;'>
                                    <h2 style='color: #ecf0f1; margin: 0 0 8px 0; font-size: 1.5rem;'>{p1.Rank}</h2>
                                    <div style='display: flex; gap: 8px;'>
                                        <div class='stat-box' style='border-left: 3px solid {p1.ArchColor};'><div style='font-size:0.55rem; color:#7f8c8d;'>CLASS</div><div style='color:{p1.ArchColor}; font-weight:bold;'>{p1.ArchTitle}</div></div>
                                        <div class='stat-box' style='border-left: 3px solid #f1c40f;'><div style='font-size:0.55rem; color:#7f8c8d;'>LEVEL</div><div style='color:#f1c40f; font-weight:bold;'>{p1.LevelLabel}</div></div>
                                        <div class='stat-box' style='border-left: 3px solid #3498db;'><div style='font-size:0.55rem; color:#7f8c8d;'>GPA</div><div style='color:#3498db; font-weight:bold;'>{p1.Gpa:F2}</div></div>
                                    </div>
                                </div>
                            </div>
                        </div>
                        <div style='text-align: center; font-size: 2.5rem; color: #f1c40f; font-weight: bold; text-shadow: 0 0 15px #f1c40f; margin-top: 35px;'>VS</div>
                        <!-- P2 -->
                        <div>
                            <h3 style='text-align: center; color: {p2.ArchColor}; text-shadow: 0 0 10px {p2.ArchColor}80; margin-bottom: 10px;'>P2 (ΑΝΤΙΠΑΛΟΣ)</h3>
                            <div class='id-box' style='border: 2px solid {p2.ArchColor};'>
                                <div class='avatar' style='border: 2px solid {p2.ArchColor}; text-shadow: 0 0 15px {p2.ArchColor}; box-shadow: inset 0 0 15px {p2.ArchColor}40;'>{p2.Avatar}<div class='tag' style='background: {p2.ArchColor};'>VERIFIED</div></div>
                                <div style='flex-grow: 1;'>
                                    <h2 style='color: #ecf0f1; margin: 0 0 8px 0; font-size: 1.5rem;'>{p2.Rank}</h2>
                                    <div style='display: flex; gap: 8px;'>
                                        <div class='stat-box' style='border-left: 3px solid {p2.ArchColor};'><div style='font-size:0.55rem; color:#7f8c8d;'>CLASS</div><div style='color:{p2.ArchColor}; font-weight:bold;'>{p2.ArchTitle}</div></div>
                                        <div class='stat-box' style='border-left: 3px solid #f1c40f;'><div style='font-size:0.55rem; color:#7f8c8d;'>LEVEL</div><div style='color:#f1c40f; font-weight:bold;'>{p2.LevelLabel}</div></div>
                                        <div class='stat-box' style='border-left: 3px solid #3498db;'><div style='font-size:0.55rem; color:#7f8c8d;'>GPA</div><div style='color:#3498db; font-weight:bold;'>{p2.Gpa:F2}</div></div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>

                    <!-- ARCADE MATCHUP -->
                    <h3 style='text-align: center; color: #bdc3c7; letter-spacing: 3px; margin-top: 40px; margin-bottom: 30px;'>🔥 ARCADE MATCHUP 🔥</h3>
                    <div style='max-width: 900px; margin: 0 auto; position: relative;'>
                        <div style='position: absolute; left: 50%; top: 0; bottom: 0; width: 2px; background: rgba(241, 196, 15, 0.3); box-shadow: 0 0 15px rgba(241, 196, 15, 0.5); transform: translateX(-50%); z-index: 0;'></div>
                        {BuildArcadeRow("Overall GPA", p1.Gpa.ToString("F2"), p2.Gpa.ToString("F2"), p1.Gpa, p2.Gpa, p1.ArchColor, p2.ArchColor, true)}
                        {BuildArcadeRow("Total ECTS", p1.Ects.ToString("g"), p2.Ects.ToString("g"), p1.Ects, p2.Ects, p1.ArchColor, p2.ArchColor, false)}
                        {BuildArcadeRow("Max Exam Streak", p1.Streak + " 🔥", "🔥 " + p2.Streak, p1.Streak, p2.Streak, p1.ArchColor, p2.ArchColor, false)}
                        {BuildArcadeRow("Achievements", p1.Badges + " 🏅", "🏅 " + p2.Badges, p1.Badges, p2.Badges, p1.ArchColor, p2.ArchColor, false)}
                    </div>

                    <!-- SPECIAL INFO -->
                    <div style='display: flex; gap: 20px; margin-top: 30px;'>
                        <div class='spec-box' style='border-top: 3px solid {p1.ArchColor};'>
                            <h4 style='color: {p1.ArchColor}; margin: 0 0 10px 0;'>💼 Ειδικά Μαθήματα (P1)</h4>
                            <div style='color: #ecf0f1; font-size: 0.9rem; margin-bottom: 5px;'><b>Διπλωματική:</b> {(p1.HasThesis ? $"✅ Ολοκληρώθηκε ({p1.ThesisGrade})" : "❌ Εκκρεμεί")}</div>
                            <div style='color: #ecf0f1; font-size: 0.9rem; margin-bottom: 10px;'><b>Πρακτική:</b> {(p1.HasInternship ? "✅ Ολοκληρώθηκε" : "❌ Εκκρεμεί")}</div>
                            {(p1.GoldenPeriod != null ? $"<div style='background: rgba(241,196,15,0.1); border-left: 3px solid #f1c40f; padding: 8px; color: #f1c40f; font-size: 0.85rem;'>🌟 <b>Χρυσή Εξεταστική:</b><br>{p1.GoldenPeriod}</div>" : "")}
                        </div>
                        <div class='spec-box' style='border-top: 3px solid {p2.ArchColor};'>
                            <h4 style='color: {p2.ArchColor}; margin: 0 0 10px 0;'>💼 Ειδικά Μαθήματα (P2)</h4>
                            <div style='color: #ecf0f1; font-size: 0.9rem; margin-bottom: 5px;'><b>Διπλωματική:</b> {(p2.HasThesis ? $"✅ Ολοκληρώθηκε ({p2.ThesisGrade})" : "❌ Εκκρεμεί")}</div>
                            <div style='color: #ecf0f1; font-size: 0.9rem; margin-bottom: 10px;'><b>Πρακτική:</b> {(p2.HasInternship ? "✅ Ολοκληρώθηκε" : "❌ Εκκρεμεί")}</div>
                            {(p2.GoldenPeriod != null ? $"<div style='background: rgba(241,196,15,0.1); border-left: 3px solid #f1c40f; padding: 8px; color: #f1c40f; font-size: 0.85rem;'>🌟 <b>Χρυσή Εξεταστική:</b><br>{p2.GoldenPeriod}</div>" : "")}
                        </div>
                    </div>

                    <!-- AI VERDICT -->
                    <div style='background: #0a0e17; border: 2px solid {vColor}; border-radius: 10px; padding: 25px; text-align: center; box-shadow: 0 0 25px {vColor}44; margin-top: 40px; position: relative; overflow: hidden;'>
                        <div style='position: absolute; top: -10px; right: -10px; font-size: 5rem; opacity: 0.1;'>🥊</div>
                        <h2 style='color: {vColor}; margin-top: 0; letter-spacing: 2px;'>{vTitle}</h2>
                        <div style='color: #ecf0f1; font-size: 1.1rem; line-height: 1.5;'>{vMsg}</div>
                    </div>

                    <!-- RADAR -->
                    <h3 style='margin-top: 40px; border-bottom: 1px solid #2c3e50; padding-bottom: 10px;'>📊 Head-to-Head Skill Matchup</h3>
                    <div id='radar-coop' style='height: 500px;'></div>
                    <script>
                        Plotly.newPlot('radar-coop', [
                            {{ type: 'scatterpolar', r: [{(p1.Gpa * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {p1.Courses}, {(p1.Streak * 10)}, {(p1.Badges * 10)}, {p1Prog}], theta: ['GPA (x10)', 'Σύνολο Μαθημάτων', 'Max Streak (x10)', 'Achievements (x10)', 'Πρόοδος (%)'], fill: 'toself', name: 'P1 (Εσύ)', line: {{ color: '{p1.ArchColor}' }} }},
                            {{ type: 'scatterpolar', r: [{(p2.Gpa * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {p2.Courses}, {(p2.Streak * 10)}, {(p2.Badges * 10)}, {p2Prog}], theta: ['GPA (x10)', 'Σύνολο Μαθημάτων', 'Max Streak (x10)', 'Achievements (x10)', 'Πρόοδος (%)'], fill: 'toself', name: 'P2', line: {{ color: '{p2.ArchColor}' }} }}
                        ], {{
                            polar: {{ radialaxis: {{ visible: true, showticklabels: false, gridcolor: 'rgba(255,255,255,0.1)' }}, angularaxis: {{ tickfont: {{ color: '#bdc3c7', size: 14 }}, gridcolor: 'rgba(255,255,255,0.1)' }}, bgcolor: 'rgba(0,0,0,0)' }},
                            paper_bgcolor: 'rgba(0,0,0,0)', plot_bgcolor: 'rgba(0,0,0,0)', font: {{ color: '#bdc3c7', family: 'monospace' }}, showlegend: true, margin: {{ t: 50, b: 50 }}
                        }}, {{ displayModeBar: false, responsive: true }});
                    </script>
                    <script>
                        document.getElementById('p2change').addEventListener('change', function (ev) {{
                            var file = ev.target.files[0];
                            if (!file) return;
                            var reader = new FileReader();
                            reader.onload = function (r) {{ window.chrome.webview.postMessage(r.target.result); }};
                            reader.readAsText(file);
                        }});
                        function resetRival() {{
                            if (confirm('Να αποσυνδεθεί ο αντίπαλος;')) window.chrome.webview.postMessage('@@COOP_RESET@@');
                        }}
                    </script>
                </body></html>";

                CoopBrowser.NavigateToString(ApplyFx(html));
                return true;
            }
            catch (Exception ex)
            {
                LogError("ShowCoopMatchup", ex);
                if (!silent) MessageBox.Show("Σφάλμα ανάλυσης αρχείου: " + ex.Message);
                return false;
            }
        }

        private string BuildArcadeRow(string label, string str1, string str2, double val1, double val2, string c1, string c2, bool isFloat)
        {
            double total = val1 + val2;
            double w1 = total <= 0 ? 50 : (val1 / total) * 100;
            double w2 = total <= 0 ? 50 : (val2 / total) * 100;
            string crown1 = val1 > val2 ? $"<span style='background: rgba(0,0,0,0.6); padding: 2px 8px; border-radius: 12px; font-size: 0.8rem; margin-left: 10px; box-shadow: 0 0 5px #000;'>+{(val1 - val2).ToString(isFloat ? "F2" : "F0")} 👑</span>" : "";
            string crown2 = val2 > val1 ? $"<span style='background: rgba(0,0,0,0.6); padding: 2px 8px; border-radius: 12px; font-size: 0.8rem; margin-right: 10px; box-shadow: 0 0 5px #000;'>👑 +{(val2 - val1).ToString(isFloat ? "F2" : "F0")}</span>" : "";

            return $@"
            <div class='tug-row'>
                <div style='text-align: center; position: relative; z-index: 3;'><span style='background: #0e1117; padding: 4px 15px; color: #ecf0f1; border: 1px solid #2c3e50; border-radius: 4px; font-size: 0.9rem;'>{label}</span></div>
                <div class='tug-bar'>
                    <div style='width: {w1.ToString(System.Globalization.CultureInfo.InvariantCulture)}%; background: {c1}; border-right: 2px solid #fff; opacity: 0.85;'></div>
                    <div style='width: {w2.ToString(System.Globalization.CultureInfo.InvariantCulture)}%; background: {c2}; opacity: 0.85;'></div>
                    <div style='position: absolute; left: 15px; top: 50%; transform: translateY(-50%); color: #fff; font-size: 1.2rem; font-weight: bold; text-shadow: 1px 1px 3px #000; z-index: 3;'>{str1}{crown1}</div>
                    <div style='position: absolute; right: 15px; top: 50%; transform: translateY(-50%); color: #fff; font-size: 1.2rem; font-weight: bold; text-shadow: 1px 1px 3px #000; z-index: 3;'>{crown2}{str2}</div>
                    <div style='position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); width: 34px; height: 34px; background: #111b24; border: 2px solid #f1c40f; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: bold; color: #f1c40f; box-shadow: 0 0 10px rgba(241,196,15,0.8); z-index: 4;'>VS</div>
                </div>
            </div>";
        }

        private PlayerProfile CalculatePlayerProfile(List<GradeRecord> grades)
        {
            var p = new PlayerProfile();
            var regular = grades.Where(c => !c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase) && !c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase)).ToList();

            p.Ects = grades.Sum(g => g.Ects);
            p.Gpa = p.Ects > 0 ? grades.Sum(g => g.FinalGrade * g.Ects) / p.Ects : 0;
            p.Courses = regular.Count;
            p.IsMax = p.Ects >= 300;
            p.Level = p.IsMax ? 10 : (int)(p.Ects / 30) + 1;
            p.LevelLabel = p.IsMax ? "MAX" : $"LVL {p.Level}";
            p.XpInLevel = p.IsMax ? 30 : p.Ects % 30;
            p.XpPercent = p.XpInLevel / 30.0 * 100.0;
            p.Avatar = p.IsMax ? "🧙‍♂️" : p.Level <= 2 ? "🥚" : p.Level <= 4 ? "🤓" : p.Level <= 6 ? "🥷" : p.Level <= 8 ? "🦾" : "🦸‍♂️";
            string[] ranks = { "Hello World Novice", "Loop Scripter", "Bug Hunter", "Object-Oriented Knight", "Tree Traverser", "Database Ranger", "Machine Learning Apprentice", "The 8-Bit Legend", "3D Rendering Mage", "System Architect", "Master of the Code" };
            p.Rank = p.Ects >= 300 ? "MASTER OF THE CODE" : ranks[Math.Min(p.Level - 1, 9)].ToUpper();

            var thesis = grades.FirstOrDefault(c => c.CourseId.Title.Contains("ΔΙΠΛΩΜΑΤΙΚ", StringComparison.OrdinalIgnoreCase));
            p.HasThesis = thesis != null; p.ThesisGrade = p.HasThesis ? thesis.FinalGrade.ToString("F1") : "";
            p.HasInternship = grades.Any(c => c.CourseId.Title.Contains("ΠΡΑΚΤΙΚ", StringComparison.OrdinalIgnoreCase));

            // Badges
            p.Badges = regular.Any(g => g.FinalGrade == 10) ? 1 : 0;
            var ePeriod = regular.GroupBy(g => new { g.AcademicYear, g.ExamMonth }).Select(g => g.Sum(c => c.Ects));
            if (ePeriod.Any() && ePeriod.Max() >= 30) p.Badges++;
            var sCourses = regular.Where(g => g.ExamMonth == "Σεπ");
            if (sCourses.Any() && sCourses.GroupBy(g => g.AcademicYear).Select(g => g.Count()).Max() >= 3) p.Badges++;
            if (p.HasThesis) p.Badges++; if (p.HasInternship) p.Badges++;

            // Streak & Golden
            var pCounts = regular.GroupBy(g => new { g.AcademicYear, g.ExamMonth }).ToDictionary(g => g.Key, g => g.Count());
            if (pCounts.Any())
            {
                int minYr = pCounts.Keys.Min(k => int.TryParse(k.AcademicYear, out int y) ? y : DateTime.Now.Year);
                int maxYr = pCounts.Keys.Max(k => int.TryParse(k.AcademicYear, out int y) ? y : DateTime.Now.Year);
                int curStr = 0; string[] order = { "Φεβ", "Ιουν", "Σεπ" };
                for (int y = minYr; y <= maxYr; y++)
                    foreach (var m in order)
                        if (pCounts.TryGetValue(new { AcademicYear = y.ToString(), ExamMonth = m }, out int count) && count >= 2) { curStr++; p.Streak = Math.Max(p.Streak, curStr); } else curStr = 0;
                if (p.Streak >= 3) p.Badges++;
            }
            var sStats = regular.GroupBy(g => new { g.AcademicYear, g.ExamMonth }).Select(g => new { Period = PeriodLabel(g.Key.ExamMonth, g.Key.AcademicYear), Count = g.Count(), GPA = g.Sum(c => c.Ects) > 0 ? g.Sum(c => c.FinalGrade * c.Ects) / g.Sum(c => c.Ects) : 0 }).OrderByDescending(s => s.Count).ThenByDescending(s => s.GPA).FirstOrDefault();
            if (sStats != null) p.GoldenPeriod = $"{sStats.Period} ({sStats.Count} μαθ. | Μ.Ο. {sStats.GPA:F2})";

            // Archetype
            var bScores = new Dictionary<string, double> { { "Software & Systems", 0 }, { "Hardware & Architecture", 0 }, { "Math & Theory", 0 }, { "Data & AI", 0 }, { "Networks & Comms", 0 }, { "Graphics & Vision", 0 }, { "Cybersecurity", 0 } };
            foreach (var g in grades)
            {
                string b = GetBranch(g.CourseId.Title);
                if (b != "General / Core") bScores[b] += 0.004 * (g.Ects * Math.Pow(g.FinalGrade, 4.5));
            }
            p.DomCat = "None"; p.ArchTitle = "Tech-Mercenary"; p.ArchColor = "#ffffff";
            var mB = bScores.OrderByDescending(x => x.Value).First();
            if (mB.Value > 0)
            {
                p.DomCat = mB.Key;
                switch (p.DomCat)
                {
                    case "Software & Systems": p.ArchTitle = "Cyber-Mage"; p.ArchColor = "#3498db"; break;
                    case "Hardware & Architecture": p.ArchTitle = "Mecha-Paladin"; p.ArchColor = "#e74c3c"; break;
                    case "Networks & Comms": p.ArchTitle = "Network Ninja"; p.ArchColor = "#9b59b6"; break;
                    case "Math & Theory": p.ArchTitle = "Logic Oracle"; p.ArchColor = "#f1c40f"; break;
                    case "Data & AI": p.ArchTitle = "AI-Netrunner"; p.ArchColor = "#00e5b0"; break;
                    case "Graphics & Vision": p.ArchTitle = "Holo-Artisan"; p.ArchColor = "#ff007f"; break;
                    case "Cybersecurity": p.ArchTitle = "Stealth Decker"; p.ArchColor = "#00ff00"; break;
                }
            }
            return p;
        }

        public class PlayerProfile
        {
            public double Ects { get; set; }
            public double Gpa { get; set; }
            public int Courses { get; set; }
            public int Level { get; set; }
            public string Avatar { get; set; }
            public string Rank { get; set; }
            public int Badges { get; set; }
            public int Streak { get; set; }
            public string DomCat { get; set; }
            public string ArchTitle { get; set; }
            public string ArchColor { get; set; }
            public bool HasThesis { get; set; }
            public string ThesisGrade { get; set; }
            public bool HasInternship { get; set; }
            public string GoldenPeriod { get; set; }
            public bool IsMax { get; set; }
            public string LevelLabel { get; set; }
            public double XpInLevel { get; set; }
            public double XpPercent { get; set; }
        }

        public class MissingCourse
        {
            public string Code { get; set; }
            public string Title { get; set; }
            public double Ects { get; set; }
            public string Category { get; set; }
            public string Semester { get; set; }
            public string Period { get; set; }
            public int SemesterOrder { get; set; } = 999;
        }

        public class CourseInfo { public string Title { get; set; } }
        public class ExamPeriodInfo { public string Title { get; set; } public string Abbr { get; set; } }
        public class CategoryInfo { public string Title { get; set; } }

        public class GradeRecord
        {
            public double? Grade { get; set; }
            public double Ects { get; set; }
            public bool IsPassed { get; set; }
            public CourseInfo CourseId { get; set; }
            public ExamPeriodInfo ExamPeriodId { get; set; }
            public CategoryInfo CategId { get; set; }
            public double FinalGrade => (Grade ?? 0) * 10;
            public string AcademicYear
            {
                get { if (ExamPeriodId != null && !string.IsNullOrEmpty(ExamPeriodId.Title) && ExamPeriodId.Title.Length >= 4) return ExamPeriodId.Title.Substring(0, 4); return "Άγνωστο"; }
            }
            public string ExamMonth
            {
                get
                {
                    if (ExamPeriodId != null && !string.IsNullOrEmpty(ExamPeriodId.Abbr))
                    {
                        if (ExamPeriodId.Abbr.Contains("ΦΕΒ")) return "Φεβ";
                        if (ExamPeriodId.Abbr.Contains("ΙΟΥΝ") || ExamPeriodId.Abbr.Contains("IOYN")) return "Ιουν";
                        if (ExamPeriodId.Abbr.Contains("ΣΕΠ")) return "Σεπ";
                    }
                    return "Άγνωστο";
                }
            }
            public int ExamOrder => ExamMonth == "Φεβ" ? 1 : ExamMonth == "Ιουν" ? 2 : ExamMonth == "Σεπ" ? 3 : 4;
        }
    }
}