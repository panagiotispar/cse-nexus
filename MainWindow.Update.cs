using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // UPDATE: έλεγχοι εκκίνησης (μία εκτέλεση, WebView2) και ειδοποίηση για νέα έκδοση από τα GitHub Releases.
    // Χρησιμοποιεί το HttpClient _http, που ορίζεται στον κώδικα του crawler προγράμματος (στο κύριο MainWindow.xaml.cs).
    // ==========================================================================
    public partial class MainWindow
    {
        // ΡΥΘΜΙΣΗ: το αποθετήριό σου στο GitHub, στη μορφή "χρήστης/όνομα-repo". Π.χ. "panagiotispar/cse-nexus"
        private const string GitHubRepo = "panagiotispar/cse-nexus";

        private const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        private class UpdateInfo
        {
            public Version Version;
            public string Tag = "";
            public string Url = "";
            public string Notes = "";
        }

        private UpdateInfo _update;
        private bool _updateChecking = false;
        private Border _updateIcon;
        private TextBlock _updateStatus;
        private Border _updateDownloadBtn;
        private static Mutex _singleInstance;

        // ======================================================================
        // 1) ΈΛΕΓΧΟΙ ΕΚΚΙΝΗΣΗΣ (πρώτη γραμμή του constructor)
        // ======================================================================
        private void EnsureStartupRequirements()
        {
            // Μία μόνο εκτέλεση τη φορά (αλλιώς δύο παράθυρα θα έγραφαν στα ίδια αρχεία)
            bool created;
            _singleInstance = new Mutex(true, "Local\\UoiStudyHub-SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show("Το CSE Nexus τρέχει ήδη.", "CSE Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
                Environment.Exit(0);
            }

            // Χρειάζεται το Microsoft Edge WebView2 Runtime (υπάρχει στα Windows 11 και στα περισσότερα Windows 10)
            try
            {
                CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                var answer = MessageBox.Show(
                    "Για να τρέξει η εφαρμογή χρειάζεται το «Microsoft Edge WebView2 Runtime» (δωρεάν, από τη Microsoft).\n\nΝα ανοίξω τη σελίδα λήψης;",
                    "CSE Nexus", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes) UpdOpenUrl(WebView2DownloadUrl);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogError("WebView2Check", ex); }
        }

        // ======================================================================
        // 2) ΕΚΔΟΣΕΙΣ
        // ======================================================================
        // "v1.2.3", "1.2.3", "1.2.3-beta.1", "1.2.3+abc123" -> 1.2.3. Ό,τι δεν είναι έκδοση -> null
        private static Version ParseAppVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            Version v;
            if (!Version.TryParse(s, out v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        private static Version CurrentAppVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                Version v = ParseAppVersion(info != null ? info.InformationalVersion : null);
                if (v != null) return v;
                var n = asm.GetName().Version;
                return n != null ? new Version(n.Major, n.Minor, Math.Max(0, n.Build)) : new Version(0, 0, 0);
            }
            catch { return new Version(0, 0, 0); }
        }

        private static string VersionText(Version v) { return "v" + v.Major + "." + v.Minor + "." + v.Build; }

        private void UpdOpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { LogError("OpenUrl", ex); }
        }

        // ======================================================================
        // 3) ΕΛΕΓΧΟΣ ΓΙΑ ΝΕΑ ΕΚΔΟΣΗ
        // ======================================================================
        private async Task CheckForUpdateAsync(bool manual)
        {
            if (_updateChecking) return;

            if (GitHubRepo == "OWNER/REPO")
            {
                if (manual) MessageBox.Show(this, "Δεν έχει οριστεί ακόμα το αποθετήριο GitHub στον κώδικα (σταθερά GitHubRepo).", "Ενημερώσεις", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _updateChecking = true;
            if (_updateStatus != null) _updateStatus.Text = "🔄 Έλεγχος...";

            try
            {
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.github.com/repos/" + GitHubRepo + "/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                var resp = await _http.SendAsync(req);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    _update = null;
                    if (_updateStatus != null) _updateStatus.Text = "Δεν υπάρχει ακόμα δημοσιευμένη έκδοση.";
                    return;
                }
                resp.EnsureSuccessStatusCode();

                string json = await resp.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string tag = root.GetProperty("tag_name").GetString() ?? "";
                    string url = root.GetProperty("html_url").GetString() ?? "";
                    string body = root.TryGetProperty("body", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";

                    Version latest = ParseAppVersion(tag);
                    Version current = CurrentAppVersion();

                    if (latest != null && latest > current && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                    {
                        _update = new UpdateInfo { Version = latest, Tag = tag, Url = url, Notes = body ?? "" };
                    }
                    else
                    {
                        _update = null;
                    }
                }

                ShowUpdateState();
                if (_update != null && manual) ShowUpdateDialog();
            }
            catch (Exception ex)
            {
                LogError("CheckForUpdate", ex);
                if (_updateStatus != null) _updateStatus.Text = manual ? "⚠️ Δεν ήταν δυνατός ο έλεγχος (internet;)." : "Έκδοση " + VersionText(CurrentAppVersion());
            }
            finally { _updateChecking = false; }
        }

        private void ShowUpdateState()
        {
            if (_updateStatus == null) return;
            if (_update != null)
            {
                _updateStatus.Text = "🆕 Νέα έκδοση: " + VersionText(_update.Version);
                _updateStatus.Foreground = Br("#f1c40f");
                if (_updateIcon != null) _updateIcon.Visibility = Visibility.Visible;
                if (_updateDownloadBtn != null) _updateDownloadBtn.Visibility = Visibility.Visible;
            }
            else
            {
                _updateStatus.Text = "✅ Έχεις την τελευταία έκδοση";
                _updateStatus.Foreground = Br("#2ecc71");
                if (_updateIcon != null) _updateIcon.Visibility = Visibility.Collapsed;
                if (_updateDownloadBtn != null) _updateDownloadBtn.Visibility = Visibility.Collapsed;
            }
        }

        // ======================================================================
        // 4) ΠΑΡΑΘΥΡΟ "ΝΕΑ ΕΚΔΟΣΗ"
        // ======================================================================
        private void ShowUpdateDialog()
        {
            if (_update == null) return;

            var win = new Window
            {
                Title = "Νέα έκδοση",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = Br("#0a0e17"),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            var mono = new FontFamily("Consolas");
            var root = new StackPanel { Margin = new Thickness(24) };

            Func<string, double, bool, string, TextBlock> label = (t, size, bold, color) => new TextBlock
            {
                Text = t,
                Foreground = Br(color),
                FontFamily = mono,
                FontSize = size,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            };

            root.Children.Add(label("🆕 Νέα έκδοση διαθέσιμη", 18, true, "#ecf0f1"));
            root.Children.Add(label("Έχεις την " + VersionText(CurrentAppVersion()) + " και η τελευταία είναι η " + VersionText(_update.Version) + ".", 12, false, "#7f8c8d"));

            string notes = string.IsNullOrWhiteSpace(_update.Notes) ? "(Δεν υπάρχουν σημειώσεις έκδοσης.)" : _update.Notes.Trim();
            if (notes.Length > 3000) notes = notes.Substring(0, 3000) + "…";
            var notesBox = label(notes, 12, false, "#bdc3c7");
            var scroll = new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 16, 0, 0),
                Content = new Border
                {
                    Background = Br("#0d1520"),
                    BorderBrush = Br("#1f2d3d"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Child = notesBox
                }
            };
            root.Children.Add(label("ΤΙ ΝΕΟ ΥΠΑΡΧΕΙ", 11, true, "#7f8c8d"));
            ((TextBlock)root.Children[root.Children.Count - 1]).Margin = new Thickness(0, 16, 0, 0);
            scroll.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(scroll);

            root.Children.Add(label("Η λήψη ανοίγει τη σελίδα της έκδοσης στο GitHub. Κατέβασε το zip, αποσυμπίεσέ το και αντικατάστησε τον παλιό φάκελο. Τα δεδομένα σου (βαθμοί, σημειώσεις, πρόοδος) δεν χάνονται.", 11, false, "#7f8c8d"));
            ((TextBlock)root.Children[root.Children.Count - 1]).Margin = new Thickness(0, 14, 0, 0);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };

            var later = new Border
            {
                Background = Br("#111b24"),
                BorderBrush = Br("#2c3e50"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Child = label("Αργότερα", 13, false, "#bdc3c7")
            };
            later.MouseLeftButtonUp += (s1, e1) => { win.DialogResult = false; };

            var download = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 8, 18, 8),
                Cursor = Cursors.Hand,
                Child = label("⬇ Λήψη", 13, true, "#05070a")
            };
            download.SetResourceReference(Border.BackgroundProperty, "ThemeBrush");
            download.SetResourceReference(Border.BorderBrushProperty, "ThemeBrush");
            download.MouseLeftButtonUp += (s2, e2) => { UpdOpenUrl(_update.Url); win.DialogResult = true; };

            buttons.Children.Add(later);
            buttons.Children.Add(download);
            root.Children.Add(buttons);

            win.Content = root;
            win.ShowDialog();
        }

        // ======================================================================
        // 5) ΚΑΡΤΑ "ΕΦΑΡΜΟΓΗ" ΣΤΟ CONTROL CENTER (χτίζεται από κώδικα) + ΕΙΚΟΝΙΔΙΟ ΣΤΗ ΣΤΕΝΗ ΜΠΑΡΑ
        // ======================================================================
        // Καλείται από τον constructor, μετά το InitWindowControls()
        private void InitUpdates()
        {
            // --- Στενή μπάρα: εικονίδιο "🆕" (κρυφό μέχρι να βρεθεί νέα έκδοση) ---
            _updateIcon = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(10),
                Background = Br("#1f1a0d"),
                BorderBrush = Br("#f1c40f"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 10, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Νέα έκδοση διαθέσιμη. Κάνε κλικ για λεπτομέρειες.",
                Visibility = Visibility.Collapsed,
                Child = new TextBlock { Text = "🆕", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            _updateIcon.MouseLeftButtonUp += (s, e) => ShowUpdateDialog();
            RailCollapsedView.Children.Add(_updateIcon);

            // --- Ανοιχτό πάνελ: κάρτα ---
            var panel = RailExpandedView.Content as StackPanel;
            if (panel != null)
            {
                var card = new Border
                {
                    Background = Br("#0d1520"),
                    BorderBrush = Br("#2c3e50"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 14, 0, 0)
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = "ℹ️ ΕΦΑΡΜΟΓΗ",
                    Foreground = Br("#bdc3c7"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold
                });
                sp.Children.Add(new TextBlock
                {
                    Text = "CSE Nexus " + VersionText(CurrentAppVersion()),
                    Foreground = Br("#ecf0f1"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(0, 10, 0, 0)
                });
                _updateStatus = new TextBlock
                {
                    Text = "Έλεγχος για νέα έκδοση...",
                    Foreground = Br("#7f8c8d"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                sp.Children.Add(_updateStatus);

                var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                Func<string, string, Action, Border> pill = (text, fg, action) =>
                {
                    var b = new Border
                    {
                        Background = Br("#111b24"),
                        BorderBrush = Br(fg),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 8, 6),
                        Cursor = Cursors.Hand,
                        Child = new TextBlock { Text = text, Foreground = Br(fg), FontFamily = new FontFamily("Consolas"), FontSize = 11 }
                    };
                    b.MouseLeftButtonUp += (s, e) => action();
                    wrap.Children.Add(b);
                    return b;
                };

                pill("🔄 Έλεγχος", "#bdc3c7", () => { var ignore = CheckForUpdateAsync(true); });
                _updateDownloadBtn = pill("⬇ Λήψη νέας έκδοσης", "#f1c40f", () => ShowUpdateDialog());
                _updateDownloadBtn.Visibility = Visibility.Collapsed;
                pill("🌐 GitHub", "#bdc3c7", () =>
                {
                    if (GitHubRepo != "OWNER/REPO") UpdOpenUrl("https://github.com/" + GitHubRepo);
                });

                sp.Children.Add(wrap);
                card.Child = sp;
                panel.Children.Add(card);
            }

            // Αυτόματος έλεγχος λίγα δευτερόλεπτα μετά την εκκίνηση (δεν καθυστερεί το άνοιγμα)
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                await CheckForUpdateAsync(false);
            };
            timer.Start();
        }
    }
}
