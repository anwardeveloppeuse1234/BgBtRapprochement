using BgBtRapprochement.Models;
using Microsoft.Data.SqlClient;

namespace BgBtRapprochement.Services;

/// <summary>
/// Accès à SQL Server. IMPORTANT : cette application ne fait QUE des SELECT.
/// Aucune requête INSERT / UPDATE / DELETE n'existe dans le code.
/// </summary>
public static class DatabaseService
{
    public static string BuildConnectionString(ConnectionSettings s)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s.Server.Trim(),
            InitialCatalog = s.Database.Trim(),
            TrustServerCertificate = s.TrustServerCertificate,
            ApplicationIntent = ApplicationIntent.ReadOnly,   // demande une connexion en lecture seule
            ApplicationName = "BgBtRapprochement",
            ConnectTimeout = 15
        };

        if (s.WindowsAuth)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = s.User;
            b.Password = s.Password;
        }
        return b.ConnectionString;
    }

    /// <summary>Teste la connexion. Renvoie le nom de la base si tout va bien, sinon lève une exception.</summary>
    public static async Task<string> TestConnectionAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
        return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "";
    }

    /// <summary>Liste les tables et vues de la base (étape "analyser la structure réelle").</summary>
    public static async Task<List<TableRef>> GetTablesAsync(string connectionString)
    {
        const string sql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            ORDER BY TABLE_SCHEMA, TABLE_NAME";

        var list = new List<TableRef>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new TableRef(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    /// <summary>Liste les colonnes d'une table.</summary>
    public static async Task<List<string>> GetColumnsAsync(string connectionString, string schema, string table)
    {
        const string sql = @"
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION";

        var list = new List<string>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>Aperçu des premières lignes d'une table (pour vérifier le mappage à l'œil).</summary>
    public static async Task<System.Data.DataTable> PreviewAsync(string connectionString, string schema, string table, int top = 20)
    {
        var sql = $"SELECT TOP ({top}) * FROM {SqlHelper.Quote(schema)}.{SqlHelper.Quote(table)}";
        var dt = new System.Data.DataTable();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        dt.Load(reader);
        return dt;
    }
}

public static class SqlHelper
{
    /// <summary>Met un nom de table/colonne entre crochets en échappant les ']' (protection contre l'injection SQL).</summary>
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
}
