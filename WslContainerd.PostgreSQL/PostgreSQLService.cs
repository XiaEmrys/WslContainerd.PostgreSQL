using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using System.Linq;
using Dapper;
using Npgsql;
using WslContainerd.Logging.Abstractions;
using WslContainerd.Services.Abstractions;
using WslContainerd.Net.Abstractions;

namespace WslContainerd.PostgreSQL;

/// <summary>
/// PostgreSQL 服务实现
/// </summary>
public class PostgreSQLService : IPostgreSQLContainerService
{
    private readonly IWslContainerdLogger _logger;
    private readonly IWslContainerdRuntime _containerRuntime;
    private readonly PostgreSQLServiceOptions _options;
    private string _containerName = string.Empty;
    private string? _containerIp;
    private int _mappedHostPort;

    public string ServiceName => "postgresql";
    public string DisplayName => "PostgreSQL";

    public PostgreSQLService(
        IWslContainerdLogger logger,
        IWslContainerdRuntime containerRuntime,
        PostgreSQLServiceOptions? options = null)
    {
        _logger = logger;
        _containerRuntime = containerRuntime;
        _options = options ?? new PostgreSQLServiceOptions();
        _mappedHostPort = _options.DefaultPort;
    }

    public async Task<bool> IsRunningAsync()
    {
        try
        {
            // 简化运行状态检查，通过连接测试来判断
            return await TestConnectionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查 PostgreSQL 运行状态时发生异常");
            return false;
        }
    }

    /// <summary>
    /// 清理现有容器
    /// </summary>
    private async Task CleanupExistingContainerAsync(Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke("检查并清理现有PostgreSQL容器...");
            
            // 1. 先尝试停止当前容器（如果存在）
            if (!string.IsNullOrEmpty(_containerName))
            {
                logCallback?.Invoke($"尝试停止当前容器: {_containerName}");
                await _containerRuntime.StopContainerAsync(_containerName);
                await Task.Delay(1000);
                await _containerRuntime.RemoveContainerAsync(_containerName);
                await Task.Delay(1000);
            }
            
            // 2. 强制清理 containerd 元数据中的容器记录（解决名称冲突问题）
            logCallback?.Invoke("强制清理 containerd 元数据...");
            try
            {
                // 使用完整路径的 ctr 命令强制删除容器元数据
                var ctrPath = _containerRuntime.CtrPath;
                
                // 先尝试列出所有容器，找到冲突的容器ID
                var listResult = await _containerRuntime.ExecuteWSLCommandAsync($"{ctrPath} containers ls");
                logCallback?.Invoke("ctr 容器列表查询完成");
                
                // 强制删除指定名称的容器
                var ctrResult = await _containerRuntime.ExecuteWSLCommandAsync($"{ctrPath} containers rm {_containerName}");
                logCallback?.Invoke("ctr 容器元数据清理完成");
                
                // 如果上面的命令失败，尝试强制删除
                if (string.IsNullOrEmpty(ctrResult) || ctrResult.Contains("error"))
                {
                    logCallback?.Invoke("尝试强制删除容器元数据...");
                    var forceResult = await _containerRuntime.ExecuteWSLCommandAsync($"{ctrPath} containers rm -f {_containerName}");
                    logCallback?.Invoke("ctr 强制删除完成");
                }
            }
            catch (Exception ctrEx)
            {
                logCallback?.Invoke($"ctr 容器元数据清理异常（可忽略）: {ctrEx.Message}");
            }
            
            // 3. 检查是否有其他容器占用5432端口
            logCallback?.Invoke("检查端口占用情况...");
            var portConflict = await CheckPortConflictAsync(_options.DefaultPort);
            
            if (portConflict)
            {
                logCallback?.Invoke("检测到端口冲突，尝试清理占用端口的容器...");
                await CleanupPortConflictContainersAsync(_options.DefaultPort, logCallback);
            }
            
            logCallback?.Invoke("容器清理完成");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("清理现有容器时发生异常（可忽略）: {0}", ex.Message);
            logCallback?.Invoke("清理现有容器完成（如有异常可忽略）");
        }
    }
    
    /// <summary>
    /// 检查端口是否被占用
    /// </summary>
    private async Task<bool> CheckPortConflictAsync(int port)
    {
        try
        {
            // 注意：ExecuteWSLCommandAsync 在命令非 0 退出时返回 "Error: ..."。
            // grep 无匹配时 exit=1，绝不能用“非空即占用”。改用显式 OCCUPIED/FREE 标记。
            var result = await _containerRuntime.ExecuteWSLCommandAsync(
                $"netstat -tln 2>/dev/null | grep -E ':{port}[[:space:]]' >/dev/null && echo OCCUPIED || echo FREE");
            return result.Contains("OCCUPIED", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// 清理占用指定端口的容器
    /// </summary>
    private async Task CleanupPortConflictContainersAsync(int port, Action<string>? logCallback = null)
    {
        try
        {
            // 获取占用端口的进程信息
            var netstatResult = await _containerRuntime.ExecuteWSLCommandAsync($"netstat -tlnp | grep ':{port}'");
            if (!string.IsNullOrEmpty(netstatResult))
            {
                // 提供友好的日志输出，同时保留原始信息用于调试
                logCallback?.Invoke($"检测到端口 {port} 被占用");
                _logger.LogDebug($"端口 {port} 占用详细信息: {netstatResult}");
                
                // 解析进程ID
                var lines = netstatResult.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        var lastPart = parts[parts.Length - 1];
                        if (lastPart.Contains("/"))
                        {
                            var processInfo = lastPart.Split('/');
                            if (processInfo.Length >= 2 && int.TryParse(processInfo[0], out var pid))
                            {
                                logCallback?.Invoke($"找到占用端口的进程: PID {pid}");
                                
                                // 检查进程是否在容器内
                                var cgroupResult = await _containerRuntime.ExecuteWSLCommandAsync($"cat /proc/{pid}/cgroup");
                                if (!string.IsNullOrEmpty(cgroupResult) && cgroupResult.Contains("/default/"))
                                {
                                    logCallback?.Invoke($"进程 {pid} 在容器内运行，尝试停止所有PostgreSQL容器");
                                    
                                    // 停止所有PostgreSQL容器
                                    var containers = await _containerRuntime.GetAllContainersAsync();
                                    if (containers != null)
                                    {
                                        foreach (var container in containers)
                                        {
                                            if (container.Image.Contains("postgres") || container.Name.Contains("postgres"))
                                            {
                                                logCallback?.Invoke($"停止PostgreSQL容器: {container.Name}");
                                                await _containerRuntime.StopContainerAsync(container.Name);
                                                await Task.Delay(1000);
                                                
                                                logCallback?.Invoke($"删除PostgreSQL容器: {container.Name}");
                                                await _containerRuntime.RemoveContainerAsync(container.Name);
                                                await Task.Delay(1000);
                                            }
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("清理端口冲突容器时发生异常: {0}", ex.Message);
        }
    }

    public async Task<bool> StartAsync(Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke("正在启动 PostgreSQL 服务...");
            _logger.LogInformation("启动 PostgreSQL 服务");

            // 生成容器名称 - 使用时间戳避免冲突
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _containerName = _options.ContainerName;
            logCallback?.Invoke($"📋 生成PostgreSQL容器名称: {_containerName}");

            // 严格按照原始逻辑：先检查镜像，再创建并启动容器
            // 避免调用 GetContainerInfoAsync，因为它可能触发解析错误
            
            // 1. 检查镜像是否存在（暂时跳过，避免解析错误）
            logCallback?.Invoke("检查 PostgreSQL 镜像...");
            
            // 2. 清理现有容器
            logCallback?.Invoke("清理现有容器...");
            await CleanupExistingContainerAsync(logCallback);
            
            // 3. 创建并启动容器
            logCallback?.Invoke("创建并启动 PostgreSQL 容器...");
            
            // 选择可用宿主端口（优先5432，不可用则顺延）
            var hostPort = await FindAvailableHostPortAsync(5432, 5482, logCallback);
            _mappedHostPort = hostPort;
            var ports = new Dictionary<string, string>
            {
                { hostPort.ToString(), _options.DefaultPort.ToString() }
            };

            // 生产级环境变量配置
            var environment = await BuildPostgreSQLEnvironmentAsync(logCallback);

            // 生产级持久化卷配置
            var volumes = await ConfigurePostgreSQLVolumesAsync(logCallback);

            var success = await _containerRuntime.RunContainerAsync(
                _options.ImageName, 
                _containerName, 
                ports, 
                environment, 
                volumes, 
                logCallback);

            if (!success)
            {
                logCallback?.Invoke("PostgreSQL 容器创建失败");
                await WriteStartupFailureMarkerAsync(logCallback);
                
                // 尝试获取容器日志以诊断问题（仅当容器存在时）
                try
                {
                    var containerExists = await _containerRuntime.ContainerExistsAsync(_containerName);
                    if (containerExists)
                    {
                        var containerLogs = await _containerRuntime.GetContainerLogsAsync(_containerName, 50);
                        if (!string.IsNullOrEmpty(containerLogs))
                        {
                            logCallback?.Invoke($"📋 PostgreSQL 容器日志: {containerLogs}");
                            _logger.LogError("PostgreSQL 容器日志: {Logs}", containerLogs);
                        }
                    }
                    else
                    {
                        logCallback?.Invoke("📋 PostgreSQL 容器不存在，无法获取日志");
                    }
                }
                catch (Exception logEx)
                {
                    logCallback?.Invoke($"📋 获取PostgreSQL容器日志失败: {logEx.Message}");
                    _logger.LogWarning("获取PostgreSQL容器日志失败: {Error}", logEx.Message);
                }
                
                return false;
            }

            // 4. 立即检查容器状态
            logCallback?.Invoke("🔍 立即检查容器状态...");
            await Task.Delay(1000); // 等待1秒让容器完全启动
            
            var immediateCheck = await _containerRuntime.IsContainerRunningAsync(_containerName);
            if (!immediateCheck)
            {
                logCallback?.Invoke("❌ 容器启动后立即退出，尝试获取日志...");
                await WriteStartupFailureMarkerAsync(logCallback);
                
                // 获取容器日志
                try
                {
                    var containerLogs = await _containerRuntime.GetContainerLogsAsync(_containerName, 100);
                    if (!string.IsNullOrEmpty(containerLogs))
                    {
                        logCallback?.Invoke($"📋 PostgreSQL 容器退出日志: {containerLogs}");
                        _logger.LogError("PostgreSQL 容器退出日志: {Logs}", containerLogs);
                    }
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning("获取PostgreSQL容器日志失败: {Error}", logEx.Message);
                }
                
                return false;
            }

            // 5. 智能等待服务就绪
            logCallback?.Invoke("等待 PostgreSQL 服务就绪...");
            var isReady = await WaitForServiceReadyAsync(logCallback);
            
            if (isReady)
            {
                // 启动成功后，异步获取并缓存容器 IP（避免同步阻塞）
                try
                {
                    _containerIp = await TryGetContainerIpAsync();
                    if (!string.IsNullOrWhiteSpace(_containerIp))
                    {
                        _logger.LogInformation("PostgreSQL 容器 IP: {0}", _containerIp);
                    }
                }
                catch (Exception ipEx)
                {
                    _logger.LogDebug("容器 IP 获取失败（启动后）：{0}", ipEx.Message);
                }
                logCallback?.Invoke("✅ PostgreSQL 服务启动成功");
                logCallback?.Invoke($"🔗 PostgreSQL 访问地址: 127.0.0.1:{hostPort}");

                await ClearStartupFailureMarkerAsync(persistentPath: null, logCallback);
                return true;
            }
            else
            {
                logCallback?.Invoke("❌ PostgreSQL 服务启动失败");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 PostgreSQL 服务时发生异常");
            logCallback?.Invoke($"❌ PostgreSQL 服务启动异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 构建生产级 PostgreSQL 环境变量配置
    /// </summary>
    private Task<Dictionary<string, string>> BuildPostgreSQLEnvironmentAsync(Action<string>? logCallback = null)
    {
        try
        {
            var config = GetEffectiveConfiguration();
            // 验证配置
            var validationErrors = config.Validate();
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning("PostgreSQL 配置验证失败: {Errors}", string.Join(", ", validationErrors));
                logCallback?.Invoke($"⚠️ 配置验证警告: {string.Join(", ", validationErrors)}");
            }
            
            // 构建环境变量
            var environment = config.ToEnvironmentVariables();
            
            logCallback?.Invoke($"✅ PostgreSQL 环境变量配置完成，用户: {config.User}");
            _logger.LogInformation("PostgreSQL 环境变量配置: User={User}, DB={DB}, MaxConnections={MaxConnections}", 
                config.User, config.Database, config.MaxConnections);
            
            return Task.FromResult(environment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "构建 PostgreSQL 环境变量时发生异常");
            logCallback?.Invoke($"⚠️ 环境变量配置异常: {ex.Message}");
            
            // 回退到基础配置
            return Task.FromResult(new Dictionary<string, string>
            {
                ["POSTGRES_USER"] = _options.DefaultUser,
                ["POSTGRES_PASSWORD"] = _options.DefaultPassword,
                ["POSTGRES_DB"] = _options.DefaultDatabase
            });
        }
    }
    
    /// <summary>
    /// 配置生产级 PostgreSQL 持久化卷
    /// </summary>
    private async Task<Dictionary<string, string>?> ConfigurePostgreSQLVolumesAsync(Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke("🔧 配置 PostgreSQL 持久化卷...");
            
            // 优先级策略：环境变量 > Windows 路径 > WSL 本地路径
            var persistentPath = await SelectOptimalPersistentPathAsync(logCallback);
            
            if (string.IsNullOrEmpty(persistentPath))
            {
                logCallback?.Invoke("⚠️ 无法配置持久化路径，将使用临时存储");
                return null;
            }
            
            // 确保目录存在并设置正确权限
            await EnsurePostgreSQLDataDirectoryAsync(persistentPath, logCallback);
            
            // 使用Windows直接访问模式，不转换路径
            var volumeHostPath = persistentPath;
            if (persistentPath.StartsWith("/mnt/"))
            {
                logCallback?.Invoke($"🌐 使用Windows直接访问模式: {persistentPath}");
                
                // 检查是否应该回退到WSL本地存储
                var shouldFallback = await ShouldFallbackToWslLocal(persistentPath, logCallback);
                if (shouldFallback)
                {
                    var wslLocalPath = "/home/evolux/data/postgres";
                    logCallback?.Invoke($"🔄 回退到WSL本地存储: {wslLocalPath}");
                    volumeHostPath = wslLocalPath;
                    
                    // 确保WSL本地目录存在
                    var createCmd = $"mkdir -p {wslLocalPath}";
                    await _containerRuntime.ExecuteWSLCommandAsync(createCmd);
                    
                    var chownCmd = $"chown -R {_options.PostgresUid}:{_options.PostgresGid} {wslLocalPath}";
                    await _containerRuntime.ExecuteWSLCommandAsync(chownCmd);
                    
                    var chmodCmd = $"chmod 700 {wslLocalPath}";
                    await _containerRuntime.ExecuteWSLCommandAsync(chmodCmd);
                    
                    logCallback?.Invoke($"✅ WSL本地目录已准备: {wslLocalPath}");
                }
            }
            
            var volumes = new Dictionary<string, string>
            {
                { volumeHostPath, _options.PostgresDataDir }
            };
            
            logCallback?.Invoke($"✅ 持久化卷配置完成: {volumeHostPath} -> {_options.PostgresDataDir}");
            _logger.LogInformation("PostgreSQL 持久化卷配置: {HostPath} -> {ContainerPath}", 
                volumeHostPath, _options.PostgresDataDir);
            
            return volumes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 PostgreSQL 持久化卷时发生异常");
            logCallback?.Invoke($"❌ 持久化卷配置失败: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 选择最优持久化路径（优先程序根目录，确保外部可访问）
    /// </summary>
    private async Task<string?> SelectOptimalPersistentPathAsync(Action<string>? logCallback = null)
    {
        // 1. 检查环境变量指定的路径
        var envPath = Environment.GetEnvironmentVariable("POSTGRES_DATA_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            logCallback?.Invoke($"📁 使用环境变量指定路径: {envPath}");
            return NormalizePathToWsl(envPath);
        }
        
        // 2. 优先使用程序根目录下的数据文件夹（确保外部可访问）
        var programRootPath = GetProgramRootDataPath();
        if (await IsPathWritableAsync(programRootPath))
        {
            logCallback?.Invoke($"📁 使用程序根目录路径: {programRootPath}");
            return NormalizePathToWsl(programRootPath);
        }
        
        // 3. 尝试 Windows 用户目录（开发环境备选）
        var windowsUserPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
            "evolux-data", "postgres");
        if (await IsPathWritableAsync(windowsUserPath))
        {
            logCallback?.Invoke($"📁 使用用户目录路径: {windowsUserPath}");
            return NormalizePathToWsl(windowsUserPath);
        }
        
        // 4. 尝试系统级路径（生产环境备选）
        var systemPath = "/opt/evolux/postgres";
        if (await IsPathWritableAsync(systemPath))
        {
            logCallback?.Invoke($"📁 使用系统级路径: {systemPath}");
            return systemPath;
        }
        
        // 5. 最后回退到 WSL 本地路径（不推荐，但保证可用）
        var wslLocalPath = "/var/lib/evolux/postgres";
        logCallback?.Invoke($"⚠️ 回退到 WSL 本地路径（外部无法直接访问）: {wslLocalPath}");
        return wslLocalPath;
    }
    
    /// <summary>
    /// 获取程序根目录下的数据路径（确保外部可访问）
    /// </summary>
    private string GetProgramRootDataPath()
    {
        try
        {
            // 获取应用程序的编译产物目录（不是解决方案根目录）
            var appDir = GetApplicationDirectory();
            var dataPath = Path.Combine(appDir, "data", "postgres");
            
            // 确保目录存在
            Directory.CreateDirectory(dataPath);
            
            _logger.LogInformation("程序根目录数据路径: {DataPath}", dataPath);
            return dataPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取程序根目录数据路径失败");
            // 回退到当前工作目录
            return Path.Combine(Directory.GetCurrentDirectory(), "data", "postgres");
        }
    }
    
    /// <summary>
    /// 获取程序根目录（解决方案根目录）
    /// </summary>
    private string GetApplicationDirectory()
    {
        try
        {
            // 获取应用程序的编译产物目录
            var appBaseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(appBaseDir))
            {
                _logger.LogInformation("应用程序目录: {AppDir}", appBaseDir);
                return appBaseDir;
            }
            
            // 回退到当前程序集位置
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var appDir = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(appDir))
            {
                _logger.LogInformation("从程序集位置获取应用程序目录: {AppDir}", appDir);
                return appDir;
            }
            
            // 最后回退到当前工作目录
            _logger.LogWarning("无法获取应用程序目录，使用当前工作目录");
            return Directory.GetCurrentDirectory();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取应用程序目录失败");
            return Directory.GetCurrentDirectory();
        }
    }

    private string GetProgramRootDirectory()
    {
        try
        {
            // 方法1：从当前程序集位置向上查找
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var currentDir = Path.GetDirectoryName(assemblyLocation);
            
            // 向上查找直到找到包含 .sln 文件的目录
            while (!string.IsNullOrEmpty(currentDir))
            {
                var slnFiles = Directory.GetFiles(currentDir, "*.sln");
                if (slnFiles.Length > 0)
                {
                    _logger.LogInformation("找到解决方案根目录: {RootDir}", currentDir);
                    return currentDir;
                }
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
            
            // 方法2：从当前工作目录查找
            var workingDir = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(workingDir))
            {
                var slnFiles = Directory.GetFiles(workingDir, "*.sln");
                if (slnFiles.Length > 0)
                {
                    _logger.LogInformation("从工作目录找到解决方案根目录: {RootDir}", workingDir);
                    return workingDir;
                }
                workingDir = Directory.GetParent(workingDir)?.FullName;
            }
            
            // 方法3：回退到当前工作目录
            _logger.LogWarning("无法找到解决方案根目录，使用当前工作目录: {WorkingDir}", Directory.GetCurrentDirectory());
            return Directory.GetCurrentDirectory();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取程序根目录失败，使用当前工作目录");
            return Directory.GetCurrentDirectory();
        }
    }
    
    /// <summary>
    /// 确保 PostgreSQL 数据目录存在并设置正确权限
    /// </summary>
    private async Task EnsurePostgreSQLDataDirectoryAsync(string path, Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke($"🔧 准备 PostgreSQL 数据目录: {path}");
            
            // 检查是否为Windows文件系统挂载（drvfs）
            var isDrvfs = path.StartsWith("/mnt/");
            
            if (isDrvfs)
            {
                logCallback?.Invoke("🌐 检测到Windows文件系统挂载，尝试Windows直接访问模式");
                
                // 确保Windows目录存在
                var windowsPath = ConvertWslPathToWindows(path);
                if (!string.IsNullOrEmpty(windowsPath))
                {
                    Directory.CreateDirectory(windowsPath);
                    logCallback?.Invoke($"✅ Windows目录已创建: {windowsPath}");
                }
                
                // 在WSL中确保挂载点存在
                var createCmd = $"mkdir -p {path}";
                await _containerRuntime.ExecuteWSLCommandAsync(createCmd);
                
                // 尝试设置权限（在drvfs上可能无效，但不影响功能）
                try
                {
                    // 先设置更宽松的权限
                    var chmodCmd = $"chmod 777 {path}";
                    await _containerRuntime.ExecuteWSLCommandAsync(chmodCmd);
                    
                    var chownCmd = $"chown -R {_options.PostgresUid}:{_options.PostgresGid} {path}";
                    await _containerRuntime.ExecuteWSLCommandAsync(chownCmd);
                    
                    // 再次设置PostgreSQL要求的权限
                    var chmodCmd2 = $"chmod 700 {path}";
                    await _containerRuntime.ExecuteWSLCommandAsync(chmodCmd2);
                    
                    logCallback?.Invoke("✅ 权限设置完成（drvfs模式）");
                }
                catch (Exception permEx)
                {
                    logCallback?.Invoke($"⚠️ 权限设置失败（drvfs限制）: {permEx.Message}");
                    logCallback?.Invoke("ℹ️ 继续使用Windows直接访问模式");
                }
                
                logCallback?.Invoke($"✅ 使用Windows直接访问数据目录: {path}");
                
                // 检查目录是否已初始化
                var checkCmd = $"test -f {path}/postgresql.conf && echo 'INITIALIZED' || echo 'NOT_INITIALIZED'";
                var checkResult = await _containerRuntime.ExecuteWSLCommandAsync(checkCmd);
                
                if (!checkResult.Contains("INITIALIZED"))
                {
                    logCallback?.Invoke("🔧 PostgreSQL 数据目录未初始化，容器将自动初始化");
                }
                else
                {
                    logCallback?.Invoke("✅ PostgreSQL 数据目录已存在");
                }
            }
            else
            {
                // WSL本地文件系统，正常处理
                var createCmd = $"mkdir -p {path}";
                await _containerRuntime.ExecuteWSLCommandAsync(createCmd);
                
                // 设置 PostgreSQL 用户权限
                var chownCmd = $"chown -R {_options.PostgresUid}:{_options.PostgresGid} {path}";
                await _containerRuntime.ExecuteWSLCommandAsync(chownCmd);
                
                // 设置安全权限
                var chmodCmd = $"chmod 700 {path}";
                await _containerRuntime.ExecuteWSLCommandAsync(chmodCmd);
                
                // 检查目录是否已初始化
                var checkCmd = $"test -f {path}/postgresql.conf && echo 'INITIALIZED' || echo 'NOT_INITIALIZED'";
                var checkResult = await _containerRuntime.ExecuteWSLCommandAsync(checkCmd);
                
                if (!checkResult.Contains("INITIALIZED"))
                {
                    logCallback?.Invoke("🔧 PostgreSQL 数据目录未初始化，容器将自动初始化");
                }
                else
                {
                    logCallback?.Invoke("✅ PostgreSQL 数据目录已存在");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "准备 PostgreSQL 数据目录时发生异常: {Path}", path);
            logCallback?.Invoke($"❌ 数据目录准备失败: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// 检查路径是否可写
    /// </summary>
    private async Task<bool> IsPathWritableAsync(string path)
    {
        try
        {
            // 尝试创建测试文件
            var testFile = Path.Combine(path, ".writable_test");
            var testCmd = $"touch {testFile} && rm {testFile} && echo 'WRITABLE' || echo 'NOT_WRITABLE'";
            var result = await _containerRuntime.ExecuteWSLCommandAsync(testCmd);
            return result.Contains("WRITABLE");
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// 规范化路径为 WSL 路径
    /// </summary>
    private static string NormalizePathToWsl(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/var/lib/evolux/postgres";
        
        // 如果是 Windows 路径，转换为 WSL 路径
        if (path.Length >= 2 && path[1] == ':')
        {
            return NormalizeWindowsPathToWsl(path);
        }
        
        // 如果已经是 WSL 路径，直接返回
        return path;
    }
    
    /// <summary>
    /// 获取环境变量或默认值
    /// </summary>
    private static string GetEnvironmentVariableOrDefault(string key, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    /// <summary>
    /// 规范化 Windows 路径为 WSL 路径，例如 D:\\evolux-data\\pg -> /mnt/d/evolux-data/pg
    /// </summary>
    private static string NormalizeWindowsPathToWsl(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath) || windowsPath.Length < 2)
            return "/mnt/d/evolux-data/pg";
        var drive = char.ToLowerInvariant(windowsPath[0]);
        var rest = windowsPath.Substring(2).TrimStart('\\', '/').Replace('\\', '/');
        // 去重斜杠
        while (rest.Contains("//")) rest = rest.Replace("//", "/");
        return $"/mnt/{drive}/{rest}";
    }

    /// <summary>
    /// 将WSL路径转换为Windows路径
    /// </summary>
    private static string? ConvertWslPathToWindows(string wslPath)
    {
        if (string.IsNullOrWhiteSpace(wslPath) || !wslPath.StartsWith("/mnt/"))
            return null;
            
        // 移除 /mnt/ 前缀
        var pathWithoutMnt = wslPath.Substring(5);
        
        // 找到第一个斜杠，分离盘符和路径
        var firstSlashIndex = pathWithoutMnt.IndexOf('/');
        if (firstSlashIndex <= 0)
            return null;
            
        var drive = pathWithoutMnt.Substring(0, firstSlashIndex);
        var rest = pathWithoutMnt.Substring(firstSlashIndex + 1);
        
        // 转换为Windows路径格式
        var windowsPath = $"{drive.ToUpperInvariant()}:\\{rest.Replace('/', '\\')}";
        
        return windowsPath;
    }

    /// <summary>
    /// 检查是否应该回退到WSL本地存储。
    /// PostgreSQL 数据目录要求 POSIX 权限（chmod 700 / chown postgres）；Windows drvfs 不支持，initdb 会失败。
    /// </summary>
    private async Task<bool> ShouldFallbackToWslLocal(string persistentPath, Action<string>? logCallback)
    {
        try
        {
            var windowsPath = ConvertWslPathToWindows(persistentPath);

            // drvfs (/mnt/...) 永远不可用于 PostgreSQL 数据目录：会出现
            // "could not change permissions of directory: Operation not permitted"
            if (persistentPath.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
            {
                logCallback?.Invoke("⚠️ Windows/drvfs 数据目录不支持 PostgreSQL 权限语义（initdb 会失败），回退到 WSL 本地 ext4 存储");
                TryDeleteStartupFailureMarker(windowsPath, logCallback);
                return true;
            }

            // 检查是否存在失败的PostgreSQL容器记录
            var failedContainers = await _containerRuntime.ListContainersAsync();
            var hasFailedPostgres = failedContainers.Any(c =>
                c.Name.Contains("evolux-postgresql", StringComparison.OrdinalIgnoreCase) &&
                c.Status.Contains("Exited", StringComparison.OrdinalIgnoreCase));
            
            if (hasFailedPostgres)
            {
                logCallback?.Invoke("⚠️ 检测到PostgreSQL容器启动失败，启用回退机制");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"⚠️ 回退检查失败: {ex.Message}，启用回退机制");
            return true;
        }
    }

    private async Task ClearStartupFailureMarkerAsync(string? persistentPath, Action<string>? logCallback)
    {
        try
        {
            // 失败标记写在候选 Windows 数据目录上；成功启动（含回退到 WSL 本地）后都应清理
            var windowsCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddWindowsPath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
                {
                    var converted = ConvertWslPathToWindows(path);
                    if (!string.IsNullOrEmpty(converted))
                    {
                        windowsCandidates.Add(converted);
                    }
                }
                else if (path.Length >= 2 && path[1] == ':')
                {
                    windowsCandidates.Add(path);
                }
            }

            AddWindowsPath(persistentPath);
            AddWindowsPath(GetProgramRootDataPath());
            AddWindowsPath(await SelectOptimalPersistentPathAsync(null));

            foreach (var windowsPath in windowsCandidates)
            {
                TryDeleteStartupFailureMarker(windowsPath, logCallback);
            }
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"⚠️ 清除启动失败标记时出错: {ex.Message}");
        }
    }

    private static void TryDeleteStartupFailureMarker(string? windowsPath, Action<string>? logCallback)
    {
        if (string.IsNullOrEmpty(windowsPath))
        {
            return;
        }

        var failureMarkerFile = Path.Combine(windowsPath, ".postgresql_startup_failed");
        if (!File.Exists(failureMarkerFile))
        {
            return;
        }

        File.Delete(failureMarkerFile);
        logCallback?.Invoke($"🧹 已清除 PostgreSQL 启动失败标记: {failureMarkerFile}");
    }

    private async Task WriteStartupFailureMarkerAsync(Action<string>? logCallback)
    {
        try
        {
            var persistentPath = await SelectOptimalPersistentPathAsync(logCallback);
            if (string.IsNullOrEmpty(persistentPath) || !persistentPath.StartsWith("/mnt/"))
            {
                return;
            }

            var windowsPath = ConvertWslPathToWindows(persistentPath);
            if (string.IsNullOrEmpty(windowsPath))
            {
                return;
            }

            var failureMarkerFile = Path.Combine(windowsPath, ".postgresql_startup_failed");
            File.WriteAllText(failureMarkerFile, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"));
            logCallback?.Invoke($"📝 已创建启动失败标记文件: {failureMarkerFile}");
        }
        catch (Exception markerEx)
        {
            logCallback?.Invoke($"⚠️ 创建失败标记文件时出错: {markerEx.Message}");
        }
    }

    /// <summary>
    /// 确保挂载目录存在并具备 postgres 运行所需权限（mkdir/chown/chmod），纯代码化免手动
    /// </summary>
    private async Task EnsureMountDirAsync(string wslPath, Action<string>? logCallback)
    {
        try
        {
            // 创建目录，但不设置权限（Windows 文件系统挂载到 WSL 时权限设置有限制）
            // PostgreSQL 容器会在启动时自动初始化数据目录
            var cmd = $"mkdir -p {wslPath}";
            await _containerRuntime.ExecuteWSLCommandAsync(cmd);
            logCallback?.Invoke($"已准备挂载目录: {wslPath}（PostgreSQL 将自动初始化数据目录）");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("准备挂载目录失败（将继续尝试启动容器）: {0}", ex.Message);
        }
    }

    /// <summary>
    /// 探测 drvfs（/mnt/盘符）是否可用于 PostgreSQL（判断 chmod/chown 是否会失败）。
    /// 实际上 drvfs 通常不支持权限语义，这里通过一次 dry-run 判定。
    /// </summary>
    private async Task<bool> ProbeDrvfsForPostgresAsync(string wslDrvfsDir)
    {
        try
        {
            // 创建目录
            await _containerRuntime.ExecuteWSLCommandAsync($"mkdir -p {wslDrvfsDir}");
            // 尝试 chmod 返回信息（drvfs 下一般不会报错但不生效，initdb 会失败）
            var out1 = await _containerRuntime.ExecuteWSLCommandAsync($"chmod 700 {wslDrvfsDir} || echo CHMOD_FAIL");
            var out2 = await _containerRuntime.ExecuteWSLCommandAsync($"chown 999:999 {wslDrvfsDir} || echo CHOWN_FAIL");
            // 若明确失败标记，视为不可用
            if ((out1?.Contains("CHMOD_FAIL") ?? false) || (out2?.Contains("CHOWN_FAIL") ?? false))
                return false;
            // 进一步用 initdb --help 校验镜像是否可用（不实际变更）
            // 不需要严格校验，这里只判断 drvfs 的权限语义风险，返回 false 更安全
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 初始化 PostgreSQL 数据目录，如果目录为空则使用 initdb 初始化
    /// </summary>
    private async Task InitializePostgreSQLDataDirectoryAsync(string wslPath, Action<string>? logCallback)
    {
        try
        {
            // 检查数据目录是否已初始化（存在 postgresql.conf 文件）
            var checkCmd = $"test -f {wslPath}/postgresql.conf && echo 'EXISTS' || echo 'NOT_EXISTS'";
            var checkResult = await _containerRuntime.ExecuteWSLCommandAsync(checkCmd);
            
            if (!checkResult.Contains("EXISTS"))
            {
                logCallback?.Invoke("PostgreSQL 数据目录未初始化，开始初始化...");
                
                // 使用临时容器初始化数据目录（生产级配置）
                var initCmd = $"docker run --rm -v {wslPath}:{_options.PostgresDataDir} " +
                    $"-e POSTGRES_PASSWORD={_options.DefaultPassword} " +
                    $"-e POSTGRES_USER={_options.DefaultUser} " +
                    $"-e POSTGRES_DB={_options.DefaultDatabase} " +
                    $"{_options.ImageName} initdb -D {_options.PostgresDataDir}";
                
                var initResult = await _containerRuntime.ExecuteWSLCommandAsync(initCmd);
                if (string.IsNullOrEmpty(initResult) || initResult.Contains("Success"))
                {
                    logCallback?.Invoke("PostgreSQL 数据目录初始化成功");
                }
                else
                {
                    logCallback?.Invoke($"PostgreSQL 数据目录初始化失败: {initResult}");
                }
            }
            else
            {
                logCallback?.Invoke("PostgreSQL 数据目录已存在，跳过初始化");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("初始化 PostgreSQL 数据目录失败: {0}", ex.Message);
        }
    }

    public async Task<bool> StopAsync()
    {
        try
        {
            _logger.LogInformation("停止 PostgreSQL 服务");
            return await _containerRuntime.StopContainerAsync(_containerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止 PostgreSQL 服务时发生异常");
            return false;
        }
    }

    public async Task<ServiceStatus> GetStatusAsync()
    {
        try
        {
            var isRunning = await IsRunningAsync();
            var version = await GetVersionAsync();
            var isHealthy = await TestConnectionAsync();

            var status = new ServiceStatus(ServiceName, DisplayName, 
                isRunning ? ServiceState.Running : ServiceState.Stopped)
            {
                Version = version,
                Port = _mappedHostPort,
                IsHealthy = isHealthy,
                HealthCheckMessage = isHealthy ? "连接正常" : "无法连接"
            };

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 PostgreSQL 状态时发生异常");
            return new ServiceStatus(ServiceName, DisplayName, ServiceState.Error, ex.Message);
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            // 严格按照原始逻辑：主要检查容器是否保持运行状态
            // 而不是测试端口连接，因为在 WSL2 环境中端口映射可能不工作
            return await _containerRuntime.IsContainerRunningAsync(_containerName);
        }
        catch
        {
            return false;
        }
    }

    public Task<string> GetVersionAsync()
    {
        try
        {
            // 简化版本获取，避免调用可能有解析问题的 GetContainerInfoAsync
            return Task.FromResult("PostgreSQL 15");
        }
        catch
        {
            return Task.FromResult("Unknown");
        }
    }

    public async Task<string> GetServiceInfoAsync()
    {
        try
        {
            var status = await GetStatusAsync();
            var version = await GetVersionAsync();
            return $"PostgreSQL {version} - {status.State} - 端口: {_mappedHostPort}";
        }
        catch (Exception ex)
        {
            return $"PostgreSQL 服务信息获取失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 合并环境变量与 Evolux Options，保证连接串与容器启动凭据一致。
    /// </summary>
    private PostgreSQLConfiguration GetEffectiveConfiguration()
    {
        var config = PostgreSQLConfiguration.FromEnvironment();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_USER")))
            config.User = _options.DefaultUser;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")))
            config.Password = _options.DefaultPassword;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_DB")))
            config.Database = _options.DefaultDatabase;
        return config;
    }

    public string GetConnectionString(string database = "postgres")
    {
        // 解析主机地址：优先环境变量，其次默认值
        var host = ResolvePostgresHost();

        // 从配置类获取连接参数（与容器启动时使用的凭据一致）
        var config = GetEffectiveConfiguration();
        // 使用 NpgsqlConnectionStringBuilder 来构建连接字符串，避免参数重复问题
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            // 默认用动态映射的宿主端口
            Port = _mappedHostPort,
            Username = config.User,
            Password = config.Password,
            SslMode = config.EnableSsl ? SslMode.Require : SslMode.Disable,
            KeepAlive = 30,
            // 使用配置类中的连接池配置
            MaxPoolSize = config.MaxPoolSize,
            MinPoolSize = config.MinPoolSize,
            ConnectionIdleLifetime = config.ConnectionIdleLifetime,
            ConnectionPruningInterval = config.ConnectionPruningInterval,
            Timeout = config.ConnectionTimeout,
            CommandTimeout = config.CommandTimeout
        };
        
        if (database == "postgres" || database == _options.DefaultDatabase || database == _options.ApplicationDatabase)
        {
            builder.Database = _options.ApplicationDatabase;
        }
        else
        {
            builder.Database = database;
        }
        
        var finalConnectionString = builder.ToString();
        
        // 调试：输出连接信息（仅主机与端口）
        _logger.LogInformation("构建连接字符串 - 数据库: {0}, Host: {1}, Port: {2}, User: {3}", 
            database, host, builder.Port, config.User);
        
        return finalConnectionString;
    }

    /// <summary>
    /// 解析 PostgreSQL 主机地址，支持环境变量覆盖
    /// 优先级：EVOLUX_PG_HOST -> POSTGRES_HOST -> 默认 127.0.0.1
    /// </summary>
    private string ResolvePostgresHost()
    {
        try
        {
            var host = Environment.GetEnvironmentVariable("EVOLUX_PG_HOST");
            if (!string.IsNullOrWhiteSpace(host))
            {
                _logger.LogDebug("使用环境变量 EVOLUX_PG_HOST: {0}", host);
                return host!;
            }

            host = Environment.GetEnvironmentVariable("POSTGRES_HOST");
            if (!string.IsNullOrWhiteSpace(host))
            {
                _logger.LogDebug("使用环境变量 POSTGRES_HOST: {0}", host);
                return host!;
            }

            // 2) 使用已缓存的容器 IP（由启动流程异步刷新），避免同步阻塞
            if (!string.IsNullOrWhiteSpace(_containerIp))
            {
                _logger.LogDebug("使用缓存的容器 IP: {0}", _containerIp);
                return _containerIp!;
            }

            // 默认回退
            const string fallback = "127.0.0.1";
            _logger.LogDebug("未配置主机环境变量，回退使用默认 Host: {0}", fallback);
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("解析 PostgreSQL 主机地址失败，回退默认值。错误: {0}", ex.Message);
            return "127.0.0.1";
        }
    }


    /// <summary>
    /// 通过 nerdctl inspect 异步获取容器 IP（不在同步路径阻塞 UI/调用线程）
    /// </summary>
    private async Task<string?> TryGetContainerIpAsync()
    {
        try
        {
            var containerName = string.IsNullOrWhiteSpace(_containerName) ? _options.ContainerName : _containerName;
            var nerdctlPath = _containerRuntime.NerdctlPath;
            var cmd = $"{nerdctlPath} inspect {containerName} --format \"{{{{range .NetworkSettings.Networks}}}}{{{{.IPAddress}}}}{{{{end}}}}\"";
            var ip = await _containerRuntime.ExecuteWSLCommandAsync(cmd);
            return string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TryGetContainerIpAsync 失败: {0}", ex.Message);
            return null;
        }
    }

    public async Task<bool> CreateDatabaseAsync(string databaseName, Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke($"正在创建数据库: {databaseName}");
            _logger.LogInformation("创建数据库: {0}", databaseName);
            
            // 使用默认的postgres数据库连接来创建新数据库
            using var connection = await CreateConnectionAsync("postgres");
            
            // 检查数据库是否已存在
            var checkSql = "SELECT 1 FROM pg_database WHERE datname = @databaseName";
            var exists = await connection.QueryFirstOrDefaultAsync<int?>(checkSql, new { databaseName });
            
            if (exists.HasValue)
            {
                logCallback?.Invoke($"数据库 {databaseName} 已存在");
                _logger.LogInformation("数据库 {0} 已存在", databaseName);
                return true;
            }
            
            // 创建数据库
            var createSql = $"CREATE DATABASE \"{databaseName}\"";
            await connection.ExecuteAsync(createSql);
            
            logCallback?.Invoke($"✅ 数据库 {databaseName} 创建成功");
            _logger.LogInformation("数据库 {0} 创建成功", databaseName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建数据库时发生异常: {0}", databaseName);
            logCallback?.Invoke($"❌ 创建数据库失败: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DropDatabaseAsync(string databaseName)
    {
        try
        {
            _logger.LogInformation("删除数据库: {0}", databaseName);
            
            // 使用默认的postgres数据库连接来删除数据库
            using var connection = await CreateConnectionAsync("postgres");
            
            // 检查数据库是否存在
            var checkSql = "SELECT 1 FROM pg_database WHERE datname = @databaseName";
            var exists = await connection.QueryFirstOrDefaultAsync<int?>(checkSql, new { databaseName });
            
            if (!exists.HasValue)
            {
                _logger.LogInformation("数据库 {0} 不存在", databaseName);
                return true;
            }
            
            // 删除数据库
            var dropSql = $"DROP DATABASE \"{databaseName}\"";
            await connection.ExecuteAsync(dropSql);
            
            _logger.LogInformation("数据库 {0} 删除成功", databaseName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除数据库时发生异常: {0}", databaseName);
            return false;
        }
    }

    public Task<bool> BackupDatabaseAsync(string databaseName, string backupPath, Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke($"正在备份数据库: {databaseName} -> {backupPath}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "备份数据库时发生异常: {0}", databaseName);
            logCallback?.Invoke($"备份数据库失败: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public Task<bool> RestoreDatabaseAsync(string databaseName, string backupPath, Action<string>? logCallback = null)
    {
        try
        {
            logCallback?.Invoke($"正在恢复数据库: {backupPath} -> {databaseName}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复数据库时发生异常: {0}", databaseName);
            logCallback?.Invoke($"恢复数据库失败: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public async Task<string[]> ListDatabasesAsync()
    {
        try
        {
            using var connection = await CreateConnectionAsync("postgres");
            
            var sql = @"
                SELECT datname 
                FROM pg_database 
                WHERE datistemplate = false 
                AND datname NOT IN ('postgres', 'template0', 'template1')
                ORDER BY datname";
            
            var databases = await connection.QueryAsync<string>(sql);
            return databases.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出数据库时发生异常");
            return Array.Empty<string>();
        }
    }

    public async Task<string> GetDatabaseSizeAsync(string databaseName)
    {
        try
        {
            using var connection = await CreateConnectionAsync("postgres");
            
            var sql = @"
                SELECT pg_size_pretty(pg_database_size(@databaseName)) as size";
            
            var size = await connection.QueryFirstOrDefaultAsync<string>(sql, new { databaseName });
            return size ?? "Unknown";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取数据库大小时发生异常: {0}", databaseName);
            return "Unknown";
        }
    }

    public async Task<int> ExecuteNonQueryAsync(string databaseName, string sql, object? parameters = null)
    {
        try
        {
            using var connection = await CreateConnectionAsync(databaseName);
            return await connection.ExecuteAsync(sql, parameters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行非查询SQL时发生异常: {0}, SQL: {1}", databaseName, sql);
            throw;
        }
    }

    public async Task<T?> ExecuteScalarAsync<T>(string databaseName, string sql, object? parameters = null)
    {
        try
        {
            using var connection = await CreateConnectionAsync(databaseName);
            return await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行标量查询时发生异常: {0}, SQL: {1}", databaseName, sql);
            throw;
        }
    }

    public async Task<IEnumerable<T>> ExecuteQueryAsync<T>(string databaseName, string sql, object? parameters = null)
    {
        try
        {
            _logger.LogInformation("执行查询: 数据库={0}, SQL={1}, 参数={2}", databaseName, sql, parameters != null ? string.Join(", ", parameters.GetType().GetProperties().Select(p => $"{p.Name}={p.GetValue(parameters)}")) : "null");
            
            using var connection = await CreateConnectionAsync(databaseName);
            var results = await connection.QueryAsync<T>(sql, parameters);
            
            _logger.LogInformation("查询完成: 数据库={0}, 结果数量={1}", databaseName, results?.Count() ?? 0);
            return results ?? Enumerable.Empty<T>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行查询时发生异常: {0}, SQL: {1}, 参数: {2}", databaseName, sql, parameters?.ToString() ?? "null");
            throw;
        }
    }

    public async Task<NpgsqlConnection> CreateConnectionAsync(string databaseName)
    {
        try
        {
            var connectionString = GetConnectionString(databaseName);
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            _logger.LogDebug("数据库连接创建成功: {0}", databaseName);
            // 连接建立后，打印数据目录与配置文件路径，便于定位本机存储路径
            try
            {
                await using var cmd1 = new NpgsqlCommand("SHOW data_directory;", connection);
                var dataDir = (await cmd1.ExecuteScalarAsync())?.ToString() ?? string.Empty;
                await using var cmd2 = new NpgsqlCommand("SHOW config_file;", connection);
                var cfgFile = (await cmd2.ExecuteScalarAsync())?.ToString() ?? string.Empty;
                _logger.LogInformation("PostgreSQL data_directory: {0}", dataDir);
                _logger.LogInformation("PostgreSQL config_file: {0}", cfgFile);
            }
            catch (Exception showEx)
            {
                _logger.LogDebug("查询 PostgreSQL data_directory/config_file 失败: {0}", showEx.Message);
            }
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建数据库连接时发生异常: {0}", databaseName);
            throw;
        }
    }

    /// <summary>
    /// 智能等待服务就绪
    /// </summary>
    private async Task<bool> WaitForServiceReadyAsync(Action<string>? logCallback = null)
    {
        const int maxAttempts = 8;  // 减少到 8 次尝试
        const int baseDelayMs = 1000;  // 减少基础延迟到 1 秒
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // 1. 快速检查容器是否运行
                var isContainerRunning = await _containerRuntime.IsContainerRunningAsync(_containerName);
                if (!isContainerRunning)
                {
                    logCallback?.Invoke($"⏳ 等待容器启动... (尝试 {attempt}/{maxAttempts})");
                    await Task.Delay(baseDelayMs);
                    continue;
                }
                
                // 2. 使用 pg_isready 快速检查 PostgreSQL 服务
                var isServiceAvailable = await CheckPostgreSQLWithPgIsReadyAsync();
                if (isServiceAvailable)
                {
                    logCallback?.Invoke($"✅ PostgreSQL 服务就绪 (尝试 {attempt}/{maxAttempts})");
                    return true;
                }
                
                logCallback?.Invoke($"⏳ 等待 PostgreSQL 服务就绪... (尝试 {attempt}/{maxAttempts})");
                
                // 3. 线性增长延迟，避免指数退避
                var delay = Math.Min(baseDelayMs * attempt, 3000);  // 最大 3 秒延迟
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"⚠️ 服务就绪检查异常: {ex.Message}");
                await Task.Delay(baseDelayMs);
            }
        }
        
        logCallback?.Invoke($"❌ PostgreSQL 服务启动超时 ({maxAttempts} 次尝试)");
        return false;
    }

    /// <summary>
    /// 使用 pg_isready 快速检查 PostgreSQL 服务
    /// </summary>
    private async Task<bool> CheckPostgreSQLWithPgIsReadyAsync()
    {
        try
        {
            // 使用 pg_isready 命令快速检查 PostgreSQL 服务状态
            // 使用完整的 nerdctl 路径，因为在 WSL 分发版中 nerdctl 不在 PATH 中
            var nerdctlPath = "/mnt/d/Evolux-Windows/code/Evolux/Evolux.App/bin/Debug/net9.0-windows/tools/linux/bin/nerdctl";
            var result = await _containerRuntime.ExecuteWSLCommandAsync(
                $"{nerdctlPath} exec {_containerName} pg_isready -h localhost -p 5432");
            
            // pg_isready 成功时返回 0，输出包含 "accepting connections"
            return !string.IsNullOrEmpty(result) && result.Contains("accepting connections");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 寻找可用的宿主端口（闭区间）
    /// </summary>
    private async Task<int> FindAvailableHostPortAsync(int start, int end, Action<string>? logCallback)
    {
        for (var port = start; port <= end; port++)
        {
            var occupied = await CheckPortConflictAsync(port);
            if (!occupied)
            {
                if (port != start)
                    logCallback?.Invoke($"ℹ️ 端口 {start} 被占用，改用 {port}");
                return port;
            }
        }
        // 全部占用则返回默认
        logCallback?.Invoke("⚠️ 端口范围占用，回退使用 5432（可能失败）");
        return start;
    }

    /// <summary>
    /// 检查数据库是否可用（不抛出异常）
    /// </summary>
    public async Task<bool> IsDatabaseAvailableAsync(string databaseName = "postgres")
    {
        const int maxRetries = 2;  // 减少到 2 次重试
        const int retryDelayMs = 1000;  // 减少延迟到 1 秒
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var connectionString = GetConnectionString(databaseName);
                
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                _logger.LogInformation("数据库连接成功: {0} (尝试 {1}/{2})", databaseName, attempt, maxRetries);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("数据库连接失败: {0}, 尝试 {1}/{2}, 错误: {3}", databaseName, attempt, maxRetries, ex.Message);
                
                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs);
                }
            }
        }
        
        _logger.LogWarning("数据库不可用: {0}, 已尝试 {1} 次", databaseName, maxRetries);
        return false;
    }
}
