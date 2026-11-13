using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // NOTES: σημειώσεις ανά μάθημα (λίστα "τι πρέπει να διαβάσω" + ελεύθερο κείμενο + αναζήτηση).
    // Αντικαθιστά το παλιό πεδίο Memory Buffer, χωρίς αλλαγές στο XAML.
    // Απαιτεί το αρχείο  notes.html  (Copy to Output Directory = Copy if newer).
    // ==========================================================================
    public partial class MainWindow
    {
        public class NoteItem
        {
            public string t { get; set; } = "";
            public bool d { get; set; } = false;
        }

        public class CourseNote
        {
            public List<NoteItem> items { get; set; } = new List<NoteItem>();
            public string text { get; set; } = "";
            public string updated { get; set; } = "";
        }

        public class NotesStore
        {
            public string last { get; set; } = "_";
            // Κλειδί: κωδικός μαθήματος, ή "_" για τις γενικές σημειώσεις
            public Dictionary<string, CourseNote> notes { get; set; } = new Dictionary<string, CourseNote>();
        }

        private NotesStore _notesStore;
        private Microsoft.Web.WebView2.Wpf.WebView2 _notesBrowser;
        private string _notesAccent = "";
        private DispatcherTimer _notesAccentTimer;

        private string NotesPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "notes.json"); }
        }

        // ======================================================================
        // 1) ΑΠΟΘΗΚΕΥΣΗ (notes.json)
        // ======================================================================
        private NotesStore LoadNotesStore()
        {
            if (_notesStore != null) return _notesStore;
            bool existed = File.Exists(NotesPath);
            try
            {
                if (existed) _notesStore = JsonSerializer.Deserialize<NotesStore>(File.ReadAllText(NotesPath));
            }
            catch (Exception ex)
            {
                LogError("LoadNotesStore", ex);
                try { File.Copy(NotesPath, NotesPath + ".bak", true); } catch { }   // αντίγραφο του προβληματικού αρχείου
            }
            if (_notesStore == null) _notesStore = new NotesStore();
            if (_notesStore.notes == null) _notesStore.notes = new Dictionary<string, CourseNote>();
            if (string.IsNullOrEmpty(_notesStore.last)) _notesStore.last = "_";

            // Πρώτη φορά: το κείμενο του παλιού Memory Buffer μεταφέρεται στις Γενικές σημειώσεις
            if (!existed)
            {
                try
                {
                    if (File.Exists(MemoryFilePath))
                    {
                        string old = File.ReadAllText(MemoryFilePath);
                        if (!string.IsNullOrWhiteSpace(old))
                        {
                            _notesStore.notes["_"] = new CourseNote { text = old, updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
                            SaveNotesStore();
                        }
                    }
                }
                catch (Exception ex) { LogError("NotesMigration", ex); }
            }
            return _notesStore;
        }

        private void SaveNotesStore()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(NotesPath));
                string tmp = NotesPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(LoadNotesStore()));
                File.Copy(tmp, NotesPath, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { LogError("SaveNotesStore", ex); }
        }

        // ======================================================================
        // 2) ΑΝΤΙΚΑΤΑΣΤΑΣΗ ΤΟΥ ΠΑΛΙΟΥ ΠΕΔΙΟΥ ΜΕ ΤΗ ΝΕΑ ΣΕΛΙΔΑ
        // ======================================================================
        // Καλείται από τη LoadDeepDiveHub, μετά το ShowFocusCore(...)
        private async void InitNotes()
        {
            if (_notesBrowser != null) return;

            string path = Path.Combine(AppContext.BaseDirectory, "notes.html");
            if (!File.Exists(path))
            {
                LogError("InitNotes", new FileNotFoundException("Λείπει το αρχείο notes.html. Μένει το παλιό πεδίο Memory Buffer: " + path));
                return;   // το παλιό πεδίο συνεχίζει να δουλεύει
            }

            var grid = MemoryBufferText.Parent as Grid;
            if (grid == null)
            {
                LogError("InitNotes", new InvalidOperationException("Δεν βρέθηκε το πλαίσιο του Memory Buffer."));
                return;
            }

            try
            {
                var browser = new Microsoft.Web.WebView2.Wpf.WebView2();
                grid.Children.Add(browser);
                await browser.EnsureCoreWebView2Async(await GetEnvAsync());

                // F11 και μέσα σε αυτή τη σελίδα
                await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HotkeyScript);
                browser.CoreWebView2.WebMessageReceived += (s2, e2) =>
                {
                    try { if (IsHotkeyMessage(e2.TryGetWebMessageAsString())) ToggleFullscreen(); } catch { }
                };

                // Μηνύματα της σελίδας: αποθήκευση σημειώσεων
                browser.WebMessageReceived += (s, e) =>
                {
                    string raw = null;
                    try { raw = e.TryGetWebMessageAsString(); } catch { }
                    if (!string.IsNullOrEmpty(raw)) HandleNotesMessage(raw);
                };

                // Όταν αλλάζεις μάθημα στο Focus Core, ανοίγουν οι σημειώσεις του
                PomodoroBrowser.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        string raw = e.TryGetWebMessageAsString();
                        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("{\"type\":\"focus\"")) return;
                        using (var doc = JsonDocument.Parse(raw))
                        {
                            var root = doc.RootElement;
                            if (root.GetProperty("action").GetString() == "settings")
                                NotesSetCourse(root.GetProperty("settings").GetProperty("course").GetString());
                        }
                    }
                    catch { }
                };

                // Η νέα σελίδα παίρνει τη θέση του παλιού πεδίου
                MemoryBufferText.Visibility = Visibility.Collapsed;
                MemoryPlaceholder.Visibility = Visibility.Collapsed;
                _notesBrowser = browser;
                RenameNotesHeader();

                _notesAccent = ThemeHex;
                browser.NavigateToString(BuildNotesHtml(path));

                _notesAccentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _notesAccentTimer.Tick += (s, e) => NotesSyncAccent();
                _notesAccentTimer.Start();
            }
            catch (Exception ex)
            {
                LogError("InitNotes", ex);
                // Αποτυχία: επιστρέφει το παλιό πεδίο
                MemoryBufferText.Visibility = Visibility.Visible;
                MemoryPlaceholder.Visibility = Visibility.Visible;
            }
        }

        // Αλλάζει τον τίτλο της στήλης από "Memory Buffer" σε "Σημειώσεις"
        private void RenameNotesHeader()
        {
            try
            {
                var inner = (MemoryBufferText.Parent as FrameworkElement) != null ? ((FrameworkElement)MemoryBufferText.Parent).Parent as Border : null;
                var outer = inner != null ? inner.Parent as Border : null;
                var column = outer != null ? outer.Parent as Grid : null;
                var header = (column != null && column.Children.Count > 0) ? column.Children[0] as StackPanel : null;
                if (header == null || header.Children.Count < 2) return;

                var title = header.Children[0] as TextBlock;
                var sub = header.Children[1] as TextBlock;
                if (title != null) title.Text = "📝 Σημειώσεις";
                if (sub != null) sub.Text = "Ανά μάθημα: λίστα «τι πρέπει να διαβάσω» και ελεύθερο κείμενο.";
            }
            catch (Exception ex) { LogError("RenameNotesHeader", ex); }
        }

        private string BuildNotesHtml(string templatePath)
        {
            var store = LoadNotesStore();
            var titles = SchedCurriculumTitles();
            var owed = SchedOwedCodes();
            var courses = owed.OrderBy(c => c).Select(c => new { c = c, t = titles.ContainsKey(c) ? titles[c] : c }).ToList();

            // Ανοίγει το μάθημα που έχεις επιλέξει στο Focus Core, αλλιώς το τελευταίο που είχες ανοιχτό
            string focusCourse = LoadFocusStore().settings.course;
            string start = !string.IsNullOrEmpty(focusCourse) ? focusCourse : (string.IsNullOrEmpty(store.last) ? "_" : store.last);

            return File.ReadAllText(templatePath, Encoding.UTF8)
                .Replace("__ACCENT_RGB__", ThemeRgb)
                .Replace("__ACCENT__", ThemeHex)
                .Replace("__NOTES__", JsonSerializer.Serialize(store.notes))
                .Replace("__COURSES__", JsonSerializer.Serialize(courses))
                .Replace("__NAMES__", JsonSerializer.Serialize(titles))
                .Replace("__LAST__", JsonSerializer.Serialize(start));
        }

        // ======================================================================
        // 3) ΜΗΝΥΜΑΤΑ ΑΠΟ ΤΗ ΣΕΛΙΔΑ
        // ======================================================================
        private void HandleNotesMessage(string raw)
        {
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    var root = doc.RootElement;
                    if (root.GetProperty("type").GetString() != "notes") return;   // π.χ. "hotkey": αγνοείται

                    string action = root.GetProperty("action").GetString();
                    var store = LoadNotesStore();

                    if (action == "save")
                    {
                        string code = root.GetProperty("code").GetString();
                        if (string.IsNullOrEmpty(code)) code = "_";

                        var n = root.GetProperty("note");
                        string text = n.GetProperty("text").GetString() ?? "";
                        if (text.Length > 200000) text = text.Substring(0, 200000);   // όριο ασφαλείας

                        var cn = new CourseNote { text = text, updated = n.GetProperty("updated").GetString() ?? "" };
                        foreach (var it in n.GetProperty("items").EnumerateArray())
                        {
                            if (cn.items.Count >= 500) break;
                            cn.items.Add(new NoteItem { t = it.GetProperty("t").GetString() ?? "", d = it.GetProperty("d").GetBoolean() });
                        }

                        // Άδειες σημειώσεις δεν κρατιούνται
                        if (cn.items.Count == 0 && string.IsNullOrWhiteSpace(cn.text)) store.notes.Remove(code);
                        else store.notes[code] = cn;
                        SaveNotesStore();
                    }
                    else if (action == "last")
                    {
                        string code = root.GetProperty("code").GetString();
                        store.last = string.IsNullOrEmpty(code) ? "_" : code;
                        SaveNotesStore();
                    }
                }
            }
            catch (Exception ex) { LogError("HandleNotesMessage", ex); }
        }

        // ======================================================================
        // 4) ΣΥΓΧΡΟΝΙΣΜΟΣ ΜΕ ΤΗΝ ΕΦΑΡΜΟΓΗ
        // ======================================================================
        private async void NotesSetCourse(string code)
        {
            try
            {
                if (_notesBrowser == null || _notesBrowser.CoreWebView2 == null) return;
                await _notesBrowser.ExecuteScriptAsync("window.setCourse&&window.setCourse(" + JsonSerializer.Serialize(code ?? "") + ")");
            }
            catch (Exception ex) { LogError("NotesSetCourse", ex); }
        }

        private async void NotesSyncAccent()
        {
            try
            {
                if (_notesBrowser == null || _notesBrowser.CoreWebView2 == null || _notesAccent == ThemeHex) return;
                _notesAccent = ThemeHex;
                await _notesBrowser.ExecuteScriptAsync("window.setAccent&&window.setAccent('" + ThemeHex + "','" + ThemeRgb + "')");
            }
            catch (Exception ex) { LogError("NotesSyncAccent", ex); }
        }
    }
}
