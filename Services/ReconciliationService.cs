using BgBtRapprochement.Models;
using Microsoft.Data.SqlClient;

namespace BgBtRapprochement.Services;

/// <summary>
/// Calcule la BG et la BT sur la même période, compare les soldes et détecte les anomalies.
/// Lecture seule : uniquement des SELECT.
/// Tolérance d'arrondi configurable (ReconciliationParams.Tolerance).
/// Convention de signe : solde = Débit - Crédit (positif = débiteur, négatif = créditeur).
/// </summary>
public class ReconciliationService
{
    private const int MaxDetailLines = 5000;    // limite d'affichage des écritures sans tiers
    private const int MaxMisaffectedThirdParties = 200;

    private readonly string _cs;
    private readonly ColumnMapping _m;

    public ReconciliationService(string connectionString, ColumnMapping mapping)
    {
        _cs = connectionString;
        _m = mapping;
    }

    // ------------------------------------------------------------------
    // Construction des morceaux de SQL (les noms viennent de la base : on les valide puis on les protège)
    // ------------------------------------------------------------------
    private string Table => $"{SqlHelper.Quote(_m.Schema)}.{SqlHelper.Quote(_m.Table)}";
    private string Acc => $"LTRIM(RTRIM(CAST({SqlHelper.Quote(_m.AccountColumn)} AS NVARCHAR(50))))";
    private string Tiers => $"LTRIM(RTRIM(CAST({SqlHelper.Quote(_m.ThirdPartyColumn)} AS NVARCHAR(50))))";
    private string TiersEmpty => $"({SqlHelper.Quote(_m.ThirdPartyColumn)} IS NULL OR {Tiers} = '')";
    private string TiersFilled => $"({SqlHelper.Quote(_m.ThirdPartyColumn)} IS NOT NULL AND {Tiers} <> '')";
    private string DateCol => SqlHelper.Quote(_m.DateColumn);

    /// <summary>Montant signé d'une écriture : Débit - Crédit.</summary>
    private string Signed
    {
        get
        {
            if (_m.Mode == AmountMode.DebitCredit)
            {
                return $"(ISNULL(CAST({SqlHelper.Quote(_m.DebitColumn)} AS DECIMAL(19,4)), 0) - " +
                       $"ISNULL(CAST({SqlHelper.Quote(_m.CreditColumn)} AS DECIMAL(19,4)), 0))";
            }
            var amount = $"ISNULL(CAST({SqlHelper.Quote(_m.AmountColumn)} AS DECIMAL(19,4)), 0)";
            var sign = $"CAST({SqlHelper.Quote(_m.SignColumn)} AS NVARCHAR(50))";
            return $"(CASE WHEN {sign} = @signDebit THEN {amount} WHEN {sign} = @signCredit THEN -{amount} ELSE 0 END)";
        }
    }

    private static string Optional(string column) =>
        string.IsNullOrWhiteSpace(column) ? "CAST(NULL AS NVARCHAR(100))" : $"CAST({SqlHelper.Quote(column)} AS NVARCHAR(100))";

    private string AccountFilter(ReconciliationParams p) =>
        "(" + string.Join(" OR ", p.AccountPrefixes.Select((_, i) => $"{Acc} LIKE @p{i}")) + ")";

    private const string DateFilterSql = "@d1 <= {0} AND {0} < @d2";
    private string DateFilter => string.Format(DateFilterSql, DateCol);

    private void AddParams(SqlCommand cmd, ReconciliationParams p)
    {
        cmd.Parameters.AddWithValue("@d1", p.StartDate.Date);
        cmd.Parameters.AddWithValue("@d2", p.EndDate.Date.AddDays(1));   // fin incluse
        for (int i = 0; i < p.AccountPrefixes.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", p.AccountPrefixes[i] + "%");
        if (_m.Mode == AmountMode.MontantSens)
        {
            cmd.Parameters.AddWithValue("@signDebit", _m.SignDebitValue);
            cmd.Parameters.AddWithValue("@signCredit", _m.SignCreditValue);
        }
    }

    private static decimal Dec(SqlDataReader r, int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
    private static string Str(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
    private static DateTime? Dt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDateTime(r.GetValue(i));

    // ------------------------------------------------------------------
    // Vérification du mappage : toutes les colonnes doivent exister dans la table
    // ------------------------------------------------------------------
    public async Task ValidateMappingAsync()
    {
        var existing = await DatabaseService.GetColumnsAsync(_cs, _m.Schema, _m.Table);
        if (existing.Count == 0)
            throw new InvalidOperationException($"La table {_m.Schema}.{_m.Table} est introuvable ou n'a aucune colonne.");

        var required = new List<string> { _m.AccountColumn, _m.ThirdPartyColumn, _m.DateColumn };
        if (_m.Mode == AmountMode.DebitCredit) required.AddRange(new[] { _m.DebitColumn, _m.CreditColumn });
        else required.AddRange(new[] { _m.AmountColumn, _m.SignColumn });

        var optional = new[] { _m.JournalColumn, _m.PieceColumn, _m.LabelColumn };

        foreach (var c in required)
        {
            if (string.IsNullOrWhiteSpace(c))
                throw new InvalidOperationException("Une colonne obligatoire n'est pas renseignée (compte, tiers, date, montants).");
            if (!existing.Contains(c, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"La colonne « {c} » n'existe pas dans {_m.Schema}.{_m.Table}.");
        }
        if (_m.HasTiersRule)
        {
            var tp = await DatabaseService.GetColumnsAsync(_cs, _m.TpSchema, _m.TpTable);
            foreach (var c in new[] { _m.TpCodeColumn, _m.TpAccountColumn })
                if (!tp.Contains(c, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"La colonne « {c} » n'existe pas dans {_m.TpSchema}.{_m.TpTable}.");
        }
        foreach (var c in optional)
        {
            if (!string.IsNullOrWhiteSpace(c) && !existing.Contains(c, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"La colonne « {c} » n'existe pas dans {_m.Schema}.{_m.Table}.");
        }
    }

    // ------------------------------------------------------------------
    // Rapprochement complet
    // ------------------------------------------------------------------
    private record Totals(decimal Debit, decimal Credit)
    {
        public decimal Net => Debit - Credit;
    }

    public async Task<ReconciliationResult> RunAsync(ReconciliationParams p)
    {
        if (p.AccountPrefixes.Count == 0)
            throw new InvalidOperationException("Aucun compte à contrôler n'est défini.");
        if (p.StartDate.Date > p.EndDate.Date)
            throw new InvalidOperationException("La date de début est postérieure à la date de fin.");
        if (p.Tolerance < 0) p.Tolerance = 0;

        await ValidateMappingAsync();

        var result = new ReconciliationResult();

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();

        // 1) BG : débit, crédit et solde net par compte collectif (toutes écritures, avec ou sans tiers)
        var bg = await LoadBgAsync(conn, p);

        // 2) BT : débit, crédit et solde net par (compte, tiers), pour les écritures qui portent un code tiers
        var bt = await LoadBtAsync(conn, p);
        result.ThirdPartyBalances = bt.OrderBy(x => x.Account).ThenBy(x => x.ThirdParty).ToList();

        // 3) Comparaison compte par compte :  Écart = Solde net BG - Somme des soldes nets BT
        var accounts = bg.Keys.Union(bt.Select(x => x.Account)).OrderBy(a => a).ToList();
        foreach (var acc in accounts)
        {
            var bgT = bg.TryGetValue(acc, out var v) ? v : new Totals(0, 0);
            var tiersOfAcc = bt.Where(x => x.Account == acc).ToList();
            var btNet = tiersOfAcc.Sum(x => x.Balance);
            var gap = bgT.Net - btNet;
            result.Rows.Add(new ReconciliationRow
            {
                Account = acc,
                BgDebit = bgT.Debit,
                BgCredit = bgT.Credit,
                BgBalance = bgT.Net,
                BtDebit = tiersOfAcc.Sum(x => x.Debit),
                BtCredit = tiersOfAcc.Sum(x => x.Credit),
                BtBalance = btNet,
                Gap = gap,
                ThirdPartyCount = tiersOfAcc.Count,
                HasGap = Math.Abs(gap) > p.Tolerance
            });
        }

        // 4) Anomalies
        var noTiersByAccount = await LoadNoThirdPartyTotalsAsync(conn, p);
        await AddNoThirdPartyAnomaliesAsync(conn, p, result, noTiersByAccount);          // A
        if (_m.HasTiersRule)
            await AddRuleAnomaliesAsync(conn, p, bt, result);                            // B + D (règle configurée)
        else
            await AddHeuristicAnomaliesAsync(conn, p, bt, result);                       // B + D (heuristique)
        AddGapAnomalies(p, result, noTiersByAccount);                                    // C + E

        result.Anomalies = result.Anomalies
            .OrderBy(a => Level.Rank(a.Level)).ThenBy(a => a.Type).ThenBy(a => a.Account).ThenBy(a => a.ThirdParty).ThenBy(a => a.Date)
            .ToList();

        if (result.Rows.Count == 0)
            result.Notes.Add("Aucune écriture trouvée pour ces comptes et cette période. Vérifiez le mappage des colonnes, les préfixes et les dates.");

        return result;
    }

    private string DebitSql => $"SUM(CASE WHEN {Signed} > 0 THEN {Signed} ELSE 0 END)";
    private string CreditSql => $"SUM(CASE WHEN {Signed} < 0 THEN -{Signed} ELSE 0 END)";

    private async Task<Dictionary<string, Totals>> LoadBgAsync(SqlConnection conn, ReconciliationParams p)
    {
        var sql = $@"SELECT {Acc} AS Compte, {DebitSql} AS TotDebit, {CreditSql} AS TotCredit
                     FROM {Table}
                     WHERE {AccountFilter(p)} AND {DateFilter}
                     GROUP BY {Acc}";
        var dict = new Dictionary<string, Totals>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        AddParams(cmd, p);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) dict[Str(r, 0)] = new Totals(Dec(r, 1), Dec(r, 2));
        return dict;
    }

    private async Task<List<ThirdPartyBalanceRow>> LoadBtAsync(SqlConnection conn, ReconciliationParams p)
    {
        var sql = $@"SELECT {Acc} AS Compte, {Tiers} AS Tiers, COUNT(*) AS Nb, {DebitSql} AS TotDebit, {CreditSql} AS TotCredit
                     FROM {Table}
                     WHERE {AccountFilter(p)} AND {DateFilter} AND {TiersFilled}
                     GROUP BY {Acc}, {Tiers}";
        var list = new List<ThirdPartyBalanceRow>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        AddParams(cmd, p);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var d = Dec(r, 3); var c = Dec(r, 4);
            list.Add(new ThirdPartyBalanceRow
            {
                Account = Str(r, 0), ThirdParty = Str(r, 1), EntryCount = Convert.ToInt32(r.GetValue(2)),
                Debit = d, Credit = c, Balance = d - c
            });
        }
        return list;
    }

    private async Task<Dictionary<string, decimal>> LoadNoThirdPartyTotalsAsync(SqlConnection conn, ReconciliationParams p)
    {
        var sql = $@"SELECT {Acc}, SUM({Signed})
                     FROM {Table}
                     WHERE {AccountFilter(p)} AND {DateFilter} AND {TiersEmpty}
                     GROUP BY {Acc}";
        var dict = new Dictionary<string, decimal>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        AddParams(cmd, p);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) dict[Str(r, 0)] = Dec(r, 1);
        return dict;
    }

    /// <summary>Écritures d'un tiers sur un compte donné (avec toutes les informations utiles à l'analyse).</summary>
    private async Task<List<AnomalyRow>> LoadEntriesAsync(SqlConnection conn, ReconciliationParams p, string account, string tiers,
        string type, string level, string comment)
    {
        var sql = $@"SELECT {DateCol}, {Optional(_m.JournalColumn)}, {Optional(_m.PieceColumn)}, {Optional(_m.LabelColumn)}, {Signed}
                     FROM {Table}
                     WHERE {Acc} = @acc AND {Tiers} = @tiers AND {DateFilter}
                     ORDER BY {DateCol}";
        var list = new List<AnomalyRow>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        AddParams(cmd, p);
        cmd.Parameters.AddWithValue("@acc", account);
        cmd.Parameters.AddWithValue("@tiers", tiers);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AnomalyRow
            {
                Level = level, Type = type, Account = account, ThirdParty = tiers,
                Date = Dt(r, 0), Journal = Str(r, 1), Piece = Str(r, 2), Label = Str(r, 3), Amount = Dec(r, 4),
                Comment = comment
            });
        return list;
    }

    // ---------- A. Écritures sans tiers ----------
    private async Task AddNoThirdPartyAnomaliesAsync(SqlConnection conn, ReconciliationParams p, ReconciliationResult result,
        Dictionary<string, decimal> noTiersByAccount)
    {
        var sql = $@"SELECT TOP ({MaxDetailLines}) {Acc}, {DateCol}, {Optional(_m.JournalColumn)}, {Optional(_m.PieceColumn)}, {Optional(_m.LabelColumn)}, {Signed}
                     FROM {Table}
                     WHERE {AccountFilter(p)} AND {DateFilter} AND {TiersEmpty}
                     ORDER BY {Acc}, {DateCol}";
        var gapByAccount = result.Rows.ToDictionary(row => row.Account, row => row.Gap);
        int count = 0;
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        AddParams(cmd, p);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            count++;
            var acc = Str(r, 0);
            // Confirmée si ces écritures expliquent exactement l'écart du compte ; sinon probable (fait vrai : pas de tiers).
            bool explains = gapByAccount.TryGetValue(acc, out var gap) && Math.Abs(gap) > p.Tolerance
                            && noTiersByAccount.TryGetValue(acc, out var nt) && Math.Abs(gap - nt) <= p.Tolerance;
            result.Anomalies.Add(new AnomalyRow
            {
                Level = explains ? Level.Confirmed : Level.Probable,
                Type = "A. Écriture sans code tiers",
                Account = acc,
                ThirdParty = "(aucun)",
                Date = Dt(r, 1), Journal = Str(r, 2), Piece = Str(r, 3), Label = Str(r, 4), Amount = Dec(r, 5),
                Comment = explains
                    ? "Écriture sur compte collectif sans tiers : elle est dans la BG mais pas dans la BT, et elle explique l'écart du compte."
                    : "Écriture sur compte collectif sans tiers : elle est dans la BG mais pas dans la BT. À rattacher à un tiers si le suivi individuel est requis."
            });
        }
        if (count >= MaxDetailLines)
            result.Notes.Add($"Les écritures sans tiers sont limitées aux {MaxDetailLines} premières ; il peut y en avoir davantage.");
    }

    // ---------- B + D avec règle configurée (fichier des tiers) ----------
    private async Task AddRuleAnomaliesAsync(SqlConnection conn, ReconciliationParams p,
        List<ThirdPartyBalanceRow> bt, ReconciliationResult result)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tpSql = $@"SELECT LTRIM(RTRIM(CAST({SqlHelper.Quote(_m.TpCodeColumn)} AS NVARCHAR(50)))),
                              LTRIM(RTRIM(CAST({SqlHelper.Quote(_m.TpAccountColumn)} AS NVARCHAR(50))))
                       FROM {SqlHelper.Quote(_m.TpSchema)}.{SqlHelper.Quote(_m.TpTable)}";
        await using (var cmd = new SqlCommand(tpSql, conn) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) expected[Str(r, 0)] = Str(r, 1);

        int groups = 0;
        foreach (var row in bt)
        {
            if (!expected.TryGetValue(row.ThirdParty, out var exp))
            {
                // D. tiers absent du fichier des tiers
                result.Anomalies.Add(new AnomalyRow
                {
                    Level = Level.Probable, Type = "D. Tiers inconnu du fichier des tiers",
                    Account = row.Account, ThirdParty = row.ThirdParty, Amount = row.Balance,
                    Comment = $"Le tiers {row.ThirdParty} ({row.EntryCount} écritures) n'existe pas dans {_m.TpSchema}.{_m.TpTable}."
                });
            }
            else if (exp.Length > 0 && !string.Equals(exp, row.Account, StringComparison.OrdinalIgnoreCase))
            {
                // B. compte différent du compte collectif attendu pour ce tiers
                if (++groups > MaxMisaffectedThirdParties) continue;
                var lines = await LoadEntriesAsync(conn, p, row.Account, row.ThirdParty,
                    "B. Compte général différent de la règle", Level.Probable,
                    $"Le fichier des tiers attend le compte {exp} pour {row.ThirdParty} ; l'écriture est sur {row.Account}. Peut être légitime (compte spécifique).");
                result.Anomalies.AddRange(lines);
            }
        }
        if (groups > MaxMisaffectedThirdParties)
            result.Notes.Add($"{groups} couples (compte, tiers) ne respectent pas la règle ; seuls {MaxMisaffectedThirdParties} sont détaillés.");
    }

    // ---------- B + D sans règle : heuristique prudente ----------
    private async Task AddHeuristicAnomaliesAsync(SqlConnection conn, ReconciliationParams p,
        List<ThirdPartyBalanceRow> bt, ReconciliationResult result)
    {
        var suspects = bt.GroupBy(x => x.ThirdParty)
                         .Where(g => g.Select(x => x.Account).Distinct().Count() > 1)
                         .ToList();

        if (suspects.Count > MaxMisaffectedThirdParties)
        {
            result.Notes.Add($"{suspects.Count} tiers sont mouvementés sur plusieurs comptes collectifs ; seuls les {MaxMisaffectedThirdParties} premiers sont détaillés.");
            suspects = suspects.Take(MaxMisaffectedThirdParties).ToList();
        }

        foreach (var g in suspects)
        {
            var ordered = g.OrderByDescending(x => x.EntryCount).ToList();
            var dominant = ordered[0];

            if (ordered[1].EntryCount == dominant.EntryCount)
            {
                // D. aucun compte dominant : impossible de dire quel compte est attendu
                result.Anomalies.Add(new AnomalyRow
                {
                    Level = Level.ToCheck, Type = "D. Tiers sans compte dominant",
                    Account = string.Join(" / ", ordered.Select(x => x.Account)), ThirdParty = g.Key,
                    Amount = g.Sum(x => x.Balance),
                    Comment = $"Le tiers {g.Key} est réparti à parts égales sur plusieurs comptes collectifs ({string.Join(", ", ordered.Select(x => $"{x.Account} : {x.EntryCount}"))}). Aucun compte attendu ne peut être déduit."
                });
                continue;
            }

            foreach (var other in g.Where(x => x.Account != dominant.Account))
            {
                var lines = await LoadEntriesAsync(conn, p, other.Account, g.Key,
                    "B. Compte général à vérifier (heuristique)", Level.ToCheck,
                    $"Le tiers {g.Key} est surtout sur le compte {dominant.Account} ({dominant.EntryCount} écritures) ; cette écriture est sur {other.Account}. Peut être légitime. " +
                    "Configurez le fichier des tiers (onglet Paramétrage) pour une vraie règle.");
                result.Anomalies.AddRange(lines);
            }
        }
    }

    // ---------- C + E. Écart BG / BT et origine ----------
    private static void AddGapAnomalies(ReconciliationParams p, ReconciliationResult result, Dictionary<string, decimal> noTiersByAccount)
    {
        foreach (var row in result.Rows.Where(r => r.HasGap))
        {
            noTiersByAccount.TryGetValue(row.Account, out var noTiers);
            bool fullyExplained = Math.Abs(row.Gap - noTiers) <= p.Tolerance;
            string comment = fullyExplained
                ? $"Différence à analyser. Elle est entièrement expliquée par les écritures sans code tiers ({noTiers:N2}) listées dans ce tableau."
                : Math.Abs(noTiers) > p.Tolerance
                    ? $"Différence à analyser. Les écritures sans code tiers représentent {noTiers:N2} ; le reste ({row.Gap - noTiers:N2}) ne peut pas être expliqué avec les données disponibles."
                    : "Différence à analyser. Cause non déterminable avec les données disponibles (aucune écriture sans tiers).";

            result.Anomalies.Add(new AnomalyRow
            {
                Level = fullyExplained ? Level.Confirmed : Level.ToCheck,
                Type = "C. Compte sans correspondance BG / BT",
                Account = row.Account,
                ThirdParty = "(tous tiers)",
                Amount = row.Gap,
                Comment = comment
            });
        }
    }
}
