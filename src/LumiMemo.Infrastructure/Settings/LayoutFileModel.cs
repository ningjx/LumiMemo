using System.Text.Json.Serialization;
using LumiMemo.Core.Models;

namespace LumiMemo.Infrastructure.Settings;

/// <summary>
/// <c>layout.json</c> 的磁盘形态（§8.3）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么不给 <see cref="NoteLayout"/> 直接挂 <c>[JsonPropertyName]</c></strong>
/// （<c>AppSettings</c> 走的是那条路）：磁盘结构与模型并不是一一对应的。文件顶层有
/// <c>version</c>、有一张按便签 id 索引的 <c>notes</c> 表、还有一张与便签无关的
/// <c>displays</c> 表；而便签 id 在文件里是<strong>字典的键</strong>，在模型里是
/// <see cref="NoteLayout.NoteId"/> 属性。既然外层的包装类型无论如何都要写，
/// 内层也一并显式映射——这样改一个 C# 属性名不会被误当成改磁盘格式（§8.4 用显式特性
/// 而不是全局命名策略的同一个理由）。
/// </para>
/// <para>
/// 两张表的键都用 <see cref="string"/> 而不是 <c>Guid</c>：文件可能被用户手改、
/// 也可能被同步工具弄坏，遇到一个非法 id 时只该跳过那一条，
/// 而不是让整个文件反序列化失败、把全部窗口位置一起丢掉。
/// </para>
/// </remarks>
internal sealed class LayoutFileModel
{
    /// <summary>
    /// 文件版本（§8.4）。写盘时总是写成 <see cref="JsonLayoutStore.CurrentVersion"/>。
    /// </summary>
    /// <remarks>
    /// 读盘时<strong>不</strong>经过这里，而是先用 <see cref="JsonFileFormat.ReadVersion"/>
    /// 单独看一眼再决定要不要解析。所以这里缺失时得到 0 并不影响判断，
    /// 只有写盘方向会用到它。
    /// </remarks>
    [JsonPropertyName("version")]
    public int Version { get; set; } = JsonLayoutStore.CurrentVersion;

    /// <summary>上一次写盘时各显示器的形态，键是显示器设备路径（§8.3）。</summary>
    [JsonPropertyName("displays")]
    public Dictionary<string, DisplayEntry>? Displays { get; set; }

    /// <summary>各便签的布局，键是便签 id 的字符串形式。</summary>
    [JsonPropertyName("notes")]
    public Dictionary<string, NoteEntry>? Notes { get; set; }
}

/// <summary>一台显示器在写盘当时的形态（§8.3 的 <c>displays</c> 表条目）。</summary>
/// <remarks>
/// 刻意<strong>不记工作区</strong>：工作区随任务栏自动隐藏、随副屏旋转而变，
/// 记下来的是那一刻的噪声，而恢复算法要的是「现在」的工作区。
/// 这张表整体是诊断材料——排查「便签跑到屏幕外」的报告时，
/// 它给出的是「当时那台显示器是什么样」这个上下文。
/// </remarks>
internal sealed class DisplayEntry
{
    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    [JsonPropertyName("boundsPx")]
    public RectEntry BoundsPx { get; set; } = new();

    [JsonPropertyName("dpi")]
    public uint Dpi { get; set; } = 96;

    public static DisplayEntry From(DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new DisplayEntry
        {
            FriendlyName = snapshot.FriendlyName,
            BoundsPx = RectEntry.From(snapshot.BoundsPx),
            Dpi = snapshot.Dpi,
        };
    }
}

/// <summary>一个矩形的磁盘形态：<c>{ "x": …, "y": …, "width": …, "height": … }</c>。</summary>
/// <remarks>
/// 形状与 <see cref="PixelRect"/> 完全一致，仍然单独写一个类型而不是直接序列化它：
/// <see cref="PixelRect"/> 在 Core，给它挂 JSON 特性等于让 Core 认识磁盘格式。
/// 单位是<strong>物理像素</strong>（§8.3），不要在这里做任何 DIP 换算。
/// </remarks>
internal sealed class RectEntry
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    public static RectEntry From(PixelRect rect) => new()
    {
        X = rect.X,
        Y = rect.Y,
        Width = rect.Width,
        Height = rect.Height,
    };

    public PixelRect ToPixelRect() => new(X, Y, Width, Height);
}

/// <summary>一张便签的布局在磁盘上的形态（§8.3 的 <c>notes</c> 表条目）。</summary>
/// <remarks>
/// 条目里<strong>不再重复存 <c>noteId</c></strong>：它就是字典的键，重存一遍等于给
/// 「键与值不一致」留了后门，而那种不一致没有任何合理的解释方式。
/// </remarks>
internal sealed class NoteEntry
{
    [JsonPropertyName("isOpen")]
    public bool IsOpen { get; set; } = true;

    [JsonPropertyName("displayId")]
    public string? DisplayId { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; } = 360;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 420;

    [JsonPropertyName("dpi")]
    public uint Dpi { get; set; } = 96;

    [JsonPropertyName("expandedHeight")]
    public double ExpandedHeight { get; set; } = 420;

    [JsonPropertyName("contentScale")]
    public double ContentScale { get; set; } = 1.0;

    [JsonPropertyName("isCollapsed")]
    public bool IsCollapsed { get; set; }

    [JsonPropertyName("isTopMost")]
    public bool IsTopMost { get; set; }

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }

    public static NoteEntry From(NoteLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return new NoteEntry
        {
            IsOpen = layout.IsOpen,
            DisplayId = layout.DisplayId,
            X = layout.X,
            Y = layout.Y,
            Width = layout.Width,
            Height = layout.Height,
            Dpi = layout.Dpi,
            ExpandedHeight = layout.ExpandedHeight,
            ContentScale = layout.ContentScale,
            IsCollapsed = layout.IsCollapsed,
            IsTopMost = layout.IsTopMost,
            IsLocked = layout.IsLocked,
        };
    }

    /// <param name="noteId">字典的键，作为便签身份写回模型。</param>
    public NoteLayout ToLayout(Guid noteId) => new()
    {
        NoteId = noteId,
        IsOpen = IsOpen,
        DisplayId = DisplayId,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Dpi = Dpi,
        ExpandedHeight = ExpandedHeight,
        ContentScale = ContentScale,
        IsCollapsed = IsCollapsed,
        IsTopMost = IsTopMost,
        IsLocked = IsLocked,
    };
}
