# 今日任务看板 + 漏做补做 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 新增「今日任务」页面，逐账号显示当天每项任务的完成情况，并支持单项手动补做与每 N 小时一次的自动补做（每项每天最多自动 3 次）。

**架构：** 状态判定由两层数据合成 —— B 站 `/x/member/web/exp/reward`（每日任务 4 项的权威状态）与新增的 `bili_task_records` 执行记录表（"工具今天有没有给这个账号跑过"）。判定逻辑抽成不依赖 IO 的纯函数以便单测；查询、补做、调度分别放在 Service 与 Quartz Job 中。

**技术栈：** .NET 8 / ASP.NET Core Blazor Server + MudBlazor 8.6 / Quartz.NET 3.14 / EF Core 8 (SQLite) / xUnit

**规格：** `docs/superpowers/specs/2026-09-18-today-task-board-design.md`

---

## 文件结构

**新增**

| 文件 | 职责 |
|---|---|
| `src/Ray.BiliBiliTool.Domain/TaskRecord.cs` | 执行记录实体 + `TaskRecordStatus` / `TaskRecordTrigger` 枚举 |
| `src/Ray.BiliBiliTool.Application.Contracts/ITaskRecordWriter.cs` | 记录写入接口（Application 层用，Web 层实现） |
| `src/Ray.BiliBiliTool.Application.Contracts/IAccountTaskAppService.cs` | 单账号执行入口接口 |
| `src/Ray.BiliBiliTool.Config/Options/AutoRecoverOptions.cs` | 自动补做配置项 |
| `src/Ray.BiliBiliTool.Web/Services/TaskCatalog.cs` | 任务/检查项目录（中文名、配置键、数据来源） |
| `src/Ray.BiliBiliTool.Web/Services/TaskDueTimeCalculator.cs` | 纯逻辑：算某天触发时间、是否已到点 |
| `src/Ray.BiliBiliTool.Web/Services/TaskStatusEvaluator.cs` | 纯逻辑：单检查项状态判定 |
| `src/Ray.BiliBiliTool.Web/Services/TodayTaskDtos.cs` | 页面/接口用的 DTO 与状态枚举 |
| `src/Ray.BiliBiliTool.Web/Services/ITodayTaskService.cs` | 状态查询 + 补做编排接口 |
| `src/Ray.BiliBiliTool.Web/Services/TodayTaskService.cs` | 实现：聚合账号、B站状态、记录、判定；执行补做 |
| `src/Ray.BiliBiliTool.Web/Services/TaskRecoveryExecutor.cs` | 单项补做的动作分发 |
| `src/Ray.BiliBiliTool.Web/Services/TaskRecordWriter.cs` | `ITaskRecordWriter` 的 EF 实现 |
| `src/Ray.BiliBiliTool.Web/Jobs/AutoRecoverJob.cs` | 自动补做 Quartz Job |
| `src/Ray.BiliBiliTool.Web/Components/Pages/Today/Today.razor` / `.razor.cs` | 页面 |
| `test/TodayTaskTest/TodayTaskTest.csproj` | 新测试工程（引用 Web 项目） |
| `test/TodayTaskTest/TaskDueTimeCalculatorTest.cs` | 到点计算测试 |
| `test/TodayTaskTest/TaskStatusEvaluatorTest.cs` | 状态判定测试 |
| `test/TodayTaskTest/TaskRecordWriterTest.cs` | 记录写入/查询测试（SQLite 临时库） |

**修改**

| 文件 | 改动 |
|---|---|
| `src/Ray.BiliBiliTool.Infrastructure.EF/BiliDbContext.cs` | 注册 `DbSet<TaskRecord>` 与索引 |
| `src/Ray.BiliBiliTool.Infrastructure.EF/Migrations/*` | 新迁移 + snapshot |
| `src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs` | 循环内写执行记录；新增单账号执行入口 |
| `src/Ray.BiliBiliTool.DomainService/VideoDomainService.cs` | `GetRandomVideoForWatchAndShare`、`OpenVideo` 改 `public` |
| `src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs` | 注册 `AutoRecoverOptions` |
| `src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs` | 注册新服务 |
| `src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs` | 注册 `AutoRecoverJob` |
| `src/Ray.BiliBiliTool.Web/Components/Layout/NavMenu.razor` | 新增导航项 |
| `src/Ray.BiliBiliTool.Web/appsettings.json` | 新增 `AutoRecoverConfig` 默认节 |
| `Ray.BiliBiliTool.sln` | 加入 `TodayTaskTest` |

---

## 任务 1：测试工程骨架 + 到点时间计算（纯逻辑）

先做这一步是为了尽早验证「测试工程能引用 Web 项目」这个风险点。

**文件：**
- 创建：`test/TodayTaskTest/TodayTaskTest.csproj`
- 创建：`test/TodayTaskTest/Usings.cs`
- 创建：`test/TodayTaskTest/TaskDueTimeCalculatorTest.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/TaskDueTimeCalculator.cs`
- 修改：`Ray.BiliBiliTool.sln`

- [ ] **步骤 1：创建测试工程**

`test/TodayTaskTest/TodayTaskTest.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Ray.BiliBiliTool.Web\Ray.BiliBiliTool.Web.csproj" />
  </ItemGroup>
</Project>
```

`test/TodayTaskTest/Usings.cs`：

```csharp
global using Ray.BiliBiliTool.Web.Services;
global using Xunit;
```

- [ ] **步骤 2：把项目加入解决方案并验证能编译**

```bash
dotnet sln Ray.BiliBiliTool.sln add test/TodayTaskTest/TodayTaskTest.csproj
dotnet build test/TodayTaskTest/TodayTaskTest.csproj
```

预期：编译成功（0 error）。若 Web 项目无法被引用（框架引用不传递），在此处停下并改用「把纯逻辑放进 `Ray.BiliBiliTool.Domain` 类库」的备选方案。

- [ ] **步骤 3：编写失败的测试**

`test/TodayTaskTest/TaskDueTimeCalculatorTest.cs`：

```csharp
namespace TodayTaskTest;

public class TaskDueTimeCalculatorTest
{
    private static readonly TimeSpan CnOffset = TimeSpan.FromHours(8);

    private static DateTimeOffset Cn(int y, int m, int d, int hh, int mm) =>
        new(y, m, d, hh, mm, 0, CnOffset);

    [Fact]
    public void 每日一次的cron_今天只有一个触发点()
    {
        var times = TaskDueTimeCalculator.GetFireTimesOfDay("0 0 15 * * ?", Cn(2026, 9, 18, 8, 0));

        Assert.Single(times);
        Assert.Equal(Cn(2026, 9, 18, 15, 0), times[0]);
    }

    [Fact]
    public void 每月28号的cron_在18号没有触发点()
    {
        var times = TaskDueTimeCalculator.GetFireTimesOfDay("0 0 12 28 * ?", Cn(2026, 9, 18, 8, 0));

        Assert.Empty(times);
    }

    [Fact]
    public void 每小时一次的cron_今天有24个触发点()
    {
        var times = TaskDueTimeCalculator.GetFireTimesOfDay("0 0 * * * ?", Cn(2026, 9, 18, 8, 0));

        Assert.Equal(24, times.Count);
        Assert.Equal(Cn(2026, 9, 18, 0, 0), times[0]);
        Assert.Equal(Cn(2026, 9, 18, 23, 0), times[^1]);
    }

    [Fact]
    public void 未到今天的触发时间_判定为未到点()
    {
        Assert.False(TaskDueTimeCalculator.IsDue("0 0 15 * * ?", Cn(2026, 9, 18, 12, 0)));
    }

    [Fact]
    public void 刚过触发时间但在宽限期内_仍判定为未到点()
    {
        Assert.False(TaskDueTimeCalculator.IsDue("0 0 15 * * ?", Cn(2026, 9, 18, 15, 5)));
    }

    [Fact]
    public void 超过触发时间加宽限期_判定为已到点()
    {
        Assert.True(TaskDueTimeCalculator.IsDue("0 0 15 * * ?", Cn(2026, 9, 18, 15, 11)));
    }

    [Fact]
    public void 今天没有触发点_永远不算到点()
    {
        Assert.False(TaskDueTimeCalculator.IsDue("0 0 12 28 * ?", Cn(2026, 9, 18, 23, 0)));
    }

    [Fact]
    public void 非法cron_返回空且不算到点()
    {
        Assert.Empty(TaskDueTimeCalculator.GetFireTimesOfDay("这不是cron", Cn(2026, 9, 18, 8, 0)));
        Assert.False(TaskDueTimeCalculator.IsDue("这不是cron", Cn(2026, 9, 18, 8, 0)));
        Assert.False(TaskDueTimeCalculator.IsDue(null, Cn(2026, 9, 18, 8, 0)));
    }
}
```

- [ ] **步骤 4：运行测试验证失败**

运行：`dotnet test test/TodayTaskTest/TodayTaskTest.csproj --filter TaskDueTimeCalculatorTest`
预期：编译失败，报 `TaskDueTimeCalculator` 不存在。

- [ ] **步骤 5：编写实现**

`src/Ray.BiliBiliTool.Web/Services/TaskDueTimeCalculator.cs`：

```csharp
using Quartz;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// 计算任务「今天该做的时间点」，用于判断是否已经到点 —— 自动补做不能抢在计划时间之前执行。
/// 纯逻辑，无 IO，便于单测。
/// </summary>
public static class TaskDueTimeCalculator
{
    /// <summary>触发时间过后多久才算「真的漏了」</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 枚举 cron 在指定日期这一天的全部触发时间（升序）。cron 为空/非法时返回空列表。
    /// </summary>
    public static List<DateTimeOffset> GetFireTimesOfDay(string? cron, DateTimeOffset day)
    {
        var result = new List<DateTimeOffset>();
        if (string.IsNullOrWhiteSpace(cron) || !CronExpression.IsValidExpression(cron))
        {
            return result;
        }

        var expression = new CronExpression(cron);
        var start = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, day.Offset);
        var end = start.AddDays(1);

        var cursor = expression.GetNextValidTimeAfter(start);
        while (cursor.HasValue && cursor.Value < end)
        {
            result.Add(cursor.Value);
            cursor = expression.GetNextValidTimeAfter(cursor.Value);
        }

        return result;
    }

    /// <summary>
    /// 该任务今天是否已经到点。今天没有任何触发点 → 返回 false（本日无需执行）。
    /// </summary>
    public static bool IsDue(string? cron, DateTimeOffset now, TimeSpan? grace = null)
    {
        var fireTimes = GetFireTimesOfDay(cron, now);
        if (fireTimes.Count == 0)
        {
            return false;
        }

        return now >= fireTimes[^1] + (grace ?? DefaultGrace);
    }
}
```

- [ ] **步骤 6：运行测试验证通过**

运行：`dotnet test test/TodayTaskTest/TodayTaskTest.csproj --filter TaskDueTimeCalculatorTest`
预期：8 个测试全部 PASS。

- [ ] **步骤 7：Commit**

```bash
git add test/TodayTaskTest src/Ray.BiliBiliTool.Web/Services/TaskDueTimeCalculator.cs Ray.BiliBiliTool.sln
git commit -m "test(today): 新增测试工程与任务到点时间计算"
```

---

## 任务 2：执行记录表（实体 + 迁移 + 写入）

**文件：**
- 创建：`src/Ray.BiliBiliTool.Domain/TaskRecord.cs`
- 创建：`src/Ray.BiliBiliTool.Application.Contracts/ITaskRecordWriter.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/TaskRecordWriter.cs`
- 创建：`test/TodayTaskTest/TaskRecordWriterTest.cs`
- 修改：`src/Ray.BiliBiliTool.Infrastructure.EF/BiliDbContext.cs`
- 修改：`src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs`
- 修改：`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs`
- 新增：`src/Ray.BiliBiliTool.Infrastructure.EF/Migrations/<时间戳>_AddTaskRecords.cs`

- [ ] **步骤 1：创建实体**

`src/Ray.BiliBiliTool.Domain/TaskRecord.cs`：

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ray.BiliBiliTool.Domain;

/// <summary>
/// 账号任务执行记录：某个账号在某天执行了某项任务（或子项），结果如何。
/// 用于回答「今天这个账号的这个任务到底跑没跑过」。
/// </summary>
[Table("bili_task_records")]
public class TaskRecord
{
    [Key]
    public long Id { get; set; }

    /// <summary>B站 UID</summary>
    public long UserId { get; set; }

    /// <summary>任务键（AppService 类型名，如 DailyTaskAppService）</summary>
    [MaxLength(64)]
    public string TaskKey { get; set; } = "";

    /// <summary>子项键（Login / Watch / Share / DonateCoin / VipPrivilege）；null 表示任务级记录</summary>
    [MaxLength(64)]
    public string? TaskItemKey { get; set; }

    /// <summary>本地日期（Asia/Shanghai），格式 yyyy-MM-dd</summary>
    [MaxLength(10)]
    public string RecordDate { get; set; } = "";

    public TaskRecordStatus Status { get; set; }

    /// <summary>失败原因摘要</summary>
    [MaxLength(512)]
    public string? Message { get; set; }

    public TaskRecordTrigger Trigger { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum TaskRecordStatus
{
    Success,
    Failed,
}

public enum TaskRecordTrigger
{
    /// <summary>定时任务自己执行的</summary>
    Scheduled,

    /// <summary>自动补做</summary>
    Auto,

    /// <summary>页面手动补做</summary>
    Manual,
}
```

- [ ] **步骤 2：在 DbContext 中注册**

`src/Ray.BiliBiliTool.Infrastructure.EF/BiliDbContext.cs`，在 `DbSet<User> Users` 之后加一行：

```csharp
    public DbSet<TaskRecord> TaskRecords { get; set; }
```

在 `OnModelCreating` 的 `modelBuilder.Entity<User>(...)` 之后追加：

```csharp
        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Trigger).HasConversion<string>();
            entity.HasIndex(e => new
            {
                e.UserId,
                e.TaskKey,
                e.RecordDate,
            });
        });
```

- [ ] **步骤 3：安装 EF 工具并生成迁移**

```bash
dotnet tool install --global dotnet-ef --version 8.*
dotnet ef migrations add AddTaskRecords --project src/Ray.BiliBiliTool.Infrastructure.EF --startup-project src/Ray.BiliBiliTool.Web --output-dir Migrations --namespace Ray.BiliBiliTool.Web.Migrations
```

预期：在 `src/Ray.BiliBiliTool.Infrastructure.EF/Migrations/` 下新增 `*_AddTaskRecords.cs`、`*_AddTaskRecords.Designer.cs`，并更新 `BiliDbContextModelSnapshot.cs`。

验证生成的迁移文件里只有 `CreateTable("bili_task_records")` 与一个 `CreateIndex`，**没有任何对既有表的 Drop/Alter**。若出现既有表的改动，说明模型与快照不一致，必须先查清原因再继续。

- [ ] **步骤 4：创建写入接口与实现**

`src/Ray.BiliBiliTool.Application.Contracts/ITaskRecordWriter.cs`：

```csharp
using Ray.BiliBiliTool.Domain;

namespace Ray.BiliBiliTool.Application.Contracts;

/// <summary>
/// 任务执行记录的写入接口。由 Web 层用 EF 实现，Application 层只依赖此抽象。
/// </summary>
public interface ITaskRecordWriter
{
    Task WriteAsync(
        long userId,
        string taskKey,
        string? taskItemKey,
        TaskRecordStatus status,
        string? message,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken = default
    );
}
```

`src/Ray.BiliBiliTool.Web/Services/TaskRecordWriter.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.Infrastructure.EF;

namespace Ray.BiliBiliTool.Web.Services;

public class TaskRecordWriter(
    IDbContextFactory<BiliDbContext> dbContextFactory,
    ILogger<TaskRecordWriter> logger
) : ITaskRecordWriter
{
    /// <summary>记录写入统一使用本地日期（容器时区为 Asia/Shanghai）</summary>
    public static string TodayDateKey() => DateTimeOffset.Now.ToString("yyyy-MM-dd");

    public async Task WriteAsync(
        long userId,
        string taskKey,
        string? taskItemKey,
        TaskRecordStatus status,
        string? message,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            db.TaskRecords.Add(
                new TaskRecord
                {
                    UserId = userId,
                    TaskKey = taskKey,
                    TaskItemKey = taskItemKey,
                    RecordDate = TodayDateKey(),
                    Status = status,
                    Message = Truncate(message, 512),
                    Trigger = trigger,
                }
            );
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 记录失败不能影响任务本身
            logger.LogWarning(ex, "写入任务执行记录失败：{taskKey}/{itemKey}", taskKey, taskItemKey);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
```

- [ ] **步骤 5：编写失败的测试（记录能写进去、能查出来）**

`test/TodayTaskTest/TaskRecordWriterTest.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.Infrastructure.EF;

namespace TodayTaskTest;

public class TaskRecordWriterTest : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"bilitool-test-{Guid.NewGuid():N}.db"
    );
    private readonly IDbContextFactory<BiliDbContext> _factory;

    public TaskRecordWriterTest()
    {
        var options = new DbContextOptionsBuilder<BiliDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _factory = new PooledDbContextFactory<BiliDbContext>(options);

        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task 写入一条记录后能按账号与日期查出来()
    {
        var writer = new TaskRecordWriter(_factory, NullLogger<TaskRecordWriter>.Instance);

        await writer.WriteAsync(
            1001,
            "DailyTaskAppService",
            "DonateCoin",
            TaskRecordStatus.Success,
            null,
            TaskRecordTrigger.Manual
        );

        await using var db = await _factory.CreateDbContextAsync();
        var record = Assert.Single(db.TaskRecords.Where(r => r.UserId == 1001));
        Assert.Equal("DailyTaskAppService", record.TaskKey);
        Assert.Equal("DonateCoin", record.TaskItemKey);
        Assert.Equal(TaskRecordStatus.Success, record.Status);
        Assert.Equal(TaskRecordTrigger.Manual, record.Trigger);
        Assert.Equal(DateTimeOffset.Now.ToString("yyyy-MM-dd"), record.RecordDate);
    }

    [Fact]
    public async Task 失败信息超过512字时被截断()
    {
        var writer = new TaskRecordWriter(_factory, NullLogger<TaskRecordWriter>.Instance);
        var longMessage = new string('错', 600);

        await writer.WriteAsync(
            1002,
            "DailyTaskAppService",
            null,
            TaskRecordStatus.Failed,
            longMessage,
            TaskRecordTrigger.Scheduled
        );

        await using var db = await _factory.CreateDbContextAsync();
        var record = Assert.Single(db.TaskRecords.Where(r => r.UserId == 1002));
        Assert.Equal(512, record.Message!.Length);
    }
}
```

- [ ] **步骤 6：运行测试验证通过**

运行：`dotnet test test/TodayTaskTest/TodayTaskTest.csproj --filter TaskRecordWriterTest`
预期：2 个测试 PASS。

> 说明：`BiliDbContext` 的构造函数是 `BiliDbContext(IConfiguration config)` 且 `OnConfiguring` 会用配置里的连接串覆盖传入的 options。因此 `PooledDbContextFactory<BiliDbContext>(options)` 这一路径需要 `BiliDbContext` 在 `OnConfiguring` 中尊重已配置的 provider。若测试因连接串被覆盖而失败，改为把测试库路径写进一个内存 `ConfigurationBuilder`（键 `ConnectionStrings:Sqlite`）并用 `new BiliDbContext(config)` 构造，测试逻辑不变。

- [ ] **步骤 7：在定时执行时写任务级记录**

修改 `src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs`。

顶部 using：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.Infrastructure;
```

`DoTaskAsync` 替换为：

```csharp
    public override async Task DoTaskAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "【账号个数】{count}个" + Environment.NewLine,
            cookieStrFactory.Count
        );
        for (int i = 0; i < cookieStrFactory.Count; i++)
        {
            logger.LogInformation("######### 账号 {num} #########" + Environment.NewLine, i);
            var ck = cookieStrFactory.GetCookie(i);
            try
            {
                await DoTaskAccountAsync(ck, cancellationToken);
                await WriteRecordAsync(
                    ck,
                    TaskRecordStatus.Success,
                    null,
                    TaskRecordTrigger.Scheduled,
                    cancellationToken
                );
            }
            catch (Exception e)
            {
                //ignore
                logger.LogWarning("异常：{msg}", e);
                await WriteRecordAsync(
                    ck,
                    TaskRecordStatus.Failed,
                    e.Message,
                    TaskRecordTrigger.Scheduled,
                    cancellationToken
                );
            }
        }
    }
```

在同一类中新增：

```csharp
    /// <summary>
    /// 任务键：取具体 AppService 的类型名（如 DailyTaskAppService）。
    /// 刻意不改动 11 个既有子类。
    /// </summary>
    public string TaskKey => GetType().Name;

    private async Task WriteRecordAsync(
        BiliCookie ck,
        TaskRecordStatus status,
        string? message,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        var writer = Global.ServiceProviderRoot?.GetService<ITaskRecordWriter>();
        if (writer is null)
        {
            return;
        }

        if (!long.TryParse(ck.UserId, out var userId))
        {
            return;
        }

        await writer.WriteAsync(
            userId,
            TaskKey,
            null,
            status,
            message,
            trigger,
            cancellationToken
        );
    }
```

- [ ] **步骤 8：注册服务**

`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs` 的 `AddWebServices` 中追加：

```csharp
        services.AddSingleton<ITaskRecordWriter, TaskRecordWriter>();
```

并在文件顶部补 using：

```csharp
using Ray.BiliBiliTool.Application.Contracts;
```

- [ ] **步骤 9：编译并跑全部测试**

```bash
dotnet build Ray.BiliBiliTool.sln
dotnet test test/TodayTaskTest/TodayTaskTest.csproj
```

预期：编译 0 error；10 个测试全部 PASS。

- [ ] **步骤 10：Commit**

```bash
git add src/Ray.BiliBiliTool.Domain/TaskRecord.cs src/Ray.BiliBiliTool.Application.Contracts/ITaskRecordWriter.cs src/Ray.BiliBiliTool.Web/Services/TaskRecordWriter.cs src/Ray.BiliBiliTool.Infrastructure.EF src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs test/TodayTaskTest
git commit -m "feat(today): 新增任务执行记录表与写入点"
```

---

## 任务 3：任务目录 + 状态判定（纯逻辑）

**文件：**
- 创建：`src/Ray.BiliBiliTool.Web/Services/TaskCatalog.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/TaskStatusEvaluator.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/TodayTaskDtos.cs`
- 创建：`test/TodayTaskTest/TaskStatusEvaluatorTest.cs`
- 创建：`test/TodayTaskTest/TaskCatalogTest.cs`

- [ ] **步骤 1：定义 DTO 与状态枚举**

`src/Ray.BiliBiliTool.Web/Services/TodayTaskDtos.cs`：

```csharp
using Ray.BiliBiliTool.Domain;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>页面上单个检查项的状态</summary>
public enum TodayTaskItemState
{
    /// <summary>❓ 状态未知（B站查询失败/Cookie失效）</summary>
    Unknown,

    /// <summary>✅ 已完成</summary>
    Completed,

    /// <summary>❌ 今天还没做</summary>
    NotDone,

    /// <summary>⚠️ 执行过但失败</summary>
    Failed,

    /// <summary>⚠️ 自动重试已达上限仍未完成</summary>
    RetryExhausted,

    /// <summary>⏳ 等待执行（今天的计划时间还没到）</summary>
    Waiting,

    /// <summary>➖ 本日无需执行</summary>
    NotToday,

    /// <summary>⛔ 已关闭</summary>
    Disabled,
}

/// <summary>B站每日任务接口的当日快照</summary>
public sealed record BiliDailyRewardSnapshot(bool Login, bool Watch, bool Share, int CoinExp);

/// <summary>判定单个检查项所需的全部输入</summary>
public sealed class TodayTaskItemContext
{
    public required TaskDefinition Task { get; init; }
    public required TaskItemDefinition Item { get; init; }

    /// <summary>任务总开关</summary>
    public required bool IsTaskEnabled { get; init; }

    /// <summary>检查项自身的开关</summary>
    public required bool IsItemEnabled { get; init; }

    /// <summary>该任务今天有没有触发点</summary>
    public required bool HasFireTimeToday { get; init; }

    /// <summary>今天最后一次触发时间是否已过（含宽限期）</summary>
    public required bool IsPastDueTime { get; init; }

    /// <summary>B站每日任务状态；仅当 Item.Source == BiliDailyReward 且查询成功时非 null</summary>
    public BiliDailyRewardSnapshot? BiliReward { get; init; }

    /// <summary>B站查询是否失败（网络异常 / Cookie 失效）</summary>
    public bool BiliQueryFailed { get; init; }

    /// <summary>今天该账号该任务的全部执行记录（升序）</summary>
    public required IReadOnlyList<TaskRecord> Records { get; init; }

    /// <summary>今天该检查项被自动执行的次数</summary>
    public required int AutoAttempts { get; init; }

    /// <summary>自动执行次数上限</summary>
    public required int MaxAutoAttempts { get; init; }
}

/// <summary>判定结果</summary>
public sealed record TodayTaskItemResult(
    TodayTaskItemState State,
    string? Message,
    DateTimeOffset? CompletedAt,
    int AutoAttempts);
```

- [ ] **步骤 2：定义任务目录**

`src/Ray.BiliBiliTool.Web/Services/TaskCatalog.cs`：

```csharp
using Microsoft.Extensions.Configuration;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>检查项的状态来源</summary>
public enum TaskItemSource
{
    /// <summary>B站每日任务接口（/x/member/web/exp/reward）</summary>
    BiliDailyReward,

    /// <summary>本工具的执行记录表</summary>
    ExecutionRecord,
}

/// <summary>一个检查项（页面上的一行）。ItemKey 为 null 表示「整任务算一项」。</summary>
public sealed class TaskItemDefinition(
    string? itemKey,
    string displayName,
    TaskItemSource source,
    Func<IConfiguration, bool> isEnabled
)
{
    public string? ItemKey { get; } = itemKey;
    public string DisplayName { get; } = displayName;
    public TaskItemSource Source { get; } = source;

    /// <summary>该项是否开启（任务总开关由 TaskDefinition.IsEnabled 另行判断）</summary>
    public Func<IConfiguration, bool> IsEnabled { get; } = isEnabled;
}

/// <summary>一个任务（页面上的一组）</summary>
public sealed class TaskDefinition(
    string taskKey,
    string jobName,
    string displayName,
    Func<IConfiguration, bool> isEnabled,
    IReadOnlyList<TaskItemDefinition> items
)
{
    /// <summary>执行记录里的 TaskKey（AppService 类型名）</summary>
    public string TaskKey { get; } = taskKey;

    /// <summary>Quartz 里的 Job 名，用于读取 Cron</summary>
    public string JobName { get; } = jobName;

    public string DisplayName { get; } = displayName;
    public Func<IConfiguration, bool> IsEnabled { get; } = isEnabled;
    public IReadOnlyList<TaskItemDefinition> Items { get; } = items;
}

public static class TaskCatalog
{
    /// <summary>分享子项键（命中 B 站风控特例，见规格 §5.4）</summary>
    public const string ShareItemKey = "Share";

    public static IReadOnlyList<TaskDefinition> All { get; } =
    [
        new TaskDefinition(
            taskKey: "DailyTaskAppService",
            jobName: "DailyJob",
            displayName: "每日任务",
            isEnabled: c => c.GetValue("DailyTaskConfig:IsEnable", true),
            items:
            [
                new TaskItemDefinition(
                    "Login",
                    "登录",
                    TaskItemSource.BiliDailyReward,
                    c => c.GetValue("DailyTaskConfig:IsEnable", true)
                ),
                new TaskItemDefinition(
                    "Watch",
                    "观看视频",
                    TaskItemSource.BiliDailyReward,
                    c => c.GetValue("DailyTaskConfig:IsWatchVideo", true)
                ),
                new TaskItemDefinition(
                    ShareItemKey,
                    "分享视频",
                    TaskItemSource.BiliDailyReward,
                    c => c.GetValue("DailyTaskConfig:IsShareVideo", true)
                ),
                new TaskItemDefinition(
                    "DonateCoin",
                    "投币",
                    TaskItemSource.BiliDailyReward,
                    c => c.GetValue("DailyTaskConfig:NumberOfCoins", 5) > 0
                ),
                new TaskItemDefinition(
                    "VipPrivilege",
                    "大会员福利",
                    TaskItemSource.ExecutionRecord,
                    c => c.GetValue("DailyTaskConfig:IsEnable", true)
                ),
            ]
        ),
        new TaskDefinition(
            "LiveFansMedalAppService",
            "LiveFansMedalJob",
            "直播粉丝勋章",
            c => c.GetValue("LiveFansMedalTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "直播粉丝勋章", TaskItemSource.ExecutionRecord, c => c.GetValue("LiveFansMedalTaskConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "MangaTaskAppService",
            "MangaJob",
            "漫画签到/阅读",
            c => c.GetValue("MangaTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "漫画签到/阅读", TaskItemSource.ExecutionRecord, c => c.GetValue("MangaTaskConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "MangaPrivilegeTaskAppService",
            "MangaPrivilegeJob",
            "漫画特权",
            c => c.GetValue("MangaPrivilegeTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "漫画特权", TaskItemSource.ExecutionRecord, c => c.GetValue("MangaPrivilegeTaskConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "Silver2CoinTaskAppService",
            "Silver2CoinJob",
            "银瓜子换硬币",
            c => c.GetValue("Silver2CoinTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "银瓜子换硬币", TaskItemSource.ExecutionRecord, c => c.GetValue("Silver2CoinTaskConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "LiveLotteryTaskAppService",
            "LiveLotteryJob",
            "直播抽奖",
            c => c.GetValue("LiveLotteryTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "直播抽奖", TaskItemSource.ExecutionRecord, c => c.GetValue("LiveLotteryTaskConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "ChargeTaskAppService",
            "ChargeJob",
            "充电",
            c => c.GetValue("ChargeTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "充电", TaskItemSource.ExecutionRecord, c => c.GetValue("ChargeTaskConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "VipBigPointAppService",
            "VipBigPointJob",
            "大会员积分",
            c => c.GetValue("VipBigPointConfig:IsEnable", true),
            [new TaskItemDefinition(null, "大会员积分", TaskItemSource.ExecutionRecord, c => c.GetValue("VipBigPointConfig:IsEnable", true))]
        ),
        new TaskDefinition(
            "UnfollowBatchedTaskAppService",
            "UnfollowBatchedJob",
            "批量取关",
            c => c.GetValue("UnfollowBatchedTaskConfig:IsEnable", true),
            [new TaskItemDefinition(null, "批量取关", TaskItemSource.ExecutionRecord, c => c.GetValue("UnfollowBatchedTaskConfig:IsEnable", true))]
        ),
    ];
}
```

- [ ] **步骤 3：编写失败的测试**

`test/TodayTaskTest/TaskStatusEvaluatorTest.cs`：

```csharp
using Microsoft.Extensions.Configuration;
using Ray.BiliBiliTool.Domain;

namespace TodayTaskTest;

public class TaskStatusEvaluatorTest
{
    private static readonly TaskDefinition DailyTask = TaskCatalog.All.First(t =>
        t.TaskKey == "DailyTaskAppService"
    );

    private static TaskItemDefinition Item(string key) =>
        DailyTask.Items.Single(i => i.ItemKey == key);

    private static TaskRecord Rec(
        TaskRecordStatus status,
        string? itemKey,
        TaskRecordTrigger trigger = TaskRecordTrigger.Scheduled,
        long userId = 1
    ) =>
        new()
        {
            UserId = userId,
            TaskKey = DailyTask.TaskKey,
            TaskItemKey = itemKey,
            RecordDate = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
            Status = status,
            Trigger = trigger,
        };

    private static TodayTaskItemContext Ctx(
        string itemKey,
        bool isTaskEnabled = true,
        bool isItemEnabled = true,
        bool hasFireTimeToday = true,
        bool isPastDueTime = true,
        BiliDailyRewardSnapshot? bili = null,
        bool biliQueryFailed = false,
        IReadOnlyList<TaskRecord>? records = null,
        int autoAttempts = 0,
        int maxAutoAttempts = 3
    ) =>
        new()
        {
            Task = DailyTask,
            Item = Item(itemKey),
            IsTaskEnabled = isTaskEnabled,
            IsItemEnabled = isItemEnabled,
            HasFireTimeToday = hasFireTimeToday,
            IsPastDueTime = isPastDueTime,
            BiliReward = bili,
            BiliQueryFailed = biliQueryFailed,
            Records = records ?? [],
            AutoAttempts = autoAttempts,
            MaxAutoAttempts = maxAutoAttempts,
        };

    [Fact]
    public void 任务被关闭时显示已关闭()
    {
        var r = TaskStatusEvaluator.Evaluate(Ctx("Login", isTaskEnabled: false));
        Assert.Equal(TodayTaskItemState.Disabled, r.State);
    }

    [Fact]
    public void 单项被关闭时显示已关闭_比如关掉分享()
    {
        var r = TaskStatusEvaluator.Evaluate(
            Ctx("Share", isItemEnabled: false, bili: new(false, true, false, 0))
        );
        Assert.Equal(TodayTaskItemState.Disabled, r.State);
    }

    [Fact]
    public void 今天没有触发点显示本日无需执行()
    {
        var r = TaskStatusEvaluator.Evaluate(Ctx("Login", hasFireTimeToday: false));
        Assert.Equal(TodayTaskItemState.NotToday, r.State);
    }

    [Fact]
    public void 还没到今天的触发时间显示等待执行()
    {
        var r = TaskStatusEvaluator.Evaluate(
            Ctx("Login", isPastDueTime: false, bili: new(false, false, false, 0))
        );
        Assert.Equal(TodayTaskItemState.Waiting, r.State);
    }

    [Fact]
    public void B站查询失败显示状态未知且不参与补做()
    {
        var r = TaskStatusEvaluator.Evaluate(Ctx("Login", biliQueryFailed: true));
        Assert.Equal(TodayTaskItemState.Unknown, r.State);
    }

    [Fact]
    public void B站确认完成则为已完成()
    {
        var r = TaskStatusEvaluator.Evaluate(Ctx("Login", bili: new(true, true, false, 50)));
        Assert.Equal(TodayTaskItemState.Completed, r.State);
    }

    [Fact]
    public void 执行记录里有成功则为已完成_用于任务级检查项()
    {
        var r = TaskStatusEvaluator.Evaluate(
            Ctx("VipPrivilege", records: [Rec(TaskRecordStatus.Success, null)])
        );
        Assert.Equal(TodayTaskItemState.Completed, r.State);
    }

    [Fact]
    public void 完全没有记录且B站未完成则为未执行()
    {
        var r = TaskStatusEvaluator.Evaluate(Ctx("DonateCoin", bili: new(true, true, false, 0)));
        Assert.Equal(TodayTaskItemState.NotDone, r.State);
    }

    [Fact]
    public void 今天跑过但B站仍显示未完成则为失败_投币场景()
    {
        var r = TaskStatusEvaluator.Evaluate(
            Ctx(
                "DonateCoin",
                bili: new(true, true, false, 0),
                records: [Rec(TaskRecordStatus.Success, null)]
            )
        );
        Assert.Equal(TodayTaskItemState.Failed, r.State);
    }

    [Fact]
    public void 分享被B站拒绝时显示失败且不消耗自动重试()
    {
        var r = TaskStatusEvaluator.Evaluate(
            Ctx(
                "Share",
                autoAttempts: 3,
                bili: new(true, true, false, 50),
                records: [Rec(TaskRecordStatus.Success, null)]
            )
        );
        Assert.Equal(TodayTaskItemState.Failed, r.State);
        Assert.Contains("B站", r.Message);
    }

    [Fact]
    public void 自动重试达上限后不再重试_即使仍然失败()
    {
        var r = TaskStatusEvaluator.Evaluate(
            Ctx(
                "DonateCoin",
                autoAttempts: 3,
                maxAutoAttempts: 3,
                bili: new(true, true, false, 0),
                records: [Rec(TaskRecordStatus.Failed, "DonateCoin", TaskRecordTrigger.Auto)]
            )
        );
        Assert.Equal(TodayTaskItemState.RetryExhausted, r.State);
    }

    [Fact]
    public void 任务级检查项失败且未达上限时为失败()
    {
        var manga = TaskCatalog.All.First(t => t.TaskKey == "MangaTaskAppService");
        var ctx = new TodayTaskItemContext
        {
            Task = manga,
            Item = manga.Items[0],
            IsTaskEnabled = true,
            IsItemEnabled = true,
            HasFireTimeToday = true,
            IsPastDueTime = true,
            Records =
            [
                new TaskRecord
                {
                    UserId = 1,
                    TaskKey = manga.TaskKey,
                    TaskItemKey = null,
                    RecordDate = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
                    Status = TaskRecordStatus.Failed,
                    Message = "配置直播Cookie失败",
                    Trigger = TaskRecordTrigger.Scheduled,
                },
            ],
            AutoAttempts = 1,
            MaxAutoAttempts = 3,
        };

        var r = TaskStatusEvaluator.Evaluate(ctx);
        Assert.Equal(TodayTaskItemState.Failed, r.State);
        Assert.Equal("配置直播Cookie失败", r.Message);
    }
}

public class TaskCatalogTest
{
    [Fact]
    public void 目录里的任务键与任务名互不重复()
    {
        Assert.Equal(TaskCatalog.All.Count, TaskCatalog.All.Select(t => t.TaskKey).Distinct().Count());
        Assert.Equal(TaskCatalog.All.Count, TaskCatalog.All.Select(t => t.JobName).Distinct().Count());
    }

    [Fact]
    public void 每日任务包含登录观看分享投币四个B站检查项()
    {
        var daily = TaskCatalog.All.Single(t => t.TaskKey == "DailyTaskAppService");
        var biliItems = daily
            .Items.Where(i => i.Source == TaskItemSource.BiliDailyReward)
            .Select(i => i.ItemKey)
            .ToList();

        Assert.Equal(4, biliItems.Count);
        Assert.Contains("Login", biliItems);
        Assert.Contains("Watch", biliItems);
        Assert.Contains("Share", biliItems);
        Assert.Contains("DonateCoin", biliItems);
    }

    [Fact]
    public void 投币数配成0时投币项视为关闭()
    {
        var daily = TaskCatalog.All.Single(t => t.TaskKey == "DailyTaskAppService");
        var coin = daily.Items.Single(i => i.ItemKey == "DonateCoin");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DailyTaskConfig:NumberOfCoins"] = "0" })
            .Build();

        Assert.False(coin.IsEnabled(config));
    }
}
```

- [ ] **步骤 4：运行测试验证失败**

运行：`dotnet test test/TodayTaskTest/TodayTaskTest.csproj --filter "TaskStatusEvaluatorTest|TaskCatalogTest"`
预期：编译失败，`TaskStatusEvaluator` 不存在。

- [ ] **步骤 5：编写实现**

`src/Ray.BiliBiliTool.Web/Services/TaskStatusEvaluator.cs`：

```csharp
using Ray.BiliBiliTool.Domain;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// 单个检查项的状态判定。纯逻辑、无 IO，判定优先级见规格 §5.2：
/// 已关闭 → 本日无需执行 → 等待执行 → 状态未知 → 已完成 → 分享特例 → 已达重试上限 → 失败 → 未执行
/// </summary>
public static class TaskStatusEvaluator
{
    public static TodayTaskItemResult Evaluate(TodayTaskItemContext ctx)
    {
        if (!ctx.IsTaskEnabled || !ctx.IsItemEnabled)
        {
            return new(TodayTaskItemState.Disabled, null, null, ctx.AutoAttempts);
        }

        if (!ctx.HasFireTimeToday)
        {
            return new(TodayTaskItemState.NotToday, null, null, ctx.AutoAttempts);
        }

        if (!ctx.IsPastDueTime)
        {
            return new(TodayTaskItemState.Waiting, null, null, ctx.AutoAttempts);
        }

        var completedAt = FindCompletedAt(ctx);

        if (ctx.BiliQueryFailed)
        {
            return new(
                TodayTaskItemState.Unknown,
                "B站接口查询失败，无法判断完成情况",
                completedAt,
                ctx.AutoAttempts
            );
        }

        if (IsCompleted(ctx, completedAt))
        {
            return new(TodayTaskItemState.Completed, null, completedAt, ctx.AutoAttempts);
        }

        // 分享特例：B站对该接口恒返回 -403「账号异常,操作失败」（已验证 13 天 100% 失败）。
        // 工具明明执行成功、B站却不认，重试多少次都没用，因此不消耗自动重试次数。
        if (
            ctx.Item.ItemKey == TaskCatalog.ShareItemKey
            && ctx.Records.Any(r => r.Status == TaskRecordStatus.Success)
        )
        {
            return new(
                TodayTaskItemState.Failed,
                "B站拒绝（账号异常），无法完成",
                completedAt,
                ctx.AutoAttempts
            );
        }

        if (ctx.AutoAttempts >= ctx.MaxAutoAttempts)
        {
            return new(
                TodayTaskItemState.RetryExhausted,
                $"已自动重试 {ctx.AutoAttempts} 次仍未完成",
                completedAt,
                ctx.AutoAttempts
            );
        }

        var latest = ctx.Records.LastOrDefault();
        if (latest is { Status: TaskRecordStatus.Failed })
        {
            return new(
                TodayTaskItemState.Failed,
                latest.Message ?? "执行失败",
                completedAt,
                ctx.AutoAttempts
            );
        }

        // 今天尝试过（有记录）但 B 站仍未确认 → 视为失败，但允许继续自动重试
        if (ctx.Item.Source == TaskItemSource.BiliDailyReward && ctx.Records.Count > 0)
        {
            return new(
                TodayTaskItemState.Failed,
                "执行过但 B 站未确认完成",
                completedAt,
                ctx.AutoAttempts
            );
        }

        return new(TodayTaskItemState.NotDone, null, completedAt, ctx.AutoAttempts);
    }

    private static bool IsCompleted(TodayTaskItemContext ctx, DateTimeOffset? completedAt) =>
        ctx.Item.Source switch
        {
            TaskItemSource.BiliDailyReward => IsBiliCompleted(ctx),
            _ => completedAt.HasValue,
        };

    private static bool IsBiliCompleted(TodayTaskItemContext ctx)
    {
        if (ctx.BiliReward is null)
        {
            return false;
        }

        return ctx.Item.ItemKey switch
        {
            "Login" => ctx.BiliReward.Login,
            "Watch" => ctx.BiliReward.Watch,
            TaskCatalog.ShareItemKey => ctx.BiliReward.Share,
            "DonateCoin" => ctx.BiliReward.CoinExp > 0,
            _ => false,
        };
    }

    private static DateTimeOffset? FindCompletedAt(TodayTaskItemContext ctx)
    {
        if (ctx.Item.Source == TaskItemSource.BiliDailyReward)
        {
            return IsBiliCompleted(ctx) ? ctx.Records.LastOrDefault()?.CreatedAtUtc : null;
        }

        return ctx
            .Records.Where(r => r.Status == TaskRecordStatus.Success)
            .Select(r => (DateTimeOffset?)r.CreatedAtUtc)
            .LastOrDefault();
    }
}
```

- [ ] **步骤 6：运行测试验证通过**

运行：`dotnet test test/TodayTaskTest/TodayTaskTest.csproj --filter "TaskStatusEvaluatorTest|TaskCatalogTest"`
预期：13 个测试全部 PASS。

- [ ] **步骤 7：Commit**

```bash
git add src/Ray.BiliBiliTool.Web/Services/TaskCatalog.cs src/Ray.BiliBiliTool.Web/Services/TaskStatusEvaluator.cs src/Ray.BiliBiliTool.Web/Services/TodayTaskDtos.cs test/TodayTaskTest
git commit -m "feat(today): 新增任务目录与状态判定逻辑"
```

---

## 任务 4：单项补做执行器

**文件：**
- 创建：`src/Ray.BiliBiliTool.Application.Contracts/IAccountTaskAppService.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/TaskRecoveryExecutor.cs`
- 修改：`src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs`
- 修改：`src/Ray.BiliBiliTool.DomainService/VideoDomainService.cs`
- 修改：`test/TodayTaskTest/TodayTaskTest.csproj`（加 EF InMemory 或沿用 SQLite）

- [ ] **步骤 1：新接口**

`src/Ray.BiliBiliTool.Application.Contracts/IAccountTaskAppService.cs`：

```csharp
namespace Ray.BiliBiliTool.Application.Contracts;

/// <summary>
/// 支持「只跑某一个账号」的任务服务，供补做功能使用。
/// </summary>
public interface IAccountTaskAppService : IAppService
{
    /// <summary>任务键：具体 AppService 的类型名</summary>
    string TaskKey { get; }

    /// <summary>只对该账号执行任务；账号不存在时抛异常</summary>
    Task DoTaskForAccountAsync(long userId, CancellationToken cancellationToken = default);
}
```

- [ ] **步骤 2：实现单账号执行入口**

在 `src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs` 中，把类声明改为同时实现新接口：

```csharp
public abstract class BaseMultiAccountsAppService(
    ILogger logger,
    CookieStrFactory<BiliCookie> cookieStrFactory
) : AppService, IAccountTaskAppService
```

并新增：

```csharp
    /// <summary>
    /// 只对指定账号执行本任务（补做用）。账号不在 Cookie 列表中时抛异常。
    /// </summary>
    public async Task DoTaskForAccountAsync(
        long userId,
        CancellationToken cancellationToken = default
    )
    {
        for (int i = 0; i < cookieStrFactory.Count; i++)
        {
            var ck = cookieStrFactory.GetCookie(i);
            if (ck.UserId == userId.ToString())
            {
                logger.LogInformation("######### 账号 {num}（补做） #########", i);
                await DoTaskAccountAsync(ck, cancellationToken);
                return;
            }
        }

        throw new Exception($"未找到 UID 为 {userId} 的账号");
    }
```

> 注意：`BiliCookie.UserId` 是 `string` 属性（见 `BiliCookie.cs`），因此与 `userId.ToString()` 比较。

- [ ] **步骤 3：开放视频服务的两个方法**

`src/Ray.BiliBiliTool.DomainService/VideoDomainService.cs`：

- `private async Task<bool> OpenVideo(...)` → `public async Task<bool> OpenVideo(...)`
- `private async Task<VideoInfoDto> GetRandomVideoForWatchAndShare(BiliCookie ck)` → `public async Task<VideoInfoDto> GetRandomVideoForWatchAndShare(BiliCookie ck)`

同时把它们补进 `IVideoDomainService`：

```csharp
    /// <summary>
    /// 取一个用于观看/分享的随机视频
    /// </summary>
    Task<VideoInfoDto> GetRandomVideoForWatchAndShare(BiliCookie ck);

    /// <summary>
    /// 打开视频（上报一次播放进度）
    /// </summary>
    Task<bool> OpenVideo(VideoInfoDto videoInfo, BiliCookie ck);
```

- [ ] **步骤 4：实现补做执行器**

`src/Ray.BiliBiliTool.Web/Services/TaskRecoveryExecutor.cs`：

```csharp
using Microsoft.Extensions.Configuration;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// 单项补做：把「某个检查项」映射到具体的执行动作，只做那一项。
/// </summary>
public class TaskRecoveryExecutor(
    CookieStrFactory<BiliCookie> cookieStrFactory,
    IConfiguration configuration,
    IAccountDomainService accountDomainService,
    IVideoDomainService videoDomainService,
    IDonateCoinDomainService donateCoinDomainService,
    IVipPrivilegeDomainService vipPrivilegeDomainService,
    IServiceProvider serviceProvider,
    ILogger<TaskRecoveryExecutor> logger
)
{
    /// <summary>
    /// 执行某一项补做。找不到账号或该项不支持补做时抛异常。
    /// </summary>
    public async Task ExecuteAsync(
        long userId,
        TaskDefinition task,
        TaskItemDefinition item,
        CancellationToken cancellationToken = default
    )
    {
        var ck =
            FindCookie(userId) ?? throw new Exception($"未找到 UID 为 {userId} 的账号");

        if (item.ItemKey is null)
        {
            await ExecuteWholeTaskAsync(userId, task, cancellationToken);
            return;
        }

        switch (item.ItemKey)
        {
            case "Login":
                await accountDomainService.LoginByCookie(ck);
                break;

            case "Watch":
            {
                var video = await videoDomainService.GetRandomVideoForWatchAndShare(ck);
                await videoDomainService.WatchVideo(video, ck);
                break;
            }

            case TaskCatalog.ShareItemKey:
            {
                var video = await videoDomainService.GetRandomVideoForWatchAndShare(ck);
                try
                {
                    await videoDomainService.OpenVideo(video, ck);
                }
                catch (Exception ex)
                {
                    // 打开失败不阻断分享，与 WatchAndShareVideo 的既有行为保持一致
                    logger.LogWarning(ex, "补做分享前打开视频失败");
                }

                await videoDomainService.ShareVideo(video, ck);
                break;
            }

            case "DonateCoin":
                if (configuration.GetValue("DailyTaskConfig:IsDonateCoinForArticle", false))
                {
                    logger.LogInformation("已开启专栏投币，补做仍走视频投币分支");
                }

                await donateCoinDomainService.AddCoinsForVideos(ck);
                break;

            case "VipPrivilege":
            {
                var userInfo = await accountDomainService.LoginByCookie(ck);
                await vipPrivilegeDomainService.ReceiveVipPrivilege(userInfo, ck);
                break;
            }

            default:
                throw new Exception($"未知的检查项：{item.ItemKey}");
        }
    }

    private async Task ExecuteWholeTaskAsync(
        long userId,
        TaskDefinition task,
        CancellationToken cancellationToken
    )
    {
        var appService = serviceProvider.GetServices<IAccountTaskAppService>()
            .FirstOrDefault(s => s.TaskKey == task.TaskKey);

        if (appService is null)
        {
            throw new Exception($"未注册任务服务：{task.TaskKey}");
        }

        await appService.DoTaskForAccountAsync(userId, cancellationToken);
    }

    private BiliCookie? FindCookie(long userId)
    {
        for (int i = 0; i < cookieStrFactory.Count; i++)
        {
            var ck = cookieStrFactory.GetCookie(i);
            if (ck.UserId == userId.ToString())
            {
                return ck;
            }
        }

        return null;
    }
}
```

- [ ] **步骤 5：注册服务**

`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs` 的 `AddWebServices` 中追加：

```csharp
        services.AddScoped<TaskRecoveryExecutor>();
```

> `IAccountTaskAppService` 的实现类是 `Ray.BiliBiliTool.Application` 里已有的 11 个 AppService，它们已由 `AddAppServices()` 注册，`GetServices<IAccountTaskAppService>()` 可直接取到，无需额外注册。

- [ ] **步骤 6：编译并跑测试**

```bash
dotnet build Ray.BiliBiliTool.sln
dotnet test test/TodayTaskTest/TodayTaskTest.csproj
```

预期：0 error；已有 15 个测试 PASS。

- [ ] **步骤 7：加一个补做执行器的路由测试**

在 `test/TodayTaskTest/TaskRecoveryExecutorTest.cs` 中，用一个假的 `IAccountTaskAppService` 验证「任务级补做会调用到对应的 AppService、未知子项会抛异常」：

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;
using Ray.BiliBiliTool.Agent;

namespace TodayTaskTest;

public class TaskRecoveryExecutorTest
{
    private class FakeAccountTaskAppService : IAccountTaskAppService
    {
        public string TaskKey => "MangaTaskAppService";
        public long? RanFor { get; private set; }

        public Task DoTaskAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DoTaskForAccountAsync(long userId, CancellationToken cancellationToken = default)
        {
            RanFor = userId;
            return Task.CompletedTask;
        }
    }

    private static TaskRecoveryExecutor BuildExecutor(
        FakeAccountTaskAppService fake,
        out CookieStrFactory<BiliCookie> cookieFactory
    )
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["BiliBiliCookies:0"] = "DedeUserID=1001; bili_jct=abc; SESSDATA=def",
                }
            )
            .Build();

        cookieFactory = new CookieStrFactory<BiliCookie>(config);
        var services = new ServiceCollection();
        services.AddSingleton<IAccountTaskAppService>(fake);

        return new TaskRecoveryExecutor(
            cookieFactory,
            config,
            null!,
            null!,
            null!,
            null!,
            services.BuildServiceProvider(),
            NullLogger<TaskRecoveryExecutor>.Instance
        );
    }

    [Fact]
    public async Task 任务级补做会调用到对应的AppService()
    {
        var fake = new FakeAccountTaskAppService();
        var executor = BuildExecutor(fake, out _);
        var task = TaskCatalog.All.Single(t => t.TaskKey == "MangaTaskAppService");

        await executor.ExecuteAsync(1001, task, task.Items[0]);

        Assert.Equal(1001, fake.RanFor);
    }

    [Fact]
    public async Task 账号不存在时抛异常()
    {
        var fake = new FakeAccountTaskAppService();
        var executor = BuildExecutor(fake, out _);
        var task = TaskCatalog.All.Single(t => t.TaskKey == "MangaTaskAppService");

        await Assert.ThrowsAsync<Exception>(() =>
            executor.ExecuteAsync(9999, task, task.Items[0])
        );
    }
}
```

运行：`dotnet test test/TodayTaskTest/TodayTaskTest.csproj --filter TaskRecoveryExecutorTest`
预期：2 个测试 PASS。

- [ ] **步骤 8：Commit**

```bash
git add src/Ray.BiliBiliTool.Application.Contracts/IAccountTaskAppService.cs src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs src/Ray.BiliBiliTool.DomainService/VideoDomainService.cs src/Ray.BiliBiliTool.DomainService/Interfaces/IVideoDomainService.cs src/Ray.BiliBiliTool.Web/Services/TaskRecoveryExecutor.cs src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs test/TodayTaskTest
git commit -m "feat(today): 新增单项补做执行器与单账号执行入口"
```

---

## 任务 5：今日任务查询服务

**文件：**
- 创建：`src/Ray.BiliBiliTool.Web/Services/ITodayTaskService.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/TodayTaskService.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Services/ITaskRecordReader.cs` + 实现（或直接在 TodayTaskService 中用 DbContextFactory 查询）
- 修改：`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs`

- [ ] **步骤 1：定义接口与 DTO**

`src/Ray.BiliBiliTool.Web/Services/ITodayTaskService.cs`：

```csharp
namespace Ray.BiliBiliTool.Web.Services;

/// <summary>页面上单个检查项的展示数据</summary>
public sealed class TodayTaskItemDto
{
    public required string? ItemKey { get; init; }
    public required string DisplayName { get; init; }
    public required TodayTaskItemState State { get; init; }
    public required string StateText { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int AutoAttempts { get; init; }

    /// <summary>今天是否已经跑过（用于「分享」特例判断、以及区分「漏做」与「失败」）</summary>
    public bool AttemptedToday { get; init; }

    /// <summary>是否显示「补做」按钮</summary>
    public bool CanRedo => State is TodayTaskItemState.NotDone or TodayTaskItemState.Failed
        or TodayTaskItemState.RetryExhausted;

    /// <summary>是否显示「不再尝试」按钮（仅分享且仍开启时）</summary>
    public bool CanDisableShare { get; init; }
}

/// <summary>一个任务（页面上的一组）</summary>
public sealed class TodayTaskGroupDto
{
    public required string DisplayName { get; init; }
    public required string TaskKey { get; init; }
    public required List<TodayTaskItemDto> Items { get; init; }
}

/// <summary>一个账号今日的全部任务</summary>
public sealed class AccountTodayTasksDto
{
    public required long UserId { get; init; }
    public required string UserName { get; init; }
    public required string Index { get; init; }
    public required bool IsCookieValid { get; init; }
    public required List<TodayTaskGroupDto> Groups { get; init; }
}

public sealed record TaskRedoResultDto(bool Success, string Message);

public interface ITodayTaskService
{
    Task<List<AccountTodayTasksDto>> GetTodayStatusAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default
    );

    Task<TaskRedoResultDto> RedoAsync(
        long userId,
        string taskKey,
        string? itemKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>补做该账号所有可补做的项，返回执行条数</summary>
    Task<int> RedoAllForAccountAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>补做全部账号所有可补做的项，返回执行条数</summary>
    Task<int> RedoAllMissingAsync(CancellationToken cancellationToken = default);

    /// <summary>一键关闭分享任务</summary>
    Task DisableShareAsync(CancellationToken cancellationToken = default);

    /// <summary>保存自动补做设置</summary>
    Task SaveAutoRecoverSettingsAsync(
        bool isEnable,
        int intervalHours,
        int retentionDays,
        CancellationToken cancellationToken = default
    );
}
```

- [ ] **步骤 2：实现服务**

`src/Ray.BiliBiliTool.Web/Services/TodayTaskService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Quartz;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Config.SQLite;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;
using Ray.BiliBiliTool.Infrastructure.EF;

namespace Ray.BiliBiliTool.Web.Services;

public class TodayTaskService(
    CookieStrFactory<BiliCookie> cookieStrFactory,
    IConfiguration configuration,
    IDbContextFactory<BiliDbContext> dbContextFactory,
    ISchedulerFactory schedulerFactory,
    IAccountDomainService accountDomainService,
    ICoinDomainService coinDomainService,
    IBiliAccountManageService accountManageService,
    TaskRecoveryExecutor recoveryExecutor,
    ILogger<TodayTaskService> logger
) : ITodayTaskService
{
    /// <summary>自动补做次数上限（规格 §5.5）</summary>
    public const int MaxAutoAttempts = 3;

    /// <summary>并发保护：自动补做与手动补做不能同时跑</summary>
    private static readonly SemaphoreSlim RedoLock = new(1, 1);

    public async Task<List<AccountTodayTasksDto>> GetTodayStatusAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default
    )
    {
        var today = DateTimeOffset.Now;
        var dateKey = today.ToString("yyyy-MM-dd");

        var accounts = await accountManageService.GetAccountListAsync(
            forceRefresh,
            cancellationToken
        );

        // 今天全部记录（一次查完，避免逐账号查库）
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db
            .TaskRecords.Where(r => r.RecordDate == dateKey)
            .ToListAsync(cancellationToken);

        // 每个 Job 今天是否有触发点、是否已到点
        var dueInfo = await GetDueInfoAsync(today, cancellationToken);

        var result = new List<AccountTodayTasksDto>();

        foreach (var account in accounts)
        {
            var dto = new AccountTodayTasksDto
            {
                UserId = account.UserId,
                UserName = account.UserName ?? $"账号 {account.Index}",
                Index = account.Index.ToString(),
                IsCookieValid = account.IsValid ?? false,
                Groups = [],
            };

            BiliDailyRewardSnapshot? biliReward = null;
            var biliQueryFailed = false;
            var ck = FindCookie(account.UserId);
            if (ck is not null)
            {
                try
                {
                    var info = await accountDomainService.GetDailyTaskStatus(ck);
                    if (info is null)
                    {
                        biliQueryFailed = true;
                    }
                    else
                    {
                        var donated = await coinDomainService.GetDonatedCoins(ck);
                        biliReward = new BiliDailyRewardSnapshot(
                            info.Login,
                            info.Watch,
                            info.Share,
                            donated * 10
                        );
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "查询账号 {uid} 的每日任务状态失败", account.UserId);
                    biliQueryFailed = true;
                }
            }
            else
            {
                biliQueryFailed = true;
            }

            foreach (var task in TaskCatalog.All)
            {
                var taskRecords = records
                    .Where(r => r.UserId == account.UserId && r.TaskKey == task.TaskKey)
                    .OrderBy(r => r.Id)
                    .ToList();

                var due = dueInfo.GetValueOrDefault(task.JobName);

                var group = new TodayTaskGroupDto
                {
                    DisplayName = task.DisplayName,
                    TaskKey = task.TaskKey,
                    Items = [],
                };

                foreach (var item in task.Items)
                {
                    var autoAttempts = taskRecords.Count(r =>
                        r.TaskItemKey == item.ItemKey && r.Trigger == TaskRecordTrigger.Auto
                    );

                    var ctx = new TodayTaskItemContext
                    {
                        Task = task,
                        Item = item,
                        IsTaskEnabled = task.IsEnabled(configuration),
                        IsItemEnabled = item.IsEnabled(configuration),
                        HasFireTimeToday = due.HasFireTimeToday,
                        IsPastDueTime = due.IsPastDueTime,
                        BiliReward = item.Source == TaskItemSource.BiliDailyReward ? biliReward : null,
                        BiliQueryFailed =
                            item.Source == TaskItemSource.BiliDailyReward && biliQueryFailed,
                        Records = taskRecords
                            .Where(r => r.TaskItemKey == item.ItemKey || (item.ItemKey is null && r.TaskItemKey is null))
                            .ToList(),
                        AutoAttempts = autoAttempts,
                        MaxAutoAttempts = MaxAutoAttempts,
                    };

                    var evaluated = TaskStatusEvaluator.Evaluate(ctx);

                    group.Items.Add(
                        new TodayTaskItemDto
                        {
                            ItemKey = item.ItemKey,
                            DisplayName = item.DisplayName,
                            State = evaluated.State,
                            StateText = Describe(evaluated.State),
                            Message = evaluated.Message,
                            CompletedAt = evaluated.CompletedAt,
                            AutoAttempts = evaluated.AutoAttempts,
                            AttemptedToday = taskRecords.Count > 0,
                            CanDisableShare =
                                item.ItemKey == TaskCatalog.ShareItemKey
                                && item.IsEnabled(configuration),
                        }
                    );
                }

                dto.Groups.Add(group);
            }

            result.Add(dto);
        }

        return result;
    }

    public async Task<TaskRedoResultDto> RedoAsync(
        long userId,
        string taskKey,
        string? itemKey,
        CancellationToken cancellationToken = default
    )
    {
        await RedoLock.WaitAsync(cancellationToken);
        try
        {
            var task = TaskCatalog.All.FirstOrDefault(t => t.TaskKey == taskKey);
            if (task is null)
            {
                return new TaskRedoResultDto(false, $"未知任务：{taskKey}");
            }

            var item = task.Items.FirstOrDefault(i => i.ItemKey == itemKey);
            if (item is null)
            {
                return new TaskRedoResultDto(false, $"未知检查项：{itemKey}");
            }

            return await ExecuteAndRecordAsync(
                userId,
                task,
                item,
                TaskRecordTrigger.Manual,
                cancellationToken
            );
        }
        finally
        {
            RedoLock.Release();
        }
    }

    public async Task<int> RedoAllForAccountAsync(
        long userId,
        CancellationToken cancellationToken = default
    )
    {
        var status = await GetTodayStatusAsync(true, cancellationToken);
        var account = status.FirstOrDefault(a => a.UserId == userId);
        if (account is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var group in account.Groups)
        {
            foreach (var item in group.Items.Where(i => i.CanRedo))
            {
                var r = await RedoAsync(userId, group.TaskKey, item.ItemKey, cancellationToken);
                count++;
                logger.LogInformation(
                    "补做 {user}/{task}/{item}：{result}",
                    userId,
                    group.TaskKey,
                    item.ItemKey,
                    r.Message
                );
            }
        }

        return count;
    }

    public async Task<int> RedoAllMissingAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetTodayStatusAsync(true, cancellationToken);
        var count = 0;
        foreach (var account in status)
        {
            count += await RedoAllForAccountAsync(account.UserId, cancellationToken);
        }

        return count;
    }

    public async Task DisableShareAsync(CancellationToken cancellationToken = default)
    {
        await SaveSettingsAsync(new Dictionary<string, string?>
        {
            ["DailyTaskConfig:IsShareVideo"] = "false",
        });
    }

    public async Task SaveAutoRecoverSettingsAsync(
        bool isEnable,
        int intervalHours,
        int retentionDays,
        CancellationToken cancellationToken = default
    )
    {
        await SaveSettingsAsync(new Dictionary<string, string?>
        {
            ["AutoRecoverConfig:IsEnable"] = isEnable.ToString().ToLower(),
            ["AutoRecoverConfig:IntervalHours"] = Math.Clamp(intervalHours, 1, 24).ToString(),
            ["AutoRecoverConfig:RecordRetentionDays"] = Math.Clamp(retentionDays, 1, 90).ToString(),
        });
    }

    #region private

    private async Task<TaskRedoResultDto> ExecuteAndRecordAsync(
        long userId,
        TaskDefinition task,
        TaskItemDefinition item,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await recoveryExecutor.ExecuteAsync(userId, task, item, cancellationToken);
            await WriteRecordAsync(
                userId,
                task.TaskKey,
                item.ItemKey,
                TaskRecordStatus.Success,
                null,
                trigger,
                cancellationToken
            );
            return new TaskRedoResultDto(true, $"{item.DisplayName}：执行完成");
        }
        catch (Exception ex)
        {
            await WriteRecordAsync(
                userId,
                task.TaskKey,
                item.ItemKey,
                TaskRecordStatus.Failed,
                ex.Message,
                trigger,
                cancellationToken
            );
            return new TaskRedoResultDto(false, $"{item.DisplayName}：{ex.Message}");
        }
    }

    private async Task WriteRecordAsync(
        long userId,
        string taskKey,
        string? itemKey,
        TaskRecordStatus status,
        string? message,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.TaskRecords.Add(
            new TaskRecord
            {
                UserId = userId,
                TaskKey = taskKey,
                TaskItemKey = itemKey,
                RecordDate = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
                Status = status,
                Message = message is { Length: > 512 } ? message[..512] : message,
                Trigger = trigger,
            }
        );
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, (bool HasFireTimeToday, bool IsPastDueTime)>> GetDueInfoAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, (bool, bool)>();
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);

        foreach (var task in TaskCatalog.All)
        {
            var jobKey = new JobKey(task.JobName, Ray.BiliBiliTool.Web.Constants.BiliJobGroup);
            var triggers = await scheduler.GetTriggersOfJob(jobKey, cancellationToken);
            var cron = triggers.OfType<ICronTrigger>().FirstOrDefault()?.CronExpressionString;

            var fireTimes = TaskDueTimeCalculator.GetFireTimesOfDay(cron, now);
            result[task.JobName] = (
                fireTimes.Count > 0,
                TaskDueTimeCalculator.IsDue(cron, now)
            );
        }

        return result;
    }

    private BiliCookie? FindCookie(long userId)
    {
        for (int i = 0; i < cookieStrFactory.Count; i++)
        {
            var ck = cookieStrFactory.GetCookie(i);
            if (ck.UserId == userId.ToString())
            {
                return ck;
            }
        }

        return null;
    }

    private async Task SaveSettingsAsync(Dictionary<string, string?> values)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return;
        }

        var provider = root.Providers.OfType<SqliteConfigurationProvider>().FirstOrDefault();
        if (provider is null)
        {
            throw new Exception("无法获取数据库配置提供器");
        }

        provider.BatchSet(values!);
        root.Reload();

        await AutoRecoverJob.RescheduleAsync(schedulerFactory);
    }

    private static string Describe(TodayTaskItemState state) =>
        state switch
        {
            TodayTaskItemState.Completed => "已完成",
            TodayTaskItemState.NotDone => "今天还没做",
            TodayTaskItemState.Failed => "失败",
            TodayTaskItemState.RetryExhausted => "已自动重试 3 次仍未完成",
            TodayTaskItemState.Waiting => "等待执行",
            TodayTaskItemState.NotToday => "本日无需执行",
            TodayTaskItemState.Disabled => "已关闭",
            TodayTaskItemState.Unknown => "状态未知",
            _ => state.ToString(),
        };

    #endregion private
}
```

> `SqliteConfigurationProvider.BatchSet` 的签名请以 `src/Ray.BiliBiliTool.Config/SQLite/SqliteConfigurationProvider.cs` 中的实际定义为准（`Dictionary<string, string?>` 或 `Dictionary<string, string>`），不一致时调整调用处。

- [ ] **步骤 3：注册服务**

`AddWebServices` 中追加：

```csharp
        services.AddScoped<ITodayTaskService, TodayTaskService>();
```

- [ ] **步骤 4：编译**

```bash
dotnet build Ray.BiliBiliTool.sln
```

预期：0 error（`AutoRecoverJob` 尚未创建会让本步骤报错，属于正常 —— 它在任务 6 创建；如需提前解耦，可先把 `SaveSettingsAsync` 里对 `AutoRecoverJob.RescheduleAsync` 的调用留到任务 6 再加）。

- [ ] **步骤 5：Commit**

```bash
git add src/Ray.BiliBiliTool.Web/Services/ITodayTaskService.cs src/Ray.BiliBiliTool.Web/Services/TodayTaskService.cs src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs
git commit -m "feat(today): 新增今日任务查询与补做编排服务"
```

---

## 任务 6：自动补做定时任务 + 配置项

**文件：**
- 创建：`src/Ray.BiliBiliTool.Config/Options/AutoRecoverOptions.cs`
- 创建：`src/Ray.BiliBiliTool.Web/Jobs/AutoRecoverJob.cs`
- 修改：`src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs`
- 修改：`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs`
- 修改：`src/Ray.BiliBiliTool.Web/appsettings.json`

- [ ] **步骤 1：配置项**

`src/Ray.BiliBiliTool.Config/Options/AutoRecoverOptions.cs`：

```csharp
namespace Ray.BiliBiliTool.Config.Options;

/// <summary>
/// 自动补做配置。用固定间隔触发（SimpleTrigger），因此没有 Cron。
/// </summary>
public class AutoRecoverOptions
{
    public const string SectionName = "AutoRecoverConfig";

    /// <summary>是否启用自动补做</summary>
    public bool IsEnable { get; set; } = true;

    /// <summary>每隔几小时检查一次</summary>
    public int IntervalHours { get; set; } = 2;

    /// <summary>执行记录保留天数</summary>
    public int RecordRetentionDays { get; set; } = 3;
}
```

- [ ] **步骤 2：注册配置**

`src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs` 的链式 `Configure<...>` 中追加：

```csharp
            .Configure<AutoRecoverOptions>(configuration.GetSection(AutoRecoverOptions.SectionName))
```

- [ ] **步骤 3：Quartz 任务**

`src/Ray.BiliBiliTool.Web/Jobs/AutoRecoverJob.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Infrastructure.EF;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Jobs;

/// <summary>
/// 自动补做：每隔 N 小时检查一次，把今天到点但没做（或做了失败但未达重试上限）的项补上。
/// 判定规则见规格 §5。
/// </summary>
public class AutoRecoverJob(
    ILogger<AutoRecoverJob> logger,
    IOptionsMonitor<AutoRecoverOptions> options,
    ITodayTaskService todayTaskService,
    IDbContextFactory<BiliDbContext> dbContextFactory
) : BaseJob<AutoRecoverJob>(logger)
{
    public static readonly JobKey Key = new(nameof(AutoRecoverJob), Constants.BiliJobGroup);
    public static readonly TriggerKey Trigger = new($"{nameof(AutoRecoverJob)}.Interval.Trigger", Constants.BiliJobGroup);

    protected override async Task DoExecuteAsync(IJobExecutionContext context)
    {
        var config = options.CurrentValue;

        await CleanupAsync(config.RecordRetentionDays, context.CancellationToken);

        if (!config.IsEnable)
        {
            logger.LogInformation("自动补做已配置为关闭，跳过");
            return;
        }

        var status = await todayTaskService.GetTodayStatusAsync(false, context.CancellationToken);

        foreach (var account in status)
        {
            if (!account.IsCookieValid)
            {
                continue;
            }

            foreach (var group in account.Groups)
            {
                foreach (var item in group.Items)
                {
                    // 只补「漏做」与「执行过但失败且未达自动重试上限」的项；
                    // 已达上限（RetryExhausted）、等待执行、已关闭、本日无需执行、状态未知、已完成都不补。
                    if (
                        item.State != TodayTaskItemState.NotDone
                        && item.State != TodayTaskItemState.Failed
                    )
                    {
                        continue;
                    }

                    var result = await todayTaskService.RedoAsync(
                        account.UserId,
                        group.TaskKey,
                        item.ItemKey,
                        context.CancellationToken
                    );
                    logger.LogInformation(
                        "自动补做 {user}/{task}/{item}：{result}",
                        account.UserId,
                        group.TaskKey,
                        item.ItemKey,
                        result.Message
                    );
                }
            }
        }
    }

    private async Task CleanupAsync(int retentionDays, CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays));
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await db
                .TaskRecords.Where(r => r.CreatedAtUtc < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理过期的任务执行记录失败");
        }
    }

    /// <summary>
    /// 按当前配置重建触发器（间隔小时数变了要重新调度；开关只影响任务内逻辑，不暂停触发器）。
    /// </summary>
    public static async Task RescheduleAsync(ISchedulerFactory schedulerFactory)
    {
        try
        {
            var scheduler = await schedulerFactory.GetScheduler();
            if (!await scheduler.CheckExists(Trigger))
            {
                return;
            }

            var newTrigger = TriggerBuilder
                .Create()
                .WithIdentity(Trigger)
                .ForJob(Key)
                .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
                .WithSimpleSchedule(x =>
                    x.WithIntervalInHours(ReadIntervalHours(scheduler)).RepeatForever()
                )
                .Build();

            await scheduler.RescheduleJob(Trigger, newTrigger);
        }
        catch
        {
            // 重新调度失败不影响已保存的配置
        }
    }

    private static int ReadIntervalHours(IScheduler scheduler)
    {
        var config = Global.ServiceProviderRoot?.GetService<IConfiguration>();
        var hours = config?.GetValue("AutoRecoverConfig:IntervalHours", 2) ?? 2;
        return Math.Clamp(hours, 1, 24);
    }
}
```

> 需要 `using Microsoft.Extensions.DependencyInjection;` 与 `using Ray.BiliBiliTool.Infrastructure;`。

- [ ] **步骤 4：注册 Quartz 任务**

`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs` 的 `AddBiliJobs` 末尾（`TestBiliJob` 之前）追加：

```csharp
        // 自动补做 job（固定间隔，非 Cron）
        var autoRecoverInterval = configuration.GetValue("AutoRecoverConfig:IntervalHours", 2);
        autoRecoverInterval = Math.Clamp(autoRecoverInterval, 1, 24);

        quartz.AddJob<AutoRecoverJob>(opts => opts.WithIdentity(AutoRecoverJob.Key));
        quartz.AddTrigger(opts =>
            opts.ForJob(AutoRecoverJob.Key)
                .WithIdentity(AutoRecoverJob.Trigger)
                .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
                .WithSimpleSchedule(x =>
                    x.WithIntervalInHours(autoRecoverInterval).RepeatForever()
                )
        );
```

- [ ] **步骤 5：默认配置节**

`src/Ray.BiliBiliTool.Web/appsettings.json` 中，在 `"UnfollowBatchedTaskConfig"` 之后追加：

```json
  "AutoRecoverConfig": {
    "IsEnable": true, // 是否启用自动补做
    "IntervalHours": 2, // 每隔几小时检查一次 [1,24]
    "RecordRetentionDays": 3 // 执行记录保留天数 [1,90]
  },
```

- [ ] **步骤 6：编译并启动验证**

```bash
dotnet build Ray.BiliBiliTool.sln
```

预期：0 error。

- [ ] **步骤 7：Commit**

```bash
git add src/Ray.BiliBiliTool.Config/Options/AutoRecoverOptions.cs src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs src/Ray.BiliBiliTool.Web/Jobs/AutoRecoverJob.cs src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs src/Ray.BiliBiliTool.Web/appsettings.json
git commit -m "feat(today): 新增自动补做定时任务与配置项"
```

---

## 任务 7：今日任务页面

**文件：**
- 创建：`src/Ray.BiliBiliTool.Web/Components/Pages/Today/Today.razor`
- 创建：`src/Ray.BiliBiliTool.Web/Components/Pages/Today/Today.razor.cs`
- 修改：`src/Ray.BiliBiliTool.Web/Components/Layout/NavMenu.razor`

- [ ] **步骤 1：导航项**

`NavMenu.razor` 中，在「账号管理」那个 `nav-item` 之后插入：

```razor
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="Today">
                <span class="bi bi-check2-square" aria-hidden="true"></span> 今日任务
            </NavLink>
        </div>
```

- [ ] **步骤 2：页面标记**

`src/Ray.BiliBiliTool.Web/Components/Pages/Today/Today.razor`：

```razor
@page "/Today"
@attribute [Authorize]
@rendermode InteractiveServer

<PageTitle>今日任务</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="my-4">
    <MudText Typo="Typo.h5">今日任务</MudText>
    <MudText Typo="Typo.body2" Class="mud-text-secondary mb-3">
        显示每个账号今天的任务完成情况。漏做的可以自动补做，也可以单独点「补做」。
    </MudText>

    <MudPaper Class="pa-3 mb-4" Elevation="1">
        <div class="d-flex flex-wrap align-center gap-3">
            <MudText Typo="Typo.body2">日期：@DateTime.Now.ToString("yyyy-MM-dd")</MudText>
            @if (_lastRefresh.HasValue)
            {
                <MudText Typo="Typo.body2">最后检查：@_lastRefresh.Value.ToString("HH:mm:ss")</MudText>
            }
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Refresh"
                       Disabled="_busy" OnClick="RefreshAsync">立即刷新</MudButton>
            <MudButton Variant="Variant.Outlined" Color="Color.Secondary"
                       Disabled="_busy" OnClick="RedoAllAsync">一键补做全部漏做项</MudButton>
        </div>

        <MudDivider Class="my-3" />

        <div class="d-flex flex-wrap align-center gap-3">
            <MudSwitch @bind-Value="_autoEnable" Color="Color.Primary" Label="自动补做" />
            <MudNumericField @bind-Value="_intervalHours" Label="每几小时检查一次" Min="1" Max="24" Style="max-width:180px" />
            <MudNumericField @bind-Value="_retentionDays" Label="记录保留天数" Min="1" Max="90" Style="max-width:160px" />
            <MudButton Variant="Variant.Filled" Color="Color.Primary" Disabled="_busy" OnClick="SaveSettingsAsync">保存设置</MudButton>
        </div>
    </MudPaper>

    @if (_loading)
    {
        <MudProgressLinear Indeterminate="true" Color="Color.Primary" />
    }
    else if (_accounts.Count == 0)
    {
        <MudAlert Severity="Severity.Info">还没有添加B站账号，请先到「账号管理」扫码添加。</MudAlert>
    }
    else
    {
        @foreach (var account in _accounts)
        {
            <MudCard Class="mb-3">
                <MudCardHeader>
                    <CardHeaderContent>
                        <MudText Typo="Typo.h6">👤 账号@(account.Index) · @account.UserName</MudText>
                        <MudText Typo="Typo.caption" Class="mud-text-secondary">UID @account.UserId</MudText>
                    </CardHeaderContent>
                    <CardHeaderActions>
                        <MudButton Size="Size.Small" Variant="Variant.Outlined" Disabled="_busy"
                                   OnClick="@(() => RedoAccountAsync(account))">整卡补做</MudButton>
                    </CardHeaderActions>
                </MudCardHeader>
                <MudCardContent Class="pt-0">
                    @if (!account.IsCookieValid)
                    {
                        <MudAlert Severity="Severity.Warning" Dense="true" Class="mb-2">
                            该账号 Cookie 已失效，请到「账号管理」重新扫码登录。
                        </MudAlert>
                    }

                    @foreach (var group in account.Groups)
                    {
                        @foreach (var item in group.Items)
                        {
                            <div class="d-flex align-center justify-space-between py-1 task-row">
                                <div class="d-flex align-center gap-2">
                                    <span class="state-icon">@StateIcon(item.State)</span>
                                    <MudText Typo="Typo.body2">@item.DisplayName</MudText>
                                </div>
                                <div class="d-flex align-center gap-3">
                                    <MudText Typo="Typo.body2" Class="@StateClass(item.State)">
                                        @item.StateText@(item.Message is null ? "" : $"（{item.Message}）")
                                    </MudText>
                                    @if (item.CompletedAt.HasValue)
                                    {
                                        <MudText Typo="Typo.caption" Class="mud-text-secondary">
                                            @item.CompletedAt.Value.ToLocalTime().ToString("MM-dd HH:mm")
                                        </MudText>
                                    }
                                    @if (item.CanRedo)
                                    {
                                        <MudButton Size="Size.Small" Variant="Variant.Text" Color="Color.Primary"
                                                   Disabled="_busy"
                                                   OnClick="@(() => RedoItemAsync(account, group.TaskKey, item))">补做</MudButton>
                                    }
                                    @if (item.CanDisableShare)
                                    {
                                        <MudButton Size="Size.Small" Variant="Variant.Text" Color="Color.Warning"
                                                   Disabled="_busy"
                                                   OnClick="DisableShareAsync">不再尝试</MudButton>
                                    }
                                </div>
                            </div>
                        }
                    }
                </MudCardContent>
            </MudCard>
        }
    }
</MudContainer>
```

- [ ] **步骤 3：页面样式**

在 `Today.razor` 末尾追加：

```razor
<style>
    .task-row { border-bottom: 1px solid var(--mud-palette-lines-default); }
    .task-row:last-child { border-bottom: none; }
    .state-icon { width: 1.2rem; text-align: center; }
    .state-ok { color: var(--mud-palette-success); }
    .state-warn { color: var(--mud-palette-warning); }
    .state-bad { color: var(--mud-palette-error); }
    .state-muted { color: var(--mud-palette-text-secondary); }
</style>
```

- [ ] **步骤 4：页面代码**

`src/Ray.BiliBiliTool.Web/Components/Pages/Today/Today.razor.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MudBlazor;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Components.Pages.Today;

[Authorize]
public partial class Today : ComponentBase
{
    [Inject]
    private ITodayTaskService TodayTaskService { get; set; } = null!;

    [Inject]
    private IOptionsMonitor<AutoRecoverOptions> AutoRecoverOptions { get; set; } = null!;

    [Inject]
    private IConfiguration Configuration { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private List<AccountTodayTasksDto> _accounts = [];
    private bool _loading;
    private bool _busy;
    private DateTimeOffset? _lastRefresh;

    private bool _autoEnable;
    private int _intervalHours = 2;
    private int _retentionDays = 3;

    protected override async Task OnInitializedAsync()
    {
        LoadSettings();
        await RefreshAsync();
    }

    private void LoadSettings()
    {
        var config = AutoRecoverOptions.CurrentValue;
        _autoEnable = config.IsEnable;
        _intervalHours = Math.Clamp(config.IntervalHours, 1, 24);
        _retentionDays = Math.Clamp(config.RecordRetentionDays, 1, 90);
    }

    private async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            _accounts = await TodayTaskService.GetTodayStatusAsync(false);
            _lastRefresh = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"加载今日任务失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RedoItemAsync(AccountTodayTasksDto account, string taskKey, TodayTaskItemDto item)
    {
        _busy = true;
        try
        {
            var result = await TodayTaskService.RedoAsync(account.UserId, taskKey, item.ItemKey);
            Snackbar.Add(result.Message, result.Success ? Severity.Success : Severity.Warning);
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RedoAccountAsync(AccountTodayTasksDto account)
    {
        _busy = true;
        try
        {
            var count = await TodayTaskService.RedoAllForAccountAsync(account.UserId);
            Snackbar.Add($"已补做 {count} 项", Severity.Success);
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RedoAllAsync()
    {
        _busy = true;
        try
        {
            var count = await TodayTaskService.RedoAllMissingAsync();
            Snackbar.Add($"已补做 {count} 项", Severity.Success);
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DisableShareAsync()
    {
        _busy = true;
        try
        {
            await TodayTaskService.DisableShareAsync();
            Snackbar.Add("已关闭分享任务，之后不再尝试分享", Severity.Success);
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        _busy = true;
        try
        {
            await TodayTaskService.SaveAutoRecoverSettingsAsync(
                _autoEnable,
                _intervalHours,
                _retentionDays
            );
            Snackbar.Add("设置已保存", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"保存失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private static string StateIcon(TodayTaskItemState state) =>
        state switch
        {
            TodayTaskItemState.Completed => "✅",
            TodayTaskItemState.NotDone => "❌",
            TodayTaskItemState.Failed => "⚠️",
            TodayTaskItemState.RetryExhausted => "⚠️",
            TodayTaskItemState.Waiting => "⏳",
            TodayTaskItemState.NotToday => "➖",
            TodayTaskItemState.Disabled => "⛔",
            TodayTaskItemState.Unknown => "❓",
            _ => "•",
        };

    private static string StateClass(TodayTaskItemState state) =>
        state switch
        {
            TodayTaskItemState.Completed => "state-ok",
            TodayTaskItemState.NotDone => "state-bad",
            TodayTaskItemState.Failed or TodayTaskItemState.RetryExhausted => "state-warn",
            TodayTaskItemState.Unknown => "state-bad",
            _ => "state-muted",
        };
}
```

> `_autoEnable` 等字段在 `.razor` 中通过 `@bind-Value` 绑定，需为 `private` 字段（Blazor 生成的 partial 类在同一程序集内，可访问）。

- [ ] **步骤 5：编译**

```bash
dotnet build Ray.BiliBiliTool.sln
```

预期：0 error。

- [ ] **步骤 6：Commit**

```bash
git add src/Ray.BiliBiliTool.Web/Components/Pages/Today src/Ray.BiliBiliTool.Web/Components/Layout/NavMenu.razor
git commit -m "feat(today): 新增今日任务页面"
```

---

## 任务 8：端到端验证

**文件：** 不新增代码；如发现问题回到对应任务修复。

- [ ] **步骤 1：准备独立验证环境（不动用户线上数据）**

```bash
mkdir -p /tmp/bilitool-verify/config
cp "F:/Docker/bili_tool_web/config/cookies.json" /tmp/bilitool-verify/config/cookies.json
cp src/Ray.BiliBiliTool.Web/appsettings.json /tmp/bilitool-verify/appsettings.json
```

- [ ] **步骤 2：启动并确认迁移**

```bash
dotnet run --project src/Ray.BiliBiliTool.Web -- --contentRoot /tmp/bilitool-verify
```

访问 `http://localhost:<port>/Today`（看启动日志里的端口）。
用 sqlite3 检查表：

```bash
sqlite3 /tmp/bilitool-verify/config/BiliBiliTool.db ".schema bili_task_records"
```

预期：表与索引存在。

- [ ] **步骤 3：核对页面状态与 B 站真实数据一致**

手工调用接口，逐账号比对「登录 / 观看 / 分享 / 投币」四项：

（使用规格 §4.1 的 `/x/member/web/exp/reward`；账号 0 的 Cookie 从验证用 cookies.json 读取）

预期：页面显示与接口返回完全一致；分享行显示「⚠️ B站拒绝（账号异常）」。

- [ ] **步骤 4：验证手动补做**

点投币行的「补做」。
预期：出现 Snackbar 结果；数据库新增一条 `TaskKey='DailyTaskAppService' AND TaskItemKey='DonateCoin' AND Trigger='Manual'` 的记录；再次刷新页面，投币行状态随之变化。

- [ ] **步骤 5：验证「不再尝试」**

点分享行的「不再尝试」。
预期：`bili_appsettings` 表中 `DailyTaskConfig:IsShareVideo` = `false`；分享行变为「⛔ 已关闭」。

- [ ] **步骤 6：验证自动补做三条规则**

在「计划任务」页面对 `AutoRecoverJob` 点「立即执行」，并观察日志：

- **到点判定**：把 `DailyTaskConfig:Cron` 改到今晚更晚的时间 → 每日任务各行应显示「⏳ 等待执行」，且日志中没有对应的补做记录。
- **3 次上限**：对某个必然失败的项连续触发 4 次 → 第 4 次不再执行；页面显示「已自动重试 3 次仍未完成」；「补做」按钮仍在。
- **未知状态不补做**：临时把 `Security:WebProxy` 指向一个不可达地址后触发 → 状态显示「❓ 状态未知」，且无补做记录写入。

- [ ] **步骤 7：清理验证环境**

```bash
rm -rf /tmp/bilitool-verify
```

确认 `F:\Docker\bili_tool_web\config` 未被改动（cookies.json 与数据库的修改时间）。

- [ ] **步骤 8：推送并同步到 main**

用户的 `F:\Docker\bili_tool_web\rebuild.ps1` 会执行 `git -C C:\Users\Beatrice\BiliBiliToolPro pull origin main`，因此改动必须落到 `origin/main`。

注意：`main` 已被主检出（`C:\Users\Beatrice\BiliBiliToolPro`）占用，**不能在本 worktree 里 `git checkout main`**。改用直接推送分支的方式：

```bash
git push origin dev/loving-driscoll-39dc0e

# 先确认 main 是当前分支的祖先（即能快进），不是则停下来人工处理
git merge-base --is-ancestor origin/main HEAD && echo "可以快进"

git push origin HEAD:main
```

- [ ] **步骤 9：同步本地主检出并重建容器**

```bash
git -C "C:/Users/Beatrice/BiliBiliToolPro" pull origin main
powershell -NoProfile -ExecutionPolicy Bypass -File "F:/Docker/bili_tool_web/rebuild.ps1"
```

预期：镜像 `bili_tool_web:local` 构建成功，容器 `bili_tool_web` 重启。

- [ ] **步骤 10：确认部署生效**

```bash
docker ps --filter name=bili_tool_web
```

访问 `http://localhost:22330/Today`，确认页面可打开、5 个账号显示正常，且用户在用的 `F:\Docker\bili_tool_web\config` 中 `cookies.json` 未被改动（账号没有丢失）。

---

## 自检记录

- **规格覆盖**：§4.1 → 任务 5；§4.2 → 任务 2；§5.1 → 任务 3；§5.2 → 任务 3；§5.3 → 任务 1；§5.4 → 任务 3 + 任务 7；§5.5 → 任务 3 + 任务 5；§6 → 任务 4；§7 → 任务 4；§8 → 任务 6；§9 → 任务 7；§10 → 任务 6 + 任务 5；§11 → 全部；§12 → 任务 8。
- **已知需在实现时确认的点（非占位符，是接口核对项）**：
  1. `BiliDbContext` 在测试中能否用 `PooledDbContextFactory` 注入连接串（任务 2 步骤 6 给了备选做法）。
  2. `SqliteConfigurationProvider.BatchSet` 的确切签名（任务 5）。
  3. `dotnet ef migrations add` 生成的文件命名空间需为 `Ray.BiliBiliTool.Web.Migrations`（任务 2 步骤 3）。
