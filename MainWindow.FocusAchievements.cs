using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // FOCUS ACHIEVEMENTS: επιτεύγματα από το Study Log, για τα Legendary Achievements του Base Camp.
    // Χρησιμοποιεί το FocusStore (MainWindow.Focus.cs) και τους τίτλους μαθημάτων (MainWindow.ScheduleExport.cs).
    // ==========================================================================
    public partial class MainWindow
    {
        // Δευτέρα της εβδομάδας στην οποία ανήκει η ημερομηνία
        private static DateTime FocusWeekStart(DateTime d)
        {
            return d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        }

        private static Dictionary<string, string> FocusBadge(string icon, string title, string desc)
        {
            return new Dictionary<string, string> { { "icon", icon }, { "title", title }, { "desc", desc } };
        }

        // Ποια επιτεύγματα προκύπτουν από μια λίστα ολοκληρωμένων κύκλων μελέτης
        private List<Dictionary<string, string>> FocusAchievementsFor(IList<StudySession> sessions, Dictionary<string, string> titles)
        {
            var res = new List<Dictionary<string, string>>();
            if (sessions == null || sessions.Count == 0) return res;

            int cycles = sessions.Count;

            // Λεπτά ανά ημέρα
            var byDay = new Dictionary<DateTime, int>();
            foreach (var s in sessions)
            {
                DateTime d;
                if (!DateTime.TryParseExact(s.d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                byDay[d] = (byDay.ContainsKey(d) ? byDay[d] : 0) + s.m;
            }

            // Καλύτερη εβδομάδα (Δευτέρα έως Κυριακή)
            int bestWeek = byDay.GroupBy(kv => FocusWeekStart(kv.Key)).Select(g => g.Sum(x => x.Value)).DefaultIfEmpty(0).Max();

            // Μεγαλύτερο σερί συνεχόμενων ημερών με κύκλο
            int bestStreak = 0, runLen = 0;
            DateTime prev = DateTime.MinValue;
            foreach (var day in byDay.Keys.OrderBy(x => x))
            {
                runLen = (prev != DateTime.MinValue && (day - prev).Days == 1) ? runLen + 1 : 1;
                if (runLen > bestStreak) bestStreak = runLen;
                prev = day;
            }

            // Το μάθημα με τα περισσότερα λεπτά (οι κύκλοι "Γενικό" δεν μετράνε)
            var topCourse = sessions.Where(s => !string.IsNullOrEmpty(s.c))
                .GroupBy(s => s.c)
                .Select(g => new { Code = g.Key, Min = g.Sum(x => x.m) })
                .OrderByDescending(x => x.Min)
                .FirstOrDefault();

            if (cycles >= 1) res.Add(FocusBadge("🤿", "Πρώτη Βουτιά", "Ολοκλήρωσες τον πρώτο σου κύκλο μελέτης!"));
            if (cycles >= 25) res.Add(FocusBadge("🌊", "Deep Diver", "25 ολοκληρωμένοι κύκλοι μελέτης!"));
            if (bestWeek >= 300) res.Add(FocusBadge("⚡", "Εβδομάδα Φωτιά", "5+ ώρες μελέτης μέσα σε μία εβδομάδα!"));
            if (bestStreak >= 7) res.Add(FocusBadge("📅", "Σερί 7", "Μελέτη 7 ημέρες στη σειρά!"));
            if (topCourse != null && topCourse.Min >= 600)
            {
                string name = (titles != null && titles.ContainsKey(topCourse.Code)) ? titles[topCourse.Code] : topCourse.Code;
                res.Add(FocusBadge("🎯", "Ειδικός Μαθήματος", "10+ ώρες μελέτης στο " + name + "!"));
            }
            if (cycles >= 100) res.Add(FocusBadge("🏆", "Master of Focus", "100 κύκλοι μελέτης (50+ ώρες)!"));

            return res;
        }

        // Για το Base Camp: τα επιτεύγματα που έχεις ξεκλειδώσει τώρα.
        // Με το Time Machine ενεργό δεν εμφανίζονται, γιατί οι κύκλοι δεν αντιστοιχούν σε εξεταστικές περιόδους του παρελθόντος.
        private List<Dictionary<string, string>> FocusAchievements()
        {
            var none = new List<Dictionary<string, string>>();
            if (_tmOn) return none;
            try
            {
                var sessions = LoadFocusStore().sessions;
                if (sessions == null || sessions.Count == 0) return none;
                return FocusAchievementsFor(sessions, SchedCurriculumTitles());
            }
            catch (Exception ex)
            {
                LogError("FocusAchievements", ex);
                return none;
            }
        }

        // Καλείται μετά την αποθήκευση ενός νέου κύκλου: αν ξεκλείδωσε κάτι, ειδοποιεί και ξαναχτίζει το Base Camp
        private void OnFocusSessionSaved()
        {
            try
            {
                var sessions = LoadFocusStore().sessions;
                if (sessions == null || sessions.Count == 0) return;

                var titles = SchedCurriculumTitles();
                var before = FocusAchievementsFor(sessions.Take(sessions.Count - 1).ToList(), titles).Select(x => x["title"]).ToList();
                var after = FocusAchievementsFor(sessions, titles);
                var added = after.Where(x => !before.Contains(x["title"])).ToList();
                if (added.Count == 0) return;

                isBaseCampLoaded = false;   // θα ξαναχτιστεί την επόμενη φορά που θα ανοίξεις το Base Camp
                FocusToast("🏅 Νέο επίτευγμα: " + string.Join(" · ", added.Select(x => x["icon"] + " " + x["title"])));
            }
            catch (Exception ex) { LogError("OnFocusSessionSaved", ex); }
        }

        private async void FocusToast(string msg)
        {
            try
            {
                if (PomodoroBrowser.CoreWebView2 != null)
                    await PomodoroBrowser.ExecuteScriptAsync("window.showToast&&window.showToast(" + JsonSerializer.Serialize(msg) + ")");
            }
            catch (Exception ex) { LogError("FocusToast", ex); }
        }
    }
}
