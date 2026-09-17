using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.Infrastructure;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Application;

public abstract class BaseMultiAccountsAppService(
    ILogger logger,
    CookieStrFactory<BiliCookie> cookieStrFactory
) : AppService, IAccountTaskAppService
{
    /// <summary>
    /// 任务键：取具体 AppService 的类型名（如 DailyTaskAppService）。
    /// 刻意不改动既有的各个子类。
    /// </summary>
    public string TaskKey => GetType().Name;

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

    protected abstract Task DoTaskAccountAsync(
        BiliCookie ck,
        CancellationToken cancellationToken = default
    );

    private async Task WriteRecordAsync(
        BiliCookie ck,
        TaskRecordStatus status,
        string? message,
        TaskRecordTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        // 用服务定位而非构造注入：不想为了加一条记录去改动 11 个子类的构造函数。
        var writer = Global.ServiceProviderRoot?.GetService<ITaskRecordWriter>();
        if (writer is null || !long.TryParse(ck.UserId, out var userId))
        {
            return;
        }

        await writer.WriteAsync(userId, TaskKey, null, status, message, trigger, cancellationToken);
    }
}
