namespace LumiMemo.WinUI;

/// <summary>
/// 图标码位：Fluent UI System Icons（微软，MIT）的 Regular 变体。
/// 码位取自官方 <c>fonts/FluentSystemIcons-Regular.css</c> 的"名字 → 码位"表，
/// 并对着 <c>FluentSystemIcons-Regular.ttf</c> 的 cmap 逐个核过（17/17 命中）。
/// </summary>
/// <remarks>
/// <para><b>为什么用它</b>：与我们系统字体 Segoe Fluent Icons 同一套设计语言，但**开源且随应用走**——
/// 系统字体在 Win10 上会回退成 Segoe MDL2 Assets，字形对不上（README 写明支持 Win10 1809+）。</para>
/// <para><b>尺寸要配对</b>：字体里每个图标按尺寸各存一份字形（12/16/20/24/28/32/48），
/// 所以"取哪个尺寸的码位"必须和 <c>FontSize</c> 一致，否则光学重量不对。
/// 下面分两组：工具栏 20（<c>LumiIconSizeToolbar</c>）、标题栏与管理器 16（<c>LumiIconSizeChrome</c>）。</para>
/// <para><b>写死成转义</b>：字符串一律用 <c>\uXXXX</c> / <c>\U000XXXXX</c> 写，不放裸的私用区字符——
/// 那种字符在编辑器里看不见，编码一动就悄悄坏掉。</para>
/// <para><b>字体升级后怎么重核</b>：按注释里的名字去官方 <c>fonts/FluentSystemIcons-Regular.css</c>
/// 查新码位，再确认 ttf 的 cmap 里有它（补充平面 &gt; U+FFFF 的字形需要 format 12 的 cmap，
/// 现行字体有；历史上缺过）。核法记在 docs/design/ui-theme-round-design.md §1。</para>
/// <para><b>XAML 侧</b>直接用字面量（<c>Glyph="&amp;#xE47B;"</c>）配
/// <c>FontFamily="{StaticResource LumiIconFont}"</c>；这份常量给代码里用。</para>
/// </remarks>
public static class LumiIcons
{
    // ── 工具栏：20px（ic_fluent_*_20_regular）──

    /// <summary>粗体（ic_fluent_text_bold_20_regular）。</summary>
    public const string TextBold = "\uF7A4";

    /// <summary>斜体（ic_fluent_text_italic_20_regular）。</summary>
    public const string TextItalic = "\uF7F4";

    /// <summary>下划线（ic_fluent_text_underline_20_regular）。</summary>
    public const string TextUnderline = "\uF80A";

    /// <summary>删除线（ic_fluent_text_strikethrough_20_regular）。</summary>
    public const string TextStrikethrough = "\uED5F";

    /// <summary>标题 1（ic_fluent_text_header_1_20_regular）。</summary>
    public const string TextHeader1 = "\uF7EF";

    /// <summary>标题 2（ic_fluent_text_header_2_20_regular）。</summary>
    public const string TextHeader2 = "\uF7F0";

    /// <summary>标题 3（ic_fluent_text_header_3_20_regular）。</summary>
    public const string TextHeader3 = "\uF7F1";

    /// <summary>分点列表（ic_fluent_text_bullet_list_20_regular）——唯一一个在补充平面（U+F0290）的字形。</summary>
    public const string TextBulletList = "\U000F0290";

    /// <summary>待办勾选（ic_fluent_checkbox_checked_20_regular）。</summary>
    public const string CheckboxChecked = "\uF28D";

    /// <summary>文字底色（ic_fluent_highlight_20_regular）。</summary>
    public const string Highlight = "\uF47C";

    // ── 标题栏与管理器：16px（ic_fluent_*_16_regular）──

    /// <summary>刷新（ic_fluent_arrow_clockwise_16_regular）。</summary>
    public const string ArrowClockwise = "\uE0AA";

    /// <summary>新建（ic_fluent_add_16_regular）。</summary>
    public const string Add = "\uF108";

    /// <summary>新建便笺（ic_fluent_note_add_16_regular）。</summary>
    public const string NoteAdd = "\uF56D";

    /// <summary>置顶（ic_fluent_pin_16_regular）。</summary>
    public const string Pin = "\uF600";

    /// <summary>删除（ic_fluent_delete_16_regular）。</summary>
    public const string Delete = "\uE47B";

    /// <summary>关闭（ic_fluent_dismiss_16_regular）。</summary>
    public const string Dismiss = "\uF368";

    /// <summary>设置（ic_fluent_settings_16_regular）。</summary>
    public const string Settings = "\uF6A8";
}
