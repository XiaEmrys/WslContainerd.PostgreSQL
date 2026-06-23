namespace WslContainerd.PostgreSQL;

/// <summary>
/// Configuration for PostgreSQL container deployment.
/// </summary>
public sealed class PostgreSQLServiceOptions
{
    public string ImageName { get; set; } = "postgres:15";
    public string ContainerName { get; set; } = "postgresql";
    public int DefaultPort { get; set; } = 5432;
    public string DefaultUser { get; set; } = "postgres";
    public string DefaultPassword { get; set; } = "postgres";
    public string DefaultDatabase { get; set; } = "postgres";
    public string ApplicationDatabase { get; set; } = "app";
    public string PostgresDataDir { get; set; } = "/var/lib/postgresql/data";
    public string PostgresUid { get; set; } = "999";
    public string PostgresGid { get; set; } = "999";
}
