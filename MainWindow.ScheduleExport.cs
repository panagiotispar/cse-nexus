using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // SCHEDULE EXPORT: εξαγωγή του προγράμματος σε αρχείο ημερολογίου (.ics)
    // για Google Calendar, Outlook, Apple Calendar.
    // Χρησιμοποιεί τα μοντέλα και τις μεθόδους του MainWindow.Schedule.cs
    // ==========================================================================
    public partial class MainWindow
    {
        private const string IcsTz = "Europe/Athens";

        private class IcsOptions
        {
            public bool Lectures, Labs, Exams, OnlyFiltered;
            public DateTime LabsStart;
            public string HolidaysText = "";
        }

        // ======================================================================
        // 1) ΒΟΗΘΗΤΙΚΕΣ (μορφοποίηση iCalendar)
        // ======================================================================
        private static string IcsEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
        }

        // Οι γραμμές του iCalendar δεν πρέπει να ξεπερνούν τα 75 bytes (UTF-8). Οι συνέχειες ξεκινούν με κενό.
        private static void IcsFold(StringBuilder sb, string line)
        {
            var cur = new StringBuilder();
            int bytes = 0;
            for (int i = 0; i < line.Length; i++)
            {
                string unit = line[i].ToString();
                if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length)
                {
                    unit = line.Substring(i, 2);
                    i++;
                }
                int n = Encoding.UTF8.GetByteCount(unit);
                if (bytes + n > 75)
                {
                    sb.Append(cur.ToString()).Append("\r\n");
                    cur.Clear();
                    cur.Append(' ');
                    bytes = 1;
                }
                cur.Append(unit);
                bytes += n;
            }
            sb.Append(cur.ToString()).Append("\r\n");
        }

        private static string IcsLocal(DateTime date, string hhmm)
        {
            return date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "T" + hhmm.Replace(":", "") + "00";
        }

        // Σταθερό αναγνωριστικό: αν ξαναεξαγάγεις το ίδιο πρόγραμμα, το Google Calendar το αναγνωρίζει
        private static string IcsUid(string key)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant() + "@uoi-study-hub";
            }
        }

        // ======================================================================
        // 2) ΑΡΓΙΕΣ / ΜΕΡΕΣ ΧΩΡΙΣ ΜΑΘΗΜΑ
        // ======================================================================
        private static bool IcsTryParseDate(string s, DateTime ts, DateTime te, out DateTime d)
        {
            d = DateTime.MinValue;
            var m = Regex.Match(s.Trim(), @"^(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?$");
            if (!m.Success) return false;

            int day = int.Parse(m.Groups[1].Value), mon = int.Parse(m.Groups[2].Value);
            if (m.Groups[3].Success)
            {
                int y = int.Parse(m.Groups[3].Value);
                if (y < 100) y += 2000;
                try { d = new DateTime(y, mon, day); return true; } catch { return false; }
            }
            // Χωρίς έτος: διαλέγουμε το έτος για το οποίο η ημερομηνία πέφτει μέσα στο εξάμηνο
            for (int y = ts.Year; y <= te.Year; y++)
            {
                try
                {
                    var c = new DateTime(y, mon, day);
                    if (c >= ts.Date && c <= te.Date) { d = c; return true; }
                }
                catch { }
            }
            return false;
        }

        private static HashSet<DateTime> IcsParseSkip(string text, DateTime ts, DateTime te)
        {
            var skip = new HashSet<DateTime>();
            foreach (string raw in Regex.Split(text ?? "", @"[,;\r\n]+"))
            {
                string tok = raw.Trim();
                if (tok.Length == 0) continue;

                int dash = tok.IndexOf('-');
                if (dash > 0)
                {
                    DateTime a, b;
                    if (IcsTryParseDate(tok.Substring(0, dash), ts, te, out a) && IcsTryParseDate(tok.Substring(dash + 1), ts, te, out b))
                    {
                        for (DateTime x = a; x <= b; x = x.AddDays(1)) skip.Add(x.Date);
                    }
                }
                else
                {
                    DateTime d;
                    if (IcsTryParseDate(tok, ts, te, out d)) skip.Add(d.Date);
                }
            }
            return skip;
        }

        // Αργίες με σταθερή ημερομηνία που πέφτουν μέσα στο εξάμηνο
        private static string IcsDefaultHolidays(DateTime ts, DateTime te)
        {
            var list = new List<DateTime>();
            int[][] fixedDays = { new[] { 10, 28 }, new[] { 11, 17 }, new[] { 12, 25 }, new[] { 12, 26 }, new[] { 1, 1 }, new[] { 1, 6 } };
            for (int y = ts.Year; y <= te.Year; y++)
            {
                foreach (var md in fixedDays)
                {
                    try
                    {
                        var c = new DateTime(y, md[0], md[1]);
                        if (c >= ts.Date && c <= te.Date) list.Add(c);
                    }
                    catch { }
                }
            }
            return string.Join(", ", list.OrderBy(x => x).Select(x => x.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
        }

        // ======================================================================
        // 3) ΔΕΔΟΜΕΝΑ ΑΠΟ ΤΟ ΠΡΟΓΡΑΜΜΑ ΣΠΟΥΔΩΝ / ΒΑΘΜΟΥΣ
        // ======================================================================
        private Dictionary<string, string> SchedCurriculumTitles()
        {
            var map = new Dictionary<string, string>();
            try
            {
                foreach (var m in GetMissingCoursesList(new HashSet<string>()))
                {
                    string code = SchedNormCode(m.Code);
                    if (SchedIsCourseCode(code) && !map.ContainsKey(code)) map[code] = m.Title;
                }
            }
            catch (Exception ex) { LogError("SchedCurriculumTitles", ex); }
            return map;
        }

        // Κωδικοί μαθημάτων που ΔΕΝ έχεις περάσει ακόμα
        private List<string> SchedOwedCodes()
        {
            var owed = new List<string>();
            try
            {
                if (File.Exists(DataFilePath))
                {
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var passedKeys = GetPassedGrades(options).Select(g => NormalizeKey(g.CourseId.Title)).ToHashSet();
                    owed = GetMissingCoursesList(passedKeys).Select(m => SchedNormCode(m.Code)).Where(SchedIsCourseCode).Distinct().ToList();
                }
            }
            catch (Exception ex) { LogError("SchedOwedCodes", ex); }
            return owed;
        }

        // Ποια μαθήματα "βλέπει" ο χρήστης τώρα στη γραφική προβολή (ίδια λογική με το schedule.html)
        private HashSet<string> SchedVisibleCodes(HashSet<string> allCodes, List<string> owed)
        {
            var st = _save.Sched ?? new SchedState();
            string mode = string.IsNullOrEmpty(st.Mode) ? (owed.Count > 0 ? "owed" : "all") : st.Mode;
            if (mode == "all") return new HashSet<string>(allCodes);
            if (mode == "none") return new HashSet<string>();
            if (mode == "owed") return new HashSet<string>(owed);
            return new HashSet<string>(st.Codes ?? new List<string>());
        }

        // ======================================================================
        // 4) ΔΗΜΙΟΥΡΓΙΑ ΤΟΥ ΑΡΧΕΙΟΥ .ics
        // ======================================================================
        private string BuildIcs(IcsOptions opt, SchedParsed lec, SchedParsed lab, SchedParsed exa,
                                HashSet<string> visible, Dictionary<string, string> titles,
                                bool haveTerm, DateTime ts, DateTime te, HashSet<DateTime> skip, out int count)
        {
            var sb = new StringBuilder();
            count = 0;

            Action<string> L = line => IcsFold(sb, line);

            L("BEGIN:VCALENDAR");
            L("VERSION:2.0");
            L("PRODID:-//UOI Study Hub//Schedule//EL");
            L("CALSCALE:GREGORIAN");
            L("METHOD:PUBLISH");
            L("X-WR-CALNAME:Πρόγραμμα ΤΜΗΥΠ");
            L("X-WR-TIMEZONE:" + IcsTz);

            // Ζώνη ώρας Ελλάδας (με θερινή/χειμερινή ώρα). Οι διαλέξεις μένουν στην ίδια ώρα και μετά την αλλαγή ώρας.
            L("BEGIN:VTIMEZONE");
            L("TZID:" + IcsTz);
            L("BEGIN:STANDARD");
            L("DTSTART:19701025T040000");
            L("TZOFFSETFROM:+0300");
            L("TZOFFSETTO:+0200");
            L("TZNAME:EET");
            L("RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU");
            L("END:STANDARD");
            L("BEGIN:DAYLIGHT");
            L("DTSTART:19700329T030000");
            L("TZOFFSETFROM:+0200");
            L("TZOFFSETTO:+0300");
            L("TZNAME:EEST");
            L("RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU");
            L("END:DAYLIGHT");
            L("END:VTIMEZONE");

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var kinds = new[]
            {
                Tuple.Create("lectures", lec),
                Tuple.Create("labs", lab),
                Tuple.Create("exams", exa)
            };

            foreach (var kv in kinds)
            {
                string kind = kv.Item1;
                SchedParsed P = kv.Item2;
                if (P == null) continue;
                if (kind == "lectures" && (!opt.Lectures || !haveTerm)) continue;
                if (kind == "labs" && (!opt.Labs || !haveTerm)) continue;
                if (kind == "exams" && !opt.Exams) continue;

                foreach (var s in P.sessions)
                {
                    if (!visible.Contains(s.c)) continue;

                    string title = titles.ContainsKey(s.c) ? titles[s.c] : s.t;
                    bool hasNote = !string.IsNullOrEmpty(s.n);
                    string kl = hasNote ? s.n : (kind == "labs" ? "Εργαστήριο" : kind == "exams" ? "Εξέταση" : "Διάλεξη");
                    string summary = (kind == "lectures" && !hasNote) ? s.c + " " + title : kl + ": " + s.c + " " + title;

                    string room = s.r ?? "";
                    if (kind == "labs" && room.Length > 0)
                    {
                        // ΠΕΠ-Ι -> Προπτυχιακό Εργαστήριο Προγραμματισμού Ι (από το υπόμνημα του PDF)
                        room = string.Join("; ", room.Split(',').Select(t => t.Trim())
                            .Select(t => (P.legend != null && P.legend.ContainsKey(t)) ? P.legend[t] : t));
                    }

                    var desc = new StringBuilder();
                    desc.Append("Μάθημα: ").Append(s.c).Append(' ').Append(title).Append('\n');
                    desc.Append("Είδος: ").Append(kl).Append('\n');
                    if (kind == "labs") desc.Append("Ενδεικτικό: τα εργαστήρια ξεκινούν συνήθως αργότερα από τις διαλέξεις, σε συνεννόηση με τον διδάσκοντα.").Append('\n');
                    desc.Append("Πηγή: Πρόγραμμα ΤΜΗΥΠ (UOI Study Hub)");

                    if (kind == "exams")
                    {
                        DateTime ed;
                        if (!DateTime.TryParseExact(s.dt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out ed)) continue;

                        L("BEGIN:VEVENT");
                        L("UID:" + IcsUid("exam|" + s.c + "|" + s.dt + "|" + s.s));
                        L("DTSTAMP:" + stamp);
                        L("DTSTART;TZID=" + IcsTz + ":" + IcsLocal(ed, s.s));
                        L("DTEND;TZID=" + IcsTz + ":" + IcsLocal(ed, s.e));
                        L("SUMMARY:" + IcsEsc(summary));
                        L("DESCRIPTION:" + IcsEsc(desc.ToString()));
                        L("CATEGORIES:" + IcsEsc(kl));
                        L("BEGIN:VALARM");
                        L("ACTION:DISPLAY");
                        L("DESCRIPTION:" + IcsEsc("Αύριο εξέταση: " + s.c + " " + title));
                        L("TRIGGER:-P1D");
                        L("END:VALARM");
                        L("END:VEVENT");
                        count++;
                        continue;
                    }

                    // Διαλέξεις και εργαστήρια: εβδομαδιαία επανάληψη μέσα στο εξάμηνο
                    int target = s.d + 1;                       // Δευτέρα=1 ... Παρασκευή=5
                    if (target < 1 || target > 5) continue;

                    DateTime start = kind == "lectures" ? ts.Date : (opt.LabsStart.Date > ts.Date ? opt.LabsStart.Date : ts.Date);
                    int delta = (target - (int)start.DayOfWeek + 7) % 7;
                    DateTime first = start.AddDays(delta);
                    if (first > te.Date) continue;

                    L("BEGIN:VEVENT");
                    L("UID:" + IcsUid(kind + "|" + s.c + "|" + s.d + "|" + s.s + "|" + s.e + "|" + s.r + "|" + s.n));
                    L("DTSTAMP:" + stamp);
                    L("DTSTART;TZID=" + IcsTz + ":" + IcsLocal(first, s.s));
                    L("DTEND;TZID=" + IcsTz + ":" + IcsLocal(first, s.e));
                    L("RRULE:FREQ=WEEKLY;UNTIL=" + te.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "T235959Z");
                    foreach (var sd in skip.OrderBy(x => x))
                    {
                        if ((int)sd.DayOfWeek == target && sd >= first && sd <= te.Date)
                            L("EXDATE;TZID=" + IcsTz + ":" + IcsLocal(sd, s.s));
                    }
                    L("SUMMARY:" + IcsEsc(summary));
                    if (room.Length > 0) L("LOCATION:" + IcsEsc(room));
                    L("DESCRIPTION:" + IcsEsc(desc.ToString()));
                    L("CATEGORIES:" + IcsEsc(kl));
                    L("BEGIN:VALARM");
                    L("ACTION:DISPLAY");
                    L("DESCRIPTION:Υπενθύμιση");
                    L("TRIGGER:-PT15M");
                    L("END:VALARM");
                    L("END:VEVENT");
                    count++;
                }
            }

            L("END:VCALENDAR");
            return sb.ToString();
        }

        // ======================================================================
        // 5) ΠΑΡΑΘΥΡΟ ΕΠΙΛΟΓΩΝ
        // ======================================================================
        private IcsOptions ShowIcsDialog(bool hasLec, bool hasLab, bool hasExa, bool haveTerm,
                                         DateTime ts, DateTime te, int filteredN, int totalN)
        {
            var win = new Window
            {
                Title = "Εξαγωγή σε ημερολόγιο",
                Width = 600,
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

            root.Children.Add(label("📅 Εξαγωγή σε ημερολόγιο (.ics)", 18, true, "#ecf0f1"));
            root.Children.Add(label("Συμβατό με Google Calendar, Outlook και Apple Calendar.", 12, false, "#7f8c8d"));

            // --- Τι να περιλαμβάνεται ---
            var secWhat = label("ΤΙ ΝΑ ΠΕΡΙΛΑΒΕΙ", 11, true, "#7f8c8d");
            secWhat.Margin = new Thickness(0, 18, 0, 4);
            root.Children.Add(secWhat);

            string termTxt = haveTerm ? ts.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + " έως " + te.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "δεν βρέθηκαν ημερομηνίες εξαμήνου";

            Func<string, bool, CheckBox> check = (t, enabled) => new CheckBox
            {
                Content = t,
                IsChecked = enabled,
                IsEnabled = enabled,
                Foreground = Br("#ecf0f1"),
                FontFamily = mono,
                FontSize = 13,
                Margin = new Thickness(0, 5, 0, 0)
            };
            var cbLec = check("📘 Διαλέξεις (κάθε εβδομάδα, " + termTxt + ")", hasLec && haveTerm);
            var cbLab = check("🧪 Εργαστήρια (ενδεικτικά, κάθε εβδομάδα)", hasLab && haveTerm);
            var cbExa = check("📝 Εξετάσεις (μία φορά η καθεμία, με υπενθύμιση την προηγούμενη ημέρα)", hasExa);
            root.Children.Add(cbLec);
            root.Children.Add(cbLab);
            root.Children.Add(cbExa);

            // --- Ποια μαθήματα ---
            var secWhich = label("ΠΟΙΑ ΜΑΘΗΜΑΤΑ", 11, true, "#7f8c8d");
            secWhich.Margin = new Thickness(0, 16, 0, 4);
            root.Children.Add(secWhich);

            var rbFiltered = new RadioButton
            {
                Content = "Μόνο όσα βλέπω τώρα στο πρόγραμμα (" + filteredN + " μαθήματα)",
                GroupName = "ics_scope",
                IsChecked = filteredN > 0,
                IsEnabled = filteredN > 0,
                Foreground = Br("#ecf0f1"),
                FontFamily = mono,
                FontSize = 13,
                Margin = new Thickness(0, 5, 0, 0)
            };
            var rbAll = new RadioButton
            {
                Content = "Όλα τα μαθήματα του προγράμματος (" + totalN + " μαθήματα)",
                GroupName = "ics_scope",
                IsChecked = filteredN == 0,
                Foreground = Br("#ecf0f1"),
                FontFamily = mono,
                FontSize = 13,
                Margin = new Thickness(0, 5, 0, 0)
            };
            root.Children.Add(rbFiltered);
            root.Children.Add(rbAll);

            Func<TextBox> box = () => new TextBox
            {
                Background = Br("#111b24"),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = Br("#2c3e50"),
                FontFamily = mono,
                FontSize = 13,
                Padding = new Thickness(6)
            };

            // --- Έναρξη εργαστηρίων ---
            var secLab = label("ΕΝΑΡΞΗ ΕΡΓΑΣΤΗΡΙΩΝ (ηη/μμ/εεεε)", 11, true, "#7f8c8d");
            secLab.Margin = new Thickness(0, 16, 0, 4);
            root.Children.Add(secLab);
            var tbLab = box();
            tbLab.Text = ts.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            tbLab.IsEnabled = hasLab && haveTerm;
            root.Children.Add(tbLab);
            root.Children.Add(label("Τα εργαστήρια συνήθως ξεκινούν αργότερα από τις διαλέξεις. Βάλε την πρώτη ημερομηνία που θα γίνουν.", 11, false, "#7f8c8d"));

            // --- Αργίες ---
            var secHol = label("ΜΕΡΕΣ ΧΩΡΙΣ ΜΑΘΗΜΑ (αργίες)", 11, true, "#7f8c8d");
            secHol.Margin = new Thickness(0, 16, 0, 4);
            root.Children.Add(secHol);
            var tbHol = box();
            tbHol.AcceptsReturn = true;
            tbHol.TextWrapping = TextWrapping.Wrap;
            tbHol.Height = 64;
            tbHol.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            tbHol.Text = haveTerm ? IcsDefaultHolidays(ts, te) : "";
            tbHol.IsEnabled = haveTerm;
            root.Children.Add(tbHol);
            root.Children.Add(label("Μία ημερομηνία ή περιοχή, χωρισμένες με κόμμα, π.χ. 28/10/2026, 23/12/2026-06/01/2027. Το PDF δεν αναφέρει τις διακοπές Χριστουγέννων, πρόσθεσέ τις αν ισχύουν.", 11, false, "#7f8c8d"));

            // --- Κουμπιά ---
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };

            var cancel = new Border
            {
                Background = Br("#111b24"),
                BorderBrush = Br("#2c3e50"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Child = label("Άκυρο", 13, false, "#bdc3c7")
            };
            cancel.MouseLeftButtonUp += (s1, e1) => { win.DialogResult = false; };

            var ok = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 8, 18, 8),
                Cursor = Cursors.Hand,
                Child = label("Εξαγωγή", 13, true, "#05070a")
            };
            ok.SetResourceReference(Border.BackgroundProperty, "ThemeBrush");
            ok.SetResourceReference(Border.BorderBrushProperty, "ThemeBrush");
            ok.MouseLeftButtonUp += (s2, e2) => { win.DialogResult = true; };

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            win.Content = root;
            bool? result = win.ShowDialog();
            if (result != true) return null;

            var res = new IcsOptions
            {
                Lectures = cbLec.IsEnabled && cbLec.IsChecked == true,
                Labs = cbLab.IsEnabled && cbLab.IsChecked == true,
                Exams = cbExa.IsEnabled && cbExa.IsChecked == true,
                OnlyFiltered = rbFiltered.IsChecked == true,
                HolidaysText = tbHol.Text
            };

            DateTime labStart;
            res.LabsStart = DateTime.TryParseExact(tbLab.Text.Trim(), new[] { "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out labStart)
                ? labStart : ts;
            return res;
        }

        // ======================================================================
        // 6) ΤΟ ΚΟΥΜΠΙ "ΕΞΑΓΩΓΗ .ics"
        // ======================================================================
        private void SchedExportIcs_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var lecDoc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == "lectures");
                var labDoc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == "labs");
                var exaDoc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == "exams");

                SchedParsed lec = lecDoc != null ? GetParsedSchedule(lecDoc) : null;
                SchedParsed lab = labDoc != null ? GetParsedSchedule(labDoc) : null;
                SchedParsed exa = exaDoc != null ? GetParsedSchedule(exaDoc) : null;

                if (lec == null && lab == null && exa == null)
                {
                    MessageBox.Show(this, "Δεν έχει διαβαστεί ακόμα κανένα πρόγραμμα. Πάτα πρώτα «Ανανέωση» και δες ότι εμφανίζεται το γραφικό πρόγραμμα.",
                        "Εξαγωγή ημερολογίου", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Ημερομηνίες εξαμήνου: προκύπτουν από το πρόγραμμα μαθημάτων
                DateTime ts = DateTime.MinValue, te = DateTime.MinValue;
                bool haveTerm = lec != null
                    && DateTime.TryParseExact(lec.termStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out ts)
                    && DateTime.TryParseExact(lec.termEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out te);

                var allCodes = new HashSet<string>();
                foreach (var p in new[] { lec, lab, exa })
                {
                    if (p == null) continue;
                    foreach (var s in p.sessions) allCodes.Add(s.c);
                }
                var owed = SchedOwedCodes();
                var filtered = SchedVisibleCodes(allCodes, owed);
                filtered.IntersectWith(allCodes);
                var titles = SchedCurriculumTitles();

                var opt = ShowIcsDialog(lec != null, lab != null, exa != null, haveTerm, ts, te, filtered.Count, allCodes.Count);
                if (opt == null) return;
                if (!opt.Lectures && !opt.Labs && !opt.Exams)
                {
                    MessageBox.Show(this, "Δεν επέλεξες τίποτα για εξαγωγή.", "Εξαγωγή ημερολογίου", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var visible = opt.OnlyFiltered ? filtered : allCodes;
                var skip = haveTerm ? IcsParseSkip(opt.HolidaysText, ts, te) : new HashSet<DateTime>();

                int count;
                string ics = BuildIcs(opt, lec, lab, exa, visible, titles, haveTerm, ts, te, skip, out count);
                if (count == 0)
                {
                    MessageBox.Show(this, "Δεν βρέθηκαν συνεδρίες με αυτές τις επιλογές. Δοκίμασε «Όλα τα μαθήματα του προγράμματος».",
                        "Εξαγωγή ημερολογίου", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Αποθήκευση ημερολογίου",
                    Filter = "Αρχείο ημερολογίου (*.ics)|*.ics",
                    DefaultExt = ".ics",
                    FileName = "UOI_Programma.ics"
                };
                if (dlg.ShowDialog(this) != true) return;

                File.WriteAllText(dlg.FileName, ics, new UTF8Encoding(false));
                ScheduleStatus.Text = "📅 Εξήχθησαν " + count + " συμβάντα στο " + Path.GetFileName(dlg.FileName);

                MessageBox.Show(this,
                    "✅ Εξήχθησαν " + count + " συμβάντα στο αρχείο:\n" + dlg.FileName + "\n\n" +
                    "Πώς το εισάγεις:\n" +
                    "• Google Calendar: calendar.google.com → ⚙ Ρυθμίσεις → Εισαγωγή & εξαγωγή → Εισαγωγή.\n" +
                    "• Outlook: Αρχείο → Άνοιγμα και εξαγωγή → Εισαγωγή/Εξαγωγή → «Εισαγωγή αρχείου iCalendar (.ics)».\n\n" +
                    "Συμβουλή: εισήγαγέ το σε ΝΕΟ ημερολόγιο, ώστε να το σβήσεις εύκολα όταν αλλάξει το πρόγραμμα.",
                    "Εξαγωγή ημερολογίου", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogError("SchedExportIcs", ex);
                MessageBox.Show(this, "Η εξαγωγή απέτυχε: " + ex.Message, "Εξαγωγή ημερολογίου", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
