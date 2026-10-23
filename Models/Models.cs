using System.ComponentModel;

namespace BgBtRapprochement.Models;

/// <summary>Paramètres saisis dans l'écran de connexion.</summary>
public class ConnectionSettings
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool WindowsAuth { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
}

/// <summary>Une table (ou vue) de la base.</summary>
public record TableRef(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

/// <summary>Deux façons de stocker le montant d'une écriture.</summary>
public enum AmountMode
{
    /// <summary>Deux colonnes : Débit et Crédit.</summary>
    DebitCredit,
    /// <summary>Une colonne Montant + une colonne Sens (ex. D/C ou 0/1).</summary>
    MontantSens
}

/// <summary>
/// Indique quelles colonnes de la table des écritures correspondent à quoi.
/// C'est ce "mappage" qui permet de ne pas être limité à la base Bijou.
/// </summary>
public class ColumnMapping
{
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "";

    public string AccountColumn { get; set; } = "";      // compte général
    public string ThirdPartyColumn { get; set; } = "";   // code tiers
    public string DateColumn { get; set; } = "";         // date d'écriture
    public string JournalColumn { get; set; } = "";      // optionnel
    public string PieceColumn { get; set; } = "";        // optionnel
    public string LabelColumn { get; set; } = "";        // optionnel

    public AmountMode Mode { get; set; } = AmountMode.DebitCredit;
    public string DebitColumn { get; set; } = "";
    public string CreditColumn { get; set; } = "";
    public string AmountColumn { get; set; } = "";
    public string SignColumn { get; set; } = "";
    public string SignDebitValue { get; set; } = "D";
    public string SignCreditValue { get; set; } = "C";

    // Règle facultative : fichier des tiers (Sage : F_COMPTET) qui donne le compte collectif attendu de chaque tiers.
    public string TpSchema { get; set; } = "dbo";
    public string TpTable { get; set; } = "";
    public string TpCodeColumn { get; set; } = "";       // ex. CT_Num
    public string TpAccountColumn { get; set; } = "";    // ex. CG_NumPrinc
    public bool HasTiersRule => !string.IsNullOrWhiteSpace(TpTable) && !string.IsNullOrWhiteSpace(TpCodeColumn) && !string.IsNullOrWhiteSpace(TpAccountColumn);
}

/// <summary>Paramètres du rapprochement (période + comptes à contrôler).</summary>
public class ReconciliationParams
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    /// <summary>Préfixes des comptes collectifs à contrôler, ex. "41", "40".</summary>
    public List<string> AccountPrefixes { get; set; } = new() { "41", "40" };
    /// <summary>Tolérance d'arrondi : un écart inférieur (en valeur absolue) est considéré comme nul.</summary>
    public decimal Tolerance { get; set; } = 0.01m;
}

/// <summary>Marque une colonne de code (compte, tiers, pièce) à exporter comme TEXTE, pour garder les zéros du début.</summary>
[AttributeUsage(AttributeTargets.Property)]
public class TextCodeAttribute : Attribute { }

public static class SensHelper
{
    /// <summary>Libellé lisible du sens d'un solde (Débit - Crédit).</summary>
    public static string Of(decimal v) => Math.Abs(v) < 0.005m ? "Soldé" : v > 0 ? "Débiteur" : "Créditeur";
}

/// <summary>Une ligne du tableau BG / BT.</summary>
public class ReconciliationRow
{
    [TextCode, DisplayName("Compte collectif")] public string Account { get; set; } = "";
    [DisplayName("BG : total débit")] public decimal BgDebit { get; set; }
    [DisplayName("BG : total crédit")] public decimal BgCredit { get; set; }
    [DisplayName("Solde net BG")] public decimal BgBalance { get; set; }
    [DisplayName("BT : total débit")] public decimal BtDebit { get; set; }
    [DisplayName("BT : total crédit")] public decimal BtCredit { get; set; }
    [DisplayName("Solde net BT (somme des tiers)")] public decimal BtBalance { get; set; }
    [DisplayName("Écart (BG - BT)")] public decimal Gap { get; set; }
    [DisplayName("Sens solde BG")] public string BgSens => SensHelper.Of(BgBalance);
    [DisplayName("Nb tiers")] public int ThirdPartyCount { get; set; }
    [Browsable(false)] public bool HasGap { get; set; }
    [DisplayName("Résultat")] public string Status => HasGap ? "Différence à analyser" : "Soldes concordants";
}

/// <summary>Solde d'un tiers sur un compte collectif.</summary>
public class ThirdPartyBalanceRow
{
    [TextCode, DisplayName("Compte collectif")] public string Account { get; set; } = "";
    [TextCode, DisplayName("Code tiers")] public string ThirdParty { get; set; } = "";
    [DisplayName("Nb écritures")] public int EntryCount { get; set; }
    [DisplayName("Total débit")] public decimal Debit { get; set; }
    [DisplayName("Total crédit")] public decimal Credit { get; set; }
    [DisplayName("Solde net (Débit - Crédit)")] public decimal Balance { get; set; }
    [DisplayName("Sens du solde")] public string Sens => SensHelper.Of(Balance);
}

/// <summary>Niveau de certitude d'une anomalie.</summary>
public static class Level
{
    /// <summary>Démontrée par les chiffres (ex. l'écart est exactement égal aux écritures sans tiers).</summary>
    public const string Confirmed = "Confirmée";
    /// <summary>Contredit une règle configurée (ex. compte collectif attendu du tiers), sans preuve que ce soit faux.</summary>
    public const string Probable = "Probable";
    /// <summary>Simple signal (heuristique ou cause inconnue) : à contrôler par un humain.</summary>
    public const string ToCheck = "À vérifier";

    public static int Rank(string level) => level == Confirmed ? 0 : level == Probable ? 1 : 2;
}

/// <summary>Une anomalie détectée.</summary>
public class AnomalyRow
{
    [DisplayName("Niveau")] public string Level { get; set; } = "";
    [DisplayName("Contrôle")] public string Type { get; set; } = "";
    [TextCode, DisplayName("Compte général")] public string Account { get; set; } = "";
    [TextCode, DisplayName("Code tiers")] public string ThirdParty { get; set; } = "";
    [DisplayName("Date")] public DateTime? Date { get; set; }
    [DisplayName("Journal")] public string Journal { get; set; } = "";
    [TextCode, DisplayName("N° pièce")] public string Piece { get; set; } = "";
    [DisplayName("Libellé")] public string Label { get; set; } = "";
    [DisplayName("Débit")] public decimal Debit { get; set; }
    [DisplayName("Crédit")] public decimal Credit { get; set; }
    [DisplayName("Explication")] public string Comment { get; set; } = "";

    /// <summary>Montant signé (Débit - Crédit) ; l'affecter renseigne Débit ou Crédit.</summary>
    [Browsable(false)]
    public decimal Amount
    {
        get => Debit - Credit;
        set { Debit = value > 0 ? value : 0; Credit = value < 0 ? -value : 0; }
    }
}

public class ReconciliationResult
{
    public List<ReconciliationRow> Rows { get; set; } = new();
    public List<ThirdPartyBalanceRow> ThirdPartyBalances { get; set; } = new();
    public List<AnomalyRow> Anomalies { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}
