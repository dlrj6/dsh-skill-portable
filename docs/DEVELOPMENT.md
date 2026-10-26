# 开发笔记

想改这份代码，先看这一页。里面记的都是「看着奇怪、但动了就会坏」的地方。

## 架构

```
MainWindow（界面，WPF 代码手搭）
      │  所有读写都往下调
      ▼
Kernel（内核，static）
      │
      ├─ Scan(root)            扫目录，认出单文件 / 目录式 / 停用池
      ├─ DetectState(keys)     三个状态怎么认
      ├─ ApplyState(text, st)  把状态写回 frontmatter
      ├─ Apply(item, st)       改状态 + 必要时在根目录和 .disabled 之间移动
      ├─ Delete(item)          真删
      └─ SelfTest(writer)      32 项断言
```

界面不直接碰文件——「什么算启用」只有 `Kernel` 一处实现，界面只负责显示和转发。
改行为优先改 `Kernel`，改外观优先改 `MainWindow`，两边别互相渗透。

## 三种状态 → 文件的映射

| 界面 | frontmatter | 位置 |
| --- | --- | --- |
| 已启用 | 两个键都不写 | 根目录 |
| 已禁用 | `disable-model-invocation: true` | 根目录 |
| 已卸载 | 两个键都写（`disable-model-invocation: true` + `user-invocable: false`） | `根目录\.disabled\` |

状态判断是把两个键一起看：只写了 `disable-model-invocation: true` 算「已禁用」；两个都关才算「已卸载」。
「已卸载」同时写两个键，是为了文件被手动挪回根目录时也不会突然被模型调用。

## 写文件的两个硬约束

1. **原子写**：先写同目录的临时文件，再整体替换；中途断电不会留下半截 skill 文件。
2. **编码保真**：读出来记下有没有 BOM，写回去照原样；只动 frontmatter 里那两行，正文、注释、其它键一律不碰。
   frontmatter 删行/加行后行号会错位——所以实现里是「按块重写整个 frontmatter」，不是按行号去改。

## WPF 的坑（都是真摔过的）

### 1. 用 `FrameworkElementFactory` 搭 ComboBox 的 ControlTemplate 会栈溢出

进程退出码 `0xc00000fd`，WPF 直接死，`catch` 抓不到，`crash.txt` 都写不出来（只能去事件日志看 CLR20r3）。
换 `RelativeSource(TemplatedParent)` → `FindAncestor`、删 `ItemContainerStyle`、删整个 `Popup`，都还是崩。

**唯一可靠写法**：模板写成 XAML 字符串，交给 `XamlReader.Parse`。别再改回代码构建。
（模板里不能写事件名——`XamlReader` 解析时没有代码后置类，处理器必须解析后在代码里挂。）

### 2. 自绘 ComboBox 必须包含 `ToggleButton`

`ToggleButton` 才是切换 `IsDropDownOpen` 的部件。漏了它，框体就是纯装饰品，点了毫无反应。
即使有 `ToggleButton`，命中测试仍可能落在空白处：模板最上层再铺一个 `Background="Transparent"` 的 `Border`（`x:Name="ClickCatcher"`），
在 `Loaded` 里挂 `PreviewMouseLeftButtonDown` 直接开合。

### 3. 按钮禁用态会走系统 chrome

设 `Background` 无效，表现是白底白字。必须自绘 `ButtonTemplate`；
字色要用 `TextBlock Text="{TemplateBinding Content}" Foreground="{TemplateBinding Foreground}"` 承载，
再靠样式里的 `IsEnabled=False` 触发器改 `Foreground` 才生效（直接设 `Foreground` 或 `TextElement.Foreground` 都改不动）。

### 4. headless 测不了模板

`--smoke` 不显示窗口时模板布局不跑、绑定不传播，在那儿测下拉永远得到假结果。
所以有 `--check-ui`：把窗口挪到屏幕外、`Show()` + `UpdateLayout()` 之后再检查，才靠得住。

### 5. 改完界面只刷新受影响的那一行

整表重建会拆掉正在提交选择的控件，直接导致渲染坏掉（早期「下拉框文字消失」就是这么来的）。
`RefreshAfterChange` 只重画一行。

## 根目录解析顺序

`roots.txt` → `portable.txt` → `exe\skills\` → `exe 所在目录`。
`roots.txt` 是界面写的「上次选的」，`portable.txt` 是用户手写的「固定默认值」，两者分工不同，别合并。
路径按 UTF-8 读写；界面路径框支持回车切换 / Esc 放弃，切到不存在的目录会问要不要创建。

## 自检模式

- `--selftest`：纯内核，临时目录里造 skill、切状态、断言（32 项），不碰真实目录。
- `--smoke`：把界面搭起来、扫一遍目录，验证行渲染/状态切换/撤销。
- `--check-ui`：真正渲染（屏幕外），验证两个自绘模板。
- 全部支持 `--out 文件`；GUI 子系统没有 stdout，必须写文件。

新增行为时，优先补 `Kernel.SelfTest` 里的断言——它跑起来没有副作用，最适合当回归测试。

## 编译

`src\build.cmd` 用系统自带 `csc.exe`（.NET Framework 4.x），不依赖 VS / SDK。
两个不能改的约定：

- **build.cmd 保持纯 ASCII、编译命令一行**。cmd.exe 对中文（编码）和脱字符续行都不可靠。
- 源码里中文很多，编译加 `/codepage:65001`；否则在英文系统上可能按 ANSI 读坏。

`src\app.ico` 是可选的（构建脚本里有 `if exist` 判断），删了照样能编。
