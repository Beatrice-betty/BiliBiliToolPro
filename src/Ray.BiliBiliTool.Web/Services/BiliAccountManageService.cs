using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using QRCoder;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.Passport;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// B站账号管理：读取/修改 cookies.json（复用项目既有的 Cookie 存储与扫码登录机制），
/// 绝不向前端暴露完整 Cookie。
/// </summary>
public class BiliAccountManageService(
    IConfiguration configuration,
    IWebHostEnvironment hostEnvironment,
    IPassportApi passportApi,
    IUserInfoApi userInfoApi,
    ILoginDomainService loginDomainService
) : IBiliAccountManageService
{
    private const string CookieConfigSection = "BiliBiliCookies";

    // 简单进程内缓存，避免频繁打开页面时反复请求B站接口
    private static readonly ConcurrentDictionary<
        long,
        (BiliAccountDto Dto, DateTime CheckedAtUtc)
    > _profileCache = new();
    private static readonly TimeSpan CacheLifeTime = TimeSpan.FromSeconds(45);

    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<List<BiliAccountDto>> GetAccountListAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default
    )
    {
        var rawList = await ReadCookieStringsAsync(cancellationToken);
        var result = new List<BiliAccountDto>();

        for (var i = 0; i < rawList.Count; i++)
        {
            var dto = await BuildAccountDtoAsync(i, rawList[i], forceRefresh, cancellationToken);
            result.Add(dto);
        }

        return result;
    }

    public async Task<BiliAccountDto> CheckAccountAsync(
        long userId,
        CancellationToken cancellationToken = default
    )
    {
        var rawList = await ReadCookieStringsAsync(cancellationToken);
        for (var i = 0; i < rawList.Count; i++)
        {
            if (TryGetUserId(rawList[i]) == userId)
            {
                return await BuildAccountDtoAsync(i, rawList[i], true, cancellationToken);
            }
        }

        throw new Exception($"未找到 UID 为 {userId} 的账号");
    }

    public async Task<BiliQrCodeDto> CreateLoginQrCodeAsync(
        CancellationToken cancellationToken = default
    )
    {
        var re = await passportApi.GenerateQrCode();
        if (re.Code != 0)
        {
            throw new Exception($"获取登录二维码失败：{re.Message}");
        }

        var url = re.Data.Url;
        var qrImageDataUrl = GenerateQrImageDataUrl(url);

        return new BiliQrCodeDto
        {
            QrcodeKey = re.Data.Qrcode_key,
            QrImageDataUrl = qrImageDataUrl,
        };
    }

    public async Task<BiliQrLoginCheckDto> CheckQrLoginAsync(
        string qrcodeKey,
        CancellationToken cancellationToken = default
    )
    {
        var check = await passportApi.CheckQrCodeHasScaned(qrcodeKey);
        if (!check.IsSuccessStatusCode)
        {
            return new BiliQrLoginCheckDto
            {
                Success = false,
                State = "pending",
                Message = "二维码检测接口请求失败，请稍后重试",
            };
        }

        var contentStr = await check.Content.ReadAsStringAsync(cancellationToken);
        var content = JsonSerializer.Deserialize<BiliApiResponse<TokenDto>>(
            contentStr,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            }
        );

        if (content?.Code != 0)
        {
            return new BiliQrLoginCheckDto
            {
                Success = false,
                State = "failed",
                Message = content?.Message ?? "登录接口返回异常",
            };
        }

        var dataCode = content.Data.Code;
        switch (dataCode)
        {
            case 0: // 扫描并确认成功
                return await HandleQrLoginSuccessAsync(check, cancellationToken);
            case 86038: // 二维码已失效
                return new BiliQrLoginCheckDto
                {
                    Success = false,
                    State = "expired",
                    Message = "二维码已失效，请重新获取后扫码",
                };
            case 86090: // 已扫码，等待确认
                return new BiliQrLoginCheckDto
                {
                    Success = false,
                    State = "scanned",
                    Message = "已扫码，请在手机上确认登录",
                };
            case 86101: // 等待扫码
            default:
                return new BiliQrLoginCheckDto
                {
                    Success = false,
                    State = "pending",
                    Message = "请使用哔哩哔哩 App 扫描二维码登录",
                };
        }
    }

    public async Task DeleteAccountAsync(long userId, CancellationToken cancellationToken = default)
    {
        var path = GetCookieJsonPath();
        if (!File.Exists(path))
        {
            throw new Exception("未找到账号配置文件 cookies.json");
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            var root = ParseCookiesJson(text);
            var array = root?[CookieConfigSection] as JsonArray;
            if (array == null || array.Count == 0)
            {
                throw new Exception($"未找到要删除的账号（UID：{userId}）");
            }

            var removed = false;
            var keep = new JsonArray();
            foreach (var item in array)
            {
                var raw = item?.GetValue<string>();
                if (raw != null && TryGetUserId(raw) == userId)
                {
                    removed = true;
                    continue;
                }

                keep.Add(raw);
            }

            if (!removed)
            {
                throw new Exception($"未找到 UID 为 {userId} 的账号，请刷新后重试");
            }

            root![CookieConfigSection] = keep;
            var newJson = SerializeCookiesJson(root);
            await File.WriteAllTextAsync(
                path,
                newJson,
                new System.Text.UTF8Encoding(false),
                cancellationToken
            );

            ReloadConfiguration();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    #region private

    /// <summary>
    /// 计算 cookies.json 完整路径（与现有扫码登录持久化逻辑保持一致）
    /// </summary>
    private string GetCookieJsonPath()
    {
        var path = hostEnvironment.ContentRootPath;
        var indexOfBin = path.LastIndexOf("bin", StringComparison.OrdinalIgnoreCase);
        if (indexOfBin != -1)
        {
            path = path[..indexOfBin];
        }

        if (string.Equals(configuration["PlatformType"], "Web", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(path, "config");
        }

        return Path.Combine(path, "cookies.json");
    }

    /// <summary>
    /// 确保 cookies.json 所在目录存在（登录保存前调用）
    /// </summary>
    private void EnsureCookieDirectory()
    {
        var dir = Path.GetDirectoryName(GetCookieJsonPath());
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private async Task<List<string>> ReadCookieStringsAsync(CancellationToken cancellationToken)
    {
        var path = GetCookieJsonPath();
        if (!File.Exists(path))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            var root = ParseCookiesJson(text);
            var array = root?[CookieConfigSection] as JsonArray;
            var result = new List<string>();
            if (array == null)
            {
                return result;
            }

            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var raw))
                {
                    result.Add(raw);
                }
            }

            return result;
        }
        catch
        {
            // 配置文件异常时返回空列表，避免页面报错
            return [];
        }
    }

    private static JsonNode? ParseCookiesJson(string text)
    {
        // 兼容 UTF-8 BOM 以及项目自身写入时可能存在的行尾逗号与注释
        if (!string.IsNullOrEmpty(text) && text[0] == '﻿')
        {
            text = text[1..];
        }

        var docOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };
        return JsonNode.Parse(text, nodeOptions: null, documentOptions: docOptions);
    }

    private async Task<BiliAccountDto> BuildAccountDtoAsync(
        int index,
        string rawCookie,
        bool forceRefresh,
        CancellationToken cancellationToken
    )
    {
        var userId = TryGetUserId(rawCookie);
        var dto = new BiliAccountDto
        {
            Index = index,
            UserId = userId ?? 0,
            LastCheckedAt = DateTime.Now,
        };

        if (userId == null)
        {
            dto.IsValid = false;
            dto.StatusText = "Cookie 格式异常，无法解析 UID";
            return dto;
        }

        // 缓存命中（且非强制刷新）时直接返回
        if (
            !forceRefresh
            && _profileCache.TryGetValue(userId.Value, out var cached)
            && DateTime.UtcNow - cached.CheckedAtUtc < CacheLifeTime
        )
        {
            return CloneDto(cached.Dto, index);
        }

        try
        {
            var cookie = CookieStrFactory<BiliCookie>.CreateNew(rawCookie);
            var resp = await userInfoApi.LoginByCookie(cookie.ToString());

            if (resp.Code == 0 && resp.Data?.IsLogin == true)
            {
                dto.UserName = resp.Data.Uname;
                dto.Level = resp.Data.Level_info?.Current_level;
                dto.IsValid = true;
                dto.StatusText = "Cookie 有效";
            }
            else
            {
                dto.IsValid = false;
                dto.StatusText =
                    resp.Code == -101 ? "未登录或 Cookie 已失效，请重新扫码登录"
                    : string.IsNullOrWhiteSpace(resp.Message) ? "Cookie 无效"
                    : $"Cookie 无效：{resp.Message}";
            }
        }
        catch
        {
            // 检测失败（网络异常等），不向前端暴露内部细节，避免泄露信息
            dto.IsValid = false;
            dto.StatusText = "检测失败，请稍后重试或重新扫码登录";
        }

        dto.LastCheckedAt = DateTime.Now;
        _profileCache[userId.Value] = (CloneDto(dto, index), DateTime.UtcNow);
        return dto;
    }

    private static BiliAccountDto CloneDto(BiliAccountDto dto, int index) =>
        new()
        {
            Index = index,
            UserId = dto.UserId,
            UserName = dto.UserName,
            Level = dto.Level,
            IsValid = dto.IsValid,
            StatusText = dto.StatusText,
            LastCheckedAt = dto.LastCheckedAt,
        };

    private static string GenerateQrImageDataUrl(string url)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        var png = qrCode.GetGraphic(10);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    private async Task<BiliQrLoginCheckDto> HandleQrLoginSuccessAsync(
        HttpResponseMessage check,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var setCookies = check
                .Headers.SingleOrDefault(header => header.Key == "Set-Cookie")
                .Value;
            var cookieStr = CookieInfo.ConvertSetCkHeadersToCkStr(setCookies);
            var cookie = CookieStrFactory<BiliCookie>.CreateNew(cookieStr);
            cookie.Check();

            // 复用现有登录逻辑：访问主站补全 buvid 等信息
            var mergedCookie = await loginDomainService.SetCookieAsync(cookie, cancellationToken);

            // 确保 config 目录存在（非 Docker 裸跑时也可正常保存）
            EnsureCookieDirectory();

            // 复用现有逻辑持久化到 cookies.json（同 UID 自动更新，不同 UID 自动新增）
            await loginDomainService.SaveCookieToJsonFileAsync(mergedCookie, cancellationToken);
            ReloadConfiguration();

            var userId = TryGetUserId(mergedCookie.CookieStr) ?? 0;
            var dto = await BuildAccountDtoAsync(
                await GetAccountIndexAsync(userId, cancellationToken),
                mergedCookie.CookieStr,
                true,
                cancellationToken
            );

            return new BiliQrLoginCheckDto
            {
                Success = true,
                State = "success",
                Message = "登录成功，账号已保存",
                UserId = dto.UserId,
                UserName = dto.UserName,
            };
        }
        catch (Exception ex)
        {
            // 只把失败原因提示给用户，不外泄 Cookie 内容
            return new BiliQrLoginCheckDto
            {
                Success = false,
                State = "failed",
                Message = $"登录保存失败：{ex.Message}",
            };
        }
    }

    private async Task<int> GetAccountIndexAsync(long userId, CancellationToken cancellationToken)
    {
        var rawList = await ReadCookieStringsAsync(cancellationToken);
        for (var i = 0; i < rawList.Count; i++)
        {
            if (TryGetUserId(rawList[i]) == userId)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// 将 cookies.json 根节点序列化为带缩进的多行 JSON。
    /// 注意：.NET 8 的 JsonNode.ToJsonString(JsonSerializerOptions) 要求 options 必须指定
    /// TypeInfoResolver，否则会抛出 InvalidOperationException。
    /// </summary>
    private static string SerializeCookiesJson(JsonNode root)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        return root.ToJsonString(options);
    }

    private void ReloadConfiguration()
    {
        if (configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }

    /// <summary>
    /// 从 Cookie 字符串中解析 DedeUserID
    /// </summary>
    private static long? TryGetUserId(string cookieStr)
    {
        if (string.IsNullOrWhiteSpace(cookieStr))
        {
            return null;
        }

        foreach (var item in cookieStr.Split(';', StringSplitOptions.TrimEntries))
        {
            var index = item.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = item[..index].Trim();
            if (
                string.Equals(key, "DedeUserID", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(item[(index + 1)..].Trim(), out var uid)
            )
            {
                return uid;
            }
        }

        return null;
    }

    #endregion
}
