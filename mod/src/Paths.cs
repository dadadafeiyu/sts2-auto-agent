using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

// 所有外部路径集中在这里：不再硬编码，别人装到任何盘都能跑。
// 优先级：配置文件 > 模组目录 > 默认（%APPDATA%\Sts2Agent）
public static class Paths
{
    public static string Data;      // 数据根目录（flag/记忆/日志/截图都在里面）
    public static string Kb;        // 知识库目录
    public static string Logs;      // 日志
    public static string Shots;     // 结算截图
    public static string Game;      // 游戏安装目录（可能为 null）
    public static int Port = 18731; // 决策服务端口

    static Paths() { try { Init(); } catch { } }

    static void Init()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Data = Path.Combine(appData, "Sts2Agent");
        string modDir = ModDirectory();

        // ① 配置文件
        string cfg = Path.Combine(Data, "config.json");
        if (File.Exists(cfg))
        {
            try
            {
                string txt = File.ReadAllText(cfg);
                string d = JsonStr(txt, "dataDir");
                string k = JsonStr(txt, "kbDir");
                string g = JsonStr(txt, "gameDir");
                int p;
                if (!string.IsNullOrEmpty(d)) Data = d;
                if (!string.IsNullOrEmpty(k)) Kb = k;
                if (!string.IsNullOrEmpty(g)) Game = g;
                if (int.TryParse(JsonStr(txt, "port"), out p) && p > 0) Port = p;
            }
            catch { }
        }

        // ② 环境变量
        try
        {
            string envKb = Environment.GetEnvironmentVariable("STS2_KB_DIR");
            if (string.IsNullOrEmpty(Kb) && !string.IsNullOrEmpty(envKb)) Kb = envKb;
            if (string.IsNullOrEmpty(Game))
            {
                string envGame = Environment.GetEnvironmentVariable("STS2_GAME_DIR");
                if (!string.IsNullOrEmpty(envGame)) Game = envGame;
            }
        }
        catch { }

        // ③ 知识库默认：模组目录\knowledge（跟着 DLL 走），再退到 Data\knowledge
        if (string.IsNullOrEmpty(Kb))
        {
            string near = modDir == null ? null : Path.Combine(modDir, "knowledge");
            if (near != null && Directory.Exists(near)) Kb = near;
            else Kb = Path.Combine(Data, "knowledge");
        }

        // ④ 游戏目录：默认 Steam 路径兜底
        if (string.IsNullOrEmpty(Game))
        {
            string[] guess = new string[] {
                @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
                @"C:\Program Files\Steam\steamapps\common\Slay the Spire 2",
                @"D:\Steam\steamapps\common\Slay the Spire 2",
                @"D:\SteamLibrary\steamapps\common\Slay the Spire 2",
                @"E:\Steam\steamapps\common\Slay the Spire 2",
                @"E:\SteamLibrary\steamapps\common\Slay the Spire 2"
            };
            for (int i = 0; i < guess.Length; i++)
            {
                try { if (File.Exists(Path.Combine(guess[i], "data_sts2_windows_x86_64\\sts2.dll"))) { Game = guess[i]; break; } }
                catch { }
            }
        }

        Logs = Path.Combine(Data, "logs");
        Shots = Path.Combine(Data, "shots");
        try { Directory.CreateDirectory(Data); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Shots); } catch { }
        if (!string.IsNullOrEmpty(Kb)) { try { Directory.CreateDirectory(Kb); } catch { } }
    }

    // 模组所在目录（DLL 的位置）
    static string ModDirectory()
    {
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string loc = asm == null ? null : asm.Location;
            if (string.IsNullOrEmpty(loc)) return null;
            return Path.GetDirectoryName(loc);
        }
        catch { return null; }
    }

    // 极简 JSON 取字符串字段（只用于读 config.json，不引外部库）
    static string JsonStr(string json, string key)
    {
        try
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\");
        }
        catch { return null; }
    }
}
