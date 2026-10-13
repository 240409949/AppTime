 
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static AppTime.Recorder;

namespace AppTime
{
    class Controller
    {
        public WebServer Server =>Program.server;
        public Recorder Recorder=>Program.recorder;


        #region basic
    
        int getColor(string str)
        {
            // GetHashCode 直接当 ARGB 会有接近白色的颜色（或 alpha 过低显得发白），白色时间轴背景上不可见
            // 统一处理：固定不透明 + 三通道都偏亮的颜色压暗到可见范围
            var v = unchecked((uint)str.GetHashCode());
            var r = (byte)(v >> 16);
            var g = (byte)(v >> 8);
            var b = (byte)v;
            var min = Math.Min(r, Math.Min(g, b));
            if (min > 160)
            {
                r = (byte)(40 + r % 160);
                g = (byte)(40 + g % 160);
                b = (byte)(40 + b % 160);
            }
            return unchecked((int)(0xFF000000u | (uint)r << 16 | (uint)g << 8 | b));
        }

        static int Argb(byte a, byte r, byte g, byte b)
        {
            return unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b));
        }

        // 高区分度调色板：按使用时长排名分配，最常用的应用获得最醒目的颜色
        static readonly int[] Palette = new[]
        {
            Argb(0xFF, 0x4E, 0x79, 0xA7), Argb(0xFF, 0xF2, 0x8E, 0x2B), Argb(0xFF, 0xE1, 0x57, 0x59),
            Argb(0xFF, 0x59, 0xA1, 0x4F), Argb(0xFF, 0x76, 0xB7, 0xB2), Argb(0xFF, 0xB0, 0x7A, 0xA1),
            Argb(0xFF, 0xED, 0xC9, 0x48), Argb(0xFF, 0x9C, 0x75, 0x5F), Argb(0xFF, 0xD3, 0x72, 0x95),
            Argb(0xFF, 0x49, 0x98, 0x94), Argb(0xFF, 0xB6, 0x99, 0x2D), Argb(0xFF, 0x5D, 0x69, 0xB1),
            Argb(0xFF, 0x2E, 0x6E, 0x8E), Argb(0xFF, 0xCC, 0x4B, 0x44), Argb(0xFF, 0x79, 0x70, 0xB3),
            Argb(0xFF, 0x34, 0x8C, 0x31)
        };

        static Dictionary<long, int> appColors = new Dictionary<long, int>();
        static DateTime appColorsTime = DateTime.MinValue;
        static readonly object appColorsLock = new object();

        /// <summary>
        /// 按应用总使用时长排名刷新颜色映射（60 秒内不重复计算）。
        /// </summary>
        void RefreshAppColors()
        {
            lock (appColorsLock)
            {
                if ((DateTime.Now - appColorsTime).TotalSeconds < 300)
                {
                    return;
                }
                var next = new Dictionary<long, int>();
                var rows = db.ExecuteDynamic(@"
select app.id id
from app
left join win on win.appid = app.id
left join period p on p.winid = win.id
group by app.id
order by ifnull(sum(julianday(p.timeEnd) - julianday(p.timeStart)), 0) desc
").ToList();
                for (var i = 0; i < rows.Count; i++)
                {
                    next[(long)rows[i].id] = Palette[i % Palette.Length];
                }
                appColors = next;
                appColorsTime = DateTime.Now;
            }
        }

        int getAppColor(long appId)
        {
            if (!appColors.TryGetValue(appId, out var color))
            {
                RefreshAppColors();
                if (!appColors.TryGetValue(appId, out color))
                {
                    color = Palette[0];
                }
            }
            return color;
        }

        static Dictionary<long, int> tagColors = new Dictionary<long, int>();
        int getTagColor(long tagId)
        {
            if (!tagColors.TryGetValue(tagId, out var color))
            {
                if (tagId == -1)
                {
                    color = tagColors[tagId] = BitConverter.ToInt32(new byte[] { 0xFF, 0x99, 0x99, 0x99 }, 0);
                }
                else
                {
                    var text = db.ExecuteValue<string>($"select text from tag where id={tagId}");
                    color = tagColors[tagId] = getColor(text);
                }
            }
            return color;
        }


        public unsafe byte[] getPeriodBar(DateTime timefrom, DateTime timeto, string view, int width)
        {
            if (width <= 0 || width > 8000)
            {
                return null;
            }


            var totalsecs = (timeto - timefrom).TotalSeconds;
            IEnumerable<dynamic> data;
            if (view == "app")
            {
                data = db.ExecuteDynamic(@"
select  
	app.id appId,
	p.timeStart,
	p.timeEnd
from app
join win on win.appid = app.id
join period p on p.winid = win.id   
where 
    timeStart between @v0 and @v1 
    or timeEnd between @v0 and @v1
    or @v0 between timeStart and timeEnd
order by p.timeStart
",
                timefrom, timeto
                );
            }
            else
            {
                data = db.ExecuteDynamic(@"
select  
	ifnull(tag.id,-1) tagId,
	p.timeStart,
	p.timeEnd
from app
join win on win.appid = app.id
join period p on p.winid = win.id  
left join tag on tag.id = win.tagId or (win.tagId = 0 and tag.id = app.tagId) 
where 
    timeStart between @v0 and @v1 
    or timeEnd between @v0 and @v1
    or @v0 between timeStart and timeEnd
order by p.timeStart
",
                timefrom, timeto
                );
            }

            //绘制PeriodBar，直接写内存比gdi+快
            var imgdata = new int[width]; 
            foreach (var period in data)
            {
                var from = Math.Max(0, (int)Math.Round((period.timeStart - timefrom).TotalSeconds / totalsecs * width));
                var to = Math.Min(width - 1, (int)Math.Round((period.timeEnd - timefrom).TotalSeconds / totalsecs * width));

                for (var x = from; x <= Math.Min(width - 1, to); x++)
                {
                    imgdata[x] = view == "app" ? (int)getAppColor(period.appId) : (int)getTagColor(period.tagId);
                }
            }

            // 叠加离开时段：锁屏=深灰，无输入=浅灰（离开时段本无记录，不影响正常着色）
            var aways = db.ExecuteDynamic(@"
select timeStart, timeEnd, reason
from away
where 
    timeStart between @v0 and @v1 
    or timeEnd between @v0 and @v1
    or @v0 between timeStart and timeEnd
",
            timefrom, timeto
            );
            foreach (var a in aways)
            {
                var from = Math.Max(0, (int)Math.Round((a.timeStart - timefrom).TotalSeconds / totalsecs * width));
                object endObj = a.timeEnd;
                var end = endObj == null || endObj is DBNull ? timeto : (DateTime)endObj;
                var to = Math.Min(width - 1, (int)Math.Round((end - timefrom).TotalSeconds / totalsecs * width));
                var color = (int)a.reason == 1 ? Argb(0xFF, 0x78, 0x78, 0x78) : Argb(0xFF, 0xC8, 0xC8, 0xC8);
                for (var x = from; x <= to; x++)
                {
                    imgdata[x] = color;
                }
            }
             
            fixed (int* p = &imgdata[0])
            {
                var ptr = new IntPtr(p);
                using var bmp = new Bitmap(width, 1, width * 4, PixelFormat.Format32bppArgb, ptr);
                using var mem = new MemoryStream();
                bmp.Save(mem, ImageFormat.Png);
                return mem.ToArray();
            }

        }

        public object getTree(DateTime timefrom, DateTime timeto, string view, long parentKey)
        {
            var result = new List<object>();
            var totalSeconds = (timeto - timefrom).TotalSeconds;
            IEnumerable<dynamic> data;

            if (view == "app")
            {
                if (parentKey == 0)
                {
                    data = db.ExecuteDynamic(@"
select 
	app.id appId,
	app.text appText,
    tag.text tagText,
	sum(julianday(case when timeEnd > @v1 then @v1 else timeEnd end) - 
        julianday(case when timeStart < @v0 then @v0 else timeStart end)) days
from app
join win on win.appid = app.id
join period p on p.winid = win.id
left join tag on tag.id = app.tagId
where 
    timeStart between @v0 and @v1 
    or timeEnd between @v0 and @v1
    or @v0 between timeStart and timeEnd
group by app.id 
order by days desc
",
        timefrom, timeto
    );

                    foreach (var i in data)
                    {
                        var time = new TimeSpan((long)(i.days * TimeSpan.TicksPerDay));
                        result.Add(
                            new
                            {
                                i.appId,
                                i.tagText,
                                text = i.appText,
                                time = time.ToString(@"hh\:mm\:ss"),
                                percent = Math.Round(time.TotalSeconds * 100 / totalSeconds, 2) + "%",
                                color = getAppColor(i.appId),
                                children = new object[0]
                            }
                        );
                    }

                    return result;
                }

                data = db.ExecuteDynamic(@"
select 
	win.id winId,
	win.text winText,
    ifnull(tag.text, '(其他)') tagText,
	sum(julianday(case when timeEnd > @v1 then @v1 else timeEnd end) - 
        julianday(case when timeStart < @v0 then @v0 else timeStart end)) days
from win
join period p on p.winid = win.id
join app on app.id = win.appid
left join tag on tag.id = win.tagId or (win.tagId = 0 and tag.id = app.tagId)
where 
    win.appId = @v2
    and (
        timeStart between @v0 and @v1 
        or timeEnd between @v0 and @v1
        or @v0 between timeStart and timeEnd
    )
group by win.id 
order by days desc
",
                timefrom, timeto, parentKey 
);

                // 按标签把窗口收进分组行（summary），前端点击可展开查看窗口明细
                var groups = new List<Group>();
                foreach (var i in data)
                {
                    var time = new TimeSpan((long)(i.days * TimeSpan.TicksPerDay));
                    var g = groups.Find(x => x.text == i.tagText);
                    if (g == null)
                    {
                        g = new Group { text = i.tagText, wins = new List<object>() };
                        groups.Add(g);
                    }
                    g.time += time;
                    g.wins.Add(
                        new
                        {
                            i.winId,
                            text = string.IsNullOrWhiteSpace(i.winText) ? "(无标题)" : i.winText,
                            time = time.ToString(@"hh\:mm\:ss"),
                            percent = Math.Round(time.TotalSeconds * 100 / totalSeconds, 2) + "%"
                        }
                    );
                }

                foreach (var g in groups.OrderByDescending(x => x.time))
                {
                    result.Add(
                        new
                        {
                            text = g.text,
                            summary = true,
                            expanded = false,
                            time = g.time.ToString(@"hh\:mm\:ss"),
                            percent = Math.Round(g.time.TotalSeconds * 100 / totalSeconds, 2) + "%",
                            children = g.wins
                        }
                    );
                }

                return result;
            }


            if (parentKey == 0)
            {

                data = db.ExecuteDynamic(@"
select 
    ifnull(tag.id,-1) tagId,
    ifnull(tag.text, '(无标签)') tagText,
	sum(julianday(case when timeEnd > @v1 then @v1 else timeEnd end) - 
        julianday(case when timeStart < @v0 then @v0 else timeStart end)) days
from win
join app on app.id = win.appid
join period p on p.winid = win.id
left join tag on tag.id = win.tagId or (win.tagId = 0 and tag.id = app.tagId)
where 
    timeStart between @v0 and @v1 
    or timeEnd between @v0 and @v1
    or @v0 between timeStart and timeEnd
group by tag.id
order by days desc 
",
                    timefrom, timeto
                );

                foreach (var i in data)
                {
                    var time = new TimeSpan((long)(i.days * TimeSpan.TicksPerDay));
                    result.Add(
                        new
                        {
                            i.tagId,
                            i.tagText,
                            time = time.ToString(@"hh\:mm\:ss"),
                            percent = Math.Round(time.TotalSeconds * 100 / totalSeconds, 2) + "%",
                            color = getTagColor(i.tagId)
                        }
                    );
                }
                return result;

            }

            data = db.ExecuteDynamic(@"
select 
	win.id winId,
	win.text winText, 
	sum(julianday(case when timeEnd > @v1 then @v1 else timeEnd end) - 
        julianday(case when timeStart < @v0 then @v0 else timeStart end)) days
from win
join period p on p.winid = win.id
join app on app.id = win.appid
left join tag on tag.id = win.tagId or (win.tagId = 0 and tag.id = app.tagId)
where 
    ifnull(tag.id, -1) = @v2
    and (
        timeStart between @v0 and @v1 
        or timeEnd between @v0 and @v1
        or @v0 between timeStart and timeEnd
    )
group by win.id 
order by days desc
",
                timefrom, timeto, parentKey
            );

            foreach (var i in data)
            {
                var time = new TimeSpan((long)(i.days * TimeSpan.TicksPerDay));
                result.Add(
                    new
                    {
                        i.winId,
                        text = string.IsNullOrWhiteSpace(i.winText) ? "(无标题)" : i.winText,
                        time = time.ToString(@"hh\:mm\:ss"),
                        percent = Math.Round(time.TotalSeconds * 100 / totalSeconds, 2) + "%"
                    }
                );
            }

            return result;
        }
 
        /// <summary>
        /// 窗口按标签分组行（app 视图二级）
        /// </summary>
        class Group
        {
            public string text;
            public TimeSpan time;
            public List<object> wins;
        }

        /// <summary>
        /// 时间
        /// </summary>
        public class TimeInfo
        {
            /// <summary>
            /// 原始时间
            /// </summary>
            public DateTime timeSrc;
            /// <summary>
            /// 切换到应用的时间
            /// </summary>
            public DateTime timeStart;
            /// <summary>
            /// 应用名
            /// </summary>
            public string app;
            /// <summary>
            /// 应用id
            /// </summary>
            public long appId;
            /// <summary>
            /// 窗口标题
            /// </summary>
            public string title;
        }

        /// <summary>
        /// 获取指定时间的记录信息
        /// </summary>
        /// <param name="time"></param>
        /// <returns></returns>
        public TimeInfo getTimeInfo(DateTime time)
        {
            
            var data = db.ExecuteDynamic(@" 
SELECT timeStart, app.text appText, win.text winText, app.id appId
from period
join win on win.id = period.winid
join app on app.id = win.appid
where @v0 between timeStart and timeEnd 
limit 1",
               time
           ).FirstOrDefault();

            if (data == null)
            {
                // 无使用记录时检查离开时段
                var a = db.ExecuteDynamic(@"
SELECT timeStart, reason
from away
where @v0 between timeStart and ifnull(timeEnd, '9999-12-31 23:59:59')
limit 1",
                   time
               ).FirstOrDefault();
                if (a == null)
                {
                    return null;
                }
                return new TimeInfo
                {
                    timeSrc = time,
                    timeStart = a.timeStart,
                    app = (int)a.reason == 1 ? "锁屏中" : "离开（无输入）",
                    title = "",
                    appId = 0
                };
            }
              
            return new TimeInfo
            {
                timeSrc = time,
                timeStart = data.timeStart,
                app = data.appText, 
                title = data.winText, 
                appId = data.appId
            };
        }

        static byte[] defaultIcon;

        public byte[] getIcon(int appId, bool large)
        {
            var path = Recorder.GetIconPath(appId, large);
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
            if (defaultIcon == null)
            {
                defaultIcon = File.ReadAllBytes("./webui/img/icon.png");
            }
            return defaultIcon;
        }

        static byte[] imageNone = null;

        TimeSpan getTime(string file)
        {
            return TimeSpan.ParseExact(Path.GetFileNameWithoutExtension(file), "hhmmss", CultureInfo.InvariantCulture);
        }
         

        /// <summary>
        /// 查找不满足条件的最后一个元素
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="items"></param>
        /// <param name="largerThenTarget"></param>
        /// <returns></returns>
        T find<T>(IList<T> items, Func<T, bool> largerThenTarget)
        {
            if (items.Count == 0)
            {
                return default;
            }

            var match = items[0];
            if (largerThenTarget(match))
            {
                return default;
            }

            for (var i = 1; i < items.Count; i++)
            {
                var item = items[i];
                if (largerThenTarget(item))
                {
                    break;
                }
                match = item;
            }
            return match;
        }


        static Thread lastThread;
        static readonly object threadLock = new object();
        /// <summary>
        /// 获取指定时间的截图
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public byte[] getImage(TimeInfo info)
        {

            if (imageNone == null)
            {
                imageNone = File.ReadAllBytes(Path.Combine(Server.WebRootPath, "img", "none.png"));
            }

            //只响应最后一个请求，避免运行多个ffmpeg占用资源。
            lock (threadLock)
            {
                Ffmpeg.KillLastFfmpeg();
                if (lastThread != null && lastThread.IsAlive)
                {
                    lastThread.Abort();
                }
                lastThread = Thread.CurrentThread;
            }

            try
            {
                //先从buffer中找
                {
                    var buffers = new List<MemoryBuffer>(Recorder.flushing);
                    if (Recorder.buffer != null)
                    {
                        buffers.Add(Recorder.buffer);
                    }

                    var match = find(buffers, i => i.StartTime > info.timeSrc);
                    if (match != null)
                    {
                        var time = info.timeSrc - match.StartTime;
                        var frame = find(match.Frames, f => (match.StartTime + f.Time) > info.timeSrc);
                        if (frame != null)
                        {
                            return frame.Data;
                        }
                    }
                }


                //从文件系统找 
                {
                    var path = Recorder.getFileName(info.timeSrc);
                    var needtime = info.timeSrc.TimeOfDay;
                    var needtimetext = needtime.ToString("hhmmss"); 
                    var match = (from f in Directory.GetFiles(Path.GetDirectoryName(path), "????????." + Recorder.ExName) 
                                 where Path.GetFileNameWithoutExtension(f).CompareTo(needtimetext) < 0 
                                 orderby f
                                 select f).LastOrDefault(); 
                    if (match != null)
                    { 
                        var time = needtime - getTime(match);
                        var data = Ffmpeg.Snapshot(match, time); 
                        if (data != null && data.Length > 0)
                        {
                            return data;
                        }
                    }

                    return imageNone;
                }
            }
            catch (ThreadAbortException)
            {
                return imageNone;
            }
        }

        #endregion

        #region tag

        private long nextTagId = 0;
        private long NextTagId()
        { 
            if (nextTagId == 0)
            {
                nextTagId = db.ExecuteValue<long>("select ifnull(max(id), 0) + 1 from tag");
            }
            return nextTagId++;
        }
          
        public bool existsTag(string text)
        { 
            return db.ExecuteValue< bool>(
                "select exists(select * from tag where text = @text)", 
                new SQLiteParameter("text", text)
            );
        }

        public bool addTag(string text)
        {
            if(existsTag(text))
            {
                return false;
            }
            
            db.Execute(
                "insert into tag (id, text) values(@id, @text)",
                new SQLiteParameter("id", NextTagId()),
                new SQLiteParameter("text", text)
            );
            return true;
        }

        public void removeTag(int tagId)
        {
            
            db.Execute(
                "delete from tag where id = @id",
                new SQLiteParameter("id", tagId)
            );

            db.Execute($"update app set tagId=0 where tagId={tagId}");
            db.Execute($"update win set tagId=0 where tagId={tagId}");

            // 级联删除该标签的收录规则
            db.Execute($"delete from rule where tagId={tagId}");

        }

        public void clearAppTag(long appId)
        {
            db.Execute("update app set tagid = 0 where id = @v0", appId);
        }

        public void clearWinTag(long winId)
        {
            db.Execute("update win set tagid = 0 where id = @v0", winId);
        }

        public DataTable getTags()
        { 
            return db.ExecuteTable("select id, text from tag order by id");
        }


        public bool isTagUsed(int tagId)
        {
            return db.ExecuteValue<bool>(
                @"select exists(
select * from app where tagId = @tagId
union all
select * from win where tagId = @tagId
)",
                new SQLiteParameter("tagId", tagId)
            );
        }

        DB db = DB.Instance;

        public bool renameTag(long tagId, string newName)
        {
            if(existsTag(newName))
            {
                return false;
            }

            db.Execute("update tag set text=@newName where id=@tagId",
                new SQLiteParameter("newName", newName),
                new SQLiteParameter("tagId", tagId)
            );


            return true;
        }

        public void tagApp(long appId, long tagId)
        {
            db.Execute(
                "update app set tagid = @tagId where id=@appId",
                new SQLiteParameter("appId", appId),
                new SQLiteParameter("tagId", tagId)
            );
        }

        public void tagWin(long winId, long tagId)
        {
            db.Execute(
                "update win set tagid = @tagId where id=@winId",
                new SQLiteParameter("winId", winId),
                new SQLiteParameter("tagId", tagId)
            );
        }
         
        #endregion

        #region rule

        public DataTable getRules()
        {
            return db.ExecuteTable(@"
select rule.id, rule.pattern, rule.tagId, tag.text tagText
from rule
left join tag on tag.id = rule.tagId
order by rule.id");
        }

        public bool addRule(string pattern, long tagId)
        {
            if (db.ExecuteValue<bool>(
                "select exists(select * from rule where pattern = @pattern)",
                new SQLiteParameter("pattern", pattern)
            ))
            {
                return false;
            }

            db.Execute(
                "insert into rule (id, pattern, tagId) values(@id, @pattern, @tagId)",
                new SQLiteParameter("id", (long)db.ExecuteData("select ifnull(max(id), 0) + 1 from rule")[0][0]),
                new SQLiteParameter("pattern", pattern),
                new SQLiteParameter("tagId", tagId)
            );
            return true;
        }

        public void removeRule(long ruleId)
        {
            db.Execute(
                "delete from rule where id = @id",
                new SQLiteParameter("id", ruleId)
            );
        }

        /// <summary>
        /// 按规则重刷全部历史窗口的分组，返回更新的窗口数（只影响未分组的窗口）。
        /// </summary>
        public int applyRules()
        {
            var count = 0;
            var rules = db.ExecuteDynamic("select pattern, tagId from rule").ToList();
            if (rules.Count == 0)
            {
                return 0;
            }
            var wins = db.ExecuteDynamic("select id, text from win where tagId = 0").ToList();
            foreach (var w in wins)
            {
                foreach (var r in rules)
                {
                    if (((string)w.text).Contains((string)r.pattern))
                    {
                        db.Execute($"update win set tagId={r.tagId} where id={w.id}");
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        #endregion
    }
}
