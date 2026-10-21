using Memo.Models;
using Microsoft.Data.Sqlite;

namespace Memo.Services;

/// <summary>
/// SQLite 存储层。日期一律用本地日期字符串 yyyy-MM-dd 存，方便直接做字符串比较。
///
/// 关键设计：created_date 是这条待办的"归属日期"——默认就是新建它的那天，
/// 但也可以是一个未来的日子（提前把事安排到明天、下周）。
/// 靠 WHERE (created_date = @today OR (created_date &lt; @today AND is_done = 0) OR done_date = @today)
/// 实现"昨天没做完的自动出现在今天"，同时"还没到那天的计划"不会提前冒出来，
/// 最后一段让"今天才勾掉的旧条目"当天仍然留在卡上，不做后台定时搬运，避免跨天不刷新时数据错位。
/// </summary>
public static class MemoStore
{
    private static string _connString = string.Empty;
    private static string? _databasePathOverride;

    /// <summary>仅自检使用：把数据库指向别处，避免污染真实数据。</summary>
    public static void UseDatabasePath(string path) => _databasePathOverride = path;

    public static string DateKey(DateOnly date) => date.ToString("yyyy-MM-dd");

    public static void Initialize()
    {
        var databasePath = _databasePathOverride ?? AppPaths.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memo_item (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                content      TEXT    NOT NULL,
                priority     INTEGER NOT NULL DEFAULT 1,
                is_done      INTEGER NOT NULL DEFAULT 0,
                sort_order   INTEGER NOT NULL DEFAULT 0,
                created_date TEXT    NOT NULL,
                done_date    TEXT,
                done_at      TEXT,
                created_at   TEXT    NOT NULL,
                deleted_at   TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_memo_item_date ON memo_item(created_date, done_date);
            """;
        cmd.ExecuteNonQuery();
        EnsureSoftDeleteColumn(conn);

        // 只有正式库才做备份，自检用的临时库没必要
        if (_databasePathOverride is null)
        {
            BackupDatabase();
        }
    }

    /// <summary>
    /// 老库升级：给已有的 memo_item 补 deleted_at 列。
    /// 走 PRAGMA table_info 判断，不靠 ALTER TABLE 抛异常来试探。
    /// </summary>
    private static void EnsureSoftDeleteColumn(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(memo_item);";
        var hasColumn = false;
        using (var reader = check.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "deleted_at", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE memo_item ADD COLUMN deleted_at TEXT;";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 每次启动把当前数据库快照到 backups 目录，保留最近 20 份。
    /// 起因：一次维护操作误删了 %APPDATA%\Memo，数据全丢且无法恢复。
    /// 用 SQLite 的 VACUUM INTO 做一致性快照，比直接复制文件安全（不会拷到写了一半的 WAL 状态）。
    /// </summary>
    private static void BackupDatabase()
    {
        try
        {
            var backupDir = AppPaths.BackupDir;
            Directory.CreateDirectory(backupDir);

            using var conn = Open();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM memo_item;";
            var rows = Convert.ToInt64(countCmd.ExecuteScalar() ?? 0L);

            // 空库不备份，避免把"有内容"的旧快照挤出保留窗口
            if (rows == 0)
            {
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var target = Path.Combine(backupDir, $"memo_{stamp}.db");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}';";
            cmd.ExecuteNonQuery();

            // 只留最近 20 份
            var stale = new DirectoryInfo(backupDir)
                .GetFiles("memo_*.db")
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(20);
            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // 单个旧备份删不掉不影响启动
                }
            }
        }
        catch
        {
            // 备份失败绝不能挡住程序启动
        }
    }

    private static SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// 载入今天要做的待办：安排在今天做的 + 更早安排、至今还没做完的（顺延过来的）
    /// + 今天勾掉的（哪怕它原本排在别的日子）。
    /// 第二条必须是 created_date &lt; @today，否则"提前安排到明天的事"今天就会提前冒出来。
    /// 第三条是补上的：一条昨天欠下的事今天才勾完，只按前两条取数的话它会当场从卡片上消失，
    /// 看着像"刚勾掉就没了"（20 日记下、21 日才办完的那种就是这样）。补上以后今天还看得见，
    /// 到了明天 done_date 不再是今天，自然退出。
    /// </summary>
    public static List<MemoItem> LoadForDate(DateOnly today)
    {
        var result = new List<MemoItem>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, priority, is_done, sort_order, created_date, done_date, done_at, created_at
            FROM memo_item
            WHERE deleted_at IS NULL
              AND (created_date = @today
                   OR (created_date < @today AND is_done = 0)
                   OR done_date = @today)
            ORDER BY is_done ASC,
                     CASE priority WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END ASC,
                     sort_order ASC,
                     id ASC;
            """;
        cmd.Parameters.AddWithValue("@today", DateKey(today));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var createdDate = reader.GetString(5);
            var item = new MemoItem
            {
                Id = reader.GetInt64(0),
                Content = reader.GetString(1),
                Priority = reader.GetInt32(2),
                IsDone = reader.GetInt32(3) != 0,
                SortOrder = reader.GetInt32(4),
                CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                DoneAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                CarriedDays = DateOnly.TryParse(createdDate, out var created) && created < today
                    ? today.DayNumber - created.DayNumber
                    : 0,
            };
            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// 按日期载入"那一天"的记录，用于回看历史。与 LoadForDate 的区别是不受"今天"限制：
    ///   1. 那天新建的（created_date = 那天）
    ///   2. 那天完成的（done_date = 那天）—— 哪怕更早创建
    ///   3. 那天还挂着没做完的（created_date &lt; 那天 且 当时未完成）
    /// 第三条按"当天视角"判断完成时间，所以一条周三才做完的事，在周二的记录里仍然是未完成状态。
    /// </summary>
    public static List<MemoItem> LoadHistoryForDate(DateOnly date)
    {
        var result = new List<MemoItem>();
        var dayKey = DateKey(date);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, priority, is_done, sort_order, created_date, done_date, done_at, created_at
            FROM memo_item
            WHERE deleted_at IS NULL
              AND (created_date = @day
                   OR done_date = @day
                   OR (created_date < @day AND (done_date IS NULL OR done_date > @day)))
            ORDER BY is_done ASC,
                     CASE priority WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END ASC,
                     sort_order ASC,
                     id ASC;
            """;
        cmd.Parameters.AddWithValue("@day", dayKey);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var doneDate = reader.IsDBNull(6) ? null : reader.GetString(6);

            // 完成状态要用"那天当时"的状态，不能拿现在的 is_done，
            // 否则一条昨天创建、今天才做完的事，在昨天的记录里会被划掉。
            var doneAsOf = doneDate is not null && string.CompareOrdinal(doneDate, dayKey) <= 0;

            result.Add(new MemoItem
            {
                Id = reader.GetInt64(0),
                Content = reader.GetString(1),
                Priority = reader.GetInt32(2),
                IsDone = doneAsOf,
                SortOrder = reader.GetInt32(4),
                CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                DoneAt = reader.IsDBNull(7) ? null : reader.GetString(7),

                // 历史视角下的"顺延几天"没有意义，统一不显示
                CarriedDays = 0,
            });
        }

        // 排序按"那一天当时"的状态来：SQL 里的 is_done 是现在的状态，不能直接用。
        return result
            .OrderBy(i => i.IsDone)
            .ThenBy(i => 2 - i.Priority)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.Id)
            .ToList();
    }

    /// <summary>
    /// 载入"提前安排在将来某一天"的待办，用于往后翻到未来。
    /// 只认 created_date 正好是那天的（也就是明确安排在那天的事），
    /// 不把今天还没做完的一堆拖进来 —— 否则翻到明天看到的是今天的欠账，没法当计划用。
    /// </summary>
    public static List<MemoItem> LoadPlannedForDate(DateOnly date)
    {
        var result = new List<MemoItem>();
        var dayKey = DateKey(date);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, priority, is_done, sort_order, created_date, done_date, done_at, created_at
            FROM memo_item
            WHERE deleted_at IS NULL
              AND created_date = @day
            ORDER BY is_done ASC,
                     CASE priority WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END ASC,
                     sort_order ASC,
                     id ASC;
            """;
        cmd.Parameters.AddWithValue("@day", dayKey);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoItem
            {
                Id = reader.GetInt64(0),
                Content = reader.GetString(1),
                Priority = reader.GetInt32(2),
                IsDone = reader.GetInt32(3) != 0,
                SortOrder = reader.GetInt32(4),
                CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                DoneAt = reader.IsDBNull(7) ? null : reader.GetString(7),

                // 提前安排的事情还没"顺延"这回事
                CarriedDays = 0,
            });
        }

        return result;
    }

    /// <summary>所有"有记录"的日期（新建或完成发生过的那几天），用于历史导航时跳过空白日期。</summary>
    public static List<DateOnly> GetRecordedDates()
    {
        var dates = new List<DateOnly>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT d FROM (
                SELECT created_date AS d FROM memo_item WHERE deleted_at IS NULL
                UNION
                SELECT done_date AS d FROM memo_item WHERE deleted_at IS NULL AND done_date IS NOT NULL
            )
            WHERE d IS NOT NULL AND d <> ''
            ORDER BY d DESC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (DateOnly.TryParse(reader.GetString(0), out var parsed))
            {
                dates.Add(parsed);
            }
        }

        return dates;
    }

    /// <summary>
    /// 某个月份「完成的」条目：按 done_date 落在该月内统计，不看它当初安排在哪天。
    /// 这样 9 月安排、10 月才勾掉的事，会算在 10 月的成绩里 —— 月度总结要回答的是
    /// 「我这个月干掉了什么」，不是「我本月计划里的事完成了多少」。
    /// 最近完成的排前面。
    /// </summary>
    public static List<MemoItem> LoadDoneInMonth(DateOnly monthAnchor)
    {
        var start = new DateOnly(monthAnchor.Year, monthAnchor.Month, 1);
        var next = start.AddMonths(1);

        var result = new List<MemoItem>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, priority, is_done, sort_order, created_date, done_date, done_at, created_at
            FROM memo_item
            WHERE deleted_at IS NULL
              AND done_date IS NOT NULL
              AND done_date >= @start
              AND done_date < @next
            ORDER BY done_date DESC,
                     CASE priority WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END ASC,
                     sort_order ASC,
                     id ASC;
            """;
        // 日期存的是 yyyy-MM-dd 定长字符串，直接按字符串比较就等价于按日期比较
        cmd.Parameters.AddWithValue("@start", DateKey(start));
        cmd.Parameters.AddWithValue("@next", DateKey(next));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoItem
            {
                Id = reader.GetInt64(0),
                Content = reader.GetString(1),
                Priority = reader.GetInt32(2),
                IsDone = true,
                SortOrder = reader.GetInt32(4),
                CreatedDate = reader.IsDBNull(5) ? null : reader.GetString(5),
                DoneDate = reader.IsDBNull(6) ? null : reader.GetString(6),
                DoneAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            });
        }

        return result;
    }

    /// <summary>
    /// 所有还没做完的条目，不分月份 —— 月度总结里的「仍在处理」用这个。
    /// 早安排、一直拖着的排在前面（那才是真欠账），同一天里按优先级。
    /// </summary>
    public static List<MemoItem> LoadAllOpen()
    {
        var result = new List<MemoItem>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, priority, is_done, sort_order, created_date, done_date, done_at, created_at
            FROM memo_item
            WHERE deleted_at IS NULL AND is_done = 0
            ORDER BY created_date ASC,
                     CASE priority WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END ASC,
                     sort_order ASC,
                     id ASC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoItem
            {
                Id = reader.GetInt64(0),
                Content = reader.GetString(1),
                Priority = reader.GetInt32(2),
                IsDone = false,
                SortOrder = reader.GetInt32(4),
                CreatedDate = reader.IsDBNull(5) ? null : reader.GetString(5),
                DoneDate = reader.IsDBNull(6) ? null : reader.GetString(6),
                DoneAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            });
        }

        return result;
    }

    /// <summary>某个月新增了多少条（按 created_date 落在该月统计）。</summary>
    public static int CountCreatedInMonth(DateOnly monthAnchor)
    {
        var start = new DateOnly(monthAnchor.Year, monthAnchor.Month, 1);
        var next = start.AddMonths(1);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM memo_item
            WHERE deleted_at IS NULL
              AND created_date >= @start
              AND created_date < @next;
            """;
        cmd.Parameters.AddWithValue("@start", DateKey(start));
        cmd.Parameters.AddWithValue("@next", DateKey(next));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public static long Insert(MemoItem item, DateOnly today)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memo_item (content, priority, is_done, sort_order, created_date, done_date, done_at, created_at)
            VALUES (@content, @priority, 0, @sort, @created, NULL, NULL, @createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@content", item.Content);
        cmd.Parameters.AddWithValue("@priority", item.Priority);
        cmd.Parameters.AddWithValue("@sort", item.SortOrder);
        cmd.Parameters.AddWithValue("@created", DateKey(today));
        cmd.Parameters.AddWithValue("@createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public static void UpdateText(MemoItem item)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memo_item SET content = @content WHERE id = @id;";
        cmd.Parameters.AddWithValue("@content", item.Content);
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.ExecuteNonQuery();
    }

    public static void UpdatePriority(MemoItem item)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memo_item SET priority = @priority WHERE id = @id;";
        cmd.Parameters.AddWithValue("@priority", item.Priority);
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>勾选完成时按当天记 done_date，取消勾选时清空，让它重新参与"未完成顺延"。</summary>
    public static void UpdateDone(MemoItem item, DateOnly today)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memo_item
            SET is_done = @done,
                done_date = @doneDate,
                done_at = @doneAt
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@done", item.IsDone ? 1 : 0);
        cmd.Parameters.AddWithValue("@doneDate", item.IsDone ? DateKey(today) : DBNull.Value);
        cmd.Parameters.AddWithValue("@doneAt", item.IsDone
            ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            : DBNull.Value);
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.ExecuteNonQuery();
    }

    public static void UpdateSortOrder(IEnumerable<MemoItem> items)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE memo_item SET sort_order = @sort WHERE id = @id;";
        var pSort = cmd.Parameters.Add("@sort", SqliteType.Integer);
        var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
        var index = 0;
        foreach (var item in items)
        {
            pSort.Value = index++;
            pId.Value = item.Id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 软删除：只打 deleted_at 标记，行还在库里。
    /// 起因：误操作删掉过整批待办且无法恢复，宁可留垃圾也别再出现"删了就没了"。
    ///
    /// 会写审计日志（含调用栈）：删除是"悄悄少了几条"这类问题的唯一可疑入口，
    /// 没有日志就只能靠猜。日志只写正式库，自检不写。
    /// </summary>
    public static void Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memo_item SET deleted_at = @at WHERE id = @id;";
        cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        AuditDelete(id, "MemoStore.Delete");
    }

    /// <summary>
    /// 把删除来源追加到 %APPDATA%\Memo\delete-audit.log。
    /// 记录进程 id、启动时间、线程、调用栈，用来回答"到底是谁删的"。
    /// </summary>
    private static void AuditDelete(long id, string source)
    {
        if (_databasePathOverride is not null)
        {
            return; // 自检库不写审计
        }

        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} delete id={id} source={source}");
            sb.AppendLine($"  pid={proc.Id} started={proc.StartTime:HH:mm:ss} exe={Environment.ProcessPath}");
            sb.AppendLine($"  thread={Environment.CurrentManagedThreadId} pool={Thread.CurrentThread.IsThreadPoolThread}");
            sb.AppendLine($"  stack: {Environment.StackTrace}");
            File.AppendAllText(AppPaths.AuditLogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 审计失败不能影响删除本身
        }
    }

    /// <summary>撤销删除：把最近一条被删的记录还原回来，返回它的 id（没有则 0）。</summary>
    public static long UndoLastDelete()
    {
        using var conn = Open();
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT id FROM memo_item WHERE deleted_at IS NOT NULL ORDER BY deleted_at DESC, id DESC LIMIT 1;";
        var id = find.ExecuteScalar();
        if (id is null or DBNull)
        {
            return 0L;
        }

        var target = Convert.ToInt64(id);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memo_item SET deleted_at = NULL WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", target);
        cmd.ExecuteNonQuery();
        return target;
    }

    /// <summary>最近被删除的记录数，用于底部提示「可撤销」。</summary>
    public static int CountRecentlyDeleted()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memo_item WHERE deleted_at IS NOT NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>把更早创建的未完成项挪到今天，方便明天看到"今天该做的事"（当前版本按需保留，未接 UI）。</summary>
    public static int CarryOver(DateOnly today)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memo_item
            SET created_date = @today
            WHERE is_done = 0 AND created_date < @today;
            """;
        cmd.Parameters.AddWithValue("@today", DateKey(today));
        return cmd.ExecuteNonQuery();
    }
}
