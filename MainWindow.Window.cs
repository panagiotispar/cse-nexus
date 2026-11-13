using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace StudyHub.Desktop
{
    // ==========================================================================
    // WINDOW: πλήρης οθόνη (F11), κουμπιά παραθύρου στη μπάρα Control Center
    // ==========================================================================
    public partial class MainWindow
    {
        private bool _isFullscreen = false;
        private TextBlock _fsIconCollapsed;     // εικονίδιο στη στενή μπάρα
        private TextBlock _fsTextExpanded;      // κείμενο του κουμπιού στο ανοιχτό πάνελ

        // Τα πλήκτρα που πατάς μέσα σε μια σελίδα WebView2 δεν φτάνουν στην εφαρμογή.
        // Γι' αυτό η σελίδα "πιάνει" το F11 και στέλνει μήνυμα στην εφαρμογή.
        private const string HotkeyScript =
            "(function(){if(window.__uoiHotkey)return;window.__uoiHotkey=true;" +
            "document.addEventListener('keydown',function(e){if(e.key==='F11'){e.preventDefault();" +
            "try{window.chrome.webview.postMessage(JSON.stringify({type:'hotkey',key:'F11'}));}catch(x){}}},true);})();";

        private static bool IsHotkeyMessage(string m)
        {
            return m != null && m.StartsWith("{\"type\":\"hotkey\"");
        }

        // ======================================================================
        // 1) ΠΛΗΡΗΣ ΟΘΟΝΗ
        // ======================================================================
        private void ApplyFullscreen(bool on)
        {
            if (on == _isFullscreen) return;
            _isFullscreen = on;

            if (on)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;       // πρώτα επαναφορά, ώστε να καλύψει και τη γραμμή εργασιών
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Maximized;    // μεγιστοποιημένο παράθυρο με τίτλο και κουμπιά
            }

            if (_stateReady && _save.Fullscreen != on)
            {
                _save.Fullscreen = on;
                SaveState();
            }
            UpdateFullscreenButtons();
        }

        private void ToggleFullscreen()
        {
            ApplyFullscreen(!_isFullscreen);
        }

        private void UpdateFullscreenButtons()
        {
            if (_fsIconCollapsed != null) _fsIconCollapsed.Text = _isFullscreen ? "🗗" : "⛶";
            if (_fsTextExpanded != null) _fsTextExpanded.Text = _isFullscreen ? "🗗 Έξοδος από πλήρη οθόνη (F11)" : "⛶ Πλήρης οθόνη (F11)";
        }

        // F11 όταν το πλήκτρο πατιέται πάνω σε στοιχείο της εφαρμογής (όχι μέσα σε σελίδα WebView2)
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        // ======================================================================
        // 2) F11 ΜΕΣΑ ΣΤΙΣ ΣΕΛΙΔΕΣ WEBVIEW2
        // ======================================================================
        // Καλείται ΠΡΙΝ από την InitializeBrowser. Κάθε WebView2 "οπλίζεται" μόλις αρχικοποιηθεί.
        private void WireFullscreenHotkeys()
        {
            var browsers = new[]
            {
                SplashBrowser, TypewriterBrowser, BaseCampBrowser, ChartBrowser, SpaceBrowser,
                SimulatorBrowser, PomodoroBrowser, RadioBrowser, CoopBrowser, ScheduleBrowser
            };

            foreach (var browser in browsers)
            {
                var wv = browser;
                wv.CoreWebView2InitializationCompleted += async (s, e) =>
                {
                    if (!e.IsSuccess) return;
                    try
                    {
                        // Για κάθε σελίδα που θα φορτώσει από εδώ και πέρα...
                        await wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HotkeyScript);
                        // ...και για τη σελίδα που τυχόν έχει ήδη φορτώσει
                        await wv.CoreWebView2.ExecuteScriptAsync(HotkeyScript);

                        wv.CoreWebView2.WebMessageReceived += (s2, e2) =>
                        {
                            try
                            {
                                if (IsHotkeyMessage(e2.TryGetWebMessageAsString())) ToggleFullscreen();
                            }
                            catch { }
                        };
                    }
                    catch (Exception ex) { LogError("WireFullscreenHotkeys", ex); }
                };
            }
        }

        // ======================================================================
        // 3) ΚΟΥΜΠΙΑ ΠΑΡΑΘΥΡΟΥ ΣΤΟ CONTROL CENTER (χτίζονται από κώδικα, δεν χρειάζεται XAML)
        // ======================================================================
        private void InitWindowControls()
        {
            // --- Στενή μπάρα: τρία εικονίδια στο τέλος ---
            Func<string, string, Action, double, Border> iconBtn = (glyph, tip, action, topMargin) =>
            {
                var tb = new TextBlock
                {
                    Text = glyph,
                    FontSize = 18,
                    Foreground = Br("#bdc3c7"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var b = new Border
                {
                    Width = 40,
                    Height = 40,
                    CornerRadius = new CornerRadius(10),
                    Background = Br("#111b24"),
                    Margin = new Thickness(0, topMargin, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                    Tag = tb,
                    Child = tb
                };
                b.MouseLeftButtonUp += (s, e) => action();
                return b;
            };

            var fsBtn = iconBtn("⛶", "Πλήρης οθόνη (F11)", ToggleFullscreen, 30);
            _fsIconCollapsed = (TextBlock)fsBtn.Tag;
            RailCollapsedView.Children.Add(fsBtn);
            RailCollapsedView.Children.Add(iconBtn("—", "Ελαχιστοποίηση", () => { WindowState = WindowState.Minimized; }, 10));
            RailCollapsedView.Children.Add(iconBtn("✕", "Έξοδος από την εφαρμογή", () => { Close(); }, 10));

            // --- Ανοιχτό πάνελ: κάρτα "Παράθυρο" στο τέλος ---
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
                    Text = "🖥️ ΠΑΡΑΘΥΡΟ",
                    Foreground = Br("#bdc3c7"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold
                });

                var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };

                Func<string, string, string, Action, TextBlock> pill = (text, fg, bg, action) =>
                {
                    var tb = new TextBlock { Text = text, Foreground = Br(fg), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
                    var b = new Border
                    {
                        Background = Br(bg),
                        BorderBrush = Br(fg == "#e74c3c" ? "#e74c3c" : "#2c3e50"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 8, 6),
                        Cursor = Cursors.Hand,
                        Child = tb
                    };
                    b.MouseLeftButtonUp += (s, e) => action();
                    wrap.Children.Add(b);
                    return tb;
                };

                _fsTextExpanded = pill("⛶ Πλήρης οθόνη (F11)", "#bdc3c7", "#111b24", ToggleFullscreen);
                pill("— Ελαχιστοποίηση", "#bdc3c7", "#111b24", () => { WindowState = WindowState.Minimized; });
                pill("✕ Έξοδος", "#e74c3c", "#1f0d10", () => { Close(); });

                sp.Children.Add(wrap);
                card.Child = sp;
                panel.Children.Add(card);
            }

            UpdateFullscreenButtons();
        }
    }
}
