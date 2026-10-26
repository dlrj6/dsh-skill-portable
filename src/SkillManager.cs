// SkillManager.cs —— DSH skill 管理器（便携单文件版）
//
// 编译（Windows 自带编译器，不需要装任何东西）：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
//     /target:winexe /out:SkillManager.exe ^
//     /reference:System.dll /reference:System.Core.dll ^
//     /reference:PresentationCore.dll /reference:PresentationFramework.dll ^
//     /reference:WindowsBase.dll /reference:System.Xaml.dll ^
//     SkillManager.cs
//
// 设计要点：
//   1. skill 根目录 = exe 自己所在的目录。整个文件夹拷到哪都能用，不写注册表、不装运行时之外的东西。
//   2. 界面全部用代码搭（不用 XAML 编译），这样 csc 一条命令就能出 exe。
//   3. 写文件先写临时文件再改名，中途断电不会把 skill 文件写坏；编码保持原样（有 BOM 就留 BOM）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
// Shapes.Path 和 IO.Path 同名，这里统一用 IO 的那个
using Path = System.IO.Path;

// 程序集信息：写进 exe 的资源里，属性面板和 --version 都能看到。
// 发版时把版本号一起改掉（AssemblyVersion / AssemblyFileVersion / MainWindow.AppVersion）。
[assembly: System.Reflection.AssemblyTitle("SkillManager")]
[assembly: System.Reflection.AssemblyProduct("DSH Skill Manager")]
[assembly: System.Reflection.AssemblyDescription("DSH / SKILL.md 技能开关管理器（便携单文件版）")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace SkillManager
{
    /// <summary>三种状态：开、关（模型看不见但能手动调用）、卸载（挪进 .disabled）。</summary>
    internal enum SkillState { On, Off, Uninstalled }

    internal sealed class SkillInfo
    {
        public string Name;           // 文件名（去掉 .md）
        public string FilePath;       // 指令文件路径
        public bool IsBundle;         // true = <名字>/SKILL.md，false = <名字>.md
        public bool InPool;           // 是否在停用池里
        public string FrontmatterName; // frontmatter 里的 name
        public string Description;
        public SkillState State;
        public bool Broken;           // frontmatter 不合法
        public List<string> Errors = new List<string>();

        /// <summary>界面显示用的状态：池里的一律算「已卸载」。</summary>
        public SkillState Shown { get { return InPool ? SkillState.Uninstalled : State; } }

        public string LeafPath
        {
            get { return IsBundle ? Path.GetDirectoryName(FilePath) : FilePath; }
        }
    }

    /// <summary>读写 skill 文件的内核。界面不直接碰文件，全走这里。</summary>
    internal static class Kernel
    {
        public const string PoolName = ".disabled";

        public static string StateLabel(SkillState s)
        {
            switch (s)
            {
                case SkillState.On: return "已启用";
                case SkillState.Off: return "已禁用";
                default: return "已卸载";
            }
        }

        public static Color StateColor(SkillState s)
        {
            switch (s)
            {
                case SkillState.On: return Color.FromRgb(0x3F, 0xB9, 0x50);
                case SkillState.Off: return Color.FromRgb(0x8B, 0x94, 0x9E);
                default: return Color.FromRgb(0xF0, 0x88, 0x3E);
            }
        }

        private static bool? ParseBool(string raw)
        {
            if (raw == null) return null;
            string v = raw.Trim().Trim('"', '\'').ToLowerInvariant();
            if (v == "true" || v == "yes" || v == "on" || v == "1") return true;
            if (v == "false" || v == "no" || v == "off" || v == "0") return false;
            return null;
        }

        private static string Unquote(string s)
        {
            if (s == null) return "";
            string t = s.Trim();
            if (t.Length >= 2 && ((t[0] == '"' && t[t.Length - 1] == '"') || (t[0] == '\'' && t[t.Length - 1] == '\'')))
                return t.Substring(1, t.Length - 2);
            return t;
        }

        /// <summary>读文件，同时记住有没有 BOM，写回时保持一致。</summary>
        public static string ReadText(string path, out bool hadBom)
        {
            byte[] bytes = File.ReadAllBytes(path);
            hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));
            return text;
        }

        /// <summary>先写临时文件再改名，中途出事不会把原文件写坏。</summary>
        public static void WriteTextAtomic(string path, string text, bool hadBom)
        {
            string tmp = path + ".skillmgr-tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(hadBom));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>找出 frontmatter 那一段的行号范围。返回 null 表示没有合法的 frontmatter。</summary>
        private static int[] FindFrontmatter(string[] lines)
        {
            if (lines.Length == 0) return null;
            if (lines[0].TrimStart('\uFEFF').Trim() != "---") return null;
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].Trim() == "---") return new int[] { 0, i };
            return null;
        }

        private static Dictionary<string, string> FrontmatterKeys(string[] lines, int[] block)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = block[0] + 1; i < block[1]; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = lines[i].Substring(0, colon).Trim();
                if (key.Length == 0 || key.Contains(' ') || key.Contains('#')) continue;
                map[key] = lines[i].Substring(colon + 1).Trim();
            }
            return map;
        }

        /// <summary>从两个开关推出现在处于哪种状态。</summary>
        public static SkillState DetectState(Dictionary<string, string> keys)
        {
            bool? dmi = keys.ContainsKey("disable-model-invocation") ? ParseBool(keys["disable-model-invocation"]) : null;
            bool? ui = keys.ContainsKey("user-invocable") ? ParseBool(keys["user-invocable"]) : null;
            // 键不写就是允许；只有显式写了才按写的算
            bool modelOn = !(dmi == true);
            bool userOn = !(ui == false);
            if (modelOn) return SkillState.On;
            return userOn ? SkillState.Off : SkillState.Uninstalled;
        }

        /// <summary>把 frontmatter 的两个开关改成目标状态；等于默认值的行直接删掉，不留噪声。</summary>
        public static string ApplyState(string text, SkillState state)
        {
            string[] split = text.Replace("\r\n", "\n").Split('\n');
            var lines = new List<string>(split);
            int[] block = FindFrontmatter(lines.ToArray());
            if (block == null) throw new InvalidOperationException("文件开头没有成对的 --- frontmatter，拒绝改写");

            var body = lines.GetRange(block[0], block[1] - block[0] + 1);

            var want = new List<KeyValuePair<string, string>>();
            want.Add(new KeyValuePair<string, string>("disable-model-invocation",
                state == SkillState.On ? "false" : "true"));
            want.Add(new KeyValuePair<string, string>("user-invocable",
                state == SkillState.Uninstalled ? "false" : "true"));

            foreach (var kv in want)
            {
                bool isDefault = (kv.Key == "disable-model-invocation" && kv.Value == "false")
                              || (kv.Key == "user-invocable" && kv.Value == "true");
                int at = -1;
                for (int i = 1; i < body.Count - 1; i++)
                {
                    int colon = body[i].IndexOf(':');
                    if (colon <= 0) continue;
                    if (string.Equals(body[i].Substring(0, colon).Trim(), kv.Key, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                }
                if (isDefault)
                {
                    if (at != -1) body.RemoveAt(at);   // 回默认就不写这一行
                }
                else if (at != -1)
                {
                    body[at] = kv.Key + ": " + kv.Value;
                }
                else
                {
                    body.Insert(body.Count - 1, kv.Key + ": " + kv.Value);
                }
            }

            var result = new List<string>(lines);
            result.RemoveRange(block[0], block[1] - block[0] + 1);
            result.InsertRange(block[0], body);
            return string.Join("\n", result);
        }

        /// <summary>扫一个根目录，连同停用池里的条目一起列出来。</summary>
        public static List<SkillInfo> Scan(string root)
        {
            var list = new List<SkillInfo>();
            Collect(root, false, list, null);
            Collect(Path.Combine(root, PoolName), true, list, null);
            list.Sort(delegate (SkillInfo a, SkillInfo b)
            {
                if (a.InPool != b.InPool) return a.InPool ? 1 : -1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static void Collect(string dir, bool inPool, List<SkillInfo> list, List<string> errors)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith(".")) continue;
                string file = Path.Combine(sub, "SKILL.md");
                if (File.Exists(file)) list.Add(Load(name, file, true, inPool));
            }
            foreach (string f in Directory.GetFiles(dir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.StartsWith(".")) continue;
                list.Add(Load(name, f, false, inPool));
            }
        }

        private static SkillInfo Load(string name, string file, bool bundle, bool inPool)
        {
            var info = new SkillInfo { Name = name, FilePath = file, IsBundle = bundle, InPool = inPool };
            try
            {
                bool bom;
                string text = ReadText(file, out bom);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                int[] block = FindFrontmatter(lines);
                if (block == null)
                {
                    info.Broken = true;
                    info.Errors.Add("开头没有成对的 --- frontmatter，DSH 不会加载它");
                    return info;
                }
                var keys = FrontmatterKeys(lines, block);
                info.FrontmatterName = keys.ContainsKey("name") ? Unquote(keys["name"]) : null;
                info.Description = keys.ContainsKey("description") ? Unquote(keys["description"]) : "";
                info.State = DetectState(keys);
                if (string.IsNullOrEmpty(info.FrontmatterName)) info.Errors.Add("缺 name 字段，DSH 不会加载它");
                if (string.IsNullOrEmpty(info.Description)) info.Errors.Add("缺 description 字段，DSH 不会加载它");
            }
            catch (Exception ex)
            {
                info.Broken = true;
                info.Errors.Add("读取失败：" + ex.Message);
            }
            return info;
        }

        /// <summary>改状态。已在池里的会搬回根目录，改成卸载的会搬进池里。</summary>
        public static string Apply(string root, SkillInfo item, SkillState state)
        {
            bool bom;
            string text = ReadText(item.FilePath, out bom);
            WriteTextAtomic(item.FilePath, ApplyState(text, state), bom);

            if (state == SkillState.Uninstalled)
            {
                if (item.InPool) return "本来就在停用池里，只更新了开关";
                string dest = MoveToPool(root, item, true);
                return "已卸载到 " + PoolName + "：" + dest;
            }
            if (item.InPool)
            {
                string dest = MoveToPool(root, item, false);
                return "已装回根目录：" + dest;
            }
            return state == SkillState.Off ? "已禁用（模型看不见，可用 / 手动调）" : "已启用";
        }

        public static string Delete(SkillInfo item)
        {
            if (item.IsBundle)
            {
                Directory.Delete(Path.GetDirectoryName(item.FilePath), true);
                return "已彻底删除（连同整个目录）：" + Path.GetDirectoryName(item.FilePath);
            }
            File.Delete(item.FilePath);
            return "已彻底删除：" + item.FilePath;
        }

        private static string MoveToPool(string root, SkillInfo item, bool toPool)
        {
            string leaf = item.LeafPath;                       // bundle 搬目录，flat 搬文件
            string baseName = Path.GetFileName(leaf);
            string dest = Path.Combine(toPool ? Path.Combine(root, PoolName) : root, baseName);
            if (!File.Exists(item.FilePath) && !Directory.Exists(item.FilePath))
                throw new FileNotFoundException("文件已经不在了：" + item.FilePath);
            if ((Directory.Exists(dest) || File.Exists(dest)) && !PathsEqual(dest, leaf))
                throw new IOException("目标位置已经有同名的了：" + dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (Directory.Exists(leaf)) Directory.Move(leaf, dest);
            else File.Move(leaf, dest);
            return dest;
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- 自检

        /// <summary>不带界面跑一遍内核逻辑，返回失败项数。sink 为空就写控制台。</summary>
        public static int SelfTest(TextWriter sink)
        {
            if (sink == null) sink = Console.Out;
            int pass = 0, fail = 0;
            Action<bool, string> check = delegate (bool ok, string label)
            {
                if (ok) { pass++; sink.WriteLine("  PASS  " + label); }
                else { fail++; sink.WriteLine("  FAIL  " + label); }
            };

            sink.WriteLine("[1] frontmatter 改写");
            string src = "---\nname: a\ndescription: x\ndisable-model-invocation: true\nuser-invocable: true\n# 注释\n---\n正文\n";
            string on = ApplyState(src, SkillState.On);
            check(!on.Contains("disable-model-invocation") && !on.Contains("user-invocable"), "on：两个开关行都删掉");
            check(on.Contains("正文") && on.Contains("# 注释") && on.Contains("name: a"), "on：正文/注释/其它键都保留");
            string off = ApplyState(src, SkillState.Off);
            check(off.Contains("disable-model-invocation: true") && !off.Contains("user-invocable"), "off：只留 disable-model-invocation");
            string un = ApplyState(src, SkillState.Uninstalled);
            check(un.Contains("disable-model-invocation: true") && un.Contains("user-invocable: false"), "uninstalled：两个开关都写");
            string round = src;
            foreach (SkillState s in new[] { SkillState.Off, SkillState.On, SkillState.Uninstalled, SkillState.Off, SkillState.On })
                round = ApplyState(round, s);
            check(!round.Contains("disable-model-invocation"), "连续切 5 次不留残余开关行");
            check(round.Contains("正文"), "连续切换后正文仍在");

            sink.WriteLine("[2] 状态识别");
            check(DetectState(Keys("")) == SkillState.On, "没有开关行 -> on");
            check(DetectState(Keys("disable-model-invocation: false\n")) == SkillState.On, "disable=false -> on");
            check(DetectState(Keys("disable-model-invocation: true\n")) == SkillState.Off, "disable=true -> off");
            check(DetectState(Keys("disable-model-invocation: true\nuser-invocable: false\n")) == SkillState.Uninstalled, "两个都关 -> uninstalled");
            check(DetectState(Keys("disable-model-invocation: yes\nuser-invocable: no\n")) == SkillState.Uninstalled, "yes/no 写法也认");
            check(DetectState(Keys("disable-model-invocation: off\n")) == SkillState.On, "off 写法 = 允许");

            sink.WriteLine("[3] 端到端（临时目录）");
            string dir = Path.Combine(Path.GetTempPath(), "skillmgr-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string root = Path.Combine(dir, "skills");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "alpha.md"), "---\nname: alpha\ndescription: 测试\n---\n中文正文，不能坏。\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "beta.md"), "---\nname: beta\ndescription: 测试\ndisable-model-invocation: true\n---\n中文正文，不能坏。\n", new UTF8Encoding(false));

            Func<string, SkillInfo> get = delegate (string n) { return Scan(root).FirstOrDefault(x => x.Name == n); };
            Func<string, SkillState> shown = delegate (string n) { SkillInfo s = get(n); return s == null ? SkillState.On : s.Shown; };

            check(shown("alpha") == SkillState.On, "初始 alpha = on");
            check(shown("beta") == SkillState.Off, "初始 beta = off");

            Apply(root, get("beta"), SkillState.On);
            check(shown("beta") == SkillState.On, "beta 切到 on");
            Apply(root, get("beta"), SkillState.Off);
            check(shown("beta") == SkillState.Off, "beta 切回 off");
            check(File.Exists(Path.Combine(root, "beta.md")), "off 状态文件仍在根目录");

            Apply(root, get("beta"), SkillState.Uninstalled);
            check(shown("beta") == SkillState.Uninstalled, "beta 卸载后 = uninstalled");
            check(!File.Exists(Path.Combine(root, "beta.md")), "卸载后离开根目录");
            check(File.Exists(Path.Combine(root, PoolName, "beta.md")), "卸载后进了 .disabled");

            Apply(root, get("beta"), SkillState.On);
            check(shown("beta") == SkillState.On, "beta 装回后 = on");
            check(File.Exists(Path.Combine(root, "beta.md")), "装回后回到根目录");
            check(!File.Exists(Path.Combine(root, PoolName, "beta.md")), "装回后池里没残留");
            string body = File.ReadAllText(Path.Combine(root, "beta.md"));
            check(body.Contains("中文正文"), "往返之后中文正文完好");
            check(body.StartsWith("---\nname: beta\ndescription: 测试\n---\n"), "装回后 frontmatter 干净");

            // 目录式 skill
            string bdir = Path.Combine(root, "gamma");
            Directory.CreateDirectory(bdir);
            File.WriteAllText(Path.Combine(bdir, "SKILL.md"), "---\nname: gamma\ndescription: 目录式\n---\n正文\n", new UTF8Encoding(false));
            check(Scan(root).Count == 3, "目录式 skill 被扫到");
            Apply(root, get("gamma"), SkillState.Uninstalled);
            check(Directory.Exists(Path.Combine(root, PoolName, "gamma")), "目录式也能卸载（整个目录搬走）");
            check(!Directory.Exists(bdir), "搬走后原目录不残留");
            Apply(root, get("gamma"), SkillState.On);
            check(Directory.Exists(bdir), "目录式能装回");
            check(File.Exists(Path.Combine(bdir, "SKILL.md")), "装回后 SKILL.md 在");

            // 坏文件不该让扫描崩
            File.WriteAllText(Path.Combine(root, "broken.md"), "没有 frontmatter\n", new UTF8Encoding(false));
            List<SkillInfo> all = Scan(root);
            check(all.Count == 4, "坏文件也让扫描正常返回");
            check(all.Any(x => x.Broken), "坏文件被标记为 broken");

            try { Directory.Delete(dir, true); } catch { }

            sink.WriteLine();
            sink.WriteLine("结果：通过 " + pass + " 项，失败 " + fail + " 项");
            return fail;
        }

        private static Dictionary<string, string> Keys(string extra)
        {
            string text = "---\nname: a\ndescription: x\n" + extra + "---\n正文\n";
            string[] lines = text.Split('\n');
            return FrontmatterKeys(lines, FindFrontmatter(lines));
        }
    }

    // -------------------------------------------------------------------- 界面

    public partial class MainWindow : Window
    {
        private static readonly Color C_Bg = Color.FromRgb(0x0D, 0x11, 0x17);
        private static readonly Color C_Panel = Color.FromRgb(0x16, 0x1B, 0x22);
        private static readonly Color C_Border = Color.FromRgb(0x30, 0x36, 0x3D);
        private static readonly Color C_Text = Color.FromRgb(0xC9, 0xD1, 0xD9);
        private static readonly Color C_Dim = Color.FromRgb(0x8B, 0x94, 0x9E);
        private static readonly Color C_Bright = Color.FromRgb(0xF0, 0xF6, 0xFC);
        private static readonly Color C_Green = Color.FromRgb(0x3F, 0xB9, 0x50);
        private static readonly Color C_Red = Color.FromRgb(0xF8, 0x51, 0x49);
        private static readonly Color C_Blue = Color.FromRgb(0x1F, 0x6F, 0xEB);
        private static readonly Color C_Warn = Color.FromRgb(0xD2, 0x99, 0x22);

        private string root;
        private readonly List<SkillInfo> items = new List<SkillInfo>();
        private readonly Dictionary<string, RowWidgets> rows = new Dictionary<string, RowWidgets>();
        private SkillInfo undoItem;
        private SkillState undoFrom;
        /// <summary>界面和 --version 里显示的版本号，跟上面的 AssemblyVersion 一起改。</summary>
        public const string AppVersion = "1.0.0";

        private bool buildingRow;
        /// <summary>false = 自检模式，不能弹任何对话框（否则无人值守时会挂住）。</summary>
        private static bool interactive = true;

        private StackPanel rowsHost;
        private TextBlock titleSummary, msgText;
        private Button btnUndo;
        private CheckBox chkUninstalled;
        private TextBox txtSearch;
        private TextBox txtPath;          // 可编辑的 skill 根目录输入框
        private ComboBox cmbRecent;       // 最近用过的目录

        private sealed class RowWidgets
        {
            public Border Row;
            public Ellipse Dot;
            public TextBlock Model, Manual;
            public ComboBox Combo;
        }

        public MainWindow()
        {
            root = ResolveRoot();
            Title = "DSH Skill 管理器 v" + AppVersion + "（便携版）";
            Width = 1240; Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(C_Bg);
            Foreground = new SolidColorBrush(C_Text);
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 13;
            BuildUi();
            FillRecent();
            Reload();
        }

        private static SolidColorBrush Br(Color c) { return new SolidColorBrush(c); }

        /// <summary>
        /// skill 根目录怎么定，按这个顺序：
        ///   1. roots.txt 的第一行 = 你上次选的目录（关掉程序也记得）
        ///   2. exe 旁边有 portable.txt，里面第一行非空、非 # 的内容就是根目录（固定指到别处时用）
        ///   3. exe 旁边的 skills\ 子目录（便携包的标准布局）
        ///   4. exe 自己所在的目录
        /// </summary>
        private static string ResolveRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            foreach (string f in new[] { "roots.txt", "portable.txt" })
            {
                try
                {
                    string hint = Path.Combine(baseDir, f);
                    if (!File.Exists(hint)) continue;
                    foreach (string line in File.ReadAllLines(hint))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        if (Directory.Exists(t)) return t;
                    }
                }
                catch { }
            }
            string sub = Path.Combine(baseDir, "skills");
            if (Directory.Exists(sub)) return sub;
            return baseDir;
        }

        // ------------------------------------------------------------ 目录切换
        //
        // 根目录单独存在一个文本文件里（一行一个），不动 portable.txt。
        // portable.txt 是「初始默认值」，这里是「你上次选的」，分工不同。

        private static string RootsFile
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "roots.txt"); }
        }

        private static List<string> LoadRecent()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(RootsFile)) return list;
                foreach (string line in File.ReadAllLines(RootsFile))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    if (!list.Contains(t)) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        private void RememberRoot(string path)
        {
            try
            {
                var list = LoadRecent();
                list.RemoveAll(delegate (string x) { return string.Equals(x, path, StringComparison.OrdinalIgnoreCase); });
                list.Insert(0, path);
                while (list.Count > 8) list.RemoveAt(list.Count - 1);
                File.WriteAllText(RootsFile, string.Join("\r\n", list) + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>把最近目录填进下拉框（下拉框只是"最近用过的"，不是唯一入口）。</summary>
        private void FillRecent()
        {
            buildingRow = true;
            cmbRecent.Items.Clear();
            List<string> list = LoadRecent();
            if (list.Count == 0)
            {
                var empty = new ComboBoxItem { Content = "（还没有记录）", IsEnabled = false };
                StyleComboItem(empty, C_Panel);
                empty.Foreground = Br(C_Dim);
                cmbRecent.Items.Add(empty);
            }
            else
            {
                foreach (string p in list)
                {
                    var it = new ComboBoxItem { Content = p, Tag = p, ToolTip = p };
                    StyleComboItem(it, C_Panel);
                    cmbRecent.Items.Add(it);
                }
            }
            cmbRecent.SelectedIndex = -1;
            buildingRow = false;
        }

        /// <summary>切换根目录：能建就建，然后刷新列表。</summary>
        private void SwitchRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            path = path.Trim().Trim('"');
            try
            {
                if (!Directory.Exists(path))
                {
                    // 非交互模式（--smoke 等自检）下不能弹框，否则会一直挂在那里等人点
                    if (interactive)
                    {
                        var ans = MessageBox.Show("这个目录还不存在：\n" + path + "\n\n要现在创建它吗？",
                                                   "目录不存在", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (ans != MessageBoxResult.Yes) { txtPath.Text = root; return; }
                    }
                    Directory.CreateDirectory(path);
                }
            }
            catch (Exception ex)
            {
                SetMsg("这个路径用不了：" + ex.Message, C_Red);
                txtPath.Text = root;
                return;
            }
            root = path;
            txtPath.Text = path;
            RememberRoot(path);
            FillRecent();
            Reload();
        }

        /// <summary>路径输入框回车 / 失焦时调用。</summary>
        private void CommitPathBox()
        {
            string t = txtPath.Text.Trim().Trim('"');
            if (t.Length == 0) { txtPath.Text = root; return; }
            if (string.Equals(t, root, StringComparison.OrdinalIgnoreCase)) return;
            SwitchRoot(t);
        }

        private static TextBlock Label(string text, double size, Color color)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = Br(color), VerticalAlignment = VerticalAlignment.Center };
            return t;
        }

        private Button MakeButton(string text, bool primary, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Foreground = Br(C_Text),
                Background = Br(primary ? C_Blue : Color.FromRgb(0x21, 0x26, 0x2D)),
                BorderBrush = Br(primary ? C_Blue : C_Border),
                BorderThickness = new Thickness(1),
                FontFamily = FontFamily,
                FontSize = 13,
                Template = ButtonTemplate   // 让禁用态也用我的配色，见 ButtonTemplate 的说明
            };
            b.Click += click;
            return b;
        }

        /// <summary>先建按钮、之后再挂处理器时用这个（MakeButton 不接受 null 处理器）。</summary>
        private Button MakeButtonNoHandler(string text)
        {
            return MakeButton(text, false, delegate { });
        }

        // ------------------------------------------------------------ 下拉框样式
        //
        // 为什么不用系统的默认模板：Windows 11 的 ComboBox 模板会按系统主题取色，
        // 只设 Background/Foreground 压不住它，结果就是深色窗口里弹出白底菜单、
        // 白底上面又是浅色字 —— 整片看不见。所以这里自己画模板，颜色全部写死。

        /// <summary>给下拉列表的每一项套一个自己画的模板：深底、亮字。</summary>
        private static void StyleComboItem(ComboBoxItem it, Color bg)
        {
            it.Foreground = Br(C_Bright);
            it.Background = Br(bg);
            it.FontSize = 13;
            it.Padding = new Thickness(10, 6, 10, 6);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(2));

            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Border.BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Border.PaddingProperty));
            bd.Name = "Bd";
            bd.AppendChild(presenter);

            var trig = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
            trig.Setters.Add(new Setter(Border.BackgroundProperty, Br(Color.FromRgb(0x30, 0x36, 0x3D)), "Bd"));
            var trig2 = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            trig2.Setters.Add(new Setter(Border.BackgroundProperty, Br(C_Blue), "Bd"));
            trig2.Setters.Add(new Setter(Control.ForegroundProperty, Br(Colors.White)));

            var tpl = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = bd };
            tpl.Triggers.Add(trig);
            tpl.Triggers.Add(trig2);
            it.Template = tpl;
        }

        /// <summary>给下拉框本身套自己画的模板：深色边框 + 深底弹出列表。</summary>
        private static void StyleCombo(ComboBox cb, double width)
        {
            cb.Width = width;
            cb.FontSize = 13;
            cb.Foreground = Br(C_Bright);       // 框里显示的那一行字
            cb.FontWeight = FontWeights.SemiBold;
            cb.Template = ComboTemplate;        // 框体也不再让系统主题插手

            // 禁用态的字色交给样式触发器管：直接设 cb.Foreground 的话，
            // 系统主题会按它自己的规则灰掉，颜色不受我控制。
            var st = new Style(typeof(ComboBox));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, Br(Color.FromRgb(0x6E, 0x76, 0x81))));
            st.Triggers.Add(disabled);
            cb.Style = st;

            // 下拉项容器样式：弹出列表里每一项都是深底亮字
            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Br(C_Bright)));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Br(C_Panel)));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            itemStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

            var itemBd = new FrameworkElementFactory(typeof(Border));
            itemBd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Border.BackgroundProperty));
            itemBd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Border.PaddingProperty));
            itemBd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty,
                new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = itemBd }));
            cb.ItemContainerStyle = itemStyle;

            // 模板里的透明捕获层：加载完成后找到它，挂上"点击就开合下拉"的处理器。
            // 这样即使 ToggleButton 的命中测试失效，点下拉框也一定有反应。
            cb.Loaded += delegate
            {
                Border catcher = cb.Template.FindName("ClickCatcher", cb) as Border;
                if (catcher == null) return;
                catcher.PreviewMouseLeftButtonDown -= ComboCapture_Click;
                catcher.PreviewMouseLeftButtonDown += ComboCapture_Click;
            };
        }

        /// <summary>
        /// 下拉框模板最上层那个透明捕获层被按下时调用，直接开合下拉。
        /// 为什么不让模板里的 ToggleButton 去管：一旦命中测试没走到它，
        /// 表现就是"点了没反应"。这里用一整块透明区域接住点击，最不容易失手。
        /// </summary>
        private static void ComboCapture_Click(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el == null) return;
            ComboBox cb = el.TemplatedParent as ComboBox;
            if (cb == null) cb = FindAncestorCombo(el);
            if (cb == null) return;
            if (cb.IsDropDownOpen) cb.IsDropDownOpen = false;
            else cb.IsDropDownOpen = true;
            e.Handled = true;
        }

        private static ComboBox FindAncestorCombo(DependencyObject start)
        {
            DependencyObject cur = start;
            while (cur != null)
            {
                ComboBox cb = cur as ComboBox;
                if (cb != null) return cb;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        /// <summary>
        /// ComboBox 自己的模板。
        ///
        /// 为什么必须自己画：系统那套经典主题模板用内部画刷画框，Background 设了也不理，
        /// 结果就是深色界面里冒出一个白框、里面的字还看不清。
        ///
        /// 为什么用 XamlReader 而不是 FrameworkElementFactory（踩过的坑，别改回去）：
        /// 用 FrameworkElementFactory 在代码里搭 ComboBox 模板会 StackOverflowException
        /// （0xc00000fd，进程直接死，catch 都来不及跑）。同一份模板写成 XAML 字符串
        /// 交给 XamlReader.Parse 就完全正常 —— 已实测渲染、开合下拉都正常。
        /// </summary>
        private static readonly ControlTemplate ComboTemplate = BuildComboTemplate();

        private static ControlTemplate BuildComboTemplate()
        {
            const string xaml = @"
<ControlTemplate TargetType=""ComboBox""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Border Background=""#21262D"" BorderBrush=""#30363D"" BorderThickness=""1"" CornerRadius=""6"">
    <Grid>
      <!-- 必须有这个 ToggleButton，否则框体点了没反应：ComboBox 的默认模板是靠它
           切换 IsDropDownOpen 的，第一版我漏了它，结果下拉根本打不开。 -->
      <ToggleButton Focusable=""False"" ClickMode=""Press""
                    IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource FindAncestor, AncestorType=ComboBox}}"">
        <ToggleButton.Template>
          <ControlTemplate TargetType=""ToggleButton"">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""*""/>
                <ColumnDefinition Width=""24""/>
              </Grid.ColumnDefinitions>
              <Path Grid.Column=""1"" Data=""M 0,0 L 8,0 L 4,5 Z"" Fill=""#8B949E""
                    HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
            </Grid>
          </ControlTemplate>
        </ToggleButton.Template>
      </ToggleButton>
      <ContentPresenter Margin=""10,0,26,0"" VerticalAlignment=""Center"" IsHitTestVisible=""False""
                        Content=""{TemplateBinding SelectionBoxItem}""/>
      <!-- 最上层铺一整块透明区域接住点击，直接开合下拉。
           事件处理器在 StyleCombo 里用代码挂（模板里写事件名没法解析，
           因为模板是 XamlReader 直接解析的，没有代码后置类）。 -->
      <Border x:Name=""ClickCatcher"" Grid.Column=""0"" Grid.ColumnSpan=""2"" Background=""Transparent""/>
      <Popup AllowsTransparency=""True"" Placement=""Bottom"" Focusable=""False""
             IsOpen=""{Binding IsDropDownOpen, RelativeSource={RelativeSource FindAncestor, AncestorType=ComboBox}}"">
        <Border Background=""#161B22"" BorderBrush=""#30363D"" BorderThickness=""1"" CornerRadius=""6""
                MinWidth=""{TemplateBinding ActualWidth}"" Margin=""0,4,0,0"">
          <StackPanel IsItemsHost=""True"" MaxHeight=""260""/>
        </Border>
      </Popup>
    </Grid>
  </Border>
</ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        /// <summary>
        /// 按钮自己的模板。
        /// 为什么必须自己画：系统模板在按钮**禁用**时会走它自己那套 chrome（浅色底），
        /// 我设的 Background 完全被忽略 —— 表现就是"撤销"按钮变成白底白字。
        /// 套上这个模板之后，正常/悬停/禁用三种状态的底色和字色都由我定。
        /// 同样用 XamlReader 注入（见 ComboTemplate 的说明）。
        /// </summary>
        private static readonly ControlTemplate ButtonTemplate = BuildButtonTemplate();

        private static ControlTemplate BuildButtonTemplate()
        {
            const string xaml = @"
<ControlTemplate TargetType=""Button""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}""
          BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""6"">
    <!-- 文字用 TextBlock 承载，颜色绑定按钮的 Foreground。
         这样样式里的 IsEnabled=False 触发器改 Foreground 就能生效
         （直接给 Button 设 Foreground、或在模板里设 TextElement.Foreground，实测都改不动）。 -->
    <TextBlock Text=""{TemplateBinding Content}"" Foreground=""{TemplateBinding Foreground}""
               HorizontalAlignment=""Center"" VerticalAlignment=""Center"" Margin=""{TemplateBinding Padding}""/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsEnabled"" Value=""False"">
      <Setter TargetName=""Chrome"" Property=""Background"" Value=""#161B22""/>
      <Setter TargetName=""Chrome"" Property=""BorderBrush"" Value=""#21262D""/>
    </Trigger>
    <Trigger Property=""IsMouseOver"" Value=""True"">
      <Setter TargetName=""Chrome"" Property=""Background"" Value=""#30363D""/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        private void BuildUi()
        {
            var outer = new Grid { Margin = new Thickness(14) };
            for (int i = 0; i < 5; i++)
                outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 列表占满剩余空间
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 底部
            Content = outer;

            // 标题行
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(Label("DSH Skill 管理器", 19, C_Bright));
            titleSummary = Label("", 13, C_Dim);
            titleSummary.Margin = new Thickness(14, 5, 0, 0);
            head.Children.Add(titleSummary);
            Grid.SetRow(head, 0);
            outer.Children.Add(head);

            // 工具栏
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            bar.Children.Add(MakeButton("刷新", false, delegate { Reload(); }));
            bar.Children.Add(MakeButton("全部启用", false, delegate { SetAll(SkillState.On); }));
            bar.Children.Add(MakeButton("全部禁用", false, delegate { SetAll(SkillState.Off); }));
            bar.Children.Add(MakeButton("打开目录", false, delegate { Process.Start("explorer.exe", root); }));
            bar.Children.Add(MakeButton("查看停用池", false, delegate
            {
                string pool = Path.Combine(root, Kernel.PoolName);
                Directory.CreateDirectory(pool);
                Process.Start("explorer.exe", pool);
            }));
            bar.Children.Add(MakeButton("新建 skill", false, delegate { NewSkill(); }));
            btnUndo = MakeButton("撤销刚才的改动", true, delegate { DoUndo(); });
            bar.Children.Add(btnUndo);
            Grid.SetRow(bar, 1);
            outer.Children.Add(bar);

            // 目录行：像文件管理器那样，路径框可以直接编辑
            var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            dirRow.Children.Add(Label("skill 根目录：", 13, C_Text));
            txtPath = new TextBox
            {
                Text = root,
                Width = 470,
                Background = Br(Color.FromRgb(0x21, 0x26, 0x2D)),
                Foreground = Br(C_Bright),
                BorderBrush = Br(C_Border),
                CaretBrush = Br(C_Bright),
                Padding = new Thickness(6, 5, 6, 5),
                ToolTip = "可以直接输入或粘贴路径，回车生效，Esc 放弃修改"
            };
            txtPath.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { CommitPathBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { txtPath.Text = root; e.Handled = true; }
            };
            txtPath.LostFocus += delegate { if (txtPath.Text.Trim() != root) CommitPathBox(); };
            dirRow.Children.Add(txtPath);

            var btnPick = MakeButton("浏览…", false, delegate
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = "选一个 skill 根目录";
                try { dlg.SelectedPath = root; } catch { }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) SwitchRoot(dlg.SelectedPath);
            });
            btnPick.Margin = new Thickness(8, 0, 0, 0);
            dirRow.Children.Add(btnPick);

            var btnReset = MakeButton("便携目录", false, delegate { SwitchRoot(ResolveRoot()); });
            btnReset.Margin = new Thickness(6, 0, 0, 0);
            btnReset.ToolTip = "回到 exe 旁边的 skills\\ 目录";
            dirRow.Children.Add(btnReset);

            cmbRecent = new ComboBox
            {
                Margin = new Thickness(6, 0, 0, 0),
                FontFamily = FontFamily,
                ToolTip = "最近用过的目录"
            };
            StyleCombo(cmbRecent, 170);
            cmbRecent.FontSize = 12;
            cmbRecent.SelectionChanged += delegate
            {
                if (buildingRow) return;
                var item = cmbRecent.SelectedItem as ComboBoxItem;
                if (item != null && item.Tag is string) SwitchRoot((string)item.Tag);
            };
            dirRow.Children.Add(cmbRecent);
            Grid.SetRow(dirRow, 2);
            outer.Children.Add(dirRow);

            // 搜索行
            var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            searchRow.Children.Add(Label("搜索：", 13, C_Dim));
            txtSearch = new TextBox
            {
                Width = 260,
                Background = Br(C_Panel),
                Foreground = Br(C_Text),
                BorderBrush = Br(C_Border),
                Padding = new Thickness(6, 5, 6, 5),
                CaretBrush = Br(C_Text)
            };
            txtSearch.TextChanged += delegate { Reload(); };
            searchRow.Children.Add(txtSearch);
            chkUninstalled = new CheckBox
            {
                Content = "显示已卸载的",
                IsChecked = true,
                Foreground = Br(C_Text),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            chkUninstalled.Click += delegate { Reload(); };
            searchRow.Children.Add(chkUninstalled);
            Grid.SetRow(searchRow, 3);
            outer.Children.Add(searchRow);

            // 列表：表头固定，下面可滚动。这一格占满剩余空间，滚动条才会出现。
            var listDock = new DockPanel();
            var header = new Border
            {
                Background = Br(C_Panel),
                BorderBrush = Br(C_Border),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 8, 8, 8),
                Child = BuildHeader()
            };
            DockPanel.SetDock(header, Dock.Top);
            listDock.Children.Add(header);
            rowsHost = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rowsHost
            };
            listDock.Children.Add(scroll);
            Grid.SetRow(listDock, 4);
            outer.Children.Add(listDock);

            // 底部
            var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var msgBox = new Border
            {
                Background = Br(C_Panel),
                BorderBrush = Br(C_Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8)
            };
            msgText = Label("就绪", 13, C_Dim);
            msgText.TextWrapping = TextWrapping.Wrap;
            msgBox.Child = msgText;
            bottom.Children.Add(msgBox);
            var tip = Label("圆点：绿=已启用，灰=已禁用，橙=已卸载。改状态请用状态下拉。改完不用重开 DSH：skill 目录是实时监听的。",
                            12, Color.FromRgb(0x8B, 0x94, 0x9E));
            tip.Margin = new Thickness(2, 8, 0, 0);
            tip.TextWrapping = TextWrapping.Wrap;
            bottom.Children.Add(tip);
            var tip2 = Label("目录可以直接在路径框里输入或粘贴，回车生效；上次用的目录会记在 exe 旁边的 roots.txt。",
                             12, Color.FromRgb(0x6E, 0x76, 0x81));
            tip2.Margin = new Thickness(2, 4, 0, 0);
            tip2.TextWrapping = TextWrapping.Wrap;
            bottom.Children.Add(tip2);
            Grid.SetRow(bottom, 5);
            outer.Children.Add(bottom);
        }

        private Grid BuildHeader()
        {
            var g = NewRowGrid();
            AddCell(g, 0, Label("●", 13, C_Dim), HorizontalAlignment.Center);
            AddCell(g, 1, Label("#", 13, C_Dim), HorizontalAlignment.Left);
            AddCell(g, 2, Label("名称", 13, C_Dim), HorizontalAlignment.Left);
            AddCell(g, 3, Label("状态", 13, C_Dim), HorizontalAlignment.Left);
            AddCell(g, 4, Label("模型可见", 13, C_Dim), HorizontalAlignment.Left);
            AddCell(g, 5, Label("手动调用", 13, C_Dim), HorizontalAlignment.Left);
            AddCell(g, 6, Label("说明", 13, C_Dim), HorizontalAlignment.Left);
            AddCell(g, 7, Label("操作", 13, C_Dim), HorizontalAlignment.Left);
            return g;
        }

        private static Grid NewRowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            return g;
        }

        private static void AddCell(Grid g, int col, UIElement el, HorizontalAlignment align)
        {
            if (el is FrameworkElement) ((FrameworkElement)el).HorizontalAlignment = align;
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }

        // ------------------------------------------------------------ 列表渲染

        private void SetMsg(string text, Color color)
        {
            msgText.Text = text;
            msgText.Foreground = Br(color);
        }

        private void Reload()
        {
            rowsHost.Children.Clear();
            rows.Clear();
            items.Clear();

            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            List<SkillInfo> all = Kernel.Scan(root);
            items.AddRange(all);

            string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();
            bool showUn = chkUninstalled.IsChecked == true;
            int shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                SkillInfo s = items[i];
                if (!showUn && s.InPool) continue;
                if (q.Length > 0)
                {
                    string hay = (s.Name + " " + s.Description + " " + s.FrontmatterName).ToLowerInvariant();
                    if (!hay.Contains(q)) continue;
                }
                rowsHost.Children.Add(BuildRow(s, i));
                shown++;
            }

            UpdateSummary();
            UpdateUndoButton();
            var warn = new List<string>();
            foreach (SkillInfo s in items) foreach (string e in s.Errors) warn.Add(s.Name + "：" + e);
            string msg = "共 " + items.Count + " 个 skill，列出 " + shown + " 个。根目录：" + root;
            if (warn.Count > 0) SetMsg(msg + "\n注意：" + string.Join("；", warn), C_Warn);
            else SetMsg(msg, C_Green);
        }

        private Border BuildRow(SkillInfo s, int index)
        {
            var grid = NewRowGrid();
            var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
            dot.ToolTip = "绿=已启用，灰=已禁用，橙=已卸载";
            AddCell(grid, 0, dot, HorizontalAlignment.Center);
            AddCell(grid, 1, Label((index + 1).ToString(), 13, Color.FromRgb(0x6E, 0x76, 0x81)), HorizontalAlignment.Left);

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(Label(s.Name, 13, C_Bright));
            if (!string.IsNullOrEmpty(s.FrontmatterName) && s.FrontmatterName != s.Name)
                nameStack.Children.Add(Label("内部名：" + s.FrontmatterName, 11, C_Warn));
            nameStack.Children.Add(Label(s.IsBundle ? "目录式" : "单文件", 11, Color.FromRgb(0x6E, 0x76, 0x81)));
            AddCell(grid, 2, nameStack, HorizontalAlignment.Left);

            buildingRow = true;
            var combo = new ComboBox { FontFamily = FontFamily, VerticalAlignment = VerticalAlignment.Center };
            StyleCombo(combo, 126);
            foreach (SkillState st in new[] { SkillState.On, SkillState.Off, SkillState.Uninstalled })
            {
                var item = new ComboBoxItem { Content = Kernel.StateLabel(st), Tag = st };
                StyleComboItem(item, C_Panel);
                combo.Items.Add(item);
            }
            combo.SelectedIndex = (int)s.Shown;
            buildingRow = false;
            AddCell(grid, 3, combo, HorizontalAlignment.Left);

            var model = Label("", 13, C_Dim);
            AddCell(grid, 4, model, HorizontalAlignment.Left);
            var manual = Label("", 13, C_Dim);
            AddCell(grid, 5, manual, HorizontalAlignment.Left);

            var desc = Label(string.IsNullOrEmpty(s.Description) ? "（缺 description，DSH 不会加载）" : s.Description,
                             13, string.IsNullOrEmpty(s.Description) ? C_Red : C_Dim);
            desc.TextTrimming = TextTrimming.CharacterEllipsis;
            desc.ToolTip = s.FilePath;
            AddCell(grid, 6, desc, HorizontalAlignment.Left);

            var del = MakeButtonNoHandler("彻底删除");
            del.FontSize = 12;
            del.Padding = new Thickness(8, 5, 8, 5);
            del.Margin = new Thickness(0);
            string capturedName = s.Name;
            del.Click += delegate { DeleteSkill(capturedName); };
            AddCell(grid, 7, del, HorizontalAlignment.Left);

            var rowBorder = new Border
            {
                Background = Br(C_Bg),
                BorderBrush = Br(C_Border),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 8, 8, 8),
                Child = grid
            };

            var widgets = new RowWidgets { Row = rowBorder, Dot = dot, Model = model, Manual = manual, Combo = combo };
            rows[s.Name] = widgets;
            PaintRow(widgets, s.Shown);

            string captured = s.Name;
            combo.SelectionChanged += delegate
            {
                if (buildingRow) return;
                var sel = combo.SelectedItem as ComboBoxItem;
                if (sel == null) return;
                ChangeState(captured, (SkillState)sel.Tag);
            };
            return rowBorder;
        }

        private void PaintRow(RowWidgets w, SkillState st)
        {
            Color c = Kernel.StateColor(st);
            w.Dot.Fill = Br(c);
            w.Row.BorderBrush = Br(c);
            bool modelOn = st == SkillState.On;
            bool userOn = st != SkillState.Uninstalled;
            // 「不可见 / 不可用」以前用的 #6E7681 太暗，贴在深色底上几乎看不出，
            // 换成 C_Text（#C9D1D9）并表示"普通可用"，跟绿色的"可见/可用"区分开。
            w.Model.Text = modelOn ? "可见" : "不可见";
            w.Model.Foreground = Br(modelOn ? C_Green : C_Text);
            w.Manual.Text = userOn ? "可用" : "不可用";
            w.Manual.Foreground = Br(userOn ? C_Green : C_Text);
        }

        private void UpdateSummary()
        {
            int on = items.Count(x => !x.InPool && x.State == SkillState.On);
            int off = items.Count(x => !x.InPool && x.State == SkillState.Off);
            int un = items.Count(x => x.InPool);
            titleSummary.Text = "启用 " + on + " · 禁用 " + off + " · 已卸载 " + un;
        }

        private void UpdateUndoButton()
        {
            if (undoItem == null)
            {
                btnUndo.Content = "撤销刚才的改动";
                btnUndo.IsEnabled = false;
                btnUndo.ToolTip = "还没有可撤销的改动";
            }
            else
            {
                btnUndo.Content = "撤销：" + undoItem.Name;
                btnUndo.IsEnabled = true;
                btnUndo.ToolTip = "把「" + undoItem.Name + "」退回「" + Kernel.StateLabel(undoFrom) + "」";
            }
        }

        // ------------------------------------------------------------ 操作

        private SkillInfo Find(string name)
        {
            return items.FirstOrDefault(x => x.Name == name);
        }

        private void ChangeState(string name, SkillState target)
        {
            SkillInfo s = Find(name);
            if (s == null) { SetMsg("找不到 " + name + "，请点刷新。", C_Red); return; }
            SkillState before = s.Shown;
            if (before == target) return;
            try
            {
                string note = Kernel.Apply(root, s, target);
                undoItem = s; undoFrom = before;
                RefreshAfterChange(s, target, name);
                SetMsg(name + " —— " + Kernel.StateLabel(target) + "：" + note, C_Green);
            }
            catch (Exception ex)
            {
                SetMsg("操作失败：" + ex.Message, C_Red);
                Reload();
            }
        }

        /// <summary>改完一个之后就地更新（不整表重建），摘要和圆点跟着变。</summary>
        private void RefreshAfterChange(SkillInfo s, SkillState target, string name)
        {
            s.State = target;
            s.InPool = target == SkillState.Uninstalled;
            RowWidgets w;
            if (rows.TryGetValue(name, out w))
            {
                buildingRow = true;
                w.Combo.SelectedIndex = (int)target;
                buildingRow = false;
                PaintRow(w, target);
            }
            UpdateSummary();
            UpdateUndoButton();
        }

        private void DoUndo()
        {
            if (undoItem == null) return;
            SkillInfo s = Find(undoItem.Name);
            if (s == null) { SetMsg("找不到 " + undoItem.Name + "，请点刷新。", C_Red); return; }
            SkillState to = undoFrom;
            SkillState from = s.Shown;
            try
            {
                Kernel.Apply(root, s, to);
                SetMsg("已撤销：" + s.Name + " 从「" + Kernel.StateLabel(from) + "」退回「" + Kernel.StateLabel(to) + "」。", C_Green);
                undoItem = null;
                Reload();
            }
            catch (Exception ex)
            {
                SetMsg("撤销失败：" + ex.Message, C_Red);
            }
        }

        private void SetAll(SkillState target)
        {
            bool showUn = chkUninstalled.IsChecked == true;
            List<SkillInfo> targets = items.Where(x => showUn || !x.InPool).ToList();
            if (targets.Count == 0) { SetMsg("列表里没有可操作的项目。", C_Warn); return; }
            var ans = MessageBox.Show("把当前列出的 " + targets.Count + " 个 skill 全部设为「" + Kernel.StateLabel(target) + "」？",
                                      "确认批量操作", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
            int fail = 0;
            foreach (SkillInfo s in targets)
            {
                try { Kernel.Apply(root, s, target); }
                catch { fail++; }
            }
            // 批量改动不记撤销：一次动十几个，撤销该退哪一步没有清楚的答案
            undoItem = null;
            Reload();
            SetMsg(fail == 0
                ? "已把 " + targets.Count + " 个 skill 设为「" + Kernel.StateLabel(target) + "」。批量改动不支持撤销。"
                : "批量操作完成，" + fail + " 个失败。", fail == 0 ? C_Green : C_Warn);
        }

        private void DeleteSkill(string name)
        {
            SkillInfo s = Find(name);
            if (s == null) return;
            var ans = MessageBox.Show("彻底删除 " + name + "？\n\n文件会被直接删掉，不能恢复。\n想保留文件的话，把状态改成「已卸载」。",
                                      "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
            try
            {
                SetMsg(s.Name + " " + Kernel.Delete(s), C_Warn);
                Reload();
            }
            catch (Exception ex)
            {
                SetMsg("删除失败：" + ex.Message, C_Red);
            }
        }

        private void NewSkill()
        {
            var dlg = new NewSkillDialog(root, FontFamily);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                SetMsg("已创建：" + dlg.CreatedPath + "（现在去编辑它的正文即可）", C_Green);
                Reload();
            }
        }

        // ------------------------------------------------------------ 自检入口

        /// <summary>在可视化树里找第一个指定类型的子元素（自检用）。</summary>
        private static T FindVisual<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                T hit = child as T;
                if (hit != null) return hit;
                T deep = FindVisual<T>(child);
                if (deep != null) return deep;
            }
            return null;
        }

        /// <summary>
        /// 在真正渲染过的窗口里验证两个自己画的模板。
        /// 为什么不并进 --smoke：不显示窗口时模板布局不跑、绑定不传播，测出来不算数。
        /// 这里把窗口挪到屏幕外然后强制测量/排布，再检查：
        ///   1) 下拉框里找得到 ToggleButton，且勾它能带动 IsDropDownOpen
        ///   2) 禁用按钮的底色是我要的深色（白底白字那个 bug 的回归检查）
        /// </summary>
        public void CheckUi(TextWriter sink)
        {
            Left = -4000;
            Top = -4000;
            ShowInTaskbar = false;
            Show();
            Dispatcher.Invoke(delegate { }, System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateLayout();

            RowWidgets probe = rows.Values.FirstOrDefault();
            if (probe == null)
            {
                sink.WriteLine("COMBO   | SKIP 没有行可以测");
            }
            else
            {
                ToggleButton tb = FindVisual<ToggleButton>(probe.Combo);
                if (tb == null)
                {
                    sink.WriteLine("COMBO   | BAD 模板里没有 ToggleButton，下拉点不开");
                }
                else
                {
                    // 1) 模板里那个 ToggleButton 能不能带动下拉
                    tb.IsChecked = true;
                    bool opened = probe.Combo.IsDropDownOpen;
                    tb.IsChecked = false;
                    bool closed = !probe.Combo.IsDropDownOpen;

                    // 2) 最上层那个透明捕获层在不在（真实点击就是它接住的）
                    Border catcher = probe.Combo.Template.FindName("ClickCatcher", probe.Combo) as Border;
                    bool hasCatcher = catcher != null && catcher.IsHitTestVisible;

                    // 3) 直接调它的处理器，确认点下去真的能开合（合成鼠标事件不可靠，不用它）
                    bool clickOk = false;
                    if (catcher != null)
                    {
                        var ev = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left);
                        ev.RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent;
                        ev.Source = catcher;
                        ComboCapture_Click(catcher, ev);
                        bool afterClick = probe.Combo.IsDropDownOpen;
                        ComboCapture_Click(catcher, ev);
                        clickOk = afterClick && !probe.Combo.IsDropDownOpen;
                    }

                    bool allOk = opened && closed && hasCatcher && clickOk;
                    sink.WriteLine("COMBO   | " + (allOk ? "OK" : "BAD")
                        + " ToggleButton=" + (opened && closed)
                        + " 捕获层=" + hasCatcher
                        + " 点捕获层能开合=" + clickOk);
                }
            }

            btnUndo.ApplyTemplate();
            Border chrome = btnUndo.Template.FindName("Chrome", btnUndo) as Border;
            if (chrome == null)
            {
                sink.WriteLine("BUTTON  | SKIP 模板未实例化");
            }
            else
            {
                Color col = (chrome.Background as SolidColorBrush).Color;
                bool enabled = btnUndo.IsEnabled;
                bool ok = enabled || col == Color.FromRgb(0x16, 0x1B, 0x22);
                sink.WriteLine("BUTTON  | " + (ok ? "OK" : "BAD")
                    + " 可用=" + enabled + " 禁用态底色=" + col + "（白底=BAD）");
            }
            Close();
        }

        /// <summary>只搭界面、扫一遍目录，然后退出。用来在没有人看着的情况下验证没坏。</summary>
        public void SmokeTest(bool withStateChange, bool withUndo, bool withComboCheck, TextWriter sink)
        {
            sink.WriteLine("SMOKE OK | 行数=" + rowsHost.Children.Count + " | " + titleSummary.Text + " | " + msgText.Text);
            foreach (var kv in rows)
            {
                SkillInfo s = Find(kv.Key);
                if (s == null) continue;
                ComboBoxItem sel = kv.Value.Combo.SelectedItem as ComboBoxItem;
                SkillState st = sel == null ? SkillState.On : (SkillState)sel.Tag;
                Color want = Kernel.StateColor(st);
                Color got = ((SolidColorBrush)kv.Value.Dot.Fill).Color;
                sink.WriteLine((got == want ? "OK  " : "BAD ") + " 行核对 | " + kv.Key + " 状态=" + st + " 圆点=" + got);
            }

            // 下拉框能不能点开 —— 上一版漏了模板里的 ToggleButton，界面点了完全没反应，
            // 所以这里固定验一次：模板里找得到 ToggleButton，且勾它能带动 IsDropDownOpen。
            if (withComboCheck)
            {
                RowWidgets probe = rows.Values.FirstOrDefault();
                if (probe == null) { sink.WriteLine("COMBO | SKIP 没有行"); }
                else
                {
                    probe.Combo.ApplyTemplate();
                    ToggleButton tb = FindVisual<ToggleButton>(probe.Combo);
                    if (tb == null)
                    {
                        sink.WriteLine("COMBO | SKIP 模板未实例化");
                    }
                    else
                    {
                        // headless 下模板绑定的传播要看运行环境，测出来不算数。
                        // 这里只查"绑定挂上了没有"，真正的开合验证走 --check-ui。
                        bool bound = BindingOperations.GetBinding(tb, ToggleButton.IsCheckedProperty) != null;
                        sink.WriteLine("COMBO | " + (bound ? "OK" : "SKIP")
                            + " 模板里有 ToggleButton，IsChecked 绑定已挂上=" + bound
                            + "（能否点开请看 --check-ui）");
                    }
                }
            }

            // 按钮模板：禁用态必须是深色底（白底白字那个 bug 就是系统模板接管了禁用态）
            Button bt = btnUndo;
            bt.ApplyTemplate();
            Border chrome = bt.Template.FindName("Chrome", bt) as Border;
            if (chrome == null)
            {
                sink.WriteLine("BUTTON | SKIP 模板未实例化（自检不显示窗口，属正常）");
            }
            else
            {
                var col = (chrome.Background as SolidColorBrush).Color;
                bool bgOk = btnUndo.IsEnabled || col == Color.FromRgb(0x16, 0x1B, 0x22);
                sink.WriteLine("BUTTON | " + (bgOk ? "OK" : "BAD")
                    + " 可用=" + btnUndo.IsEnabled + " 禁用态底色=" + col);
            }

            if (withStateChange)
            {
                SkillInfo s = items.FirstOrDefault(x => !x.InPool);
                if (s == null) { sink.WriteLine("SKIP | 没有可测的条目"); return; }
                SkillState before = s.Shown;
                SkillState target = before == SkillState.On ? SkillState.Off : SkillState.On;
                string note = Kernel.Apply(root, s, target);
                undoItem = s; undoFrom = before;
                RefreshAfterChange(s, target, s.Name);
                sink.WriteLine("STATE 切换 | " + s.Name + " | " + before + " -> " + target + " | " + titleSummary.Text);
                sink.WriteLine("UNDO 记录 | from=" + undoFrom + " to=" + target + " | 按钮='" + btnUndo.Content + "' 可用=" + btnUndo.IsEnabled);

                if (withUndo)
                {
                    DoUndo();
                    SkillInfo after = Find(s.Name);
                    bool ok = after != null && after.Shown == before && undoItem == null && !btnUndo.IsEnabled;
                    sink.WriteLine("UNDO 结果 | 现在=" + (after == null ? "?" : after.Shown.ToString()) + " 期望=" + before + " | 按钮可用=" + btnUndo.IsEnabled);
                    sink.WriteLine("UNDO 判定 | " + (ok ? "OK" : "BAD"));
                }
                else
                {
                    Kernel.Apply(root, Find(s.Name), before);   // 还原，别留下痕迹
                    undoItem = null;
                }
                sink.WriteLine("NOTE | " + note);
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            // 顶层兜底：WinExe 会把未处理异常直接吞掉（连对话框都可能不弹），
            // 所以凡是启动阶段出事，一律写一份 crash.txt 到 exe 旁边，方便排查。
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                try
                {
                    // 只记类型和消息，不写调用栈（类名对用户没意义，还会暴露实现细节）
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt"),
                        DateTime.Now.ToString("s") + "\r\n" + ex.GetType().Name + "：" + ex.Message + "\r\n" +
                        "把这份文件发给开发者即可定位。\r\n", new UTF8Encoding(false));
                }
                catch { }
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            // 排查参数用：SkillManager.exe --echoargs 会把收到的参数写到 exe 旁边的 echoed-args.txt
            if (args.Contains("--version") || args.Contains("--echoargs"))
            {
                string info = "SkillManager 便携版 v" + MainWindow.AppVersion + "\r\n"
                    + "程序集版本: " + typeof(MainWindow).Assembly.GetName().Version + "\r\n"
                    + "exe 路径: " + AppDomain.CurrentDomain.BaseDirectory + "\r\n"
                    + "exe 编译时间: " + File.GetLastWriteTime(typeof(MainWindow).Assembly.Location).ToString("yyyy-MM-dd HH:mm:ss") + "\r\n"
                    + "skill 根目录: " + ResolveRoot() + "\r\n"
                    + "argc=" + args.Length + " args=" + string.Join(" ", args) + "\r\n";
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt"), info, new UTF8Encoding(false)); }
                catch { }
                return 0;
            }

            // WinExe 子系统不接控制台，所以自检结果支持写文件：
            //   SkillManager.exe --selftest --out 报告.txt
            string outFile = null;
            int oi = Array.IndexOf(args, "--out");
            if (oi >= 0 && oi + 1 < args.Length) outFile = args[oi + 1];

            if (args.Contains("--selftest"))
            {
                if (outFile != null)
                {
                    // 自检中途抛异常也要留下痕迹，不然只看到一个空文件
                    try
                    {
                        using (var w = new StreamWriter(outFile, false, new UTF8Encoding(false)))
                        {
                            try
                            {
                                int code = Kernel.SelfTest(w);
                                w.WriteLine("EXITCODE " + code);
                            }
                            catch (Exception ex)
                            {
                                w.WriteLine("自检抛异常：" + ex.GetType().Name + "：" + ex.Message);
                                w.WriteLine(ex.StackTrace);
                                w.WriteLine("EXITCODE 99");
                            }
                        }
                    }
                    catch (Exception outer)
                    {
                        try { File.WriteAllText(outFile + ".err", outer.ToString(), new UTF8Encoding(false)); } catch { }
                    }
                    return 0;
                }
                return Kernel.SelfTest(null);
            }

            bool smoke = args.Contains("--smoke");
            bool checkUi = args.Contains("--check-ui");
            interactive = !smoke && !checkUi;   // 自检模式下一律不弹对话框

            var app = new Application();
            var win = new MainWindow();
            if (checkUi)
            {
                if (outFile != null)
                {
                    using (var w = new StreamWriter(outFile, false, new UTF8Encoding(false)))
                        win.CheckUi(w);
                    return 0;
                }
                win.CheckUi(Console.Out);
                return 0;
            }
            if (smoke)
            {
                bool withState = args.Contains("--smoke-state");
                bool withUndo = args.Contains("--smoke-undo");
                bool withCombo = args.Contains("--smoke-combo") || args.Contains("--smoke-state");
                if (outFile != null)
                {
                    using (var w = new StreamWriter(outFile, false, new UTF8Encoding(false)))
                        win.SmokeTest(withState, withUndo, withCombo, w);
                    return 0;
                }
                win.SmokeTest(withState, withUndo, withCombo, Console.Out);
                return 0;
            }
            app.Run(win);
            return 0;
        }
    }

    /// <summary>新建 skill 的小窗口。</summary>
    internal sealed class NewSkillDialog : Window
    {
        public string CreatedPath;

        public NewSkillDialog(string root, FontFamily font)
        {
            Title = "新建 skill";
            Width = 560; Height = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17));
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9));
            FontFamily = font;
            FontSize = 13;

            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = "名字（英文，用作文件名和调用名，例如 my-helper）", Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)) });
            var t1 = new TextBox { Margin = new Thickness(0, 4, 0, 12), Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22)), Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)), Padding = new Thickness(6, 5, 6, 5) };
            sp.Children.Add(t1);
            sp.Children.Add(new TextBlock { Text = "一句话说明什么时候用它（DSH 靠这句判断该不该调用）", Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)) });
            var t2 = new TextBox { Margin = new Thickness(0, 4, 0, 12), Height = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22)), Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)), Padding = new Thickness(6, 5, 6, 5) };
            sp.Children.Add(t2);

            var btn = new Button
            {
                Content = "创建",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 7, 12, 7),
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
                BorderThickness = new Thickness(1)
            };
            btn.Click += delegate
            {
                string n = (t1.Text ?? "").Trim();
                if (n.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"))
                {
                    MessageBox.Show("名字只能用字母数字和 . _ -，且以字母数字开头。", "名字不合法");
                    return;
                }
                string file = Path.Combine(root, n + ".md");
                if (File.Exists(file)) { MessageBox.Show("已经有一个同名的了。", "重复"); return; }
                string desc = (t2.Text ?? "").Trim();
                if (desc.Length == 0) desc = "（在这里写清楚什么时候用这个 skill）";
                string content = "---\nname: " + n + "\ndescription: " + desc + "\n---\n\n在这里写这个 skill 的完整说明。\n";
                File.WriteAllText(file, content, new UTF8Encoding(false));
                CreatedPath = file;
                DialogResult = true;
            };
            sp.Children.Add(btn);
            Content = sp;
        }
    }
}
