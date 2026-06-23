using System;
using System.Collections.Generic;

namespace WslContainerd.PostgreSQL;

/// <summary>
/// PostgreSQL 生产级配置类
/// 支持环境变量覆盖和配置验证
/// </summary>
public class PostgreSQLConfiguration
{
    /// <summary>
    /// 数据库用户
    /// </summary>
    public string User { get; set; } = "postgres";
    
    /// <summary>
    /// 数据库密码
    /// </summary>
    public string Password { get; set; } = "postgres";
    
    /// <summary>
    /// 默认数据库名称
    /// </summary>
    public string Database { get; set; } = "postgres";
    
    /// <summary>
    /// 数据持久化路径
    /// </summary>
    public string? DataPath { get; set; }
    
    /// <summary>
    /// 最大连接数
    /// </summary>
    public int MaxConnections { get; set; } = 200;
    
    /// <summary>
    /// 共享缓冲区大小
    /// </summary>
    public string SharedBuffers { get; set; } = "256MB";
    
    /// <summary>
    /// 有效缓存大小
    /// </summary>
    public string EffectiveCacheSize { get; set; } = "1GB";
    
    /// <summary>
    /// 是否启用查询日志
    /// </summary>
    public bool EnableQueryLog { get; set; } = true;
    
    /// <summary>
    /// 慢查询阈值（毫秒）
    /// </summary>
    public int SlowQueryThreshold { get; set; } = 1000;
    
    /// <summary>
    /// 是否启用 SSL
    /// </summary>
    public bool EnableSsl { get; set; } = false;
    
    /// <summary>
    /// 连接超时时间（秒）
    /// </summary>
    public int ConnectionTimeout { get; set; } = 30;
    
    /// <summary>
    /// 命令超时时间（秒）
    /// </summary>
    public int CommandTimeout { get; set; } = 30;
    
    /// <summary>
    /// 连接池最大大小
    /// </summary>
    public int MaxPoolSize { get; set; } = 10;
    
    /// <summary>
    /// 连接池最小大小
    /// </summary>
    public int MinPoolSize { get; set; } = 1;
    
    /// <summary>
    /// 连接空闲时间（秒）
    /// </summary>
    public int ConnectionIdleLifetime { get; set; } = 300;
    
    /// <summary>
    /// 连接清理间隔（秒）
    /// </summary>
    public int ConnectionPruningInterval { get; set; } = 10;
    
    /// <summary>
    /// 从环境变量加载配置
    /// </summary>
    public static PostgreSQLConfiguration FromEnvironment()
    {
        return new PostgreSQLConfiguration
        {
            User = GetEnvironmentVariableOrDefault("POSTGRES_USER", "postgres"),
            Password = GetEnvironmentVariableOrDefault("POSTGRES_PASSWORD", "postgres"),
            Database = GetEnvironmentVariableOrDefault("POSTGRES_DB", "postgres"),
            DataPath = Environment.GetEnvironmentVariable("POSTGRES_DATA_PATH"),
            MaxConnections = GetEnvironmentVariableOrDefault("POSTGRES_MAX_CONNECTIONS", 200),
            SharedBuffers = GetEnvironmentVariableOrDefault("POSTGRES_SHARED_BUFFERS", "256MB"),
            EffectiveCacheSize = GetEnvironmentVariableOrDefault("POSTGRES_EFFECTIVE_CACHE_SIZE", "1GB"),
            EnableQueryLog = GetEnvironmentVariableOrDefault("POSTGRES_ENABLE_QUERY_LOG", true),
            SlowQueryThreshold = GetEnvironmentVariableOrDefault("POSTGRES_SLOW_QUERY_THRESHOLD", 1000),
            EnableSsl = GetEnvironmentVariableOrDefault("POSTGRES_ENABLE_SSL", false),
            ConnectionTimeout = GetEnvironmentVariableOrDefault("POSTGRES_CONNECTION_TIMEOUT", 30),
            CommandTimeout = GetEnvironmentVariableOrDefault("POSTGRES_COMMAND_TIMEOUT", 30),
            MaxPoolSize = GetEnvironmentVariableOrDefault("POSTGRES_MAX_POOL_SIZE", 10),
            MinPoolSize = GetEnvironmentVariableOrDefault("POSTGRES_MIN_POOL_SIZE", 1),
            ConnectionIdleLifetime = GetEnvironmentVariableOrDefault("POSTGRES_CONNECTION_IDLE_LIFETIME", 300),
            ConnectionPruningInterval = GetEnvironmentVariableOrDefault("POSTGRES_CONNECTION_PRUNING_INTERVAL", 10)
        };
    }
    
    /// <summary>
    /// 构建环境变量字典
    /// </summary>
    public Dictionary<string, string> ToEnvironmentVariables()
    {
        var env = new Dictionary<string, string>
        {
            ["POSTGRES_USER"] = User,
            ["POSTGRES_PASSWORD"] = Password,
            ["POSTGRES_DB"] = Database,
            ["POSTGRES_MAX_CONNECTIONS"] = MaxConnections.ToString(),
            ["POSTGRES_SHARED_BUFFERS"] = SharedBuffers,
            ["POSTGRES_EFFECTIVE_CACHE_SIZE"] = EffectiveCacheSize,
            ["POSTGRES_HOST_AUTH_METHOD"] = "md5",
        ["POSTGRES_INITDB_ARGS"] = "--encoding=UTF-8 --auth-host=md5 --auth-local=trust"
        };
        
        if (EnableQueryLog)
        {
            env["POSTGRES_LOG_STATEMENT"] = "all";
            env["POSTGRES_LOG_MIN_DURATION_STATEMENT"] = SlowQueryThreshold.ToString();
        }
        
        return env;
    }
    
    /// <summary>
    /// 验证配置
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (string.IsNullOrWhiteSpace(User))
            errors.Add("PostgreSQL 用户不能为空");
            
        if (string.IsNullOrWhiteSpace(Password))
            errors.Add("PostgreSQL 密码不能为空");
            
        if (string.IsNullOrWhiteSpace(Database))
            errors.Add("PostgreSQL 数据库名称不能为空");
            
        if (MaxConnections <= 0)
            errors.Add("最大连接数必须大于 0");
            
        if (ConnectionTimeout <= 0)
            errors.Add("连接超时时间必须大于 0");
            
        if (CommandTimeout <= 0)
            errors.Add("命令超时时间必须大于 0");
            
        if (MaxPoolSize <= 0)
            errors.Add("连接池最大大小必须大于 0");
            
        if (MinPoolSize < 0)
            errors.Add("连接池最小大小不能为负数");
            
        if (MinPoolSize > MaxPoolSize)
            errors.Add("连接池最小大小不能大于最大大小");
        
        return errors;
    }
    
    private static string GetEnvironmentVariableOrDefault(string key, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
    
    private static int GetEnvironmentVariableOrDefault(string key, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }
    
    private static bool GetEnvironmentVariableOrDefault(string key, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }
}

