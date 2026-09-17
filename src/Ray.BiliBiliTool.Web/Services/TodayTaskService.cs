using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Quartz;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Config.SQLite;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;
using Ray.BiliBiliTool.Infrastructure.EF;
using Ray.BiliBiliTool.Web.Jobs;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// 今日任务的查询与补做编排。判定规则见规格 §5。
/// </summary>
public class TodayTaskService(
    CookieStrFactory<BiliCookie> cookieStrFactory,
    IConfiguration configuration,
    IDbContextFactory<BiliDbContext> dbContextFactory,
    ISchedulerFactory schedulerFactory,
    IAccountDomainService accountDomainService,
    ICoinDomainService coinDomainService,
    IBiliAccountManageService accountManageService,
    TaskRecoveryExecutor recoveryExecutor,
    ITaskRecordWriter recordWriter,
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
        var now = DateTimeOffset.Now;
        var dateKey = now.ToString("yyyy-MM-dd");

        var accounts = await accountManageService.GetAccountListAsync(
            forceRefresh,
            cancellationToken
        );

        // 今天的记录一次查完，避免逐账号查库
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db
            .TaskRecords.Where(r => r.RecordDate == dateKey)
            .ToListAsync(cancellationToken);

        var dueInfo = await GetDueInfoAsync(now, cancellationToken);

        var result = new List<AccountTodayTasksDto>();

        foreach (var account in accounts)
        {
            var dto = new AccountTodayTasksDto
            {
                UserId = account.UserId,
                UserName = account.UserName ?? $"账号 {account.Index}",
                Index = account.Index,
                IsCookieValid = account.IsValid ?? false,
                Groups = [],
            };

            var (biliReward, biliQueryFailed) = await QueryBiliRewardAsync(
                account.UserId,
                account.IsValid ?? false,
                cancellationToken
            );

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
                    // 传该任务今天的全部记录：每日任务的子项（登录/观看/分享/投币）定时执行时
                    // 只写任务级记录（TaskItemKey = null），子项级记录只在补做时产生。
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
                        BiliReward =
                            item.Source == TaskItemSource.BiliDailyReward ? biliReward : null,
                        BiliQueryFailed =
                            item.Source == TaskItemSource.BiliDailyReward && biliQueryFailed,
                        Records = taskRecords,
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
                            CanAutoRedo = TaskStatusEvaluator.CanAutoRedo(ctx, evaluated),
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
        TaskRecordTrigger trigger = TaskRecordTrigger.Manual,
        CancellationToken cancellationToken = default
    )
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

        await RedoLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteAndRecordAsync(userId, task, item, trigger, cancellationToken);
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
        return account is null ? 0 : await RedoAccountAsync(account, cancellationToken);
    }

    public async Task<int> RedoAllMissingAsync(CancellationToken cancellationToken = default)
    {
        // 只查一次状态，避免逐账号重复请求 B 站接口
        var status = await GetTodayStatusAsync(true, cancellationToken);

        var count = 0;
        foreach (var account in status)
        {
            count += await RedoAccountAsync(account, cancellationToken);
        }

        return count;
    }

    public async Task DisableShareAsync(CancellationToken cancellationToken = default)
    {
        await SaveSettingsAsync(
            new Dictionary<string, string> { ["DailyTaskConfig:IsShareVideo"] = "false" }
        );
    }

    public async Task SaveAutoRecoverSettingsAsync(
        bool isEnable,
        int intervalHours,
        int retentionDays,
        CancellationToken cancellationToken = default
    )
    {
        await SaveSettingsAsync(
            new Dictionary<string, string>
            {
                ["AutoRecoverConfig:IsEnable"] = isEnable.ToString().ToLower(),
                ["AutoRecoverConfig:IntervalHours"] = Math.Clamp(intervalHours, 1, 24).ToString(),
                ["AutoRecoverConfig:RecordRetentionDays"] = Math.Clamp(retentionDays, 1, 90)
                    .ToString(),
            }
        );
    }

    #region private

    /// <summary>对已完成状态快照的账号执行全部可补做项</summary>
    private async Task<int> RedoAccountAsync(
        AccountTodayTasksDto account,
        CancellationToken cancellationToken
    )
    {
        var count = 0;
        foreach (var group in account.Groups)
        {
            foreach (var item in group.Items.Where(i => i.CanRedo))
            {
                var r = await RedoAsync(
                    account.UserId,
                    group.TaskKey,
                    item.ItemKey,
                    TaskRecordTrigger.Manual,
                    cancellationToken
                );
                count++;
                logger.LogInformation(
                    "补做 {user}/{task}/{item}：{result}",
                    account.UserId,
                    group.TaskKey,
                    item.ItemKey,
                    r.Message
                );
            }
        }

        return count;
    }

    /// <summary>
    /// 查询某账号的 B 站每日任务状态。返回 (快照, 是否查询失败)。
    /// Cookie 无效或接口异常时快照为 null 且标记失败 —— 页面显示「状态未知」，且不参与补做判定。
    /// </summary>
    private async Task<(BiliDailyRewardSnapshot? Reward, bool Failed)> QueryBiliRewardAsync(
        long userId,
        bool isCookieValid,
        CancellationToken cancellationToken
    )
    {
        if (!isCookieValid)
        {
            return (null, true);
        }

        var ck = FindCookie(userId);
        if (ck is null)
        {
            return (null, true);
        }

        try
        {
            var info = await accountDomainService.GetDailyTaskStatus(ck);
            if (info is null)
            {
                return (null, true);
            }

            var donatedCoins = await coinDomainService.GetDonatedCoins(ck);

            return (
                new BiliDailyRewardSnapshot(info.Login, info.Watch, info.Share, donatedCoins * 10),
                false
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询账号 {uid} 的每日任务状态失败", userId);
            return (null, true);
        }
    }

    private async Task<TaskRedoResultDto> ExecuteAndRecordAsync(
        long userId,
        TaskDefinition task,
        TaskItemDefinition item,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        string? error = null;
        try
        {
            await recoveryExecutor.ExecuteAsync(userId, task, item, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 记录写入交给 ITaskRecordWriter：它内部会吞掉写库异常。
        // 若在这里直接写库并把写入和「任务执行」放进同一个 try，写库失败会被当成任务失败。
        await recordWriter.WriteAsync(
            userId,
            task.TaskKey,
            item.ItemKey,
            error is null ? TaskRecordStatus.Success : TaskRecordStatus.Failed,
            error,
            trigger,
            cancellationToken
        );

        return error is null
            ? new TaskRedoResultDto(true, $"{item.DisplayName}：执行完成")
            : new TaskRedoResultDto(false, $"{item.DisplayName}：{error}");
    }

    /// <summary>每个任务今天有没有触发点、是否已过今天的最后一次触发时间</summary>
    private async Task<Dictionary<string, DueInfo>> GetDueInfoAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, DueInfo>();
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);

        foreach (var task in TaskCatalog.All)
        {
            var jobKey = new JobKey(task.JobName, Constants.BiliJobGroup);
            string? cron = null;
            try
            {
                var triggers = await scheduler.GetTriggersOfJob(jobKey, cancellationToken);
                cron = triggers.OfType<ICronTrigger>().FirstOrDefault()?.CronExpressionString;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "读取任务 {job} 的触发器失败", task.JobName);
            }

            var fireTimes = TaskDueTimeCalculator.GetFireTimesOfDay(cron, now);
            result[task.JobName] = new DueInfo(
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

    private async Task SaveSettingsAsync(Dictionary<string, string> values)
    {
        if (configuration is not IConfigurationRoot root)
        {
            throw new Exception("无法获取配置根对象");
        }

        var provider = root.Providers.OfType<SqliteConfigurationProvider>().FirstOrDefault();
        if (provider is null)
        {
            throw new Exception("无法获取数据库配置提供器");
        }

        provider.BatchSet(values);
        root.Reload();

        // 间隔小时数变了要重建 Quartz 触发器
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

    private readonly record struct DueInfo(bool HasFireTimeToday, bool IsPastDueTime);

    #endregion private
}
