using System.Text;
using System.Text.RegularExpressions;
using BgBtRapprochement.Models;
using BgBtRapprochement.Services;

namespace BgBtRapprochement;

/// <summary>
/// Fenêtre principale, volontairement simple : 2 écrans.
///  1. Connexion : serveur, base, identifiants, comptes fournisseurs (40) et clients (41), puis un seul bouton.
///  2. Résultats : les 3 tableaux empilés (synthèse, anomalies, détail par tiers).
/// Tout le reste (table des écritures, colonnes, période, tolérance, fichier des tiers) est détecté automatiquement.
/// </summary>
public class MainForm : Form
{
    private ReconciliationResult? _result;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _pageConn = new("1. Connexion");
    private readonly TabPage _pageResults = new("2. Résultats");

    // ---------- Connexion ----------
    private readonly TextBox _txtServer = new() { Text = "localhost" };
    private readonly TextBox _txtDatabase = new() { Text = "Bijou" };
    private readonly CheckBox _chkWindows = new() { Text = "Utiliser mon compte Windows (sans mot de passe)", AutoSize = true };
    private readonly TextBox _txtUser = new();
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtSuppliers = new() { Text = "40" };
    private readonly TextBox _txtClients = new() { Text = "41" };
    private readonly Button _btnRun = new() { Text = "Lancer le rapprochement", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Padding = new Padding(10, 4, 10, 4) };
    private readonly Label _lblConn = new() { AutoSize = true, MaximumSize = new Size(700, 0) };

    // ---------- Résultats ----------
    private readonly Label _lblSummary = new() { Dock = DockStyle.Top, Height = 78, Padding = new Padding(6), Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    private readonly DataGridView _gridBalances = NewGrid();
    private readonly DataGridView _gridTiers = NewGrid();
    private readonly DataGridView _gridAnomalies = NewGrid();
    private readonly GroupBox _grpBalances = new() { Text = "1. Comptes : balance générale (BG) comparée à la balance tiers (BT)", Dock = DockStyle.Fill };
    private readonly GroupBox _grpAnomalies = new() { Text = "2. Écritures à contrôler (anomalies)", Dock = DockStyle.Fill };
    private readonly GroupBox _grpTiers = new() { Text = "3. Solde de chaque tiers", Dock = DockStyle.Fill };
    private readonly TextBox _txtFilterTiers = new() { Width = 220 };
    private readonly Button _btnExport = new() { Text = "Exporter en CSV", AutoSize = true, Enabled = false };
    private readonly Button _btnBack = new() { Text = "Nouvelle analyse", AutoSize = true };

    public MainForm()
    {
        Text = "Rapprochement Balance générale / Balance tiers";
        Width = 1200;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        BuildConnectionPage();
        BuildResultsPage();

        _tabs.TabPages.AddRange(new[] { _pageConn, _pageResults });
        Controls.Add(_tabs);

        _chkWindows.CheckedChanged += (_, _) => UpdateAuthFields();
        _btnRun.Click += async (_, _) => await RunAllAsync();
        _btnExport.Click += (_, _) => ExportCsv();
        _btnBack.Click += (_, _) => _tabs.SelectedTab = _pageConn;

        UpdateAuthFields();
        _pageResults.Enabled = false;
        AcceptButton = _btnRun;
    }

    // ==================================================================
    // Construction de l'interface
    // ==================================================================
    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window
    };

    private static TableLayoutPanel NewForm()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
        return t;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control c)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8) };
        c.Margin = new Padding(3, 4, 3, 4);
        if (c is TextBox) c.Dock = DockStyle.Fill;
        t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(lbl, 0, t.RowCount - 1);
        t.Controls.Add(c, 1, t.RowCount - 1);
    }

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };

    private void BuildConnectionPage()
    {
        var t = NewForm();
        AddRow(t, "", new Label { Text = "Renseignez la connexion puis lancez : le rapprochement s'affiche directement.", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) });
        AddRow(t, "Serveur :", _txtServer);
        AddRow(t, "Base de données :", _txtDatabase);
        AddRow(t, "", _chkWindows);
        AddRow(t, "Identifiant :", _txtUser);
        AddRow(t, "Mot de passe :", _txtPassword);

        AddRow(t, "Comptes fournisseurs :", _txtSuppliers);
        AddRow(t, "", Hint("Début du numéro de compte (40 = tous les comptes 40...). Plusieurs possibles, séparés par une virgule."));
        AddRow(t, "Comptes clients :", _txtClients);
        AddRow(t, "", Hint("Début du numéro de compte (41 = tous les comptes 41...). Plusieurs possibles, séparés par une virgule."));

        AddRow(t, "", _btnRun);
        AddRow(t, "", _lblConn);
        _pageConn.Controls.Add(t);
    }

    private void BuildResultsPage()
    {
        // Les trois tableaux sont empilés et visibles en même temps :
        // synthèse (haut), anomalies (milieu), détail par tiers (bas).
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 36));

        _grpBalances.Controls.Add(_gridBalances);
        _grpAnomalies.Controls.Add(_gridAnomalies);

        var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        filterBar.Controls.Add(new Label { Text = "Rechercher un compte ou un tiers :", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        filterBar.Controls.Add(_txtFilterTiers);
        _grpTiers.Controls.Add(_gridTiers);
        _grpTiers.Controls.Add(filterBar);

        stack.Controls.Add(_grpBalances, 0, 0);
        stack.Controls.Add(_grpAnomalies, 0, 1);
        stack.Controls.Add(_grpTiers, 0, 2);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        bottom.Controls.Add(_btnExport);
        bottom.Controls.Add(_btnBack);

        _pageResults.Controls.Add(stack);
        _pageResults.Controls.Add(bottom);
        _pageResults.Controls.Add(_lblSummary);

        _txtFilterTiers.TextChanged += (_, _) => BindTiers();

        _gridBalances.DataBindingComplete += (_, _) =>
        {
            FormatGrid(_gridBalances);
            foreach (DataGridViewRow r in _gridBalances.Rows)
                if (r.DataBoundItem is ReconciliationRow rr && rr.HasGap)
                    r.DefaultCellStyle.BackColor = Color.MistyRose;
        };
        _gridTiers.DataBindingComplete += (_, _) => FormatGrid(_gridTiers);
        _gridAnomalies.DataBindingComplete += (_, _) =>
        {
            FormatGrid(_gridAnomalies);
            foreach (DataGridViewRow r in _gridAnomalies.Rows)
                if (r.DataBoundItem is AnomalyRow a)
                    r.DefaultCellStyle.BackColor = a.Level == Level.Confirmed ? Color.MistyRose
                                                 : a.Level == Level.Probable ? Color.LemonChiffon : Color.White;
        };
    }

    /// <summary>Affiche le détail par tiers, filtré par le texte saisi (compte ou tiers).</summary>
    private void BindTiers()
    {
        if (_result is null) return;
        var f = _txtFilterTiers.Text.Trim();
        var list = string.IsNullOrEmpty(f)
            ? _result.ThirdPartyBalances
            : _result.ThirdPartyBalances.Where(x =>
                  x.Account.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                  x.ThirdParty.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _gridTiers.DataSource = list;
        _grpTiers.Text = $"3. Solde de chaque tiers ({list.Count})";
    }

    private static void FormatGrid(DataGridView g)
    {
        foreach (DataGridViewColumn col in g.Columns)
        {
            var type = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
            if (type == typeof(decimal))
            {
                col.DefaultCellStyle.Format = "N2";
                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            else if (type == typeof(DateTime))
            {
                col.DefaultCellStyle.Format = "dd/MM/yyyy";
            }
        }
    }

    // ==================================================================
    // Connexion + analyse automatique + rapprochement, en un seul clic
    // ==================================================================
    private void UpdateAuthFields()
    {
        _txtUser.Enabled = !_chkWindows.Checked;
        _txtPassword.Enabled = !_chkWindows.Checked;
    }

    private void SetConnMessage(string text, bool error)
    {
        _lblConn.Text = text;
        _lblConn.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    /// <summary>Lit les préfixes fournisseurs + clients saisis. Renvoie null (avec message) si invalide.</summary>
    private List<string>? ReadPrefixes()
    {
        var result = new List<string>();
        var parts = (_txtSuppliers.Text + "," + _txtClients.Text)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            // seuls lettres/chiffres acceptés pour éviter tout caractère spécial dans le LIKE
            if (!Regex.IsMatch(part, "^[A-Za-z0-9]+$"))
            {
                SetConnMessage($"Numéro de compte invalide : « {part} ». Utilisez uniquement des chiffres.", error: true);
                return null;
            }
            if (!result.Contains(part)) result.Add(part);
        }
        if (result.Count == 0)
        {
            SetConnMessage("Indiquez au moins un début de compte fournisseur (40) ou client (41).", error: true);
            return null;
        }
        return result;
    }

    private async Task RunAllAsync()
    {
        if (string.IsNullOrWhiteSpace(_txtServer.Text) || string.IsNullOrWhiteSpace(_txtDatabase.Text))
        {
            SetConnMessage("Veuillez saisir le serveur et la base de données.", error: true);
            return;
        }
        if (!_chkWindows.Checked && string.IsNullOrWhiteSpace(_txtUser.Text))
        {
            SetConnMessage("Veuillez saisir l'identifiant, ou cocher « Utiliser mon compte Windows ».", error: true);
            return;
        }
        var prefixes = ReadPrefixes();
        if (prefixes is null) return;

        SetBusy(true);
        try
        {
            SetConnMessage("Connexion en cours...", error: false);
            var cs = DatabaseService.BuildConnectionString(new ConnectionSettings
            {
                Server = _txtServer.Text,
                Database = _txtDatabase.Text,
                WindowsAuth = _chkWindows.Checked,
                User = _txtUser.Text,
                Password = _txtPassword.Text,
                TrustServerCertificate = true
            });
            await DatabaseService.TestConnectionAsync(cs);

            SetConnMessage("Connecté. Analyse de la base...", error: false);
            var mapping = await DetectMappingAsync(cs);
            if (mapping is null) return;     // le message d'erreur est déjà affiché

            SetConnMessage("Rapprochement en cours...", error: false);
            var p = new ReconciliationParams
            {
                // Soldes CUMULÉS jusqu'au 31/12 de l'année en cours, comme les balances Sage :
                // on inclut les écritures antérieures au 01/01 (à-nouveaux, « Mouvements au 31/12/N-1 »).
                // BG et BT sont calculées sur exactement la même période.
                StartDate = new DateTime(1900, 1, 1),
                EndDate = new DateTime(DateTime.Today.Year, 12, 31),
                AccountPrefixes = prefixes,
                Tolerance = 0.01m
            };
            _result = await new ReconciliationService(cs, mapping).RunAsync(p);
            ShowResults(p);
            _pageResults.Enabled = true;
            _tabs.SelectedTab = _pageResults;
            SetConnMessage("", error: false);
        }
        catch (Exception ex)
        {
            SetConnMessage("Échec : " + ex.Message, error: true);
        }
        finally { SetBusy(false); }
    }

    /// <summary>Devine tout seul la table des écritures, ses colonnes et le fichier des tiers (Sage : F_COMPTET).</summary>
    private async Task<ColumnMapping?> DetectMappingAsync(string cs)
    {
        var tables = await DatabaseService.GetTablesAsync(cs);
        // On essaie les tables candidates dans l'ordre et on garde la première dont les colonnes sont toutes reconnues.
        ColumnMapping? m = null;
        foreach (var entries in SchemaDetector.GuessEntriesCandidates(tables))
        {
            var cols = await DatabaseService.GetColumnsAsync(cs, entries.Schema, entries.Name);
            var guess = SchemaDetector.GuessColumns(entries.Schema, entries.Name, cols);
            guess.SignDebitValue = "0";      // Sage : 0 = débit, 1 = crédit
            guess.SignCreditValue = "1";
            bool complete = guess.AccountColumn != "" && guess.ThirdPartyColumn != "" && guess.DateColumn != ""
                && (guess.Mode == AmountMode.DebitCredit ? (guess.DebitColumn != "" && guess.CreditColumn != "")
                                                         : (guess.AmountColumn != "" && guess.SignColumn != ""));
            if (complete) { m = guess; break; }
        }
        if (m is null)
        {
            SetConnMessage("Cette base ne semble pas contenir d'écritures comptables exploitables. Vérifiez le nom de la base.", error: true);
            return null;
        }

        // fichier des tiers (facultatif) : permet de repérer les écritures sur un mauvais compte général
        var tp = SchemaDetector.GuessTiersTable(tables);
        if (tp is not null)
        {
            var tpCols = await DatabaseService.GetColumnsAsync(cs, tp.Schema, tp.Name);
            var code = tpCols.FirstOrDefault(c => c.Equals("CT_Num", StringComparison.OrdinalIgnoreCase));
            var acc = tpCols.FirstOrDefault(c => c.Equals("CG_NumPrinc", StringComparison.OrdinalIgnoreCase));
            if (code is not null && acc is not null)
            {
                m.TpSchema = tp.Schema; m.TpTable = tp.Name; m.TpCodeColumn = code; m.TpAccountColumn = acc;
            }
        }
        return m;
    }

    private void ShowResults(ReconciliationParams p)
    {
        if (_result is null) return;

        _gridBalances.DataSource = _result.Rows;
        _gridAnomalies.DataSource = _result.Anomalies;
        _grpAnomalies.Text = $"2. Écritures à contrôler ({_result.Anomalies.Count})";
        _txtFilterTiers.Text = "";
        BindTiers();

        int gaps = _result.Rows.Count(r => r.HasGap);
        int c1 = _result.Anomalies.Count(a => a.Level == Level.Confirmed), c2 = _result.Anomalies.Count(a => a.Level == Level.Probable), c3 = _result.Anomalies.Count(a => a.Level == Level.ToCheck);
        var sb = new StringBuilder();
        sb.Append(gaps == 0
            ? "Aucun écart : la balance générale et la balance tiers concordent."
            : $"{gaps} compte(s) en écart entre la balance générale et la balance tiers (lignes roses du tableau 1).");
        sb.Append($"\nÉcritures à contrôler : {c1} confirmée(s), {c2} probable(s), {c3} à vérifier.   Soldes cumulés au {p.EndDate:dd/MM/yyyy} (à-nouveaux inclus).");
        if (_result.Notes.Count > 0) sb.Append("\n" + string.Join(" ", _result.Notes));
        _lblSummary.Text = sb.ToString();
        _lblSummary.ForeColor = gaps == 0 ? Color.DarkGreen : Color.Firebrick;
        _btnExport.Enabled = true;
    }

    // ==================================================================
    // Export CSV (séparateur ';', UTF-8 avec BOM : s'ouvre directement dans Excel en français)
    //  - montants = vrais nombres (ex. 5425,95) : Excel peut les additionner, trier, filtrer
    //  - dates = jj/mm/aaaa
    //  - codes (compte, tiers, pièce) = texte : les zéros du début sont conservés (ex. 0121301747)
    // ==================================================================
    private void ExportCsv()
    {
        if (_result is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Fichier CSV (*.csv)|*.csv",
            FileName = $"rapprochement_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var basePath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, Path.GetFileNameWithoutExtension(dlg.FileName));
            var f1 = basePath + "_1_synthese.csv";
            var f2 = basePath + "_2_anomalies.csv";
            var f3 = basePath + "_3_detail_tiers.csv";
            WriteCsv(_result.Rows, f1);
            WriteCsv(_result.Anomalies, f2);
            WriteCsv(_result.ThirdPartyBalances, f3);
            MessageBox.Show("Fichiers exportés :\n" + f1 + "\n" + f2 + "\n" + f3,
                            "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export impossible : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void WriteCsv<T>(IEnumerable<T> items, string path)
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        var props = typeof(T).GetProperties().Where(pi => pi.GetCustomAttributes(typeof(System.ComponentModel.BrowsableAttribute), false)
            .Cast<System.ComponentModel.BrowsableAttribute>().All(b => b.Browsable)).ToArray();
        string Header(System.Reflection.PropertyInfo pi) =>
            (Attribute.GetCustomAttribute(pi, typeof(System.ComponentModel.DisplayNameAttribute)) as System.ComponentModel.DisplayNameAttribute)?.DisplayName ?? pi.Name;
        static string Esc(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

        string Cell(System.Reflection.PropertyInfo pi, object? v)
        {
            if (v is null) return "";
            if (v is decimal d) return d.ToString("0.00", fr);                 // nombre pur, virgule décimale
            if (v is DateTime dt) return dt.ToString("dd/MM/yyyy", fr);
            var text = Convert.ToString(v, fr) ?? "";
            if (Attribute.IsDefined(pi, typeof(TextCodeAttribute)) && text.Length > 0)
                return "=\"" + text.Replace("\"", "\"\"") + "\"";           // force le format texte dans Excel
            return Esc(text);
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", props.Select(pi => Esc(Header(pi)))));
        foreach (var item in items)
            sb.AppendLine(string.Join(";", props.Select(pi => Cell(pi, pi.GetValue(item)))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // ==================================================================
    // Divers
    // ==================================================================
    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _btnRun.Enabled = !busy;
    }
}
