using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    // AUTH: ασφαλής αποθήκευση κωδικού (DPAPI), οθόνη σύνδεσης, λογαριασμός, αποσύνδεση.
    // Απαιτεί το NuGet πακέτο  System.Security.Cryptography.ProtectedData
    //
    // ΣΗΜΕΙΩΣΗ: αυτό το αρχείο ΑΝΤΙΚΑΘΙΣΤΑ τις μεθόδους LoadVault() και
    // ScannerBrowser_NavigationCompleted(...) του MainWindow.xaml.cs.
    // Πρέπει να σβηστούν από εκεί, αλλιώς θα υπάρχουν δύο φορές.
    // ==========================================================================
    public partial class MainWindow
    {
        public class VaultCred
        {
            public string u { get; set; } = "";
            public string p { get; set; } = "";
        }

        private class LogoutOptions { public bool Password, Session, Data, Progress; }

        private const string ClassWebHome = "https://classweb.uoi.gr/";

        private string _authState = "unknown";     // unknown | connecting | loggedin | loggedout
        private bool _loginAttempted = false;      // έγινε ήδη προσπάθεια σύνδεσης (για να μην κάνουμε βρόχο)
        private bool _loginBusy = false;
        private bool _loginSkipped = false;        // ο χρήστης επέλεξε "συνέχεια χωρίς σύνδεση"
        private bool _pendingRemember = false;     // να αποθηκευτεί ο κωδικός μόλις πετύχει η σύνδεση
        private DispatcherTimer _loginTimeout;
        private string _lastSync = "";

        private const string EmptyDataHtml =
            "<html><body style='background:#05070a;color:#7f8c8d;font-family:Consolas,monospace;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;text-align:center;'>" +
            "<div><div style='font-size:42px;'>🔌</div><div style='margin-top:12px;'>Δεν υπάρχουν τοπικά δεδομένα.<br>Συνδέσου στο ClassWeb για συγχρονισμό.</div></div></body></html>";

        // ======================================================================
        // 1) VAULT: κρυπτογράφηση των Windows (DPAPI), δεμένη με τον λογαριασμό σου στα Windows
        // ======================================================================
        private void LoadVault()
        {
            try
            {
                if (File.Exists(VaultFilePath))
                {
                    string raw = File.ReadAllText(VaultFilePath).Trim();
                    string user = null, pass = null;

                    if (raw.StartsWith("v2:"))
                    {
                        byte[] enc = Convert.FromBase64String(raw.Substring(3));
                        byte[] dec = System.Security.Cryptography.ProtectedData.Unprotect(enc, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                        var cred = JsonSerializer.Deserialize<VaultCred>(Encoding.UTF8.GetString(dec));
                        if (cred != null) { user = cred.u; pass = cred.p; }
                    }
                    else
                    {
                        // Παλιά μορφή (απλό Base64, χωρίς κρυπτογράφηση): διαβάζεται και αναβαθμίζεται αυτόματα
                        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                        var parts = decoded.Split(new[] { "|||" }, StringSplitOptions.None);
                        if (parts.Length == 2)
                        {
                            user = parts[0]; pass = parts[1];
                            SaveVault(user, pass);
                        }
                    }

                    if (!string.IsNullOrEmpty(user) && pass != null)
                    {
                        _savedUser = user;
                        _savedPass = pass;
                        _hasSavedCredentials = true;
                    }
                }
            }
            catch (Exception ex) { LogError("LoadVault", ex); }

            SetAuthState("unknown");
        }

        private void SaveVault(string user, string pass)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(VaultFilePath));
                string json = JsonSerializer.Serialize(new VaultCred { u = user, p = pass });
                byte[] enc = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                File.WriteAllText(VaultFilePath, "v2:" + Convert.ToBase64String(enc));
            }
            catch (Exception ex) { LogError("SaveVault", ex); }
        }

        private void DeleteVault()
        {
            try { if (File.Exists(VaultFilePath)) File.Delete(VaultFilePath); }
            catch (Exception ex) { LogError("DeleteVault", ex); }
        }

        // ======================================================================
        // 2) ΣΥΝΔΕΣΗ ΣΤΟ SSO (ο κωδικός περνά ως ασφαλές JSON string, όχι ως κείμενο μέσα στο script)
        // ======================================================================
        private async Task<bool> SubmitSsoForm(string user, string pass)
        {
            string u = JsonSerializer.Serialize(user ?? "");
            string p = JsonSerializer.Serialize(pass ?? "");
            string js =
                "(function(){" +
                "var u=document.querySelector('input[name=\"username\"]');" +
                "var p=document.querySelector('input[name=\"password\"]');" +
                "if(!u||!p) return 'NOFORM';" +
                "u.value=" + u + "; p.value=" + p + ";" +
                "u.dispatchEvent(new Event('input',{bubbles:true})); p.dispatchEvent(new Event('input',{bubbles:true}));" +
                "var b=document.querySelector('button[type=\"submit\"], input[type=\"submit\"], input[name=\"submit\"]');" +
                "if(b){b.click();}else if(document.forms.length>0){HTMLFormElement.prototype.submit.call(document.forms[0]);}" +
                "return 'OK';})()";

            string res = await ScannerBrowser.ExecuteScriptAsync(js);
            if (res != null && res.Contains("NOFORM"))
            {
                StopLoginTimeout();
                _loginBusy = false;
                _loginAttempted = false;
                SetAuthState("loggedout");
                ShowLoginPanel("Δεν βρέθηκε η φόρμα σύνδεσης. Ίσως άλλαξε η σελίδα του ClassWeb.");
                return false;
            }
            return true;
        }

        private async void ScannerBrowser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                string url = ScannerBrowser.Source != null ? ScannerBrowser.Source.ToString() : "";

                if (url.Contains("sso.uoi.gr"))
                {
                    if (_hasSavedCredentials && !_loginAttempted)
                    {
                        // Αυτόματη σύνδεση με τα στοιχεία που έχουμε
                        _loginAttempted = true;
                        _loginBusy = true;
                        SetAuthState("connecting");
                        StartLoginTimeout();
                        await SubmitSsoForm(_savedUser, _savedPass);
                    }
                    else
                    {
                        // Δεν έχουμε στοιχεία, ή η προσπάθεια απέτυχε (η σελίδα σύνδεσης φορτώθηκε ξανά)
                        bool failed = _loginAttempted;
                        StopLoginTimeout();
                        _loginBusy = false;
                        _loginAttempted = false;
                        if (failed) _hasSavedCredentials = false;
                        SetAuthState("loggedout");
                        if (failed || !_loginSkipped)
                            ShowLoginPanel(failed ? "Η σύνδεση απέτυχε. Έλεγξε το όνομα χρήστη και τον κωδικό." : null);
                    }
                }
                else if (url == ClassWebHome || url.Contains("studentMain"))
                {
                    StopLoginTimeout();
                    _loginBusy = false;
                    _loginAttempted = false;
                    _loginSkipped = false;

                    // Ο κωδικός αποθηκεύεται ΜΟΝΟ αφού επιβεβαιωθεί ότι είναι σωστός
                    if (_pendingRemember && _hasSavedCredentials)
                    {
                        SaveVault(_savedUser, _savedPass);
                        _pendingRemember = false;
                    }

                    SetAuthState("loggedin");
                    HideLoginPanel();

                    if (!_hasAutoExtracted)
                    {
                        _hasAutoExtracted = true;
                        await ExtractClassWebData();
                        _lastSync = DateTime.Now.ToString("HH:mm");
                        SetAuthState("loggedin");
                    }
                }
            }
            catch (Exception ex) { LogError("ScannerNavigation", ex); }
        }

        private void StartLoginTimeout()
        {
            if (_loginTimeout != null) _loginTimeout.Stop();
            _loginTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _loginTimeout.Tick += (s, ev) =>
            {
                _loginTimeout.Stop();
                if (!_loginBusy) return;
                _loginBusy = false;
                _loginAttempted = false;
                SetAuthState("loggedout");
                ShowLoginPanel("Δεν υπήρξε απάντηση από το ClassWeb. Έλεγξε τη σύνδεσή σου και δοκίμασε ξανά.");
            };
            _loginTimeout.Start();
        }

        private void StopLoginTimeout()
        {
            if (_loginTimeout != null) _loginTimeout.Stop();
        }

        // ======================================================================
        // 3) ΟΘΟΝΗ ΣΥΝΔΕΣΗΣ
        // ======================================================================
        // Το πάνελ αντικαθιστά το κεντρικό περιεχόμενο (τα WebView2 δεν επιτρέπουν να ζωγραφιστεί κάτι πάνω τους).
        private void ShowLoginPanel(string error)
        {
            LoginError.Text = error ?? "";
            LoginError.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            LoginBusy.Visibility = Visibility.Collapsed;
            LoginPass.Password = "";
            if (string.IsNullOrEmpty(LoginUser.Text) && !string.IsNullOrEmpty(_savedUser)) LoginUser.Text = _savedUser;

            MainContent.Visibility = Visibility.Collapsed;
            LoginPanel.Visibility = Visibility.Visible;

            if (LoginUser.Text.Length == 0) LoginUser.Focus(); else LoginPass.Focus();
        }

        private void HideLoginPanel()
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            MainContent.Visibility = Visibility.Visible;
        }

        private async void BtnLogin_Click(object sender, MouseButtonEventArgs e)
        {
            await DoLoginAsync();
        }

        private async void LoginPass_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await DoLoginAsync();
        }

        private void BtnLoginSkip_Click(object sender, MouseButtonEventArgs e)
        {
            _loginSkipped = true;
            HideLoginPanel();
        }

        private async Task DoLoginAsync()
        {
            if (_loginBusy) return;

            string user = LoginUser.Text.Trim();
            string pass = LoginPass.Password;
            if (user.Length == 0 || pass.Length == 0)
            {
                LoginError.Text = "Συμπλήρωσε όνομα χρήστη και κωδικό.";
                LoginError.Visibility = Visibility.Visible;
                return;
            }

            _savedUser = user;
            _savedPass = pass;
            _hasSavedCredentials = true;
            _pendingRemember = LoginRemember.IsChecked == true;
            _loginSkipped = false;
            _loginBusy = true;
            _loginAttempted = true;

            LoginError.Visibility = Visibility.Collapsed;
            LoginBusy.Visibility = Visibility.Visible;
            SetAuthState("connecting");
            StartLoginTimeout();

            string url = ScannerBrowser.Source != null ? ScannerBrowser.Source.ToString() : "";
            if (url.Contains("sso.uoi.gr"))
            {
                await SubmitSsoForm(user, pass);
            }
            else if (ScannerBrowser.CoreWebView2 != null)
            {
                // Ο scanner δεν είναι στη σελίδα σύνδεσης: τον πάμε εκεί και η σύνδεση γίνεται αυτόματα
                _loginAttempted = false;
                ScannerBrowser.CoreWebView2.Navigate(ClassWebHome);
            }
        }

        // ======================================================================
        // 4) ΚΑΡΤΑ "ΛΟΓΑΡΙΑΣΜΟΣ" ΣΤΟ CONTROL CENTER
        // ======================================================================
        private void SetAuthState(string state)
        {
            _authState = state;
            if (AuthStatusText == null) return;

            switch (state)
            {
                case "loggedin":
                    AuthDot.Fill = Br("#2ecc71");
                    AuthStatusText.Text = "Συνδεδεμένος στο ClassWeb";
                    break;
                case "connecting":
                    AuthDot.Fill = Br("#f1c40f");
                    AuthStatusText.Text = "Σύνδεση...";
                    break;
                case "loggedout":
                    AuthDot.Fill = Br("#e74c3c");
                    AuthStatusText.Text = "Μη συνδεδεμένος";
                    break;
                default:
                    AuthDot.Fill = Br("#7f8c8d");
                    AuthStatusText.Text = "Έλεγχος σύνδεσης...";
                    break;
            }

            var lines = new List<string>();
            if (_hasSavedCredentials && !string.IsNullOrEmpty(_savedUser)) lines.Add("Χρήστης: " + _savedUser);
            if (File.Exists(VaultFilePath)) lines.Add("🔐 Ο κωδικός είναι αποθηκευμένος (κρυπτογραφημένος)");
            if (state == "loggedin" && _lastSync.Length > 0) lines.Add("Τελευταίος συγχρονισμός: " + _lastSync);
            AuthUserText.Text = string.Join("\n", lines);
            AuthUserText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            BtnLoginOpen.Visibility = state == "loggedout" ? Visibility.Visible : Visibility.Collapsed;
            BtnSyncNow.Visibility = state == "loggedin" ? Visibility.Visible : Visibility.Collapsed;
            BtnLogout.Visibility = (state == "loggedin" || File.Exists(VaultFilePath)) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AuthLogin_Click(object sender, MouseButtonEventArgs e)
        {
            _loginSkipped = false;
            ShowLoginPanel(null);
            string url = ScannerBrowser.Source != null ? ScannerBrowser.Source.ToString() : "";
            if (!url.Contains("sso.uoi.gr") && ScannerBrowser.CoreWebView2 != null)
                ScannerBrowser.CoreWebView2.Navigate(ClassWebHome);
        }

        private void AuthSync_Click(object sender, MouseButtonEventArgs e)
        {
            if (ScannerBrowser.CoreWebView2 == null) return;
            _hasAutoExtracted = false;
            _loginAttempted = false;
            AuthStatusText.Text = "🔄 Συγχρονισμός...";
            ScannerBrowser.CoreWebView2.Navigate(ClassWebHome);
        }

        private async void AuthLogout_Click(object sender, MouseButtonEventArgs e)
        {
            var opt = ShowLogoutDialog();
            if (opt == null) return;
            await PerformLogoutAsync(opt);
        }

        // ======================================================================
        // 5) ΠΑΡΑΘΥΡΟ ΑΠΟΣΥΝΔΕΣΗΣ
        // ======================================================================
        private LogoutOptions ShowLogoutDialog()
        {
            var win = new Window
            {
                Title = "Αποσύνδεση",
                Width = 580,
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

            Func<string, string, bool, bool, CheckBox> option = (title, hint, enabled, isChecked) =>
            {
                var cb = new CheckBox
                {
                    IsEnabled = enabled,
                    IsChecked = isChecked && enabled,
                    Margin = new Thickness(0, 12, 0, 0),
                    Foreground = Br("#ecf0f1")
                };
                var sp = new StackPanel();
                sp.Children.Add(label(title, 13, true, enabled ? "#ecf0f1" : "#5d6d7e"));
                sp.Children.Add(label(hint, 11, false, "#7f8c8d"));
                cb.Content = sp;
                return cb;
            };

            root.Children.Add(label("🔌 Αποσύνδεση", 18, true, "#ecf0f1"));
            root.Children.Add(label("Διάλεξε τι θα σβηστεί από αυτόν τον υπολογιστή.", 12, false, "#7f8c8d"));

            bool hasVault = _hasSavedCredentials || File.Exists(VaultFilePath);
            var cbPass = option("🔐 Σβήσιμο του αποθηκευμένου κωδικού",
                hasVault ? "Η εφαρμογή δεν θα ξανασυνδεθεί αυτόματα. Αν το αφήσεις απενεργοποιημένο, θα ξανασυνδεθεί στην επόμενη εκκίνηση."
                         : "Δεν υπάρχει αποθηκευμένος κωδικός.", hasVault, true);
            var cbSession = option("🌐 Τερματισμός της συνεδρίας ClassWeb",
                "Σβήνει τα cookies του sso.uoi.gr και του classweb.uoi.gr. Θα ζητηθούν ξανά στοιχεία για να συνδεθείς.", true, true);
            var cbData = option("🗑️ Σβήσιμο των τοπικών δεδομένων",
                "Βαθμοί και πρόγραμμα σπουδών. Οι σελίδες θα αδειάσουν μέχρι τον επόμενο συγχρονισμό.", true, false);
            var cbProg = option("♻️ Σβήσιμο της προόδου",
                "Θέματα, Boss milestones, Simulator, αντίπαλος Co-op. Οι σημειώσεις του Study Hub δεν σβήνονται.", true, false);
            root.Children.Add(cbPass);
            root.Children.Add(cbSession);
            root.Children.Add(cbData);
            root.Children.Add(cbProg);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };

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
                Background = Br("#e74c3c"),
                BorderBrush = Br("#e74c3c"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 8, 18, 8),
                Cursor = Cursors.Hand,
                Child = label("Αποσύνδεση", 13, true, "#ffffff")
            };
            ok.MouseLeftButtonUp += (s2, e2) => { win.DialogResult = true; };

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            win.Content = root;
            bool? result = win.ShowDialog();
            if (result != true) return null;

            return new LogoutOptions
            {
                Password = cbPass.IsEnabled && cbPass.IsChecked == true,
                Session = cbSession.IsChecked == true,
                Data = cbData.IsChecked == true,
                Progress = cbProg.IsChecked == true
            };
        }

        // ======================================================================
        // 6) ΕΚΤΕΛΕΣΗ ΑΠΟΣΥΝΔΕΣΗΣ
        // ======================================================================
        private async Task ClearClassWebSessionAsync()
        {
            if (ScannerBrowser.CoreWebView2 == null) return;
            var cm = ScannerBrowser.CoreWebView2.CookieManager;
            var cookies = await cm.GetCookiesAsync(null);
            foreach (var c in cookies)
            {
                if (c.Domain != null && c.Domain.ToLowerInvariant().Contains("uoi.gr")) cm.DeleteCookie(c);
            }
            try { await ScannerBrowser.ExecuteScriptAsync("try{localStorage.clear();sessionStorage.clear();}catch(e){}"); } catch { }
        }

        private void ClearLocalData()
        {
            try
            {
                if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
                string curr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UoiStudyHub", "Curriculum_Dump.json");
                if (File.Exists(curr)) File.Delete(curr);
            }
            catch (Exception ex) { LogError("ClearLocalData", ex); }
        }

        private async Task ResetProgressAsync()
        {
            if (_tmOn) await SetTimeMachineAsync(false);
            _save = new SaveData();
            _unlockedThemes.Clear();
            _activeTheme = "cyan";
            ApplyThemeResources();
            RebuildThemeSwatches();
            SaveState();
        }

        private void ResetHud()
        {
            HudAvatar.Text = "🥚";
            RailAvatar.Text = "🥚";
            HudGpa.Text = "0.00";
            HudRank.Text = "-";
            HudLevel.Text = "LVL 1";
            HudXpText.Text = "0/30 XP";
            HudXpBar.Value = 0;
        }

        // Αδειάζει τις σελίδες που δείχνουν προσωπικά δεδομένα
        private void ClearDataViews()
        {
            var browsers = new[] { BaseCampBrowser, ChartBrowser, SpaceBrowser, SimulatorBrowser, CoopBrowser };
            foreach (var b in browsers)
            {
                if (b != null && b.CoreWebView2 != null) b.NavigateToString(EmptyDataHtml);
            }
            isBaseCampLoaded = false;
            isAnalyticsLoaded = false;
            isSpaceLoaded = false;
            isSimulatorLoaded = false;
            isCoopLoaded = false;
            ResetHud();
        }

        private async Task PerformLogoutAsync(LogoutOptions o)
        {
            try
            {
                StopLoginTimeout();
                _loginBusy = false;

                if (o.Password)
                {
                    DeleteVault();
                    _savedUser = "";
                    _savedPass = "";
                    _hasSavedCredentials = false;
                    _pendingRemember = false;
                }

                if (o.Session)
                {
                    await ClearClassWebSessionAsync();
                    // Χωρίς αυτό, η εφαρμογή θα ξανασυνδεόταν αμέσως με τον κωδικό που έχει στη μνήμη
                    _hasSavedCredentials = false;
                }

                if (o.Progress) await ResetProgressAsync();

                if (o.Data)
                {
                    ClearLocalData();
                    ClearDataViews();
                }
                else if (o.Progress)
                {
                    await RefreshThemedTabs();
                }

                _loginAttempted = false;
                _loginSkipped = false;
                _hasAutoExtracted = false;

                if (o.Session)
                {
                    SetAuthState("loggedout");
                    // Η πλοήγηση θα καταλήξει στη σελίδα SSO και θα εμφανιστεί η οθόνη σύνδεσης
                    if (ScannerBrowser.CoreWebView2 != null) ScannerBrowser.CoreWebView2.Navigate(ClassWebHome);
                }
                else
                {
                    SetAuthState(_authState);
                }
            }
            catch (Exception ex)
            {
                LogError("PerformLogout", ex);
                MessageBox.Show(this, "Η αποσύνδεση δεν ολοκληρώθηκε πλήρως: " + ex.Message, "Αποσύνδεση", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
