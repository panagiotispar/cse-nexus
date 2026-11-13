using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // FOCUS: νέο Focus Core (Study Hub) με Zen Mode και Study Log.
    // Απαιτεί το αρχείο  focus.html  (Copy to Output Directory = Copy if newer).
    // Χρησιμοποιεί μεθόδους από τα MainWindow.Schedule.cs / MainWindow.ScheduleExport.cs
    // ==========================================================================
    public partial class MainWindow
    {
        // ----- Μοντέλα (τα ονόματα είναι με μικρά γράμματα για να ταιριάζουν με τη σελίδα) -----
        public class StudySession
        {
            public string d { get; set; } = "";   // ημερομηνία yyyy-MM-dd
            public string t { get; set; } = "";   // ώρα ολοκλήρωσης HH:mm
            public string c { get; set; } = "";   // κωδικός μαθήματος ("" = γενικό)
            public int m { get; set; } = 0;       // λεπτά εργασίας
        }

        public class FocusSettings
        {
            public int work { get; set; } = 30;
            public int rest { get; set; } = 5;
            public bool autoZen { get; set; } = false;
            public string course { get; set; } = "";
        }

        public class FocusStore
        {
            public FocusSettings settings { get; set; } = new FocusSettings();
            public List<StudySession> sessions { get; set; } = new List<StudySession>();
        }

        private class ZenSaved
        {
            public Visibility Rail, Header, Banner, FocusTop, FocusBottom, Left, Right;
            public GridLength C0, C2;
        }

        private const int HubTabIndex = 4;

        private FocusStore _focusStore;
        private bool _focusHooked = false;
        private string _focusAccent = "";
        private DispatcherTimer _focusAccentTimer;

        private bool _zen = false, _zenNotesOpen = false, _zenRadioOpen = false;
        private ZenSaved _zenSaved;
        private System.Windows.Controls.Primitives.TabPanel _zenTabPanel;

        private string FocusLogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "study_log.json"); }
        }

        // ======================================================================
        // 1) ΑΠΟΘΗΚΕΥΣΗ (study_log.json)
        // ======================================================================
        private FocusStore LoadFocusStore()
        {
            if (_focusStore != null) return _focusStore;
            try
            {
                if (File.Exists(FocusLogPath))
                    _focusStore = JsonSerializer.Deserialize<FocusStore>(File.ReadAllText(FocusLogPath));
            }
            catch (Exception ex)
            {
                LogError("LoadFocusStore", ex);
                try { File.Copy(FocusLogPath, FocusLogPath + ".bak", true); } catch { }   // κρατάμε αντίγραφο του προβληματικού αρχείου
            }
            if (_focusStore == null) _focusStore = new FocusStore();
            if (_focusStore.settings == null) _focusStore.settings = new FocusSettings();
            if (_focusStore.sessions == null) _focusStore.sessions = new List<StudySession>();
            return _focusStore;
        }

        private void SaveFocusStore()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FocusLogPath));
                string tmp = FocusLogPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(LoadFocusStore()));
                File.Copy(tmp, FocusLogPath, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { LogError("SaveFocusStore", ex); }
        }

        // ======================================================================
        // 2) ΦΟΡΤΩΣΗ ΤΗΣ ΣΕΛΙΔΑΣ FOCUS CORE
        // ======================================================================
        // Καλείται από τη LoadDeepDiveHub στη θέση του PomodoroBrowser.NavigateToString(pomodoroHtml)
        private void ShowFocusCore(string fallbackHtml)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "focus.html");
                if (!File.Exists(path))
                {
                    LogError("ShowFocusCore", new FileNotFoundException("Λείπει το αρχείο focus.html από τον φάκελο της εφαρμογής: " + path));
                    PomodoroBrowser.NavigateToString(fallbackHtml);
                    return;
                }

                var store = LoadFocusStore();
                var titles = SchedCurriculumTitles();
                var owed = SchedOwedCodes();
                var courses = owed.OrderBy(c => c).Select(c => new { c = c, t = titles.ContainsKey(c) ? titles[c] : c }).ToList();

                var all = store.sessions;
                var shown = all.Skip(Math.Max(0, all.Count - 3000)).ToList();
                var total = new { minutes = all.Sum(x => x.m), cycles = all.Count };

                string html = File.ReadAllText(path, Encoding.UTF8)
                    .Replace("__ACCENT_RGB__", ThemeRgb)
                    .Replace("__ACCENT__", ThemeHex)
                    .Replace("__SETTINGS__", JsonSerializer.Serialize(store.settings))
                    .Replace("__LOG__", JsonSerializer.Serialize(shown))
                    .Replace("__COURSES__", JsonSerializer.Serialize(courses))
                    .Replace("__NAMES__", JsonSerializer.Serialize(titles))
                    .Replace("__TOTAL__", JsonSerializer.Serialize(total))
                    .Replace("__AGENDA__", BuildFocusAgendaJson());

                EnsureFocusHook();
                _focusAccent = ThemeHex;
                PomodoroBrowser.NavigateToString(html);
            }
            catch (Exception ex)
            {
                LogError("ShowFocusCore", ex);
                PomodoroBrowser.NavigateToString(fallbackHtml);
            }
        }

        private void EnsureFocusHook()
        {
            if (_focusHooked) return;
            PomodoroBrowser.WebMessageReceived += (s, e) =>
            {
                string raw = null;
                try { raw = e.TryGetWebMessageAsString(); } catch { }
                if (!string.IsNullOrEmpty(raw)) HandleFocusMessage(raw);
            };
            _focusHooked = true;
        }

        // Μηνύματα από τη σελίδα: {type:'focus', action:'session'|'settings'} και {type:'zen', action:...}
        private void HandleFocusMessage(string raw)
        {
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString();
                    string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() : "";

                    if (type == "focus")
                    {
                        var store = LoadFocusStore();
                        if (action == "session")
                        {
                            var se = root.GetProperty("session");
                            store.sessions.Add(new StudySession
                            {
                                d = se.GetProperty("d").GetString() ?? "",
                                t = se.GetProperty("t").GetString() ?? "",
                                c = se.GetProperty("c").GetString() ?? "",
                                m = Math.Max(1, Math.Min(240, se.GetProperty("m").GetInt32()))
                            });
                            SaveFocusStore();
                            OnFocusSessionSaved();
                        }
                        else if (action == "settings")
                        {
                            var st = root.GetProperty("settings");
                            store.settings.work = Math.Max(5, Math.Min(120, st.GetProperty("work").GetInt32()));
                            store.settings.rest = Math.Max(1, Math.Min(60, st.GetProperty("rest").GetInt32()));
                            store.settings.autoZen = st.GetProperty("autoZen").GetBoolean();
                            store.settings.course = st.GetProperty("course").GetString() ?? "";
                            SaveFocusStore();
                        }
                    }
                    else if (type == "zen")
                    {
                        if (action == "enter") EnterZen();
                        else if (action == "exit") ExitZen();
                        else if (action == "notes") ToggleZenPanel(true);
                        else if (action == "radio") ToggleZenPanel(false);
                    }
                    // Άλλοι τύποι (π.χ. "hotkey") αγνοούνται σιωπηλά
                }
            }
            catch (Exception ex) { LogError("HandleFocusMessage", ex); }
        }

        // ======================================================================
        // 3) ZEN MODE: κρύβει ό,τι περισπά και αφήνει μόνο το ρολόι
        // ======================================================================
        private static T ZenFindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var typed = child as T;
                if (typed != null) return typed;
                var deeper = ZenFindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // Το πλέγμα του Study Hub: [0] σημειώσεις, [1] Focus Core, [2] ράδιο
        private Grid ZenHubGrid()
        {
            if (MainTabControl.Items.Count <= HubTabIndex) return null;
            var item = MainTabControl.Items[HubTabIndex] as TabItem;
            var hub = item != null ? item.Content as Grid : null;
            if (hub == null || hub.ColumnDefinitions.Count < 3 || hub.Children.Count < 3) return null;
            return hub;
        }

        private void EnterZen()
        {
            if (_zen) return;
            if (MainTabControl.SelectedIndex != HubTabIndex) return;

            var hub = ZenHubGrid();
            if (hub == null) return;
            var focusGrid = hub.Children[1] as Grid;
            if (focusGrid == null || focusGrid.Children.Count < 3) return;
            var header = TypewriterBrowser.Parent as FrameworkElement;

            _zenSaved = new ZenSaved
            {
                Rail = SidePanel.Visibility,
                Header = header != null ? header.Visibility : Visibility.Visible,
                Banner = TimeMachineBanner.Visibility,
                FocusTop = focusGrid.Children[0].Visibility,
                FocusBottom = focusGrid.Children[2].Visibility,
                Left = hub.Children[0].Visibility,
                Right = hub.Children[2].Visibility,
                C0 = hub.ColumnDefinitions[0].Width,
                C2 = hub.ColumnDefinitions[2].Width
            };

            SidePanel.Visibility = Visibility.Collapsed;                       // μπάρα Control Center
            if (header != null) header.Visibility = Visibility.Collapsed;      // επικεφαλίδα typewriter
            TimeMachineBanner.Visibility = Visibility.Collapsed;
            _zenTabPanel = ZenFindChild<System.Windows.Controls.Primitives.TabPanel>(MainTabControl);
            if (_zenTabPanel != null) _zenTabPanel.Visibility = Visibility.Collapsed;   // καρτέλες

            focusGrid.Children[0].Visibility = Visibility.Collapsed;           // τίτλος Focus Core
            focusGrid.Children[2].Visibility = Visibility.Collapsed;           // κάτω γραμμή συμβουλής
            hub.Children[0].Visibility = Visibility.Collapsed;                 // σημειώσεις
            hub.Children[2].Visibility = Visibility.Collapsed;                 // ράδιο
            hub.ColumnDefinitions[0].Width = new GridLength(0);
            hub.ColumnDefinitions[2].Width = new GridLength(0);

            _zen = true;
            _zenNotesOpen = false;
            _zenRadioOpen = false;
            FocusSetZen(true);
        }

        private void ExitZen()
        {
            if (!_zen) return;

            var hub = ZenHubGrid();
            if (hub != null && _zenSaved != null)
            {
                var focusGrid = hub.Children[1] as Grid;
                if (focusGrid != null && focusGrid.Children.Count >= 3)
                {
                    focusGrid.Children[0].Visibility = _zenSaved.FocusTop;
                    focusGrid.Children[2].Visibility = _zenSaved.FocusBottom;
                }
                hub.Children[0].Visibility = _zenSaved.Left;
                hub.Children[2].Visibility = _zenSaved.Right;
                hub.ColumnDefinitions[0].Width = _zenSaved.C0;
                hub.ColumnDefinitions[2].Width = _zenSaved.C2;
            }
            if (_zenSaved != null)
            {
                SidePanel.Visibility = _zenSaved.Rail;
                var header = TypewriterBrowser.Parent as FrameworkElement;
                if (header != null) header.Visibility = _zenSaved.Header;
                TimeMachineBanner.Visibility = _zenSaved.Banner;
            }
            if (_zenTabPanel != null)
            {
                _zenTabPanel.Visibility = Visibility.Visible;
                _zenTabPanel = null;
            }

            _zen = false;
            _zenNotesOpen = false;
            _zenRadioOpen = false;
            FocusSetZen(false);
        }

        // Ανοίγει/κλείνει τη στήλη των σημειώσεων ή του ραδιοφώνου μέσα στο Zen
        private void ToggleZenPanel(bool notes)
        {
            if (!_zen) return;
            var hub = ZenHubGrid();
            if (hub == null) return;

            if (notes)
            {
                _zenNotesOpen = !_zenNotesOpen;
                hub.Children[0].Visibility = _zenNotesOpen ? Visibility.Visible : Visibility.Collapsed;
                hub.ColumnDefinitions[0].Width = _zenNotesOpen ? new GridLength(480) : new GridLength(0);
            }
            else
            {
                _zenRadioOpen = !_zenRadioOpen;
                hub.Children[2].Visibility = _zenRadioOpen ? Visibility.Visible : Visibility.Collapsed;
                hub.ColumnDefinitions[2].Width = _zenRadioOpen ? new GridLength(520) : new GridLength(0);
            }
        }

        // Ενημερώνει τη σελίδα ότι μπήκε/βγήκε από Zen
        private async void FocusSetZen(bool on)
        {
            try
            {
                if (PomodoroBrowser.CoreWebView2 != null)
                    await PomodoroBrowser.ExecuteScriptAsync("window.setZen&&window.setZen(" + (on ? "true" : "false") + ")");
            }
            catch (Exception ex) { LogError("FocusSetZen", ex); }
        }

        // ======================================================================
        // 4) ΣΥΓΧΡΟΝΙΣΜΟΣ ΘΕΜΑΤΟΣ (χωρίς να ξαναφορτώνεται η σελίδα, ώστε να μη χάνεται το ρολόι)
        // ======================================================================
        private async void FocusSyncAccent()
        {
            try
            {
                if (PomodoroBrowser.CoreWebView2 == null || string.IsNullOrEmpty(_focusAccent) || _focusAccent == ThemeHex) return;
                _focusAccent = ThemeHex;
                await PomodoroBrowser.ExecuteScriptAsync("window.setAccent&&window.setAccent('" + ThemeHex + "','" + ThemeRgb + "')");
            }
            catch (Exception ex) { LogError("FocusSyncAccent", ex); }
        }

        // Καλείται μία φορά από τον constructor
        private void InitFocus()
        {
            // Αν αλλάξεις καρτέλα, το Zen τελειώνει
            MainTabControl.SelectionChanged += (s, e) =>
            {
                if (e.Source == MainTabControl && _zen && MainTabControl.SelectedIndex != HubTabIndex) ExitZen();
            };

            // Esc βγάζει από Zen (όταν το πλήκτρο πατιέται πάνω σε στοιχείο της εφαρμογής)
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _zen)
                {
                    ExitZen();
                    e.Handled = true;
                }
            };

            _focusAccentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _focusAccentTimer.Tick += (s, e) => FocusSyncAccent();
            _focusAccentTimer.Start();
        }
    }
}
