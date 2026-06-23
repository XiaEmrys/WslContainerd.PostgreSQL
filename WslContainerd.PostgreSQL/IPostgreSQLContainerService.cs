using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using WslContainerd.Services.Abstractions;

namespace WslContainerd.PostgreSQL;

/// <summary>
/// PostgreSQL container lifecycle and generic SQL access (no Evolux business schema).
/// </summary>
public interface IPostgreSQLContainerService : IServiceModule
{
    string GetConnectionString(string database = "postgres");

    Task<bool> CreateDatabaseAsync(string databaseName, Action<string>? logCallback = null);
    Task<bool> DropDatabaseAsync(string databaseName);
    Task<bool> BackupDatabaseAsync(string databaseName, string backupPath, Action<string>? logCallback = null);
    Task<bool> RestoreDatabaseAsync(string databaseName, string backupPath, Action<string>? logCallback = null);
    Task<string[]> ListDatabasesAsync();
    Task<string> GetDatabaseSizeAsync(string databaseName);

    Task<int> ExecuteNonQueryAsync(string databaseName, string sql, object? parameters = null);
    Task<T?> ExecuteScalarAsync<T>(string databaseName, string sql, object? parameters = null);
    Task<IEnumerable<T>> ExecuteQueryAsync<T>(string databaseName, string sql, object? parameters = null);
    Task<NpgsqlConnection> CreateConnectionAsync(string databaseName);
    Task<bool> IsDatabaseAvailableAsync(string databaseName = "postgres");
}
