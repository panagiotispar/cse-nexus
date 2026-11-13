using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // FOCUS AGENDA: δεδομένα για την κάρτα "Επόμενα" του Focus Core
    // (επόμενο μάθημα, ελεύθερος χρόνος, προσεχείς εξετάσεις).
    // Χρησιμοποιεί τα ίδια μαθήματα που βλέπεις στο Schedule (το ίδιο φίλτρο).
    // ==========================================================================
    public partial class MainWindow
    {
        private string BuildFocusAgendaJson()
        {
            const string empty = "{\"lectures\":[],\"exams\":[],\"termStart\":\"\",\"termEnd\":\"\"}";
            try
            {
                // Αν το tab Schedule δεν έχει ανοίξει ακόμα, διαβάζουμε ό,τι είναι αποθηκευμένο στον δίσκο
                if (_schedMeta == null || _schedMeta.Docs.Count == 0) _schedMeta = LoadScheduleMeta();

                var lecDoc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == "lectures");
                var exaDoc = _schedMeta.Docs.FirstOrDefault(d => d.Kind == "exams");
                SchedParsed lec = lecDoc != null ? GetParsedSchedule(lecDoc) : null;
                SchedParsed exa = exaDoc != null ? GetParsedSchedule(exaDoc) : null;
                if (lec == null && exa == null) return empty;

                // Μόνο τα μαθήματα που επιλέγεις να βλέπεις στο Schedule (π.χ. "Όσα μου λείπουν")
                var allCodes = new HashSet<string>();
                foreach (var p in new[] { lec, exa })
                {
                    if (p == null) continue;
                    foreach (var x in p.sessions) allCodes.Add(x.c);
                }
                var visible = SchedVisibleCodes(allCodes, SchedOwedCodes());
                var titles = SchedCurriculumTitles();
                Func<string, string, string> nameOf = (code, fallback) => titles.ContainsKey(code) ? titles[code] : fallback;

                var lectures = lec == null ? new List<object>() : lec.sessions
                    .Where(x => visible.Contains(x.c))
                    .Select(x => (object)new { c = x.c, t = nameOf(x.c, x.t), d = x.d, s = x.s, e = x.e, r = x.r, n = x.n })
                    .ToList();

                var exams = exa == null ? new List<object>() : exa.sessions
                    .Where(x => visible.Contains(x.c))
                    .Select(x => (object)new { c = x.c, t = nameOf(x.c, x.t), d = x.d, dt = x.dt, s = x.s, e = x.e })
                    .ToList();

                return JsonSerializer.Serialize(new
                {
                    termStart = lec != null ? lec.termStart : "",
                    termEnd = lec != null ? lec.termEnd : "",
                    lectures = lectures,
                    exams = exams
                });
            }
            catch (Exception ex)
            {
                LogError("BuildFocusAgendaJson", ex);
                return empty;
            }
        }
    }
}
