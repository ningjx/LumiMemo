# UI 主题轮：按钮 / 图标 / 动效统一（设计稿草案）

> 状态：**草案待评审**（2026-10-04）——图标四项已拍板且**已落地**（§1 实施记录，待实机验收）；
> 其余仍待评审。本轮只做调研、决策与图标落地，未动其它代码。
> 上游：`phase4-old-kernel-retirement-design.md` §0「不做」与末尾遗留（「本稿完成后，Phase 4 只剩
> UI 毛玻璃那一轮」）。
>
> **本稿 = 「UI 毛玻璃那一轮」剩下的部分。** 交代一下这个代号，免得后来人以为它指"给窗口加玻璃"：
> 玻璃（`DesktopAcrylicController`，失焦不降级）**早就做了**，而且是硬约束——它是当初从 WPF 迁到
> WinUI 的根本原因（`custom-renderer-framework.md` §约束），渲染器全程为它让路（只走 Composition
> surface，不引 WebView2；Phase 0 S1/H2 就是专门验收它）。
> 「那一轮」是 2026-09-19（**还在 WPF 时**）定下的**视觉总收口**代号：当时的界面是扁平淡色 + 标准
> chrome，目标是"整体大改成透明毛玻璃"，并**排在功能之后**；同日追加拍板"**深色模式先不做，一切样式
> 内容统推到这一轮**"——理由是深色模式要定的正是**纸面在暗色下是什么颜色**，而毛玻璃之后纸面本身
> 要重新定值，先做一套深色变体到时候要整套重来。当时记下的待定坑也一并留到现在：**半透明的纸 ×
> 每张便签自己的颜色会打架**（淡黄/淡蓝玻璃压在深色壁纸上几乎分不出区别）→ 颜色要重新定值，
> 不是换个笔刷的事。
> 今天回头看，那一轮欠着的正是：深色模式、配色在玻璃下重定、chrome（标题条/工具栏）外观统一、
> 跨 Win10/11 图标一致 —— 即本稿 §1–§4。
> 本稿落地范围：主程序两个窗口（便签 `MainWindow`、管理器 `ManagerWindow` 及其三页）
> 与 `LumiText.WinUI` 编辑器外观的参数化。

---

## 0. 范围

**目标**：把「按钮」这一层从"各处内联"收敛成一套 **token + 具名样式**；图标从系统字体/文字
换成**开源图标集**（跨 Win10/11 一致）；补齐状态与焦点视觉；给按钮加**统一口径的过渡动效**；
为**深色模式**铺好路（本轮做不做见 §6 待拍板）。

**现状盘点（2026-10-04，实测）**

1. **颜色**：5 个 XAML 里共 **24 个不同硬编码色值**——便签配色 8 + 文字底色 1 + **chrome 15**
   （墨色 `#FF403747` / `#FF75697C`、分隔线 `#5075697C` / `#8075697C`、白色叠加 11 个：
   `10/14/18/20/24/2A/30/40/58/68/78FFFFFF`）。**没有 token、没有 ThemeDictionaries**
   → 深色模式无处可挂。
2. **样式**：便签窗口在 `MainWindow.xaml` 的 `Grid.Resources` 里自带
   `GlassButtonStyle` / `GlassToggleButtonStyle` 与一套 `Button*` 覆盖；`App.xaml` 只有
   `IconButtonStyle`；管理器窗口用后者。同一件事两处定义。
3. **尺寸**：便签标题栏按钮 **32×30**、`IconButtonStyle` **32×28**、工具栏 **30×30**——图标光学对不齐。
4. **图标**：chrome 用 Segoe 字形 6 个（`E72C` 刷新 / `E710` 新建 / `E718` 钉住 / `E74D` 删除 /
   `E8BB` 关闭 / `E713` 设置）；**工具栏全是文字或字符**（`B` `I` `U` `S` `•` `☐` `H1` `H2` `H3`
   + 一个手画的色块 `Border`）。
5. **跨版本不一致**：`FontIcon` 默认走 `SymbolThemeFontFamily`——Win11 是 Segoe Fluent Icons、
   Win10 回退 Segoe MDL2 Assets（字形与覆盖都不同）。README 写明支持 Win10 1809+，这是真实的一致性问题。
6. **主题**：`AcrylicBackdrop` 里 `Theme = Light`、tint `#F4ECFF` 写死（好在只有一处）；
   **渲染器**的墨色 / 选区 / 光标 / 待办框 / 图片手柄 / 尺寸标签 / 拖放指示线全在 `LumiText` 里写死。
7. **动效**：已有滚动条滑出/收起、图片手柄淡入淡出、待办勾选 160ms、图片松手吸附 150–260ms——
   都是我们自己画的；**按钮本身的状态变化是瞬时的**。
8. **便签颜色与窗口脱钩**：8 色调色板现在只出现在**管理器列表**（色条、片段纸面预览）与文字底色
   色板里；**便签窗口本身是统一亚克力**，不随便签颜色变（改色入口也在列表侧）。
   → 2026-10-04 拍板：**接上**（窗口纸面 + 便签内强调色都跟着便签色走），见 §2.2 / D6。

**不做**：编辑器排版与编辑语义（`LumiText.Core` 不动）；功能新增；UIA 无障碍实现（维持「留接口」）；
M5 文字流动画（Phase 3 遗留，另轮，见 §4 末的协同说明）。

---

## 1. 图标方案（决策 D1：Fluent UI System Icons + 官方字体自建）

| 方案 | 许可 | 规模 / 风格 | 结论 |
|---|---|---|---|
| **Fluent UI System Icons**（微软） | **MIT** | 1,500+ 概念，Regular/Filled 两套 + Light/Color 变体；**实心几何**，与 Win11 系统字体同语言 | ✅ **选它**：与系统观感一致、跨 Win10/11 一致、许可证干净 |
| Lucide | ISC | 1,778，24px/**2px 描边** | 备选：是**另一种视觉语言**（描边 vs 实心），混用会花；要换就整套换 |
| Tabler | MIT | 6,143，2px 描边 + filled 变体 | 同上；覆盖度最大 |
| Phosphor | MIT | 9,161（六种字重） | 同上；字重可当设计工具 |
| Segoe Fluent Icons / MDL2 | 专有（系统自带） | 就是现状 | 仅作**回退**（`SymbolThemeFontFamily`），不依赖其新字形 |

**覆盖度实测**（逐个探测官方仓库 SVG 是否存在，全部命中）：**工具栏全部按钮都图标化**（2026-10-04 拍板，
含 `B/I/U/S` 与 `H1–H3`）——即下表全部要用：

| 用途 | 图标名（Fluent） | 备注 |
|---|---|---|
| 粗体 / 斜体 / 下划线 / 删除线 | `TextBold` `TextItalic` `TextUnderline` `TextStrikethrough` | 拍板换成图标（不再用文字） |
| 标题 1/2/3 | `TextHeader1` `TextHeader2` `TextHeader3` | ⚠️ **不是** `TextHeading*`（那个 404） |
| 分点 | `TextBulletList` | |
| 待办 | `CheckboxChecked` `TaskListSquareAdd` | 单态（激活态靠底色，不切 Filled 变体，见 §3） |
| 文字底色 | `Highlight` `ColorBackground` | 用 `Highlight` + **角上当前色小块**（拍板） |
| 引用 / 对齐 / 文字色 | `TextQuote` `TextAlignLeft` `TextColor` | 备将来用 |
| 标题栏 / 管理器 | `Add` `NoteAdd` `Pin` `Delete` `Dismiss` `ArrowClockwise` `Settings` | 同样单态 |

**接法：B 官方字体自建（2026-10-04 拍板）**

把 `FluentSystemIcons-Regular.ttf` 放进 `Assets/`，配一份自建的"图标名 → 码位"常量类，
`FontIcon` 直接引用（`ms-appx:///Assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular`）。
零第三方依赖、许可证链最短（只有微软的 MIT）；字体实测 **2.84 MB**（如在意体积可用子集化工具裁到只留用到的字形）。

- **配套纪律**：加一条单测断言"用到的码位在该字体里存在"（Win2D `CanvasTextLayout` 量每个字形，
  空白/notdef 即失败）——自建方案的唯一短板是拼错只能运行时发现，这条测试把它补平。
- **已否决的备选**：社区包 `FluentIcons.WinUI`（MIT，社区维护）能写 `<ic:FluentIcon Icon="TextBold"/>`、
  类型化枚举拼错编译期就报、立刻可用；否决理由是听社区包的更新节奏、且它对 Win2D 的引用版本偏旧需显式覆盖。
- **尺寸**：工具栏 20px、标题栏/管理器 16px（两个 token，见 §2.1）；`Symbol` 变体式的 16/28 度量约束
  不适用（我们走官方 `FluentIcon` 度量），换完仍需实机对一遍光学重量。

**实施记录（2026-10-04，已落地，待用户实机验收）**

- 字体：`src/LumiMemo.WinUI/Assets/FluentSystemIcons-Regular.ttf`（**2.84 MB**，MIT；
  csproj 里以 `Content` + `CopyToOutputDirectory=PreserveNewest` 进输出目录）。
  内部 family 名经 name 表核对为 **`FluentSystemIcons-Regular`**。
- 资源（`App.xaml`）：`LumiIconFont`（`ms-appx:///Assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular`）、
  `LumiIconSizeToolbar` = **20**、`LumiIconSizeChrome` = **16**。
- 码位常量（代码里用）：`src/LumiMemo.WinUI/LumiIcons.cs`；字符串一律写 `\uXXXX` / `\U000XXXXX` 转义，
  **不放裸的私用区字符**（看不见、编码一动就坏）。
- **码位核对做法**（替代原计划的单测）：取值时对着官方 `fonts/FluentSystemIcons-Regular.css`
  的"名字→码位"表 + ttf 的 cmap（**format 12**，补充平面靠它）逐个核，**17/17 命中**。
  没做成单测的原因：测试工程不初始化 XAML，加载不了应用资源字体；而 cmap 校验一次性做掉就够，
  剩下的靠实机目视一遍。字体升级后重做这一遍。

**逐按钮映射（本次落地的全部）**

| 位置 | 原样 | 图标（Fluent 名） | 码位 |
|---|---|---|---|
| 便签标题栏 | `E72C` 刷新 | `ArrowClockwise` | U+E0AA |
| | `E710` 新建便笺 | `NoteAdd` | U+F56D |
| | `E718` 置顶 | `Pin` | U+F600 |
| | `E74D` 删除便笺 | `Delete` | U+E47B |
| | `E8BB` 关闭 | `Dismiss` | U+F368 |
| 工具栏 | 文字 `B` | `TextBold` | U+F7A4 |
| | 文字 `I` | `TextItalic` | U+F7F4 |
| | 文字 `U` | `TextUnderline` | U+F80A |
| | 文字 `S` | `TextStrikethrough` | U+ED5F |
| | 字符 `•` | `TextBulletList` | **U+F0290**（补充平面） |
| | 字符 `☐` | `CheckboxChecked` | U+F28D |
| | 手画色块 | **不用图标**：按钮本体就是圆角色块（右键展开色带取色，见 §3） | — |
| | 文字 `H1/H2/H3` | `TextHeader1/2/3` | U+F7EF / F7F0 / F7F1 |
| 管理器 | `E710` 新建便笺 | `NoteAdd` | U+F56D |
| | `E713` 设置 | `Settings` | U+F6A8 |
| | `E74D` 回收站 | `Delete` | U+E47B |
| | `E8BB` 隐藏窗口 | `Dismiss` | U+F368 |

**底色按钮的小色块**：初始不显示（没选过底色）；选了之后显示"上次用的色"，点「无」清除时收起。
将来可升级成"跟着光标处的底色"（需要编辑器侧提供一个 caret 背景色的 getter，本轮没做）。

**待实测**：单文件自解压发布下这个 `Content` 字体能否被正确解出（`IncludeAllContentForSelfExtract=true`
应当覆盖——`Assets` 此前只有 MSIX 用的图，这是**第一个运行期 Content 资源**，发版前走一遍）。

**动画图标（`AnimatedIcon`）评估**：WinUI 内置 `AnimatedVisuals` 只有导航类（Back / Find / Settings…），
**没有我们的动作**；自定义要用 **LottieGen**（`dotnet tool install lottiegen`，`-WinUIVersion 3.0`）
把 Lottie 编成 `IAnimatedVisualSource2` 类（**无需运行时包**）。⚠️ 别用动态加载器
`CommunityToolkit.WinUI.Lottie`——它的 README 明确要求 Win2D ≤ 1.0.5，与我们冲突。
开放的 Lottie 图标集授权要逐个看（Lordicon 免费层不可商用）。→ **本轮不引入 Lottie**，动效自己做（§4）。

---

## 2. 主题统一

### 2.1 token 层（新文件 `src/LumiMemo.WinUI/Theme/LumiTokens.xaml`，App 级合并）

语义键（值放 `ThemeDictionaries`：**Light / Dark / HighContrast** 三套；HighContrast 只覆盖必要项，
其余交给系统语义键，避免自造色破坏无障碍）：

| token | 用途 | 现值的来源 |
|---|---|---|
| `LumiInk` / `LumiInkSecondary` | 正文、次要文字 | `#FF403747` / `#FF75697C` |
| `LumiChromeFill` | 标题栏/工具栏底 | `#18FFFFFF`（+ 深色对应值） |
| `LumiHoverFill` / `LumiPressedFill` | 悬停 / 按下 | `#14FFFFFF` / `#24FFFFFF` |
| `LumiCheckedFill` / `LumiCheckedBorder` | 激活态（ToggleButton） | `#20FFFFFF` / `#68FFFFFF` |
| `LumiHairline` | 分隔线、细描边 | `#5075697C` / `#8075697C` |
| `LumiAccent` / `LumiDanger` | 强调、删除 | 新增（深色下另配） |
| `LumiHighlightSwatch` | 文字底色预览块 | `#59FDF3C4` |

尺寸/几何 token：`LumiControlSize`（**32**）、`LumiIconSizeToolbar`（**20**）/ `LumiIconSizeChrome`（**16**）、
`LumiCornerRadius`（**6**）、`LumiToolbarGap`（4/6）——一次灭掉 32×30 / 32×28 / 30×30 的混用与 7 种白叠加。

**纪律（延续现状）**：只做**具名样式**（`LumiIconButtonStyle` / `LumiToggleButtonStyle`），
**不碰全局 `ButtonBackground` 之类主题键**——否则设置页与对话框的常规按钮会被一起改掉。

### 2.2 便签纸面色与强调色（D6，本轮的"颜色"部分）

**深色模式已推迟（D5，另开一轮）**，但"颜色"这件事这一轮要做，而且比当年设想的更彻底：
**每张便签带自己的颜色，便签内的强调色也跟着它走**。

| 面 | 现状 | 目标 |
|---|---|---|
| 便签窗口纸面 | 统一亚克力（tint `#F4ECFF` 写死），与便签色无关 | **按便签色 tint**：`AcrylicBackdrop` 接受一组随笔记色变的 tint/luminosity；改色即时生效 |
| 便签内强调色 | 无（图片手柄是固定的天蓝 `#87CEEB`） | **随本便签的颜色**：图片缩放手柄（含白边）、激活底色/指示条（D3）、焦点视觉、拖放落点线……即"这个便签的 accent" |
| 管理器 | 便签色只出现在色条与片段纸面预览 | 保持"应用固定 accent"（管理器不属于任何一张便签） |

- **两套 accent 语境**：便签窗口 = 随便签色；管理器 = 应用固定 accent。token 层的做法是
  `LumiAccent*` 一组键 + **便签窗口在自己的根元素上覆写这组键**（`Root.Resources["LumiAccent"] = …`），
  子树自动生效；管理器不覆写，落回 App 级默认值。
- **每色要出两档**：`玻璃纸面色`（tint 色 + 不透明度/亮度）与 `强调色`（手柄、激活、焦点）。
  两档都得**在深色壁纸上也分得清**——这是当年记下的"半透明纸 × 便签颜色打架"那个坑的正面回答；
  验收口径：8 色在浅色/深色壁纸前两两可辨（不要求"像不透明白纸那样准"，要求"一眼能区分"）。
- **渲染器配合**：`LumiText` 目前把这些写死（墨色、选区、光标、待办框、图片手柄/白边、尺寸标签、
  拖放指示线、行内底色）。给 `LumiEditor` 开一个 **`LumiPalette` 注入点**（一次注入整套，
  改色/换主题时整体替换并 `Invalidate`），而不是散落的若干依赖属性；手柄色从"固定天蓝"改成"取注入的强调色"。
- **与文字底色色板的关系**：现有底色色板借用的是纸面色实色（`NoteColorPalette.Paper`）——接上便签色之后
  要重新看一遍：是继续给"纸面色系"，还是改成"本便签色的深浅两档"。（实现时定，记在这里免得漏。）

---

## 3. 效果优化清单

- **图标替换**（§1 映射表）：**工具栏全部按钮图标化**（含 `B/I/U/S` 与 `H1–H3`，2026-10-04 拍板）——
  已接受"图标化的排版符号辨识度略低"这个取舍；缓解手段是**保留 Tooltip**（现有）+ 工具栏图标取 20px
  （别为了"精致"缩到 16px）。换完**实机对一遍光学尺寸**：Fluent 20px 的视觉重量与 Segoe 13–14px 字形不同。
- **文字底色按钮**（2026-10-04 五次定稿）：**不用图标**——按钮本体就是一个 **20×20 圆角方块**
  （与工具栏图标的视觉尺寸一致），底色＝当前色。左键＝把当前色刷到选中文字；
  **右键＝在按钮栏里展开色带取色**：
  - 色带是**按钮底下的一层**（`HighlightStrip`：宽 76、**高 6** 的细胶囊，色相 0°→300° 红→紫），
    展开时**左缘对齐方块左缘**、向右变长（250ms）——按钮就像骑在色带上；
  - "把右侧按钮推开"由按钮栏里一个**占位块**（`HighlightStripSpacer`，与色带**同一条动画同步变宽**，
    76 − 25 = 51）负责。色带只画、不占位：它在 Grid 里排在按钮栏之前（z 序在下）；
  - 滑块（＝那个方块）**从自己在工具栏里的位置滑到当前色的位置**，并**缩小到 16×16**
    （对齐待办勾选框**看得见的墨迹**，见纪律⑥；收起时是 18×18）。滑块**填充＝实时颜色**
    （拖动时跟着走），不再另做水滴预览；
  - 展开期间原按钮整个**让位**：`IsHitTestVisible = false` + 摘掉悬停提示——否则悬停/按下的
    边框底色与提示会浮在色带上面；
  - 在带或滑块上左键拖动**无级取色**；松手后色带缩回（167ms）、方块回位并套用新颜色。
  - 点别处 / Esc 取消。
  实现：`Controls/HighlightPicker.xaml(.cs)`（滑块那一层）+ `MainWindow.xaml` 的
  `HighlightStrip` / `HighlightStripSpacer`。
  **五条纪律**（都写进代码注释了，前两条是踩过的坑）：
  ① 自绘的可点元素**必须给 Background/Fill**——没有画刷的 `Border` 既不显色也不参与命中测试
  （"色带上的滑块点不动"就是它）；
  ② 量元素坐标要在它**可见**时做（`Collapsed` 的元素量不到有效坐标——"展开动画没出现"就是它，
  所以方块矩形由宿主在隐藏之前量好传进来）；
  ③ 色带**常驻布局**，用"宽 0 + 透明"收起，别用 `Visibility`（同上：Collapsed 量不到坐标）；
  ④ Storyboard 播完会**保持**动画值、盖住之后的直接赋值（滑块拖不动），换场前先停、播完先写回终值再停；
  ⑤ 基准值先设成**终态**、动画只负责过程——某条没跑成也不会留下坏状态；
  ⑥ **对齐图标按"墨迹"、不按 `FontIcon.FontSize`**：Fluent 图标在 20 的字号里只画 ~16dip
  （四周留白），拿 20 当尺寸去对别的东西会明显偏大（2026-10-04 从截图里量出来的）。
  可调项：色带长度 76 / 高 6 / 色相范围 / 取色 S·V（与带上所见一致）/ 展开收起时长 /
  滑块尺寸（收起 18、展开 16）。
- **待办复选框**（2026-10-04 实机调）：边长**随行高走**（行高 × 0.7，上限 20——原先写死 20，
  比正文行还高，相邻的框会贴在一起）；2px 描边；圆角给到边上 0.25（描边是**居中**画的，
  半径小的话外圈圆、内圈还是尖的）；对勾改**圆头 + 圆角接头**；框在缩进区里居中，不侵正文。
  **已勾选的行，正文压暗到 60% alpha**（"做完了"的语义；方框与对勾保持原色；
  压暗走 alpha 而不是写死灰值——深色主题那轮不用改这里）。
- **四态收敛**：hover / pressed / checked / disabled 各一个 token（现 11 种白叠加 → 收到 3~4 个）。
- **焦点视觉**：透明按钮上 WinUI 默认焦点框很难看 → 统一 `FocusVisual`（2px `LumiAccent` 描边 + 2px 偏移）。
- **激活态**（分点 / 待办 / 标题）：**统一的激活底色/指示条**，**不切 Filled 变体**（拍板）——
  理由是工具栏按钮密，Fluent 里 Filled 与 Regular 的重量差会让一排图标显得跳；
  配合 §4 L2 的滑动指示器，激活态还能"滑"过去。底色取**本便签的强调色**（§2.2）。
- **分组与对齐**：工具栏按「文字样式 / 块样式 / 插入」分三段，分隔线颜色/高度/间距统一；命中区 ≥32px；
  ToolTip 延迟统一；禁用态（标题刷新在生成中）统一。
- **对比度**：现在按钮靠"白色叠加层"假设浅色底——深色下会失灵，全部换成主题感知 token。

---

## 4. 动效方案

**度量口径**（WinUI 官方 motion 规范，直接用现成的 named resource，别自造数）：

| 资源名 | 值 | 用在哪 |
|---|---|---|
| `ControlFasterAnimationDuration` | **83ms** | 按下反馈、小范围位移 |
| `ControlFastAnimationDuration` | **167ms** | 悬停/状态过渡 |
| `ControlNormalAnimationDuration` | **250ms** | 面板出现、指示器滑动 |

曲线：**fast-out-slow-in** `cubic-bezier(0, 0, 0, 1)`（进场/出现）、**slow-out-fast-in** `cubic-bezier(1, 0, 1, 1)`（退场）。
现状对齐情况：手柄淡入淡出 300ms、吸附 150–260ms ease-out、勾选 160ms —— 同族，建议统一改引用上表资源。

| 层 | 长什么样（本应用里的具体例子） | 成本 | 建议 |
|---|---|---|---|
| **L1 状态过渡** | 按钮**按下去会轻微缩一下**（1.0→0.96，83ms，像真被按进去）；鼠标移上去时底色**淡入**（167ms）而不是瞬间变色。范围：两个窗口的所有按钮 | 低（纯 XAML：VisualState + Storyboard，不用碰代码） | **做**——便宜且全场受益 |
| **L2-a 指示器滑动** | 光标在"分点/待办/H1/H2/H3"之间移动时，工具栏上表示"当前块样式"的那块高亮**从旧按钮滑到新按钮**（250ms，弹簧曲线），而不是两个按钮一闪一灭——"状态搬过去了"这件事看得见 | 中（`Composition` 位移/弹簧动画 + 按钮组要给高亮块留固定槽位） | **建议做**——工具栏是"状态型 UI"，这是最能显手感的一处 |
| **L2-b 出现/消失** | 上下文相关的按钮组、或"禁用→可用"的过渡用淡入/淡出，而不是硬切 | 低 | 视需要 |
| **L3 图标自己会动** | 刷新图标**转起来**、钉住图标**落下去**这类"图标本体做动作"。要用 Lottie 素材 + LottieGen 生成代码（见 §1 末） | 高（自绘或找素材） | 先不做；个别图标（刷新）可以只用 Storyboard 让现成图标旋转 |

> 不在这三层里的、**已经在做**的动效：待办勾选 160ms、图片松手吸附、手柄淡入淡出、滚动条滑出/收起——
> 这些画在渲染器/覆盖层里，不在按钮层。

**编辑器内（XAML 做不到、渲染器能做）**：待办勾选、图片吸附、手柄淡入淡出已在做。
将来 **M5 文字流动画**可复用吸附那套「瞬时排版问目标 + 逐帧重排」的做法（本轮已验证可行）。

**无障碍**：跟随系统"显示动画"设置（`UISettings.AnimationsEnabled`，并订阅其变化）；
`AnimatedIcon` 自动遵守，自绘的要自己判。Fluent 官方建议：一屏内少用动效，只在需要引导注意处用。

---

## 5. 里程碑与验收

| 里程碑 | 交付物 | 验收 |
|---|---|---|
| **M1** | token 化 + 尺寸统一：`LumiTokens.xaml`、两个窗口与三页的内联资源全部搬到 App 级、尺寸/圆角/图标盒统一 | 像素级无差别（同主题下前后截图对照）+ 用户检查 |
| **M2** | 图标替换（D1/D2/D4）：Assets 内嵌 `FluentSystemIcons-Regular.ttf` + 常量类 + "码位在字体里存在"单测；工具栏**全部**按钮图标化；底色按钮加当前色小块；光学尺寸对齐 | Win10/11 一致、用户逐图标检查（含 B/I/U/S/H1–H3 换图标后的辨识度） |
| **M3** | 四态收敛 + 焦点视觉 + **激活底色/指示条**（D3）+ 动效 **L1** | 鼠标/键盘各走一遍（含 Tab 焦点、禁用态）+ 用户检查 |
| **M4** | 便签颜色落地（D6）：8 色 × 两档（玻璃纸面色 / 强调色）、`AcrylicBackdrop` 按便签色 tint、便签窗口覆写 `LumiAccent*`、`LumiEditor` 的 `LumiPalette` 注入 + 图片手柄改取强调色 | 8 色在浅色/深色壁纸前两两可辨 + 明暗壁纸截屏对照 + 用户检查 |

依赖：M1 → M2 → M3 串行（都碰同一批 XAML）；M4 依赖 M1（token 层）与 M3（强调色用在激活底色上）。
动效 L2 视 M3 效果另定，不占里程碑。**深色模式（应用主题）已推迟（D5）：不在本稿里程碑内，另开一轮**——
届时要做的仍是 §2.2 表里"应用主题"那一列（`Application.RequestedTheme`、三套 ThemeDictionaries、
管理器的深色配套），以及便签纸面色在深色主题下的第二套值。

---

## 6. 决策记录

**已拍板（2026-10-04）**

| # | 决策 | 落到哪 |
|---|---|---|
| D1 | 图标集 = **Fluent UI System Icons**；接法 = **B 官方字体自建**（Assets 内嵌 ttf + 自建"名字→码位"常量类；码位对着官方 CSS 与 ttf 的 cmap 核过） | §1；M2（**已落地待验收**） |
| D2 | **工具栏全部按钮图标化**（含 `B/I/U/S` 与 `H1–H3`，不再用文字/字符）；接受"图标化排版符号辨识度略低"，靠 Tooltip + 20px 图标缓解 | §1 映射表、§3；M2 |
| D3 | 激活态 = **统一的激活底色/指示条**，**不切 Filled 变体** | §3；M3（并给 §4 L2 的指示器滑动留口） |
| D4 | 文字底色按钮：**原案**（图标 + 角上色块）已废弃 → 改为**按钮本体即小圆角色块（18×18），右键在按钮栏里向右展开细色带**（推开右侧按钮；滑块＝方块本身、实时显示颜色） | §3；**已落地待验收** |
| D5 | **深色模式推迟**：不在本轮，另开一轮（本轮只做 token 层，给它留好挂点） | §2.2、§5 |
| D6 | **便签窗口带上自己的颜色**，且**便签内的强调色随便签色**（图片缩放手柄、激活底色/指示条、焦点视觉……）；管理器保持应用固定 accent | §2.2；M4 |

**待拍板**

1. **动效**：只做 L1（状态过渡），还是带到 L2-a（指示器滑动）/ L2-b（出现消失淡入）——三层各是什么，
   见 §4 的"长什么样"列。

---

## 7. 风险与注意

- **HighContrast**：自定义色只覆盖必要项，其余沿用系统语义键；M4 结束前抽查一次。
- **深色 + 亚克力对比度**：便签是浅色糖果色系，深色 tint/亮度需重新定并实机对（不能只靠数值推算）。
- **渲染器调色板注入**：给库公开面 +1，形态要一次定好（`LumiPalette` 整体注入，别铺开成多个依赖属性）；
  相关连带：墨色、选区、光标、待办框、图片手柄/白边、尺寸标签、拖放指示线、行内底色默认值。
- **每个便签色的两档值**（D6）：淡色玻璃纸压在深色壁纸上容易糊成一片——8 色都要在浅色/深色壁纸前
  验一遍"两两可辨"（不要求像不透明白纸那样准，要求一眼能区分）。
- **强调色与文字底色撞色**：同一张便签里"高亮"与"强调"同源（都从便签色来），要让两者在明度/饱和上
  拉开档次，否则高亮叠在激活态上会糊成一块。
- **改色即时生效的实现面**：重新 tint 亚克力 + 重注入 `LumiPalette` + 触发重绘；多窗口并存时
  各自持自己的色、别串（`AcrylicBackdrop` 每窗一份，`LumiPalette` 每编辑器一份）。
- **自带字体的后勤**：`FluentSystemIcons-Regular.ttf` 约 1 MB 进 Assets（D1 选了自建）；
  图标集更新时要重新生成"名字→码位"常量表，并重跑那条"码位在字体里存在"的单测。
- **图标光学重量**：Fluent 与 Segoe 度量不同，替换后必须实机对一遍（尤其标题栏 16px 档）。
- **动效克制**：不要给每个按钮都加动效；Fluent 官方建议一屏内少量使用，且必须尊重系统"减少动画"。

## 8. 来源

- [Fluent UI System Icons（MIT）](https://github.com/microsoft/fluentui-system-icons) ·
  [图标全集（regular）](https://github.com/microsoft/fluentui-system-icons/blob/main/icons_regular.md)
- [FluentIcons.WinUI 包（MIT，社区）](https://www.nuget.org/packages/FluentIcons.WinUI/) ·
  [davidxuang/FluentIcons](https://github.com/davidxuang/FluentIcons/)
- [Timing and easing（WinUI 时长与曲线）](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing)
- [AnimatedIcon](https://learn.microsoft.com/en-us/windows/apps/design/controls/animated-icon) ·
  [Lottie-Windows](https://github.com/CommunityToolkit/Lottie-Windows)
- [Lucide / Tabler / Phosphor 对比](https://svgicons.com/articles/lucide-vs-tabler-vs-phosphor-icons)
