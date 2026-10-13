using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AppTime
{
    /**
     * 初始化数据库
     */
    class InitDB
    {
        private DB db;

        public InitDB()
        {
            db = DB.Instance;
        }

        public void Start()
        {
            // 创建数据表

            db.Execute("CREATE TABLE IF NOT EXISTS \"app\" (\"id\" INTEGER NOT NULL, \"process\" text NOT NULL," +
                "\"text\" TEXT NOT NULL DEFAULT process, " +
                "\"tagId\" INTEGER NOT NULL DEFAULT(0), " +
                "PRIMARY KEY(\"id\") ) WITHOUT ROWID");

            db.Execute("CREATE TABLE IF NOT EXISTS \"period\" ( \"timeStart\" DATETIME NOT NULL, \"timeEnd\" DATETIME NOT NULL, " +
                "\"winId\" INTEGER NOT NULL," +
                "PRIMARY KEY(\"timeStart\")," +
                "UNIQUE(\"timeStart\" ASC) )WITHOUT ROWID");

            db.Execute("CREATE TABLE IF NOT EXISTS \"tag\" ( \"id\" INTEGER NOT NULL, \"text\" TEXT NOT NULL," +
                "PRIMARY KEY(\"id\"), UNIQUE(\"id\" ASC), UNIQUE(\"text\" ASC) ) WITHOUT ROWID");

            db.Execute("CREATE TABLE IF NOT EXISTS \"win\" (\"id\" INTEGER NOT NULL," +
                "\"appId\" INTEGER NOT NULL, \"text\" TEXT NOT NULL, " +
                "\"tagId\" INTEGER NOT NULL DEFAULT(0), " +
                "PRIMARY KEY(\"id\") ) WITHOUT ROWID");


            // 创建索引

            // 分组规则表：窗口标题包含 pattern 时自动归入 tagId 分组
            db.Execute("CREATE TABLE IF NOT EXISTS \"rule\" (\"id\" INTEGER NOT NULL, " +
                "\"pattern\" TEXT NOT NULL, \"tagId\" INTEGER NOT NULL, " +
                "PRIMARY KEY(\"id\")) WITHOUT ROWID");

            // 预置规则（仅在规则表为空时写入一次）
            long ruleCount = (long)db.ExecuteData("select count(*) from rule")[0][0];
            if (ruleCount == 0)
            {
                var presets = new[]
                {
                    new { pattern = "OC | ", tag = "OpenCode" },
                    new { pattern = "Steam 社区", tag = "Steam" },
                    new { pattern = "哔哩哔哩", tag = "B站" }
                };
                foreach (var preset in presets)
                {
                    var rows = db.ExecuteData(
                        "select id from tag where text = @text",
                        new SQLiteParameter("text", preset.tag));
                    long tagId;
                    if (rows.Count > 0)
                    {
                        tagId = (long)rows[0][0];
                    }
                    else
                    {
                        tagId = (long)db.ExecuteData("select ifnull(max(id), 0) + 1 from tag")[0][0];
                        db.Execute(
                            "insert into tag (id, text) values(@id, @text)",
                            new SQLiteParameter("id", tagId),
                            new SQLiteParameter("text", preset.tag));
                    }
                    db.Execute(
                        "insert into rule (id, pattern, tagId) values(@id, @pattern, @tagId)",
                        new SQLiteParameter("id", (long)db.ExecuteData("select ifnull(max(id), 0) + 1 from rule")[0][0]),
                        new SQLiteParameter("pattern", preset.pattern),
                        new SQLiteParameter("tagId", tagId));
                }
            }

            // 离开时段记录：reason 0=无输入 1=锁屏；timeEnd 为空表示离开未结束（运行中退出）
            db.Execute("CREATE TABLE IF NOT EXISTS \"away\" ( \"timeStart\" DATETIME NOT NULL, " +
                "\"timeEnd\" DATETIME, \"reason\" INTEGER NOT NULL DEFAULT(0), " +
                "PRIMARY KEY(\"timeStart\") )WITHOUT ROWID");

            // 启动时闭合上次运行中未结束的离开记录（从离开开始到本次启动之间人都不在电脑前）
            db.Execute("update away set timeEnd = @v0 where timeEnd is null", DateTime.Now);

            long existIndex = (long)db.ExecuteData("SELECT count(*) FROM sqlite_master WHERE type=\"table\" AND name =\'is_index\'")[0][0];
            // 判断是否已经存在索引
            if (existIndex == 0)
            {
                db.Execute("CREATE UNIQUE INDEX \"ix_app\" ON \"app\"( \"process\" ASC )");
                db.Execute("CREATE INDEX \"ix_app_tagId\" ON \"app\" ( \"tagId\" ASC )");

                db.Execute("CREATE UNIQUE INDEX \"ix_period\" ON \"period\" ( \"timeStart\" ASC, \"timeEnd\" ASC )");

                db.Execute("CREATE UNIQUE INDEX \"ix_win\" ON \"win\" ( \"appId\" ASC, \"text\" ASC )");
                db.Execute("CREATE INDEX \"ix_win_tagId\" ON \"win\" (\"tagId\" ASC )");

                // 此表仅用于标识索引已经创建完成
                db.Execute("CREATE TABLE IF NOT EXISTS \"is_index\"( \"id\" INTEGER NOT NULL, PRIMARY KEY(\"id\"))");
            }
        }
    }
}
