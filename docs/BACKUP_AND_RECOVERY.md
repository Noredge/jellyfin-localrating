# 评分与评价的备份、恢复和重启检查

## 数据分别保存在哪里

Local Rating 的两类数据不在同一个数据库中：

| 数据 | 权威存储位置 | 插件删除后的结果 |
|---|---|---|
| 1–10 个人评分 | Jellyfin 自己的用户数据 | 仍由 Jellyfin 保留 |
| 个人评价文字 | `<Jellyfin 数据目录>\localrating\localrating.db` | 需要保留该目录 |

`Reviews.RatingSnapshot` 只是评价保存时的评分快照，不是评分的权威备份。只复制 `localrating.db` 不能完整备份 Jellyfin 原生评分。

Windows 托盘安装的常见数据目录是：

```text
C:\ProgramData\Jellyfin\Server\data
```

实际路径可能因安装方式而不同。操作前应从 Jellyfin 日志或服务器配置确认数据目录，不要仅依赖示例路径。

## 建议的备份层级

### 完整备份

要同时保护个人评分、评价和 Jellyfin 其他数据，应在 Jellyfin 完全停止后备份整个 Jellyfin 数据目录。这是升级 Jellyfin、迁移机器或进行高风险维护前的首选方式。

### 仅备份插件评价

只需要保护文字评价时，可以备份：

```text
<Jellyfin 数据目录>\localrating\
```

应复制整个目录，而不是只复制 `localrating.db`。SQLite 使用 WAL 时，目录中可能暂时存在 `localrating.db-wal` 和 `localrating.db-shm`。

## 安全备份步骤

生产数据库检查和服务器启动必须使用实际 Jellyfin 运行账户。即便以 SQLite `readOnly` 方式打开 WAL 数据库，也可能创建辅助文件；不要用不同权限的沙箱账户直接打开生产数据库，以免辅助文件权限导致评价无法写入。

部署后检查数据父目录、数据库及现存 WAL/SHM 的有效写权限；不要删除活动的 WAL/SHM 来“修复”权限。若进程已缓存只读连接，修复权限后需要正常重启，并实际验证评价写入。仅 HTTP 200、旧评价可读或内容哈希相同都不能证明可写。

1. 在 Jellyfin Dashboard 中停止服务器，或通过 Windows 服务管理停止 Jellyfin。
2. 确认 Jellyfin 进程已经退出。
3. 将整个 `localrating` 目录复制到带日期的备份目录。
4. 记录数据库文件的大小和 SHA-256。
5. 重新启动 Jellyfin，并执行本文末尾的重启检查。

示例命令中的路径必须替换为实际路径：

```powershell
$source = 'C:\ProgramData\Jellyfin\Server\data\localrating'
$destination = Join-Path 'D:\JellyfinBackups' ((Get-Date -Format 'yyyy-MM-dd') + '\localrating')
Copy-Item -LiteralPath $source -Destination $destination -Recurse
Get-FileHash -Algorithm SHA256 -LiteralPath "$destination\localrating.db"
```

不要在 Jellyfin 正在运行并可能写入评价时，把单独复制的数据库文件当作可靠备份。

## 恢复插件评价

1. 停止 Jellyfin，并确认进程已经退出。
2. 将当前 `localrating` 目录重命名为带 `before-restore` 标记的安全副本。
3. 把备份的整个 `localrating` 目录复制回 Jellyfin 数据目录。
4. 保留原有文件名，不要单独编辑 SQLite 文件。
5. 启动 Jellyfin。
6. 打开一个已评价项目，核对评分、评价和卡片徽章。
7. 检查 Jellyfin 日志中是否出现 `Local Rating` 或 SQLite 错误。

恢复完整 Jellyfin 数据备份时，应恢复与该备份配套的完整数据目录，并使用兼容的 Jellyfin 版本。不要随意将单个 Jellyfin 核心数据库与其他日期的数据文件混合。

## 数据库版本行为

- 当前插件评价数据库版本为 `1`。
- 对于尚未登记结构版本的评价数据库，初始化时会在保留原有 `Reviews` 数据的前提下登记为版本 `1`。
- 如果数据库版本高于当前插件支持的版本，插件会拒绝读写评价，而不是猜测结构并可能破坏数据。
- 以后只有在真实结构发生变化时才增加版本，并为该次变化编写定向迁移测试。

## 可重复的 Jellyfin 重启检查

每次安装新插件包后执行以下步骤：

- [ ] 重启前记录一个已有评分和评价的影片。
- [ ] 重启 Jellyfin。
- [ ] Dashboard → Plugins 中的 **Local Rating** 显示为启用状态。
- [ ] 使用 `Ctrl+F5` 刷新 Web 客户端。
- [ ] 打开记录的影片，评分和评价内容正确。
- [ ] 修改评分或评价并保存，页面显示真实的成功或失败状态。
- [ ] 返回主页或媒体库，已有评分的卡片显示徽章且卡片仍可点击。
- [ ] 分别检查当前实际使用的原生 Jellyfin 或 ElegantFin 页面。
- [ ] Jellyfin 日志中没有新的 Local Rating 加载、SQLite 或 Web 注入错误。

构建通过或 DLL 已经复制到插件目录，都不能替代这项重启检查。
