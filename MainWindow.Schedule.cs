using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // SCHEDULE: ανάλυση των PDF προγραμμάτων (μαθήματα / εργαστήρια / εξεταστική)
    // και εμφάνισή τους με δικά μας γραφικά.
    // Απαιτεί το NuGet πακέτο  UglyToad.PdfPig  και το αρχείο  schedule.html
    // ==========================================================================
    public partial class MainWindow
    {
        private const int SchedParserVersion = 1;

        // ----- Μοντέλα -----
        public class SchedSession
        {
            public string c { get; set; } = "";    // κωδικός μαθήματος (π.χ. ΜΥΥ301)
            public string t { get; set; } = "";    // τίτλος όπως γράφεται στο PDF (συντομογραφία)
            public int d { get; set; } = -1;       // 0=Δευτέρα ... 4=Παρασκευή
            public string dt { get; set; } = "";   // ημερομηνία yyyy-MM-dd (μόνο εξετάσεις)
            public string s { get; set; } = "";    // ώρα έναρξης HH:mm
            public string e { get; set; } = "";    // ώρα λήξης HH:mm
            public string r { get; set; } = "";    // αίθουσα(ες)
            public string n { get; set; } = "";    // σημείωση (π.χ. Φροντιστήριο)
        }

        public class SchedParsed
        {
            public int version { get; set; }
            public string kind { get; set; } = "";
            public string title { get; set; } = "";
            public string termStart { get; set; } = "";
            public string termEnd { get; set; } = "";
            public Dictionary<string, string> legend { get; set; } = new Dictionary<string, string>();
            public List<SchedSession> sessions { get; set; } = new List<SchedSession>();
        }

        // Αποθηκεύεται στο save.json (Memory Card)
        public class SchedState
        {
            public string Mode { get; set; } = "";       // owed | all | none | custom
            public List<string> Codes { get; set; } = new List<string>();
            public string Layout { get; set; } = "grid"; // grid | agenda
            public bool Graphic { get; set; } = true;    // γραφική προβολή ή PDF
        }

        private class PdfWord { public string Text = ""; public double X0, X1, Y0, Y1; }

        private class PdfCell
        {
            public double X0, X1, Y0, Y1;
            public string Text = "";
            public List<PdfWord> Words = new List<PdfWord>();
            public double CX { get { return (X0 + X1) / 2.0; } }
        }

        private class PdfRow { public double Y0, Y1; public string Start = "", End = ""; }

        private static readonly Regex SchedTimeRe = new Regex(@"^(\d{1,2}):(\d{2})-(\d{1,2}):(\d{2})$");
        // Κωδικός μαθήματος, με ανοχή σε λατινικά "ομοιόγραμμα" (M, Y, E)
        private static readonly Regex SchedCodeRe = new Regex(@"\b([\u039CM][\u03A5Y][\u03A5\u0395YE])\s?(\d{3})");
        private static readonly string[] SchedDays = { "ΔΕΥΤΕΡΑ", "ΤΡΙΤΗ", "ΤΕΤΑΡΤΗ", "ΠΕΜΠΤΗ", "ΠΑΡΑΣΚΕΥΗ" };

        private bool _schedGraphic = true;
        private bool _schedModeInit = false;
        private bool _schedHooked = false;

        // ======================================================================
        // 1) ΓΕΩΜΕΤΡΙΑ: ανακατασκευή των κελιών του πίνακα από τις γραμμές του PDF
        // ======================================================================
        private static System.Collections.IEnumerable GetPdfPaths(UglyToad.PdfPig.Content.Page page)
        {
            var t = page.GetType();
            var p = t.GetProperty("Paths");
            if (p != null) return (System.Collections.IEnumerable)p.GetValue(page);

            var ea = t.GetProperty("ExperimentalAccess") != null ? t.GetProperty("ExperimentalAccess").GetValue(page) : null;
            if (ea != null)
            {
                var p2 = ea.GetType().GetProperty("Paths");
                if (p2 != null) return (System.Collections.IEnumerable)p2.GetValue(ea);
            }
            throw new NotSupportedException("Η έκδοση του PdfPig δεν δίνει πρόσβαση στις γραμμές του PDF (Paths).");
        }

        // Συλλέγει τις λεπτές σκούρες γραμμές (περιγράμματα): H = οριζόντιες {y, x0, x1}, V = κάθετες {x, y0, y1}
        private static void CollectPdfEdges(UglyToad.PdfPig.Content.Page page, List<double[]> H, List<double[]> V)
        {
            double ph = page.Height;
            foreach (dynamic path in GetPdfPaths(page))
            {
                bool dark = true;
                try
                {
                    bool filled = path.IsFilled;
                    dynamic col = filled ? path.FillColor : path.StrokeColor;
                    if (col != null)
                    {
                        var rgb = col.ToRGBValues();
                        double avg = ((double)rgb.Item1 + (double)rgb.Item2 + (double)rgb.Item3) / 3.0;
                        dark = avg < 0.6;
                    }
                }
                catch { }
                if (!dark) continue;

                var parts = new List<object>();
                try { foreach (object sp in (System.Collections.IEnumerable)path) parts.Add(sp); } catch { }
                if (parts.Count == 0) parts.Add(path);

                foreach (dynamic part in parts)
                {
                    try
                    {
                        dynamic r = part.GetBoundingRectangle();
                        if (r == null) continue;
                        double left = (double)r.Left, right = (double)r.Right;
                        double top = ph - (double)r.Top, bot = ph - (double)r.Bottom;
                        double w = right - left, h = bot - top;
                        if (Math.Min(w, h) >= 2.0 || Math.Max(w, h) < 3.0) continue;
                        if (w >= h) H.Add(new double[] { (top + bot) / 2.0, left, right });
                        else V.Add(new double[] { (left + right) / 2.0, top, bot });
                    }
                    catch { }
                }
            }
        }

        private static List<double> PdfCluster(IEnumerable<double> vals, double tol)
        {
            var sorted = vals.OrderBy(v => v).ToList();
            var groups = new List<List<double>>();
            foreach (double v in sorted)
            {
                if (groups.Count > 0 && v - groups[groups.Count - 1].Last() <= tol) groups[groups.Count - 1].Add(v);
                else groups.Add(new List<double> { v });
            }
            return groups.Select(g => g.Average()).ToList();
        }

        private static double PdfUnionLength(List<double[]> intervals)
        {
            intervals.Sort((a1, b1) => a1[0].CompareTo(b1[0]));
            double total = 0, cs = double.NaN, ce = double.NaN;
            foreach (var iv in intervals)
            {
                if (double.IsNaN(cs)) { cs = iv[0]; ce = iv[1]; }
                else if (iv[0] <= ce) ce = Math.Max(ce, iv[1]);
                else { total += ce - cs; cs = iv[0]; ce = iv[1]; }
            }
            if (!double.IsNaN(cs)) total += ce - cs;
            return total;
        }

        private static string PdfJoinWords(List<PdfWord> ws)
        {
            var sorted = ws.OrderBy(w => Math.Round(w.Y0 / 3.0)).ThenBy(w => w.X0).ToList();
            var lines = new List<string>();
            var cur = new List<string>();
            double lineTop = double.NaN;
            foreach (var w in sorted)
            {
                if (double.IsNaN(lineTop) || Math.Abs(w.Y0 - lineTop) <= 3.0)
                {
                    cur.Add(w.Text);
                    if (double.IsNaN(lineTop)) lineTop = w.Y0;
                }
                else
                {
                    lines.Add(string.Join(" ", cur));
                    cur = new List<string> { w.Text };
                    lineTop = w.Y0;
                }
            }
            if (cur.Count > 0) lines.Add(string.Join(" ", cur));
            return Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim();
        }

        // Επιστρέφει όλα τα (συγχωνευμένα) ορθογώνια κελιά της σελίδας, με το κείμενό τους
        private List<PdfCell> BuildPdfCells(UglyToad.PdfPig.Content.Page page, out string pageText)
        {
            double ph = page.Height;
            var H = new List<double[]>();
            var V = new List<double[]>();
            CollectPdfEdges(page, H, V);

            var words = new List<PdfWord>();
            foreach (var w in page.GetWords())
            {
                var bb = w.BoundingBox;
                words.Add(new PdfWord { Text = w.Text, X0 = bb.Left, X1 = bb.Right, Y0 = ph - bb.Top, Y1 = ph - bb.Bottom });
            }
            pageText = NormalizeKey(string.Join(" ", words.Select(w => w.Text)));

            var cells = new List<PdfCell>();
            var xs = PdfCluster(V.Select(v => v[0]), 1.5);
            var ys = PdfCluster(H.Select(h => h[0]), 1.5);
            if (xs.Count < 2 || ys.Count < 2) return cells;

            int nx = xs.Count - 1, ny = ys.Count - 1;
            var parent = new int[nx * ny];
            for (int k = 0; k < parent.Length; k++) parent[k] = k;

            Func<int, int> find = a0 =>
            {
                while (parent[a0] != a0) { parent[a0] = parent[parent[a0]]; a0 = parent[a0]; }
                return a0;
            };
            Action<int, int> union = (a2, b2) =>
            {
                int ra = find(a2), rb = find(b2);
                if (ra != rb) parent[ra] = rb;
            };

            // Υπάρχει κάθετο τοίχωμα στη θέση xs[i] κατά μήκος της γραμμής j;
            Func<int, int, bool> vwall = (i, j) =>
            {
                double x = xs[i], y0 = ys[j], y1 = ys[j + 1];
                var iv = new List<double[]>();
                foreach (var e in V)
                {
                    if (Math.Abs(e[0] - x) > 1.5) continue;
                    double lo = Math.Max(e[1], y0), hi = Math.Min(e[2], y1);
                    if (hi > lo) iv.Add(new double[] { lo, hi });
                }
                return PdfUnionLength(iv) >= 0.5 * (y1 - y0);
            };
            // Υπάρχει οριζόντιο τοίχωμα στη θέση ys[j] κατά μήκος της στήλης i;
            Func<int, int, bool> hwall = (i, j) =>
            {
                double y = ys[j], x0 = xs[i], x1 = xs[i + 1];
                var iv = new List<double[]>();
                foreach (var e in H)
                {
                    if (Math.Abs(e[0] - y) > 1.5) continue;
                    double lo = Math.Max(e[1], x0), hi = Math.Min(e[2], x1);
                    if (hi > lo) iv.Add(new double[] { lo, hi });
                }
                return PdfUnionLength(iv) >= 0.5 * (x1 - x0);
            };

            // Ενώνουμε γειτονικά μοναδιαία κελιά που δεν χωρίζονται από γραμμή (συγχωνευμένα κελιά)
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    if (i + 1 < nx && !vwall(i + 1, j)) union(j * nx + i, j * nx + i + 1);
                    if (j + 1 < ny && !hwall(i, j + 1)) union(j * nx + i, (j + 1) * nx + i);
                }
            }

            var groups = new Dictionary<int, List<int[]>>();
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int root = find(j * nx + i);
                    if (!groups.ContainsKey(root)) groups[root] = new List<int[]>();
                    groups[root].Add(new int[] { i, j });
                }
            }

            foreach (var g in groups.Values)
            {
                double gx0 = g.Min(u => xs[u[0]]), gx1 = g.Max(u => xs[u[0] + 1]);
                double gy0 = g.Min(u => ys[u[1]]), gy1 = g.Max(u => ys[u[1] + 1]);
                double area = g.Sum(u => (xs[u[0] + 1] - xs[u[0]]) * (ys[u[1] + 1] - ys[u[1]]));
                // Τα πραγματικά κελιά είναι πάντα ορθογώνια. Η "περιοχή" έξω από τους πίνακες δεν είναι.
                if (area < 0.98 * (gx1 - gx0) * (gy1 - gy0)) continue;

                var cell = new PdfCell { X0 = gx0, X1 = gx1, Y0 = gy0, Y1 = gy1 };
                cell.Words = words.Where(w =>
                {
                    double cx = (w.X0 + w.X1) / 2.0, cy = (w.Y0 + w.Y1) / 2.0;
                    return cx >= gx0 && cx <= gx1 && cy >= gy0 && cy <= gy1;
                }).ToList();
                cell.Text = PdfJoinWords(cell.Words);
                cells.Add(cell);
            }
            return cells;
        }

        // ======================================================================
        // 2) ΕΡΜΗΝΕΙΑ ΤΩΝ ΚΕΛΙΩΝ
        // ======================================================================
        private static string SchedNormCode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char ch in s.ToUpperInvariant())
            {
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(ch == 'M' ? '\u039C' : ch == 'Y' ? '\u03A5' : ch == 'E' ? '\u0395' : ch);
            }
            return sb.ToString();
        }

        private static bool SchedIsCourseCode(string c)
        {
            return !string.IsNullOrEmpty(c) && Regex.IsMatch(c, @"^\u039C\u03A5[\u03A5\u0395]\d{3}$");
        }

        private static string SchedHHmm(string h, string m) { return int.Parse(h).ToString("00") + ":" + m; }

        private static string SchedIso(string y, string m, string d)
        {
            return y + "-" + int.Parse(m).ToString("00") + "-" + int.Parse(d).ToString("00");
        }

        private static List<PdfRow> PdfTimeRows(List<PdfCell> cells)
        {
            var res = new List<PdfRow>();
            foreach (var c in cells)
            {
                var m = SchedTimeRe.Match(c.Text.Replace(" ", ""));
                if (!m.Success) continue;
                res.Add(new PdfRow
                {
                    Y0 = c.Y0,
                    Y1 = c.Y1,
                    Start = SchedHHmm(m.Groups[1].Value, m.Groups[2].Value),
                    End = SchedHHmm(m.Groups[3].Value, m.Groups[4].Value)
                });
            }
            return res;
        }

        private static PdfRow PdfRowOf(double y, List<PdfRow> rows)
        {
            foreach (var r in rows) if (r.Y0 <= y && y <= r.Y1) return r;
            return null;
        }

        // Από πού μέχρι πού (ώρες) απλώνεται ένα κελί
        private static bool PdfTimeSpan(PdfCell c, List<PdfRow> rows, out string start, out string end)
        {
            start = null; end = null;
            var a = PdfRowOf(c.Y0 + 1.0, rows);
            var b = PdfRowOf(c.Y1 - 1.0, rows);
            if (a == null || b == null) return false;
            start = a.Start; end = b.End;
            return true;
        }

        // Η πιο κοντινή επικεφαλίδα πάνω από το κελί, που καλύπτει τη στήλη του
        private static PdfCell PdfNearestAbove(PdfCell c, List<PdfCell> cands)
        {
            PdfCell best = null;
            foreach (var h in cands)
            {
                if (h.X0 - 1 <= c.CX && c.CX <= h.X1 + 1 && h.Y1 <= c.Y0 + 2)
                {
                    if (best == null || h.Y1 > best.Y1) best = h;
                }
            }
            return best;
        }

        // "ΜΥΥ301- ΑΝΑΠΤΥΞΗ ΛΟΓΙΣΜΙΚΟΥ (Αιθ Ι5)"  ->  κωδικός, τίτλος, αίθουσα
        private static bool SchedParseCellText(string text, out string code, out string title, out string room)
        {
            code = null; title = null; room = null;
            string work = text;
            string foundRoom = null;

            foreach (Match m in Regex.Matches(work, @"\(([^)]*)\)"))
            {
                string inner = NormalizeKey(m.Groups[1].Value);
                if (inner.Contains("ΑΙΘ") || inner.Contains("ΑΜΦ") || inner.Contains("ΕΡΓ"))
                {
                    foundRoom = m.Groups[1].Value.Trim();
                    work = work.Replace(m.Value, " ");
                    break;
                }
            }

            var cm = SchedCodeRe.Match(work);
            if (!cm.Success) return false;

            code = SchedNormCode(cm.Groups[1].Value + cm.Groups[2].Value);
            string rest = work.Substring(0, cm.Index) + " " + work.Substring(cm.Index + cm.Length);
            rest = Regex.Replace(rest, @"^[\s\-\u2013]+|[\s\-\u2013]+$", "");
            title = Regex.Replace(rest, @"\s+", " ").Trim();
            room = foundRoom ?? "";
            return true;
        }

        // ----- Πρόγραμμα μαθημάτων ή εργαστηρίων (εβδομαδιαίο πλέγμα) -----
        private void ParseWeeklyCells(List<PdfCell> cells, string kind, SchedParsed result)
        {
            var rows = PdfTimeRows(cells);
            var dayHeads = cells.Where(c => c.Text.Length > 0 && Array.IndexOf(SchedDays, NormalizeKey(c.Text)) >= 0).ToList();

            // Ημερομηνίες εξαμήνου (στο πλαίσιο πληροφοριών των μαθημάτων)
            foreach (var c in cells)
            {
                string n = NormalizeKey(c.Text);
                var m1 = Regex.Match(n, @"ΕΝΑΡΞΗ ΜΑΘΗΜΑΤΩΝ:?\s*(\d{1,2})/(\d{1,2})/(\d{4})");
                var m2 = Regex.Match(n, @"ΛΗΞΗ ΜΑΘΗΜΑΤΩΝ:?\s*(\d{1,2})/(\d{1,2})/(\d{4})");
                if (m1.Success) result.termStart = SchedIso(m1.Groups[3].Value, m1.Groups[2].Value, m1.Groups[1].Value);
                if (m2.Success) result.termEnd = SchedIso(m2.Groups[3].Value, m2.Groups[2].Value, m2.Groups[1].Value);
            }

            // Κελιά-επικεφαλίδες (π.χ. ΠΕΛΣ, ΠΕΠ-Ι, ΑΙΘΟΥΣΑ Ι5): σύντομο κείμενο, χαμηλό ύψος
            var hdr = cells.Where(c => c.Text.Length > 0 && c.Text.Length <= 14 && (c.Y1 - c.Y0) < 40
                                       && !SchedCodeRe.IsMatch(c.Text)
                                       && !SchedTimeRe.IsMatch(c.Text.Replace(" ", ""))
                                       && Array.IndexOf(SchedDays, NormalizeKey(c.Text)) < 0).ToList();

            // Υπόμνημα εργαστηρίων: ΠΕΠ-Ι = Προπτυχιακό Εργαστήριο Προγραμματισμού Ι κ.λπ.
            if (kind == "labs")
            {
                var hs = new HashSet<string>(hdr.Select(h => h.Text));
                foreach (var c in cells)
                {
                    if (!NormalizeKey(c.Text).StartsWith("ΠΡΟΠΤΥΧΙΑΚΟ ΕΡΓΑΣΤΗΡΙΟ")) continue;
                    var toks = c.Text.Split(' ');
                    string ab = toks.FirstOrDefault(t => hs.Contains(t));
                    if (ab != null) result.legend[ab] = string.Join(" ", toks.Where(t => t != ab));
                }
            }

            foreach (var c in cells)
            {
                if (c.Text.Length == 0) continue;
                string code, title, room;
                if (!SchedParseCellText(c.Text, out code, out title, out room)) continue;

                var dh = PdfNearestAbove(c, dayHeads);
                if (dh == null) continue;
                int day = Array.IndexOf(SchedDays, NormalizeKey(dh.Text));

                string start, end;
                if (!PdfTimeSpan(c, rows, out start, out end)) continue;

                // Εργαστήρια: η αίθουσα προκύπτει από τις επικεφαλίδες των στηλών που καλύπτει το κελί
                if (kind == "labs")
                {
                    var cand = hdr.Where(h => h.Y1 <= c.Y0 + 2 && h.CX >= c.X0 - 1 && h.CX <= c.X1 + 1).ToList();
                    room = "";
                    if (cand.Count > 0)
                    {
                        double ymax = cand.Max(h => h.Y1);
                        room = string.Join(", ", cand.Where(h => Math.Abs(h.Y1 - ymax) < 2).OrderBy(h => h.X0).Select(h => h.Text));
                    }
                }

                title = Regex.Replace(title, @"\([^)]*ΦΡΟΝΤ[^)]*\)", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, @"\s+", " ").Trim();

                // Φροντιστήριο μέσα στο ίδιο κελί: χωρίζουμε σε διάλεξη + φροντιστήριο
                if (kind == "lectures")
                {
                    var tut = c.Words.FirstOrDefault(w => NormalizeKey(w.Text).Contains("ΦΡΟΝΤ"));
                    if (tut != null)
                    {
                        var row = PdfRowOf((tut.Y0 + tut.Y1) / 2.0, rows);
                        if (row != null && row.Y0 > c.Y0 + 1)
                        {
                            result.sessions.Add(new SchedSession { c = code, t = title, d = day, s = row.Start, e = row.End, r = room, n = "Φροντιστήριο" });
                            end = row.Start;
                        }
                    }
                }

                result.sessions.Add(new SchedSession { c = code, t = title, d = day, s = start, e = end, r = room });
            }
        }

        // ----- Πρόγραμμα εξεταστικής -----
        private void ParseExamCells(List<PdfCell> cells, string pageText, SchedParsed result)
        {
            var rows = PdfTimeRows(cells);
            var dateRe = new Regex(@"(ΔΕΥΤΕΡΑ|ΤΡΙΤΗ|ΤΕΤΑΡΤΗ|ΠΕΜΠΤΗ|ΠΑΡΑΣΚΕΥΗ)\s*(\d{1,2})\s*/\s*(\d{1,2})");

            var heads = new List<Tuple<PdfCell, Match>>();
            foreach (var c in cells)
            {
                if (c.Text.Length == 0 || SchedCodeRe.IsMatch(c.Text)) continue;
                var m = dateRe.Match(NormalizeKey(c.Text));
                if (m.Success) heads.Add(Tuple.Create(c, m));
            }
            if (heads.Count == 0) return;

            // Το έτος προκύπτει από τον τίτλο της σελίδας ("ΣΕΠΤΕΜΒΡΙΟΥ 2026")
            int titleYear = DateTime.Now.Year;
            var ym = Regex.Match(pageText, @"(20\d{2})");
            if (ym.Success) titleYear = int.Parse(ym.Groups[1].Value);

            // Αν οι μήνες "γυρίζουν" (π.χ. Δεκέμβριος -> Ιανουάριος), οι πρώτες ημερομηνίες ανήκουν στο προηγούμενο έτος
            var order = heads.OrderBy(h => Math.Round(h.Item1.Y0)).ThenBy(h => h.Item1.X0).ToList();
            var months = order.Select(h => int.Parse(h.Item2.Groups[3].Value)).ToList();
            int roll = -1;
            for (int i = 1; i < months.Count; i++)
            {
                if (months[i] < months[i - 1]) { roll = i; break; }
            }
            var yearOf = new Dictionary<PdfCell, int>();
            for (int i = 0; i < order.Count; i++)
            {
                yearOf[order[i].Item1] = roll < 0 ? titleYear : (i >= roll ? titleYear : titleYear - 1);
            }

            foreach (var c in cells)
            {
                if (c.Text.Length == 0) continue;
                string code, title, room;
                if (!SchedParseCellText(c.Text, out code, out title, out room)) continue;

                Tuple<PdfCell, Match> best = null;
                foreach (var h in heads)
                {
                    if (h.Item1.X0 - 1 <= c.CX && c.CX <= h.Item1.X1 + 1 && h.Item1.Y1 <= c.Y0 + 2)
                    {
                        if (best == null || h.Item1.Y1 > best.Item1.Y1) best = h;
                    }
                }
                if (best == null) continue;

                var row = PdfRowOf((c.Y0 + c.Y1) / 2.0, rows);
                if (row == null) continue;

                string dt = yearOf[best.Item1] + "-" + int.Parse(best.Item2.Groups[3].Value).ToString("00") + "-" + int.Parse(best.Item2.Groups[2].Value).ToString("00");
                int dayIdx = Array.IndexOf(SchedDays, best.Item2.Groups[1].Value);
                result.sessions.Add(new SchedSession { c = code, t = title, d = dayIdx, dt = dt, s = row.Start, e = row.End });
            }
        }

        private SchedParsed ParseSchedulePdf(string kind, string path)
        {
            var result = new SchedParsed { version = SchedParserVersion, kind = kind };
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                foreach (var page in pdf.GetPages())
                {
                    string pageText;
                    var cells = BuildPdfCells(page, out pageText);
                    if (kind == "exams") ParseExamCells(cells, pageText, result);
                    else ParseWeeklyCells(cells, kind, result);
                }
            }
            return result;
        }

        // ======================================================================
        // 3) CACHE: ανάλυση μόνο όταν αλλάζει το PDF
        // ======================================================================
        private SchedParsed GetParsedSchedule(ScheduleDoc doc)
        {
            try
            {
                string shortHash = doc.Hash.Length >= 8 ? doc.Hash.Substring(0, 8) : doc.Hash;
                string cache = Path.Combine(ScheduleDir, "parsed_" + doc.Kind + "_" + shortHash + "_v" + SchedParserVersion + ".json");
                string pdfPath = Path.Combine(ScheduleDir, doc.FileName);

                if (File.Exists(cache))
                {
                    var cached = JsonSerializer.Deserialize<SchedParsed>(File.ReadAllText(cache));
                    if (cached != null && cached.sessions != null && cached.sessions.Count > 0) return cached;
                }
                if (!File.Exists(pdfPath)) return null;

                var parsed = ParseSchedulePdf(doc.Kind, pdfPath);
                parsed.title = doc.Title;

                File.AppendAllText(Path.Combine(ScheduleDir, "parse_log.txt"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + doc.Kind + ": " + parsed.sessions.Count + " συνεδρίες, "
                    + "εξάμηνο " + parsed.termStart + " -> " + parsed.termEnd + Environment.NewLine);

                if (parsed.sessions.Count == 0) return null;

                File.WriteAllText(cache, JsonSerializer.Serialize(parsed));
                foreach (var f in Directory.GetFiles(ScheduleDir, "parsed_" + doc.Kind + "_*.json"))
                {
                    if (!string.Equals(f, cache, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                return parsed;
            }
            catch (Exception ex)
            {
                LogError("ParseSchedule " + doc.Kind, ex);
                return null;
            }
        }

        // ======================================================================
        // 4) ΓΡΑΦΙΚΗ ΠΡΟΒΟΛΗ
        // ======================================================================
        private string BuildScheduleGraphicHtml(string kind)
        {
            var doc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == kind);
            if (doc == null) return null;

            var parsed = GetParsedSchedule(doc);
            if (parsed == null || parsed.sessions.Count == 0) return null;

            string templatePath = Path.Combine(AppContext.BaseDirectory, "schedule.html");
            if (!File.Exists(templatePath))
            {
                LogError("Schedule", new FileNotFoundException("Λείπει το αρχείο schedule.html από τον φάκελο της εφαρμογής: " + templatePath));
                return null;
            }
            string tpl = File.ReadAllText(templatePath, Encoding.UTF8);

            // Πλήρη ονόματα μαθημάτων από το πρόγραμμα σπουδών (με βάση τον κωδικό)
            var curr = new Dictionary<string, object>();
            foreach (var m in GetMissingCoursesList(new HashSet<string>()))
            {
                string code = SchedNormCode(m.Code);
                if (SchedIsCourseCode(code) && !curr.ContainsKey(code)) curr[code] = new { t = m.Title };
            }

            // Μαθήματα που ΔΕΝ έχεις περάσει ακόμα
            var owed = new List<string>();
            if (File.Exists(DataFilePath))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var passedKeys = GetPassedGrades(options).Select(g => NormalizeKey(g.CourseId.Title)).ToHashSet();
                owed = GetMissingCoursesList(passedKeys).Select(m => SchedNormCode(m.Code)).Where(SchedIsCourseCode).Distinct().ToList();
            }

            var st = _save.Sched ?? new SchedState();
            string dataJson = JsonSerializer.Serialize(new
            {
                kind = parsed.kind,
                title = doc.Title,
                termStart = parsed.termStart,
                termEnd = parsed.termEnd,
                legend = parsed.legend,
                sessions = parsed.sessions
            });
            string stateJson = JsonSerializer.Serialize(new { mode = st.Mode ?? "", codes = st.Codes ?? new List<string>(), layout = st.Layout ?? "grid" });

            string html = tpl.Replace("__DATA__", dataJson)
                             .Replace("__CURR__", JsonSerializer.Serialize(curr))
                             .Replace("__OWED__", JsonSerializer.Serialize(owed))
                             .Replace("__STATE__", stateJson);
            return ApplyTheme(html);   // η ApplyTheme εφαρμόζει ήδη και το εφέ του Time Machine
        }

        // Εμφανίζει το πρόγραμμα: γραφικά (αν γίνεται) αλλιώς το πρωτότυπο PDF
        private void ShowScheduleView()
        {
            if (ScheduleBrowser.CoreWebView2 == null) return;

            if (!_schedModeInit)
            {
                _schedModeInit = true;
                _schedGraphic = _save.Sched == null || _save.Sched.Graphic;
            }
            HighlightScheduleButtons();
            UpdateSchedModeButtons();

            if (_schedGraphic)
            {
                try
                {
                    string html = BuildScheduleGraphicHtml(_schedCurrentKind);
                    if (html != null)
                    {
                        EnsureScheduleHook();
                        ScheduleBrowser.NavigateToString(html);
                        return;
                    }
                    ScheduleStatus.Text = "⚠️ Δεν ήταν δυνατή η ανάγνωση του προγράμματος. Εμφανίζεται το PDF (δες error.log).";
                }
                catch (Exception ex)
                {
                    LogError("ShowScheduleGraphic", ex);
                    ScheduleStatus.Text = "⚠️ Αποτυχία γραφικής προβολής. Εμφανίζεται το PDF (δες error.log).";
                }
            }
            ShowSchedulePdf(_schedCurrentKind);
        }

        private void EnsureScheduleHook()
        {
            if (_schedHooked) return;
            ScheduleBrowser.WebMessageReceived += (s, e) => HandleSaveMessage(e.TryGetWebMessageAsString());
            _schedHooked = true;
        }

        private void SchedMode_Click(object sender, MouseButtonEventArgs e)
        {
            _schedGraphic = (string)((FrameworkElement)sender).Tag == "graphic";
            if (_save.Sched == null) _save.Sched = new SchedState();
            _save.Sched.Graphic = _schedGraphic;
            SaveState();
            ShowScheduleView();
        }

        private void UpdateSchedModeButtons()
        {
            Action<Border, bool> mark = (b, on) =>
            {
                if (on)
                {
                    // Δυναμική αναφορά: το χρώμα ακολουθεί αυτόματα κάθε αλλαγή θέματος
                    b.SetResourceReference(Border.BackgroundProperty, "ThemeBrush");
                    b.SetResourceReference(Border.BorderBrushProperty, "ThemeBrush");
                }
                else
                {
                    b.Background = Br("#111b24");
                    b.BorderBrush = Br("#2c3e50");
                }
                var tb = b.Child as TextBlock;
                if (tb != null) tb.Foreground = on ? Brushes.Black : Br("#bdc3c7");
            };
            mark(BtnSchedGraphic, _schedGraphic);
            mark(BtnSchedPdf, !_schedGraphic);
        }

        private bool _schedStale = false;

        // Καλείται όταν αλλάζει κάτι που επηρεάζει την εμφάνιση του προγράμματος
        // (θέμα, Time Machine, νέοι βαθμοί μετά από συγχρονισμό).
        private void SchedRefreshView()
        {
            UpdateSchedModeButtons();                       // τα κουμπιά να πάρουν αμέσως το νέο χρώμα
            if (!isScheduleLoaded || !_schedGraphic) return;

            if (MainTabControl.SelectedIndex == 6) ShowScheduleView();   // είναι ανοιχτό: ξαναχτίζεται τώρα
            else _schedStale = true;                                     // δεν είναι ανοιχτό: ξαναχτίζεται όταν πας εκεί
        }

        // Καλείται όταν ο χρήστης επιστρέφει στο tab Schedule που έχει ήδη φορτώσει
        private void SchedTabActivated()
        {
            if (!_schedStale) return;
            _schedStale = false;
            ShowScheduleView();
        }

        // Μήνυμα από τη σελίδα: αποθήκευση φίλτρων / προβολής (Memory Card)
        private void HandleSchedMessage(JsonElement root)
        {
            var v = root.GetProperty("value");
            if (_save.Sched == null) _save.Sched = new SchedState();
            _save.Sched.Mode = v.GetProperty("mode").GetString() ?? "";
            _save.Sched.Layout = v.GetProperty("layout").GetString() ?? "grid";
            _save.Sched.Codes = v.GetProperty("codes").EnumerateArray().Select(x => x.GetString()).ToList();
            SaveState();
        }
    }
}
