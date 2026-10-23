using BgBtRapprochement.Models;

namespace BgBtRapprochement.Services;

/// <summary>
/// Propose automatiquement la table des écritures et les colonnes à utiliser,
/// d'après les noms. Ce n'est qu'une suggestion : l'utilisateur peut tout corriger.
/// </summary>
public static class SchemaDetector
{
    /// <summary>
    /// Liste, du plus probable au moins probable, les tables qui peuvent contenir les écritures comptables.
    /// Sage : F_ECRITUREC en premier ; les tables annexes (F_CTRLFIELDECRITUREC, etc.) passent en dernier.
    /// </summary>
    public static List<TableRef> GuessEntriesCandidates(IEnumerable<TableRef> tables)
    {
        var all = tables.ToList();
        var result = new List<TableRef>();
        void Add(IEnumerable<TableRef> items) { foreach (var t in items) if (!result.Contains(t)) result.Add(t); }

        // 1. le nom exact Sage
        Add(all.Where(t => t.Name.Equals("F_ECRITUREC", StringComparison.OrdinalIgnoreCase)));
        // 2. autres noms évoquant des écritures, en écartant les tables annexes
        string[] annex = { "ctrlfield", "info", "lib", "analytique", "echeance", "tmp", "temp", "hist", "log" };
        string[] keywords = { "ecriturec", "ecriture", "ecr", "mouvement", "mvt", "ledger", "entry", "entries" };
        foreach (var k in keywords)
        {
            var hits = all.Where(t => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
            Add(hits.Where(t => !annex.Any(x => t.Name.Contains(x, StringComparison.OrdinalIgnoreCase))));
        }
        // 3. en dernier recours, les tables annexes
        foreach (var k in keywords)
            Add(all.Where(t => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    /// <summary>Première table candidate (compatibilité).</summary>
    public static TableRef? GuessEntriesTable(IEnumerable<TableRef> tables) => GuessEntriesCandidates(tables).FirstOrDefault();

    /// <summary>Devine la table des tiers (Sage : F_COMPTET) qui donne le compte collectif attendu de chaque tiers.</summary>
    public static TableRef? GuessTiersTable(IEnumerable<TableRef> tables) =>
        tables.FirstOrDefault(t => t.Name.Equals("F_COMPTET", StringComparison.OrdinalIgnoreCase)) ??
        tables.FirstOrDefault(t => t.Name.Contains("comptet", StringComparison.OrdinalIgnoreCase));

    /// <summary>Devine les colonnes d'après leur nom (ex. Sage : CG_Num, CT_Num, EC_Date, JO_Num, EC_Piece...).</summary>
    public static ColumnMapping GuessColumns(string schema, string table, IReadOnlyList<string> columns)
    {
        var used = new HashSet<string>();
        string Pick(params string[] keywords)
        {
            foreach (var k in keywords)
            {
                var c = columns.FirstOrDefault(col => !used.Contains(col) && col.ToLowerInvariant().Contains(k));
                if (c != null) { used.Add(c); return c; }
            }
            return "";
        }

        // L'ordre compte : on cherche d'abord le tiers pour ne pas confondre avec le compte.
        var tiers = Pick("ct_num", "codetiers", "code_tiers", "tiers", "auxiliaire", "aux");
        var compte = Pick("cg_num", "compte_general", "comptegeneral", "numcompte", "compte", "account", "cpte");
        var date = Pick("ec_date", "date_ecriture", "dateecriture", "datecompta", "date");
        var journal = Pick("jo_num", "journal", "jnl", "code_jrn");
        var piece = Pick("ec_piece", "num_piece", "numpiece", "piece", "pce", "document");
        var libelle = Pick("ec_intitule", "libelle", "intitule", "label", "designation");
        var debit = Pick("debit", "dbt");
        var credit = Pick("credit", "crd");

        var m = new ColumnMapping
        {
            Schema = schema,
            Table = table,
            AccountColumn = compte,
            ThirdPartyColumn = tiers,
            DateColumn = date,
            JournalColumn = journal,
            PieceColumn = piece,
            LabelColumn = libelle
        };

        if (debit != "" && credit != "")
        {
            m.Mode = AmountMode.DebitCredit;
            m.DebitColumn = debit;
            m.CreditColumn = credit;
        }
        else
        {
            m.Mode = AmountMode.MontantSens;
            m.AmountColumn = Pick("ec_montant", "montant", "amount", "mt");
            m.SignColumn = Pick("ec_sens", "sens", "sign", "dc");
        }
        return m;
    }
}
