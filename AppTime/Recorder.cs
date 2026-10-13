
using AppTime.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using System.Windows.Forms;

namespace AppTime
{
    class Recorder
    {
        public const string ExName = "mkv";

        public Recorder()
        {
            BuildDataPath();
        }

        /// <summary>
        /// 统计周期。
        /// </summary>
        public int IntervalMs = 1000;
        public void Start()
        { 
            new Thread(RecorderThreadProc) { IsBackground = true }.Start();
        }

        class App
        {
            public string WinText;
            public string AppProcess;
            public DateTime TimeStart;
            public long WinId;
        }

        public void BuildDataPath()
        {
            Directory.CreateDirectory(IconPath);
            Directory.CreateDirectory(ScreenPath);
        }

        Dictionary<int, Process> processes;
        public Process GetProcess(int processID)
        { 
            if (processes != null && processes.TryGetValue(processID, out var p))
            {
                return p;
            }

            processes = Process.GetProcesses().ToDictionary(p => p.Id);
            return processes[processID];
        }
         
        class app
        {
            public long id;
            public string process;
            public Dictionary<string, win> wins = new Dictionary<string, win>();
        }

        class win
        {
            public long id;
            public string text;
        }

        long nextAppId = 0;

        Icon GetIcon(string fileName, bool largeIcon)
        {
            var shfi = new SHFILEINFO();
            WinApi.SHGetFileInfo(fileName, 0, ref shfi,
                (uint)Marshal.SizeOf(shfi),
                (uint)FileInfoFlags.SHGFI_ICON | (uint)FileInfoFlags.SHGFI_USEFILEATTRIBUTES
                | (uint)(largeIcon ? FileInfoFlags.SHGFI_LARGEICON : FileInfoFlags.SHGFI_SMALLICON)
            );

            return Icon.FromHandle(shfi.hIcon);  
        }

        void SaveIcon(Icon icon, string filename)
        {
            using var img = icon.ToBitmap();
            img.Save(filename);
        } 

        public string GetIconPath(long appId, bool large)
        {
            return Path.Combine(IconPath, $"{appId}{(large ? "l" : "s")}.png");
        }

        Dictionary<string, app> apps = new Dictionary<string, app>();
        app GetApp(Process process)
        {
            var name = process.ProcessName;
            if (apps.TryGetValue(name, out var app))
            {
                return app;
            }

            var data = db.ExecuteDynamic(
                    @"select id from app where process = @process",
                    new SQLiteParameter("process", name)
            ).FirstOrDefault();
             
            if (data == null)
            {
                if (nextAppId == 0)
                {
                    nextAppId = (int)(long)db.ExecuteData("select ifnull(max(id),0) + 1 from app")[0][0];
                }

                var text = "";
                try
                {
                    text = process.MainModule.FileVersionInfo.FileDescription;

                    using var iconl = GetIcon(process.MainModule.FileName, true);
                    SaveIcon(iconl, GetIconPath(nextAppId, true));

                    using var icons = GetIcon(process.MainModule.FileName, false);
                    SaveIcon(icons, GetIconPath(nextAppId, false));
                }
                catch (Win32Exception)
                {
                    //ignore
                }
                catch (FileNotFoundException)
                {
                    //ignore
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = process.ProcessName;
                } 


                db.Execute(
                    "insert into app (id, process, text, tagId) values(@id, @process, @text, 0)",
                    new SQLiteParameter("id", nextAppId),
                    new SQLiteParameter("process", name),
                    new SQLiteParameter("text", text)
                ); 
                
                app = new app { id = nextAppId, process = name };
                nextAppId++;
            }
            else
            {
                app = new app
                {
                    id = data.id,
                    process = name
                };
            }

            apps.Add(name, app);

            //fix icons
            var largeIconPath = GetIconPath(app.id, true);
            if (!File.Exists(largeIconPath))
            {
                try
                {
                    using var iconl = GetIcon(process.MainModule.FileName, true);
                    SaveIcon(iconl, largeIconPath);

                    using var icons = GetIcon(process.MainModule.FileName, false);
                    SaveIcon(icons, GetIconPath(app.id, false));
                }
                catch(Win32Exception)
                {

                }
            }
            return app;

        }

        long nextWinId = 0;
        win GetWin(Process process, string winText)
        {
            var app = GetApp(process);
            if (app.wins.TryGetValue(winText, out var win))
            {
                return win;
            }

            var data = db.ExecuteDynamic(
                "select id from win where appid=@appid and text=@winText",
                new SQLiteParameter("appid", app.id),
                new SQLiteParameter("winText", winText)
            ).FirstOrDefault();

            if (data == null)
            {
                if (nextWinId == 0)
                {
                    nextWinId = (int)(long)db.ExecuteData("select ifnull(max(id),0) + 1 from win")[0][0];
                }
                // 分组规则：窗口标题包含 pattern 时自动归入对应标签
                var ruleRows = db.ExecuteData(
                    "select tagId from rule where @winText like '%' || pattern || '%' limit 1",
                    new SQLiteParameter("winText", winText)
                );
                var ruleTagId = ruleRows.Count > 0 ? (long)ruleRows[0][0] : 0;
                db.Execute(
                    "insert into win (id, appId, text, tagId) values(@id, @appId, @text, @tagId)",
                    new SQLiteParameter("id", nextWinId),
                    new SQLiteParameter("appId", app.id),
                    new SQLiteParameter("text", winText),
                    new SQLiteParameter("tagId", ruleTagId)
                );
                win = new win { id = nextWinId, text = winText };
                nextWinId++;
            }
            else
            {
                win = new win
                {
                    id = data.id,
                    text = winText
                };
                // 已存在的窗口若未分组，补做规则归组
                var existTagId = (long)db.ExecuteData($"select tagId from win where id={data.id}")[0][0];
                if (existTagId == 0)
                {
                    var ruleRows = db.ExecuteData(
                        "select tagId from rule where @winText like '%' || pattern || '%' limit 1",
                        new SQLiteParameter("winText", winText)
                    );
                    if (ruleRows.Count > 0)
                    {
                        db.Execute($"update win set tagId={(long)ruleRows[0][0]} where id={data.id}");
                    }
                }
            }

            app.wins.Add(winText, win);
            return win;
        }

        DB db = DB.Instance;

        /// <summary>
        /// 无输入多少秒后判定为离开（看视频时画面在变化，不会被误判）。
        /// </summary>
        const int AwayIdleSeconds = 5 * 60;

        /// <summary>
        /// 系统最后输入距现在多少秒。
        /// </summary>
        static int GetIdleSeconds()
        {
            var info = new WinApi.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(WinApi.LASTINPUTINFO)) };
            if (WinApi.GetLastInputInfo(ref info))
            {
                uint now = unchecked((uint)Environment.TickCount);
                return (int)((now - info.dwTime) / 1000);
            }
            return 0;
        }

        byte[] lastThumb;
        /// <summary>
        /// 判断屏幕画面是否静止（缩小为16x9与上一帧比较）。
        /// </summary>
        bool IsScreenStatic(Bitmap img)
        {
            using var thumb = new Bitmap(img, 16, 9);
            var data = new byte[16 * 9];
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    var p = thumb.GetPixel(x, y);
                    data[y * 16 + x] = (byte)((p.R * 30 + p.G * 59 + p.B * 11) / 100);
                }
            }
            if (lastThumb == null)
            {
                lastThumb = data;
                return false;
            }
            long diff = 0;
            for (int i = 0; i < data.Length; i++)
            {
                diff += Math.Abs(data[i] - lastThumb[i]);
            }
            lastThumb = data;
            return diff / (double)data.Length < 2.0;
        }

        public void RecorderThreadProc()
        {
            App lastApp = null;
            bool away = false;
            var awayStart = DateTime.Now;
            var awayReason = 0; // 0=无输入 1=锁屏
            var now = DateTime.Now;
            while (true)
            {
                try
                {
                now = DateTime.Now; 
                var hwnd = WinApi.GetForegroundWindow();
                var text = new StringBuilder(255);
                WinApi.GetWindowText(hwnd, text, 255);
                var winText = text.ToString();
                WinApi.GetWindowThreadProcessId(hwnd, out var processid);
                var process = GetProcess(processid);
                var appname = process.ProcessName;

                // 离开检测：长时间无输入 且 画面静止（锁屏/黑屏/人离开），看视频画面在动不会误判
                if (!away && GetIdleSeconds() >= AwayIdleSeconds)
                {
                    using var probe = GetScreenSafe();
                    if (probe == null || IsScreenStatic(probe))
                    {
                        away = true;
                        awayStart = now;
                        awayReason = WinApi.IsWorkstationLocked() ? 1 : 0;
                        db.Execute(
                            "insert into away(timeStart, timeEnd, reason) values(@v0, null, @v1)",
                            awayStart, awayReason
                        );
                    }
                }

                if (away)
                {
                    // 离开状态：结束当前周期，不录制
                    if (lastApp != null)
                    {
                        db.Execute(
                            "update period set timeend = @v1 where timestart = @v0",
                            lastApp.TimeStart,
                            now.AddMilliseconds(-1)
                        );
                        lastApp = null;
                    }
                    if (buffer != null)
                    {
                        // 先落盘当前缓冲，避免恢复后帧率计算错乱
                        FlushScreenBuffer();
                    }
                    // 离开结束：用户恢复输入 或 画面重新变化
                    if (GetIdleSeconds() < AwayIdleSeconds)
                    {
                        away = false;
                        db.Execute(
                            "update away set timeEnd = @v1 where timeStart = @v0 and timeEnd is null",
                            awayStart, now
                        );
                    }
                    else
                    {
                        using var probe = GetScreenSafe();
                        if (probe == null || !IsScreenStatic(probe))
                        {
                            away = false;
                            db.Execute(
                                "update away set timeEnd = @v1 where timeStart = @v0 and timeEnd is null",
                                awayStart, now
                            );
                        }
                    }
                }
                else
                {
                    if (lastApp != null)
                    {
                        db.Execute(
                            "update period set timeend = @v1 where timestart = @v0",
                            lastApp.TimeStart,
                            now.AddMilliseconds(-1)//必须减小，否则可能与下个周期开始时间重叠 
                        );
                    }

                    if (lastApp == null || lastApp.AppProcess != appname || lastApp.WinText != winText)
                    {
                        var win = GetWin(process, winText);
                        lastApp = new App { WinId = win.id, AppProcess = appname, TimeStart = now, WinText = winText };
                        db.Execute(
                            "insert into [period](winid, timeStart, timeEnd) values(@v0, @v1, @v1)",
                            win.id, now
                        ); 
                    }

                    Screenshot(now);
                }
                }
                catch (Exception ex)
                {
                    // 主循环任何异常都不能带崩进程，记日志后跳过本周期
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(DataPath, "recorder_error.log"),
                            $"{DateTime.Now}: {ex}\r\n"
                        );
                    }
                    catch (Exception)
                    {
                    }
                }
                finally
                {
                    //等到下一个周期
                    var nextTime = now.AddMilliseconds(IntervalMs);
                    now = DateTime.Now;
                    if(nextTime > now)
                    {
                        Thread.Sleep(nextTime - now);
                    } 
                }
            }
        }
         

        string DataPath => string.IsNullOrWhiteSpace(Settings.Default.DataPath) ? Application.StartupPath : Settings.Default.DataPath;
        public string ScreenPath => Path.Combine(DataPath, "images");
        public string IconPath => Path.Combine(DataPath, "icons");
        ImageCodecInfo jpgcodec = ImageCodecInfo.GetImageDecoders().First(codec => codec.MimeType == "image/jpeg");

        ///// <summary>
        ///// 获取图片文件路径
        ///// </summary>
        ///// <param name="timeStart"></param>
        ///// <param name="timeImage"></param>
        ///// <returns></returns>
        //public string getImageFile(DateTime timeStart, DateTime timeImage)
        //{
        //    var folder = Path.Combine(ScreenPath, timeImage.ToString("yyyyMMdd"));
        //    var filename = $"{timeStart:HHmmss}+{Math.Round((timeImage - timeStart).TotalSeconds)}";
        //    return Path.Combine(folder, $"{filename}.jpg");
        //}

        public string getFileName(DateTime time)
        {
            return Path.Combine(ScreenPath, $"{time:yyyyMMdd}", $"{time:HHmmss}." + Recorder.ExName);
        }

        DateTime lastCheck = DateTime.MinValue.Date;

        /// <summary>
        /// 截图
        /// </summary>
        /// <param name="now"></param>
        /// <param name="lastApp"></param>
        void Screenshot(DateTime now)
        {
            //检查记录天数限制
            if (lastCheck != now.Date)
            {
                DateTime firstDate;
                if (Settings.Default.RecordScreenDays != int.MaxValue)
                {
                    firstDate = now.Date.AddDays(-Settings.Default.RecordScreenDays);
                }
                else
                {
                    firstDate = DateTime.MinValue;
                }

                var dirs = Directory.EnumerateDirectories(ScreenPath, "????????");//找到所有录屏目录
                foreach (var i in dirs)
                {
                    if (DateTime.TryParseExact(Path.GetFileName(i), "yyyyMMdd", CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
                    {
                        if (date < firstDate)
                        {
                            Directory.Delete(i, true); //删除过期录屏
                        }
                    }
                }
                lastCheck = now.Date;
            }

            if (Settings.Default.RecordScreenDays == 0)
            {
                return;
            }

            if (buffer == null)
            {
                buffer = new MemoryBuffer(now);
            }

            using var img = GetScreenSafe();
            if (img == null)
            {
                // 屏幕不可用（如锁屏瞬间、分辨率变为0），跳过本帧
                return;
            }
            // 更新画面基准，供离开检测比较
            IsScreenStatic(img);
            using var mem = new MemoryStream();
            img.Save(mem, ImageFormat.Jpeg);
            buffer.Frames.Add(new Frame(now - buffer.StartTime, mem.ToArray()));
            if ((now - buffer.StartTime).TotalSeconds >= 10 * 60 || now.Date != buffer.StartTime.Date) //固定为10mins，防止保存时间长，减少出问题时影响的时长。
            {
                FlushScreenBuffer();
            }
        }


        public void FlushScreenBuffer()
        {
            //切换到新buffer
            var newBuffer = new MemoryBuffer(DateTime.Now);
            var b = buffer;
            buffer = newBuffer;

            // 限制同时保存的缓冲数量，防止ffmpeg保存慢时内存积压（每缓冲约60-180MB）
            while (true)
            {
                lock (flushing)
                {
                    if (flushing.Count < 2)
                    {
                        break;
                    }
                }
                Thread.Sleep(500);
            }

            //加入flushing
            lock (flushing)
            {
                flushing.Add(b);
            }
            var path = getFileName(b.StartTime);
            var folder = Path.GetDirectoryName(path);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            new Thread(() =>
            {
                try
                {
                    Ffmpeg.Save(getFileName(b.StartTime), b.Frames.ToArray());
                }
                catch (Exception ex)
                {
                    // 保存失败（如磁盘满、系统繁忙导致I/O错误）时不能带崩整个进程
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(DataPath, "save_error.log"),
                            $"{DateTime.Now}: {ex}\r\n"
                        );
                    }
                    catch (Exception)
                    {
                    }
                }
                finally
                {
                    lock (flushing)
                    {
                        flushing.Remove(b);
                    }
                }
            })
            {
                Priority = ThreadPriority.BelowNormal,
                IsBackground = false
            }.Start();
        }


        public class MemoryBuffer
        {
            public readonly DateTime StartTime; 
            public readonly List<Frame> Frames = new List<Frame>();
            public MemoryBuffer(DateTime startTime)
            {
                StartTime = startTime; 
            }
        } 

        public MemoryBuffer buffer;

        public List<MemoryBuffer> flushing = new List<MemoryBuffer>(); 

        /// <summary>
        /// 截取当前焦点所在屏幕。屏幕不可用（锁屏瞬间、分辨率变为0、CopyFromScreen失败）时返回null，不会抛异常或死循环。
        /// </summary>
        Bitmap GetScreenSafe()
        {
            try
            {
                var screen = Screen.FromHandle(WinApi.GetForegroundWindow());
                if (screen.Bounds.Width <= 0 || screen.Bounds.Height <= 0)
                {
                    screen = Screen.PrimaryScreen;
                }
                if (screen == null || screen.Bounds.Width <= 0 || screen.Bounds.Height <= 0)
                {
                    return null;
                }

                var result = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
                using var g = Graphics.FromImage(result);
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, screen.Bounds.Size);
                        return result;
                    }
                    catch
                    {
                        Thread.Sleep(100);
                    }
                }
                result.Dispose();
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
