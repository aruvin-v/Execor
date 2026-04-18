using Microsoft.Data.SqlClient;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Execor.Inference.Services;

public class DatabaseSchemaService
{
    public string BuildConnectionString(string server, string database, string? user, string? password, bool integratedSecurity)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = false,
            TrustServerCertificate = true
        };

        if (integratedSecurity) builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    public async Task<string> ExtractSchemaToMarkdownAsync(string connectionString, string databaseName)
    {
        var md = new StringBuilder();
        md.AppendLine($"# DB: {databaseName}");
        md.AppendLine("Tables:");

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // 1. Minified Tables and Columns
        string tableQuery = @"
            SELECT t.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE 
            FROM INFORMATION_SCHEMA.TABLES t
            JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_NAME, c.ORDINAL_POSITION;";

        using var tableCmd = new SqlCommand(tableQuery, connection);
        using var tableReader = await tableCmd.ExecuteReaderAsync();

        string currentTable = "";
        var cols = new List<string>();

        while (await tableReader.ReadAsync())
        {
            string tableName = tableReader.GetString(0);
            string columnName = tableReader.GetString(1);
            string dataType = tableReader.GetString(2);

            if (tableName != currentTable)
            {
                if (currentTable != "")
                {
                    // Ultra-dense format: - TableName: Col1(type), Col2(type)
                    md.AppendLine($"- {currentTable}: {string.Join(", ", cols)}");
                    cols.Clear();
                }
                currentTable = tableName;
            }
            cols.Add($"{columnName}({dataType})");
        }
        if (currentTable != "") md.AppendLine($"- {currentTable}: {string.Join(", ", cols)}");
        await tableReader.CloseAsync();

        // 2. Minified Relationships
        md.AppendLine("\nRelations:");
        string fkQuery = @"
            SELECT 
                OBJECT_NAME(f.parent_object_id), COL_NAME(fc.parent_object_id, fc.parent_column_id),
                OBJECT_NAME (f.referenced_object_id), COL_NAME(fc.referenced_object_id, fc.referenced_column_id)
            FROM sys.foreign_keys AS f
            INNER JOIN sys.foreign_key_columns AS fc ON f.OBJECT_ID = fc.constraint_object_id;";

        using var fkCmd = new SqlCommand(fkQuery, connection);
        using var fkReader = await fkCmd.ExecuteReaderAsync();
        while (await fkReader.ReadAsync())
        {
            md.AppendLine($"{fkReader.GetString(0)}.{fkReader.GetString(1)}->{fkReader.GetString(2)}.{fkReader.GetString(3)}");
        }

        string filePath = Path.Combine(AppContext.BaseDirectory, $"{databaseName}_schema.md");
        await File.WriteAllTextAsync(filePath, md.ToString());

        return md.ToString();
    }

    public async Task<string> ExecuteReadOnlyQueryAsync(string connectionString, string query)
    {
        string upperQuery = query.Trim().ToUpperInvariant();

        if (!upperQuery.StartsWith("SELECT") || upperQuery.Contains("UPDATE ") || upperQuery.Contains("DELETE ") ||
            upperQuery.Contains("INSERT ") || upperQuery.Contains("DROP ") || upperQuery.Contains("ALTER ") ||
            upperQuery.Contains("TRUNCATE ") || upperQuery.Contains("EXEC "))
        {
            return "❌ SECURITY BLOCK: Execor is restricted to read-only SELECT queries.";
        }

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(query, connection);
            using var reader = await command.ExecuteReaderAsync();

            var rows = new List<string[]>();
            var headers = new string[reader.FieldCount];
            int[] colWidths = new int[reader.FieldCount];

            // Initialize headers and base widths
            for (int i = 0; i < reader.FieldCount; i++)
            {
                headers[i] = reader.GetName(i);
                colWidths[i] = headers[i].Length;
            }

            // Read rows and dynamically calculate max column widths
            while (await reader.ReadAsync() && rows.Count < 100)
            {
                var row = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string val = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()?.Replace("\n", " ").Replace("\r", "") ?? "NULL";
                    if (val.Length > 50) val = val.Substring(0, 47) + "..."; // Hard cap text length at 50 chars per column
                    row[i] = val;
                    if (val.Length > colWidths[i]) colWidths[i] = val.Length;
                }
                rows.Add(row);
            }

            var sb = new StringBuilder();

            // Build padded header
            var headerRow = new List<string>();
            for (int i = 0; i < headers.Length; i++) headerRow.Add(headers[i].PadRight(colWidths[i]));
            sb.AppendLine("| " + string.Join(" | ", headerRow) + " |");

            // Build separator
            var sepRow = new List<string>();
            for (int i = 0; i < headers.Length; i++) sepRow.Add(new string('-', colWidths[i]));
            sb.AppendLine("|-" + string.Join("-|-", sepRow) + "-|");

            // Build padded rows
            foreach (var r in rows)
            {
                var paddedRow = new List<string>();
                for (int i = 0; i < r.Length; i++) paddedRow.Add(r[i].PadRight(colWidths[i]));
                sb.AppendLine("| " + string.Join(" | ", paddedRow) + " |");
            }

            if (rows.Count == 100) sb.AppendLine("\n... [Results truncated to 100 rows for memory safety]");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"❌ SQL Error: {ex.Message}";
        }
    }
}