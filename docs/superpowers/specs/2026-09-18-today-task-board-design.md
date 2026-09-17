# 今日任务看板 + 漏做补做 — 设计规格

日期：2026-09-18
状态：待用户审查

## 1. 背景与目标

用户用 Docker 部署 BiliBiliToolPro Web（`F:\Docker\bili_tool_web`，5 个 B 站账号）。
问题是：**电脑关机时容器不运行，定时任务会整天空缺**，事后没有任何补救手段，也看不到某个账号今天到底做了哪些任务。

本设计交付三件事：

1. 一个能**清楚显示每个账号今天完成了哪些任务**的页面。
2. 检查**哪些账号的哪些任务没做**，并支持**自动补做**与**手动单项补做**。
3. 把已确认的「分享」故障在页面上处理掉（一键关闭）。

## 2. 分享功能故障的结论（已实测确认）

`/x/web-interface/share/add` 对全部 5 个账号恒返回 `{"code":-403,"message":"账号异常,操作失败"}`。

已排除的原因（均已实测）：

| 假设 | 实测结果 |
|---|---|
| 请求参数写错 | 换成另一同类开源工具 BLTH 的参数（`source=pc_client_normal, eab_x=2, ramval=0`）仍 -403 |
| Cookie 缺 buvid3（历史成因） | 5 个 Cookie 均含 buvid3，仍 -403 |
| 缺 buvid4 等新设备指纹 | 换新 buvid3 + buvid4，仍 -403 |
| Referer / Origin / UA 不对 | 换视频页带 spm 的 Referer、App UA，均 -403 |
| 需要 WBI 签名 | 加 `w_rs`/`wts` 签名，仍 -403 |
| csrf 位置不对 | body 与 query string 两种都试，均 -403 |
| 账号被整体风控 | 同账号点赞返回 code 0、投币每天成功 → 账号未被封 |
| 接口整体不可用 | 同一 IP 不带 Cookie 匿名分享返回 code 0 → 接口可用 |
| 需要真实浏览器流程 | 模拟「先打开视频页再分享」的完整 CookieJar 流程，仍 -403 |

历史日志（`Logs/log*.txt`）：2026-09-05 ～ 09-17 连续 13 天，每天 5 个账号 **100% 失败**，无一次成功。

**结论：这是 B 站服务端针对「登录态分享」的风控策略，本项目无法通过修改请求稳定修复。**

因此：
- 分享任务提供**一键关闭**（复用既有配置项 `DailyTaskConfig:IsShareVideo`）。
- 分享失败**不会**被自动补做无意义地反复重试（见 §5.4 的"分享特别规则"）。

## 3. 范围

**包含**：新页面、新执行记录表、状态判定、单项补做、自动补做定时任务、分享一键关闭、记录保留天数配置。

**不包含**：修改分享接口的任何"绕过"尝试；对既有 11 个任务的业务逻辑改动；前端框架/样式体系的调整。

## 4. 数据来源

### 4.1 B 站官方接口（权威，用于"每日任务"的 4 项）

| 项目 | 接口 | 判定 |
|---|---|---|
| 登录 | `/x/member/web/exp/reward` → `login` | `true` = 已完成 |
| 观看视频 | 同上 → `watch` | `true` = 已完成 |
| 分享视频 | 同上 → `share` | `true` = 已完成（当前恒为 false） |
| 投币 | 同上 → `coins`（今日投币获得的经验，1 币 = 10 经验） | `> 0` = 已完成；"今日已投 N 枚"另用 `ICoinDomainService.GetDonatedCoins(ck)` |

前三项由现成的 `IAccountDomainService.GetDailyTaskStatus(ck)` 一次请求拿到（内部即 `/x/member/web/exp/reward`，返回 `DailyTaskInfo`，含 `Login` / `Watch` / `Share` / `Coins` 字段）；投币枚数复用现成的 `ICoinDomainService.GetDonatedCoins(ck)`。**无需新增 B 站接口方法。**

**失败降级**：接口请求异常或返回 `-101`（Cookie 失效）时，该账号标记为 `未知`，**不参与补做判定**，避免误判。

### 4.2 新增执行记录表（用于"工具今天有没有给这个账号跑过这个任务"）

新表 `bili_task_records`：

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | `long` PK 自增 | |
| `UserId` | `long` | B 站 UID（`BiliCookie.UserId`） |
| `TaskKey` | `string(64)` | 任务键，取自 AppService 具体类型名，如 `DailyTaskAppService`、`LiveFansMedalAppService` |
| `TaskItemKey` | `string(64)` NULL | 子项键，如 `Login` / `Watch` / `Share` / `DonateCoin` / `VipPrivilege`；NULL 表示"任务级"记录 |
| `RecordDate` | `string(10)` `yyyy-MM-dd` | 本地（Asia/Shanghai）日期 |
| `Status` | `string(16)` | `Success` / `Failed` |
| `Message` | `string(512)` NULL | 失败原因摘要 |
| `Trigger` | `string(16)` | `Scheduled`（定时执行）/ `Auto`（自动补做）/ `Manual`（手动补做） |
| `CreatedAtUtc` | `DateTimeOffset` | |

索引：`(UserId, TaskKey, RecordDate)`；清理时按 `CreatedAtUtc` 删除。

**迁移安全性**：本次只有一个**新建表**，不改动任何既有表/列。老库（用户线上那份 `BiliBiliTool.db`）在应用启动时由 `DbInitializer.InitializeAsync()` 的 `MigrateAsync()` 自动建表，`cookies.json` 与既有数据不受影响。

**写入点**（共 2 处，改动集中）：

1. **定时执行** — `BaseMultiAccountsAppService.DoTaskAsync` 的账号循环内，包裹 `DoTaskAccountAsync` 后写一条 `TaskItemKey = NULL`、`Trigger = Scheduled` 的记录（成功或失败）。仅此一处即可覆盖全部 11 个任务，无需改动任何子类。
   - `TaskKey` 通过 `GetType().Name` 得到，稳定且无需改子类。
2. **补做执行** — 补做服务每次执行完写一条带 `TaskItemKey`、`Trigger = Auto|Manual` 的记录。

## 5. 状态判定与补做规则

### 5.1 任务目录（页面显示哪些项）

| 显示名 | Quartz Job 名（用于查 Cron） | 执行记录 `TaskKey`（AppService 类名） | 检查粒度 |
|---|---|---|---|
| 每日任务 → 登录 | `DailyJob` | `DailyTaskAppService` | 子项（B 站接口） |
| 每日任务 → 观看视频 | `DailyJob` | `DailyTaskAppService` | 子项（B 站接口） |
| 每日任务 → 分享视频 | `DailyJob` | `DailyTaskAppService` | 子项（B 站接口 + 特别规则 §5.4） |
| 每日任务 → 投币 | `DailyJob` | `DailyTaskAppService` | 子项（B 站接口） |
| 每日任务 → 大会员福利 | `DailyJob` | `DailyTaskAppService` | 子项（执行记录） |
| 直播粉丝勋章 | `LiveFansMedalJob` | `LiveFansMedalAppService` | 任务级 |
| 漫画签到/阅读 | `MangaJob` | `MangaTaskAppService` | 任务级 |
| 漫画特权 | `MangaPrivilegeJob` | `MangaPrivilegeTaskAppService` | 任务级 |
| 银瓜子换硬币 | `Silver2CoinJob` | `Silver2CoinTaskAppService` | 任务级 |
| 直播抽奖 | `LiveLotteryJob` | `LiveLotteryTaskAppService` | 任务级 |
| 充电 | `ChargeJob` | `ChargeTaskAppService` | 任务级 |
| 大会员积分 | `VipBigPointJob` | `VipBigPointAppService` | 任务级 |
| 批量取关 | `UnfollowBatchedJob` | `UnfollowBatchedTaskAppService` | 任务级 |

`LoginJob` / `TestBiliJob`（Cron 为 `0 0 0 1 1 ?`，实际不参与日常运行）不在此表中，页面不显示。

配置中 `IsEnable = false` 的任务直接显示「⛔ 已关闭」，不参与补做判定。

### 5.2 每项的状态

| 状态 | 判定条件 | 页面显示 | 自动补做 |
|---|---|---|---|
| 已完成 | 首选 B 站接口为 true；无接口项则今天有 `Success` 记录 | ✅ 已完成（含完成时间） | — |
| 未执行 | 今天**没有任何**该账号该任务的执行记录 | ❌ 今天还没做 | ✅ 允许 |
| 失败 | 今天有记录，但最新一条为 `Failed`，或 B 站接口仍为 false | ⚠️ 失败（附原因） | ✅ 允许，最多 3 次 |
| 未到点 | 该任务今天的计划触发时间还没到 | ⏳ 等待执行（12:00） | ⏸ 不判定 |
| 本日无需执行 | 该任务今天没有任何触发点（如每月 1 号的任务在 15 号） | ➖ 本日无需执行 | ⏸ 不判定 |
| 已关闭 | 配置 `IsEnable = false` | ⛔ 已关闭 | — |
| 未知 | B 站接口请求失败或 Cookie 失效 | ❓ 状态未知（需重新登录） | ❌ 不补做 |
| 已放弃 | 自动执行次数已达 3 次且仍未完成 | ⚠️ 已自动重试 3 次仍未完成 | ❌ 不再自动补做，**保留手动补做按钮** |

判定优先级（自上而下，命中即停）：已关闭 → 本日无需执行 → 未到点 → 未知 → 已完成 → 已放弃 → 失败 → 未执行。

### 5.3 "今天该做的时间点"如何计算

自动补做**不能**在计划时间之前抢先执行。判定方式：

1. 从 Quartz 调度器读取该 Job 的 `ICronTrigger.CronExpressionString`（这是权威值，用户可能在配置页改过）。
2. 用 `CronExpression` 枚举出**今天**的全部触发时间。
3. 若「当前时间 ≥ 今天最后一次触发时间 + 宽限期（默认 10 分钟）」，则该任务今天"已到点"。
4. 今天没有触发点的任务（如每月 1 号的任务在 15 号）→ 今天**不参与**补做判定，页面显示为「本日无需执行」。

用户通过「计划任务」页手动触发（TriggerJob）也会写入 `Scheduled` 记录，因此手动跑过后页面会立即变为已完成。

### 5.4 分享特别规则

- 若 `IsShareVideo = false` → 显示「⛔ 已关闭」，不检查、不补做。
- 若开启且 B 站 `share` 仍为 false，但工具今天已尝试过 → 显示「⚠️ B站拒绝（账号异常）」，**不参与自动补做**（避免每天固定浪费 3 次自动重试）。用户仍可手动点补做。
- 页面上该行提供一个 **[不再尝试]** 按钮，一键把 `IsShareVideo` 置为 false 并保存，之后该行变为「⛔ 已关闭」。

### 5.5 自动执行次数上限

- 计数口径：某账号某检查项在**当天**产生的 `Trigger = Auto` 记录条数（含"漏做"的首次自动补做）。
- 上限：**3 次**。达到后不再自动执行，页面显示「已自动重试 3 次仍未完成」。
- 手动补做（`Trigger = Manual`）**不计入**该上限，也不受上限限制。
- 计数按自然日重置（`RecordDate`）。

## 6. 补做动作（单项）

每个检查项对应一个明确的执行动作，只执行该项，不重跑整个任务。

| 检查项 | 执行动作 |
|---|---|
| 登录 | `IAccountDomainService.LoginByCookie(ck)` |
| 观看视频 | 取随机视频后 `IVideoDomainService.WatchVideo(video, ck)` |
| 分享视频 | 取随机视频后 `IVideoDomainService.ShareVideo(video, ck)`（若今天未观看，先 `OpenVideo`） |
| 投币 | `IDonateCoinDomainService.AddCoinsForVideos(ck)`（沿用 `IsDonateCoinForArticle` 的专栏分支） |
| 大会员福利 | `accountDomainService.LoginByCookie(ck)` 取 userInfo 后 `IVipPrivilegeDomainService.ReceiveVipPrivilege(userInfo, ck)` |
| 其他任务（任务级） | 调用该 AppService 的单账号执行入口（见 §7） |

为支持「观看 / 分享」单项补做，需要把 `VideoDomainService` 的两个方法由 `private` 改为 `public`（唯一必要的既有代码可见性调整，不改逻辑）：

- `GetRandomVideoForWatchAndShare(BiliCookie)` — 取一个用于观看/分享的随机视频
- `OpenVideo(VideoInfoDto, BiliCookie)` — 分享前先打开视频（与 `WatchAndShareVideo` 中"未观看过就先打开"的既有行为保持一致）

## 7. AppService 的单账号执行入口

在 `BaseMultiAccountsAppService` 新增公开方法：

```csharp
public async Task DoTaskForAccountAsync(long userId, CancellationToken ct = default)
```

实现：遍历 `CookieStrFactory<BiliCookie>`，找到 `BiliCookie.UserId == userId` 的那个 Cookie，调用 `DoTaskAccountAsync(ck, ct)`；找不到则抛异常。

新增接口 `IAccountTaskAppService : IAppService`，声明该方法；`BaseMultiAccountsAppService` 实现它。补做服务通过 `TaskKey → AppService` 的映射拿到实例。

各子任务内部已有幂等判断（如投币先查"今日已投"、观看先查"是否看过"），因此重复调用是安全的。

## 8. 自动补做定时任务

新增 `AutoRecoverJob`（归属于 `Constant.BiliJobGroup`），使用 **Quartz SimpleTrigger**（`WithIntervalInHours(N).RepeatForever()`），首次触发在应用启动后 1 分钟。

执行步骤：

1. 读取配置，若 `IsEnable = false` 则直接返回。
2. 对每个账号 × 每个检查项：
   - 跳过：已关闭 / 未到点 / B 站状态未知 / 分享且已尝试过 / 自动次数已达 3 / 已成功。
   - 其余：执行补做，写 `Trigger = Auto` 记录。
3. 清理超过 `RecordRetentionDays` 天的 `bili_task_records` 记录。
4. 全程用 `SemaphoreSlim(1,1)` 保证同一时刻只有一个补做流程在跑（防止自动与手动并发重复执行）。

**并发与异常**：单项补做失败只记录该条记录，不中断其余项；整体异常写日志，不让 Job 抛异常中断调度。

## 9. 页面设计（`/Today`，导航「今日任务」）

顶部工具条：

```
日期：2026-09-18（今天）   最后检查：12:05
[立即刷新]  [一键补做全部漏做项]
自动补做：[开]  每 [2] 小时检查一次   记录保留 [3] 天   [保存设置]
```

账号卡片（每个账号一张）：

```
👤 账号1 · 态xx好啊                    UID 1173...   [整卡补做]
--------------------------------------------------------------
登录          ✅ 已完成                      09-18 12:00
观看视频      ✅ 已完成                      09-18 12:00
分享视频      ⚠️ B站拒绝（账号异常）          [补做] [不再尝试]
投币          ❌ 今天还没做                   [补做]
大会员福利    ✅ 已完成                      09-18 12:00
--------------------------------------------------------------
直播粉丝勋章   ❌ 今天还没做                   [补做]
漫画签到/阅读  ⚠️ 已自动重试 3 次仍未完成      [补做]
银瓜子换硬币   ✅ 已完成                      09-18 08:00
直播抽奖      ⛔ 已关闭
```

- 每个可补做的项右侧有 `[补做]` 按钮，点击后按钮进入 loading，完成后局部刷新该行并弹出提示（成功/失败原因）。
- 卡片底部提供 `[整卡补做]`，一键把该账号所有可补做项依次补做。
- 页面顶部 `[一键补做全部漏做项]` 对所有账号执行。
- 手动补做**不受 3 次上限限制**。

## 10. 配置项

新增配置节（写入 `bili_appsettings` 表，与既有配置一致）：

```json
"AutoRecoverConfig": {
  "IsEnable": true,
  "IntervalHours": 2,
  "RecordRetentionDays": 3
}
```

- 页面「保存设置」通过 `SqliteConfigurationProvider.BatchSet` 写入，随后 `scheduler.RescheduleJob` 重建 SimpleTrigger 以应用新的间隔。
- 关闭自动补做 → `PauseTrigger`；开启 → `ResumeTrigger`（复用既有配置页的做法）。
- 分享开关写入既有 `DailyTaskConfig:IsShareVideo`。

## 11. 涉及改动清单

**新增**

- `src/Ray.BiliBiliTool.Domain/TaskRecord.cs` — 记录实体
- `src/Ray.BiliBiliTool.Config/Options/AutoRecoverOptions.cs` — 配置模型
- `src/Ray.BiliBiliTool.Infrastructure.EF/Migrations/*_AddTaskRecords.cs` — EF 迁移（含 `BiliDbContextModelSnapshot.cs` 更新）
- `src/Ray.BiliBiliTool.Web/Services/TodayTaskService.cs` + `ITodayTaskService.cs` — 状态查询与补做编排
- `src/Ray.BiliBiliTool.Web/Services/TaskCatalog.cs` — 任务/子项目录与中文名映射
- `src/Ray.BiliBiliTool.Web/Jobs/AutoRecoverJob.cs` — 自动补做任务
- `src/Ray.BiliBiliTool.Web/Components/Pages/Today/Today.razor`(+`.razor.cs`) — 页面

**修改**

- `src/Ray.BiliBiliTool.Infrastructure.EF/BiliDbContext.cs` — 注册 `DbSet<TaskRecord>`
- `src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs` — 循环内写执行记录 + 新增单账号执行入口
- `src/Ray.BiliBiliTool.Application.Contracts/` — 新增 `IAccountTaskAppService`
- `src/Ray.BiliBiliTool.DomainService/VideoDomainService.cs` — `GetRandomVideoForWatchAndShare` 改 `public`
- `src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs` — 注册 `AutoRecoverJob` 与 SimpleTrigger
- `src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionExtension.cs` — 注册新服务
- `src/Ray.BiliBiliTool.Web/Components/Layout/NavMenu.razor` — 新增导航项

## 12. 验证方案

验证一律在**独立副本**上进行，不触碰用户在用的 `F:\Docker\bili_tool_web\config`（cookies.json 与数据库）。做法：把仓库根跑起来时用一份指向临时目录的 `config/`（复制 `cookies.json`，数据库用新建的空库）。

1. `dotnet build Ray.BiliBiliTool.sln` 全解决方案编译通过（0 error）。
2. 启动应用（`dotnet run --project src/Ray.BiliBiliTool.Web`，指向临时 config 目录），确认 EF 迁移自动执行；用 `sqlite3` 检查 `bili_task_records` 表与索引已创建。
3. 打开 `/Today`，对照**手工调用** `/x/member/web/exp/reward` 的返回，逐个账号核对「登录/观看/分享/投币」四项状态一致。
4. 点击「补做」投币 → 确认：界面按钮进入 loading 并给出结果；数据库中新增一条 `TaskKey=DailyTaskAppService, TaskItemKey=DonateCoin, Trigger=Manual` 的记录；B 站侧"今日已投"枚数确实增加。
5. 点击分享行的「不再尝试」→ 确认 `bili_appsettings` 中 `DailyTaskConfig:IsShareVideo` 变为 `false`，该行变为「⛔ 已关闭」；手动触发一次 `DailyJob`，从日志确认分享步骤被跳过。
6. 自动补做的三条关键规则，用「计划任务」页面对 `AutoRecoverJob` 点「立即执行」来触发，逐条验证：
   - **到点判定**：把 `DailyTaskConfig:Cron` 临时改到一个还没到的时间 → 该账号的每日任务应显示「⏳ 等待执行」，且不被补做。
   - **3 次上限**：让某个必然失败的项（如 Cookie 失效的账号）连续触发 4 次 → 第 4 次不再执行，页面显示「已自动重试 3 次仍未完成」，手动补做按钮仍可用。
   - **未知状态不补做**：断开网络（或把接口地址指向不可达地址）后触发 → 状态显示「❓ 状态未知」，且无任何补做记录写入。

## 13. 已知限制

- B 站接口查询失败的账号显示「状态未知」，此时不做任何自动补做 —— 宁可漏补也不误判。
- 「大会员福利」没有 B 站日级接口，只能按执行记录判定，无法独立验证 B 站侧是否真的领到。
- 自动补做的前提是**容器正在运行**。若关机期间跨越了整天，开机后补的是"当天"的任务，前一天的空缺不会追溯（B 站每日任务本身也只以当天为准）。
