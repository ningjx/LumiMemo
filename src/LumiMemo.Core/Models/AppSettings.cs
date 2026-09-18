using System.Text.Json.Serialization;

namespace LumiMemo.Core.Models;

/// <summary>
/// 应用设置，序列化为 <c>%LOCALAPPDATA%\LumiMemo\settings.json</c>（§9.3）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>本类型与 §8.2 的 JSON 示例必须逐字段一致</strong>，改一处必须改另一处。
/// 这是 v1 出过问题的地方，v2 用这条显式规则钉住。
/// </para>
/// <para>
/// 用显式的 <see cref="JsonPropertyNameAttribute"/> 而不是全局 <c>JsonNamingPolicy.CamelCase</c>：
/// 全局策略下改一个 C# 属性名就会静默改变磁盘上的键名，老配置文件读不出来，
/// 用户设置静默丢失（§8.4）。
/// </para>
/// </remarks>
public sealed class AppSettings
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>笔记目录。空字符串表示「尚未选择」，启动时进入首次运行向导（§8.6）。</summary>
    [JsonPropertyName("notesFolder")]
    public string NotesFolder { get; set; } = "";

    /// <summary>附件目录名。相对的目录名，<strong>不含路径分隔符</strong>（校验见 §19.4）。</summary>
    [JsonPropertyName("attachmentsFolderName")]
    public string AttachmentsFolderName { get; set; } = "attachments";

    /// <summary>
    /// 主题：<c>system</c> / <c>light</c> / <c>dark</c>。默认跟随系统（§15.3）。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="string"/> 而不是枚举：配置文件可能被用户手改或来自降级后的旧版本，
    /// 用枚举会让一个不认识的字符串导致整份配置反序列化失败，退化成「所有设置都丢了」（§9.3）。
    /// </remarks>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("defaultColor")]
    public NoteColor DefaultColor { get; set; } = NoteColor.Yellow;

    [JsonPropertyName("defaultWidth")]
    public double DefaultWidth { get; set; } = 360;

    [JsonPropertyName("defaultHeight")]
    public double DefaultHeight { get; set; } = 420;

    [JsonPropertyName("defaultContentScale")]
    public double DefaultContentScale { get; set; } = 1.0;

    /// <summary>便签底部的「已保存 / 字数 / 缩放」状态条是否显示（§15.2）。</summary>
    [JsonPropertyName("showStatusBar")]
    public bool ShowStatusBar { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 关闭<strong>管理器窗口</strong>时的行为：true = 收进托盘，false = 退出应用。
    /// 只影响管理器窗口，便签窗口的关闭语义固定（§8.2、§17.3）。
    /// </summary>
    [JsonPropertyName("minimizeToTrayOnClose")]
    public bool MinimizeToTrayOnClose { get; set; } = true;

    [JsonPropertyName("showTrayIcon")]
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>托盘图标的单击行为：<c>toggleManager</c> / <c>newNote</c> / <c>showAllNotes</c>（§8.2）。</summary>
    [JsonPropertyName("singleClickTrayAction")]
    public string SingleClickTrayAction { get; set; } = "toggleManager";

    /// <summary>速记浮窗的全局热键（§15.7、§17.6）。</summary>
    [JsonPropertyName("globalQuickCaptureHotkey")]
    public string GlobalQuickCaptureHotkey { get; set; } = "Ctrl+Shift+N";

    /// <summary>「显示全部便签」的全局热键（§13.6）。</summary>
    [JsonPropertyName("globalShowAllHotkey")]
    public string GlobalShowAllHotkey { get; set; } = "Ctrl+Alt+N";

    /// <summary>
    /// 自动保存去抖时长。<strong>合法范围 300–800</strong>，读入时钳制，
    /// 超范围的值不生效但也不报错，改写日志（§9.3、§11.1）。
    /// </summary>
    [JsonPropertyName("autoSaveDelayMs")]
    public int AutoSaveDelayMs { get; set; } = 500;

    /// <summary>搜索输入停止多久后执行。合法范围 100–500（§9.3）。</summary>
    [JsonPropertyName("searchDebounceMs")]
    public int SearchDebounceMs { get; set; } = 150;

    [JsonPropertyName("trashRetentionDays")]
    public int TrashRetentionDays { get; set; } = 30;

    [JsonPropertyName("enableAnimations")]
    public bool EnableAnimations { get; set; } = true;

    /// <summary>同时打开的便签窗口上限，超过时提示用户（§13.9）。</summary>
    [JsonPropertyName("maxOpenWindows")]
    public int MaxOpenWindows { get; set; } = 50;

    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";
}
