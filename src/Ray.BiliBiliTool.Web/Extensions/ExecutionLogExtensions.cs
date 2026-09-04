using System.Text;
using Ray.BiliBiliTool.Domain;

namespace Ray.BiliBiliTool.Web.Extensions;

public static class ExecutionLogExtensions
{
    private const int RESULT_DISPLAY_LENGTH = 80;

    public static string GetShortResultMessage(this ExecutionLog log)
    {
        StringBuilder strBldr = new StringBuilder();

        if (log.ReturnCode != null)
        {
            strBldr.Append("返回码 " + log.ReturnCode + "。");
        }

        if (log.Result != null)
        {
            var shortResult = log.Result.Substring(
                0,
                Math.Min(log.Result.Length, RESULT_DISPLAY_LENGTH)
            );
            strBldr.Append(shortResult);
        }
        else if (log.LogType == LogType.ScheduleJob)
        {
            if (log.IsSuccess is null)
            {
                strBldr.Append("任务执行中…");
            }
            else if (log.IsSuccess.Value)
            {
                strBldr.Append("任务执行成功。");
            }
            else
            {
                strBldr.Append("任务执行失败。");
            }
        }

        return strBldr.ToString();
    }

    public static string GetShortExceptionMessage(this ExecutionLog log)
    {
        StringBuilder strBldr = new StringBuilder();

        if (log.ReturnCode != null)
        {
            strBldr.Append("返回码 " + log.ReturnCode + "。");
        }

        if (log.ErrorMessage != null)
        {
            strBldr.Append(
                log.ErrorMessage.Substring(
                    0,
                    Math.Min(log.ErrorMessage.Length, RESULT_DISPLAY_LENGTH)
                )
            );
            return strBldr.ToString();
        }

        return string.Empty;
    }

    public static bool ShowExecutionDetailButton(this ExecutionLog log) =>
        log.ExecutionLogDetail?.ExecutionDetails != null
        || (log.Result?.Length ?? 0) > RESULT_DISPLAY_LENGTH;
}
