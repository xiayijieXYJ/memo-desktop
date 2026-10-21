using Memo.Models;
using Memo.Services;
using Memo.ViewModels;

namespace Memo;

/// <summary>
/// --selftest 用的端到端自检：直接走 存储层 + ViewModel，验证排序、跨天顺延、优先级、
/// 完成状态、删除等行为，把结果写到临时文件里。发布版不会触发，只在开发验证时用。
/// </summary>
public static class SelfTest
{
    public static void Run(string outputPath)
    {
        var lines = new List<string>();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }
            lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]")}");
        }

        try
        {
            // 用独立数据库，避免污染用户真实数据
            AppPathsOverride.UseSandboxDb();
            MemoStore.Initialize();

            var today = DateOnly.FromDateTime(DateTime.Now);
            var yesterday = today.AddDays(-1);

            // 标题格式固定是「M月d日 · 周X」，测试里照着拼一份用来比对
            string DateHeader(DateOnly d)
            {
                var weekday = d.DayOfWeek switch
                {
                    DayOfWeek.Monday => "周一",
                    DayOfWeek.Tuesday => "周二",
                    DayOfWeek.Wednesday => "周三",
                    DayOfWeek.Thursday => "周四",
                    DayOfWeek.Friday => "周五",
                    DayOfWeek.Saturday => "周六",
                    _ => "周日",
                };
                return $"{d.Month}月{d.Day}日 · {weekday}";
            }

            var vm = new MemoViewModel();
            Check("初始为空", vm.Items.Count == 0, $"count={vm.Items.Count}");

            // 1) 新增：三条不同优先级
            void Add(string text, int priority)
            {
                vm.NewContent = text;
                vm.SetNewPriority(priority);
                vm.AddCommand.Execute(null);
            }

            Add("低优先级事项", 0);
            Add("高优先级事项", 2);
            Add("中优先级事项", 1);

            Check("新增三条", vm.Items.Count == 3, $"count={vm.Items.Count}");
            Check("新增后输入框清空", vm.NewContent.Length == 0, $"text='{vm.NewContent}'");
            Check("按优先级排序", vm.Items[0].Priority == 2 && vm.Items[1].Priority == 1 && vm.Items[2].Priority == 0,
                string.Join(",", vm.Items.Select(i => i.Priority)));
            Check("Id 已回填", vm.Items.All(i => i.Id > 0), string.Join(",", vm.Items.Select(i => i.Id)));
            Check("标题副文本含剩余数", vm.HeaderSubText.Contains("还剩 3 项"), vm.HeaderSubText);
            Check("标题等于日期·周几", vm.HeaderText == DateHeader(today), vm.HeaderText);
            Check("标题用点号分隔日期和周几", vm.HeaderText.Contains("日 · 周"), vm.HeaderText);
            Check("副标题不再重复周几", !vm.HeaderSubText.Contains("周"), vm.HeaderSubText);

            // 2) 勾选完成 -> 移到末尾
            var high = vm.Items.First(i => i.Content == "高优先级事项");
            vm.ToggleDoneCommand.Execute(high);
            Check("完成后 is_done 生效", high.IsDone);
            Check("完成后排到末尾", vm.Items[^1].Content == "高优先级事项",
                string.Join(",", vm.Items.Select(i => i.Content)));
            Check("状态统计正确", vm.StatusText == "完成 1 / 3", vm.StatusText);

            // 3) 改优先级并持久化
            var low = vm.Items.First(i => i.Content == "低优先级事项");
            vm.CyclePriorityCommand.Execute(low);
            Check("优先级轮转 低->高", low.Priority == 2, $"{low.Priority}");
            var reloaded = new MemoViewModel();
            Check("优先级已落库",
                reloaded.Items.First(i => i.Content == "低优先级事项").Priority == 2,
                reloaded.Items.First(i => i.Content == "低优先级事项").Priority.ToString());

            // 4) 编辑文本
            var mid = vm.Items.First(i => i.Content == "中优先级事项");
            mid.Content = "改过的中优先级事项";
            vm.CommitEditCommand.Execute(mid);
            var reloaded2 = new MemoViewModel();
            Check("文本已落库", reloaded2.Items.Any(i => i.Content == "改过的中优先级事项"));

            // 5) 删除
            vm.DeleteCommand.Execute(mid);
            Check("删除后列表减少", vm.Items.Count == 2, $"count={vm.Items.Count}");
            var reloaded3 = new MemoViewModel();
            Check("删除已落库", reloaded3.Items.Count == 2, $"count={reloaded3.Items.Count}");

            // 5b) 删除是软删除，可以撤销回来
            Check("删除后可撤销", reloaded3.CanUndoDelete);
            reloaded3.UndoDeleteCommand.Execute(null);
            Check("撤销后条目回来", reloaded3.Items.Any(i => i.Content == "改过的中优先级事项"),
                string.Join(",", reloaded3.Items.Select(i => i.Content)));
            Check("撤销后无待撤销项", !reloaded3.CanUndoDelete);
            // 删掉它，后面的用例按 2 条算
            reloaded3.DeleteCommand.Execute(reloaded3.Items.First(i => i.Content == "改过的中优先级事项"));

            // 6) 跨天顺延：昨天创建、未完成 -> 今天仍可见且带顺延天数
            var carried = new MemoItem { Content = "昨天没做完的事", Priority = 1, SortOrder = 99 };
            carried.Id = MemoStore.Insert(carried, yesterday);
            var reloaded4 = new MemoViewModel();
            var carriedItem = reloaded4.Items.FirstOrDefault(i => i.Content == "昨天没做完的事");
            Check("跨天未完成仍可见", carriedItem is not null);
            Check("顺延天数 = 1", carriedItem?.CarriedDays == 1, $"carried={carriedItem?.CarriedDays}");
            Check("顺延文案", carriedItem?.CarriedText == "顺延1天", carriedItem?.CarriedText ?? "");

            // 7) 昨天已完成的不该出现在今天
            var doneYesterday = new MemoItem { Content = "昨天已完成的", Priority = 0, SortOrder = 100 };
            doneYesterday.Id = MemoStore.Insert(doneYesterday, yesterday);
            doneYesterday.IsDone = true;
            MemoStore.UpdateDone(doneYesterday, yesterday);
            var reloaded5 = new MemoViewModel();
            Check("昨天已完成不出现", reloaded5.Items.All(i => i.Content != "昨天已完成的"),
                string.Join(",", reloaded5.Items.Select(i => i.Content)));

            // 8) 清除已完成
            var before = reloaded5.Items.Count(i => !i.IsDone);
            reloaded5.ClearDoneCommand.Execute(null);
            Check("清除已完成只留未完成", reloaded5.Items.Count == before,
                $"before={before} after={reloaded5.Items.Count}");

            // 9) 空内容不新增
            reloaded5.NewContent = "   ";
            var count = reloaded5.Items.Count;
            reloaded5.AddCommand.Execute(null);
            Check("空白不新增", reloaded5.Items.Count == count);

            // 10) 按日期回看历史
            // 此时库里：今天的 2 条（低优先级事项/昨天没做完的事顺延而来），昨天创建 2 条，
            // 其中"昨天已完成的"已完成、"昨天没做完的事"未完成。
            var vh = new MemoViewModel();
            Check("默认不在历史模式", !vh.IsHistoryView);
            Check("今天可编辑", vh.IsEditable);

            vh.ViewDate(yesterday);
            Check("进入历史模式", vh.IsHistoryView);
            Check("历史模式只读", !vh.IsEditable);
            Check("历史徽标文案", vh.HistoryBadgeText == $"回看 {yesterday.Month}月{yesterday.Day}日",
                vh.HistoryBadgeText ?? "");
            Check("历史标题是那天", vh.HeaderText == DateHeader(yesterday), vh.HeaderText);
            Check("历史里有昨天完成的", vh.Items.Any(i => i.Content == "昨天已完成的"),
                string.Join(",", vh.Items.Select(i => i.Content)));
            Check("历史里昨天完成的被划掉",
                vh.Items.First(i => i.Content == "昨天已完成的").IsDone);
            Check("历史里有昨天未完成的",
                vh.Items.Any(i => i.Content == "昨天没做完的事"),
                string.Join(",", vh.Items.Select(i => i.Content)));
            Check("历史里昨天未完成没被划掉",
                !vh.Items.First(i => i.Content == "昨天没做完的事").IsDone);
            Check("历史模式不显示顺延", vh.Items.All(i => i.CarriedDays == 0),
                string.Join(",", vh.Items.Select(i => i.CarriedDays)));
            Check("历史里没有今天新建的",
                vh.Items.All(i => i.Content != "低优先级事项"),
                string.Join(",", vh.Items.Select(i => i.Content)));

            // 回到今天
            vh.ViewDate(null);
            Check("回到今天退出历史", !vh.IsHistoryView && vh.IsEditable);

            // 11) 相邻日期导航
            Check("能往前翻到有记录的日子", vh.GoAdjacentDate(-1));
            Check("翻页后进入历史", vh.IsHistoryView);
            Check("翻页落在昨天", vh.HeaderText == DateHeader(yesterday), vh.HeaderText);
            Check("能往后翻回今天", vh.GoAdjacentDate(+1));
            Check("翻到今天自动退出历史", !vh.IsHistoryView);

            // 12) 有记录的日期列表
            var dates = MemoStore.GetRecordedDates();
            Check("记录日期含今天", dates.Contains(today), string.Join(",", dates));
            Check("记录日期含昨天", dates.Contains(yesterday), string.Join(",", dates));
            Check("记录日期倒序", dates.SequenceEqual(dates.OrderByDescending(d => d)),
                string.Join(",", dates));

            // 13) 箭头按自然日翻，永远点得动：
            //     往前翻看历史、往后翻到将来提前安排，都不许"点了没反应"
            vh.ViewDate(null);
            Check("今天左箭头可点", vh.CanGoPrevious);
            Check("今天右箭头也可点", vh.CanGoNext);
            Check("今天往前翻一天成功", vh.GoAdjacentDate(-1));
            Check("往前翻后进入历史", vh.IsHistoryView);
            Check("往前翻落在昨天", vh.HeaderText == DateHeader(yesterday), vh.HeaderText);
            Check("回看时两个箭头都可点", vh.CanGoPrevious && vh.CanGoNext);
            Check("能往后翻回今天", vh.GoAdjacentDate(+1));
            Check("翻到今天自动退出历史", !vh.IsHistoryView);

            // 连翻若干天空白日子，箭头也不能失灵
            var vblank = new MemoViewModel();
            var blankOk = true;
            for (var i = 1; i <= 5; i++)
            {
                blankOk &= vblank.GoAdjacentDate(-1);
            }

            Check("连续翻 5 个空白日子都成功", blankOk);
            Check("翻到空白日子仍在历史模式", vblank.IsHistoryView);
            Check("空白日子标题正确",
                vblank.HeaderText == DateHeader(today.AddDays(-5)),
                vblank.HeaderText);

            // 14) 日历只列有记录的日子：GetRecordedDates 不该冒出空日期
            Check("记录日期无空值", dates.All(d => d != default(DateOnly)),
                string.Join(",", dates));
            Check("历史模式记住正在看的日期", vh.IsHistoryView == false && vh.ViewingDate is null);

            // 15) 空白日期不能说"已全部完成"
            var vempty = new MemoViewModel();
            vempty.ViewDate(yesterday.AddDays(-30));   // 那天空白
            Check("空白日期无条目", vempty.Items.Count == 0, vempty.Items.Count.ToString());
            Check("空白日期副标题不谎报完成", !vempty.HeaderSubText.Contains("已全部完成"),
                vempty.HeaderSubText);
            Check("空白日期空态文案", vempty.EmptyText == "这天没有记录", vempty.EmptyText);

            // 16) 提前安排到将来：往后翻能翻、那天的视图可编辑、新条目记在那天
            var tomorrow = today.AddDays(1);
            var vf = new MemoViewModel();
            Check("今天能往后翻到将来", vf.GoAdjacentDate(+1));
            Check("翻到明天进入非今天视图", vf.IsHistoryView);
            Check("明天算将来视图", vf.IsFutureView);
            Check("将来视图不是只读", !vf.IsReadOnlyView);
            Check("将来视图可编辑", vf.IsEditable);
            Check("将来徽标文案", vf.HistoryBadgeText == $"安排 {tomorrow.Month}月{tomorrow.Day}日",
                vf.HistoryBadgeText ?? "");
            Check("将来标题是明天", vf.HeaderText == DateHeader(tomorrow), vf.HeaderText);
            Check("将来视图新建记到那天", vf.EditingDate == tomorrow, vf.EditingDate.ToString());

            vf.NewContent = "明天要交的报表";
            vf.AddCommand.Execute(null);
            Check("将来日期能新增", vf.Items.Any(i => i.Content == "明天要交的报表"),
                string.Join(",", vf.Items.Select(i => i.Content)));

            // 明天安排的事今天不该提前冒出来
            var vt = new MemoViewModel();
            Check("明天的事今天不出现", vt.Items.All(i => i.Content != "明天要交的报表"),
                string.Join(",", vt.Items.Select(i => i.Content)));

            // 明天安排的事，翻到明天能看见
            var vf2 = new MemoViewModel();
            vf2.ViewDate(tomorrow);
            Check("明天的事翻到明天能看见", vf2.Items.Any(i => i.Content == "明天要交的报表"),
                string.Join(",", vf2.Items.Select(i => i.Content)));

            // 翻过明天之后仍能继续往后翻，且能翻回今天
            Check("能继续往后翻", vf2.GoAdjacentDate(+1));
            Check("往后翻到下一天不是今天", vf2.ViewingDate == tomorrow.AddDays(1));
            vf2.ViewDate(null);
            Check("从将来回到今天", !vf2.IsHistoryView && vf2.IsEditable);

            var vfar = new MemoViewModel();
            vfar.ViewDate(today.AddDays(9));
            Check("将来视图空态文案", vfar.EmptyText.Contains("还没给"), vfar.EmptyText);

            // ---------- 月度总结：完成的按月算，未完成的不分月 ----------
            var monthAnchor = new DateOnly(today.Year, today.Month, 1);
            var doneBefore = MemoStore.LoadDoneInMonth(monthAnchor).Count;
            var createdBefore = MemoStore.CountCreatedInMonth(monthAnchor);

            var sumDone = new MemoItem { Content = "月度总结·本条算作完成", Priority = 2 };
            sumDone.Id = MemoStore.Insert(sumDone, today);
            var sumOpen = new MemoItem { Content = "月度总结·本条还没做完", Priority = 1 };
            sumOpen.Id = MemoStore.Insert(sumOpen, today);
            // 一条安排在 45 天前的未完成项：用来验证"仍在处理"确实不分月份
            var sumOverdue = new MemoItem { Content = "月度总结·往月欠账", Priority = 0 };
            sumOverdue.Id = MemoStore.Insert(sumOverdue, today.AddDays(-45));
            // UpdateDone 是照 item.IsDone 落库的，得先把标志立起来
            sumDone.IsDone = true;
            MemoStore.UpdateDone(sumDone, today);

            var doneInMonth = MemoStore.LoadDoneInMonth(monthAnchor);
            Check("本月完成数 +1", doneInMonth.Count == doneBefore + 1, $"{doneBefore} -> {doneInMonth.Count}");
            Check("完成清单里有刚勾掉的那条",
                doneInMonth.Any(i => i.Content == "月度总结·本条算作完成"));
            Check("完成清单里没有未做完的",
                doneInMonth.All(i => i.Content != "月度总结·本条还没做完"));
            Check("完成清单按完成日倒序",
                doneInMonth.Select(i => i.DoneDate).SequenceEqual(
                    doneInMonth.Select(i => i.DoneDate).OrderByDescending(d => d)),
                string.Join(",", doneInMonth.Select(i => i.DoneDate)));

            Check("本月新增数 +2", MemoStore.CountCreatedInMonth(monthAnchor) == createdBefore + 2,
                $"{createdBefore} -> {MemoStore.CountCreatedInMonth(monthAnchor)}");

            var openAll = MemoStore.LoadAllOpen();
            Check("未完成清单里有刚建的那条",
                openAll.Any(i => i.Content == "月度总结·本条还没做完"));
            Check("未完成清单里没有已完成/已删除的",
                openAll.All(i => !i.IsDone && i.Content != "月度总结·本条算作完成"));
            Check("未完成清单不分月份（含往月欠账）",
                openAll.Any(i => i.Content == "月度总结·往月欠账"),
                $"count={openAll.Count}");

            var farMonth = monthAnchor.AddMonths(6);
            Check("没数据的月份完成数为 0", MemoStore.LoadDoneInMonth(farMonth).Count == 0);
            Check("没数据的月份新增数为 0", MemoStore.CountCreatedInMonth(farMonth) == 0);
            Check("上个月可单独查询", MemoStore.LoadDoneInMonth(monthAnchor.AddMonths(-1)) is not null);

            MemoStore.Delete(sumDone.Id);
            MemoStore.Delete(sumOpen.Id);
            MemoStore.Delete(sumOverdue.Id);
            Check("清理后本月完成数复原", MemoStore.LoadDoneInMonth(monthAnchor).Count == doneBefore,
                $"{MemoStore.LoadDoneInMonth(monthAnchor).Count} vs {doneBefore}");

            // ---------- 今天才勾掉的旧条目，当天仍留在卡片上 ----------
            // 起因：20 日记下的一件事今天才勾完，卡片上当场就没了，看着像丢数据
            var carry = new MemoItem { Content = "顺延勾掉·昨天欠的", Priority = 1 };
            carry.Id = MemoStore.Insert(carry, today.AddDays(-1));
            Check("昨天欠的今天在卡上（未完成）",
                MemoStore.LoadForDate(today).Any(i => i.Content == "顺延勾掉·昨天欠的"));

            carry.IsDone = true;
            MemoStore.UpdateDone(carry, today);
            var afterDone = MemoStore.LoadForDate(today);
            Check("今天勾掉的旧条目当天还看得见",
                afterDone.Any(i => i.Content == "顺延勾掉·昨天欠的" && i.IsDone),
                string.Join(",", afterDone.Select(i => $"{i.Content}:{i.IsDone}")));
            Check("勾掉的行不再挂「顺延N天」",
                afterDone.Where(i => i.Content == "顺延勾掉·昨天欠的").All(i => i.CarriedText == string.Empty));

            var vCarry = new MemoViewModel { ShowDone = true };
            Check("卡片视图里也能看到它",
                vCarry.Items.Any(i => i.Content == "顺延勾掉·昨天欠的" && i.IsDone),
                string.Join(",", vCarry.Items.Select(i => i.Content)));

            // 昨天勾掉的就不该再赖在今天的卡上
            var yesterdayDone = new MemoItem { Content = "顺延勾掉·昨天勾掉的", Priority = 1 };
            yesterdayDone.Id = MemoStore.Insert(yesterdayDone, today.AddDays(-2));
            yesterdayDone.IsDone = true;
            MemoStore.UpdateDone(yesterdayDone, today.AddDays(-1));
            Check("昨天勾掉的不再占今天的位置",
                MemoStore.LoadForDate(today).All(i => i.Content != "顺延勾掉·昨天勾掉的"));

            MemoStore.Delete(carry.Id);
            MemoStore.Delete(yesterdayDone.Id);
        }
        catch (Exception ex)
        {
            failures++;
            lines.Add($"FAIL  自检异常: {ex}");
        }

        lines.Insert(0, failures == 0 ? "=== 自检全部通过 ===" : $"=== 自检失败 {failures} 项 ===");
        File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines), Encoding.UTF8);
    }
}

/// <summary>自检时把数据库指向临时目录，避免动真实数据。</summary>
public static class AppPathsOverride
{
    public static void UseSandboxDb()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "memo_selftest");
        if (Directory.Exists(sandbox))
        {
            try
            {
                Directory.Delete(sandbox, true);
            }
            catch
            {
                // 文件被占用时忽略，后面按存在即用
            }
        }
        Directory.CreateDirectory(sandbox);
        MemoStore.UseDatabasePath(Path.Combine(sandbox, "memo_test.db"));
    }
}
