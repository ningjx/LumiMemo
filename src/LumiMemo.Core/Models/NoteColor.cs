namespace LumiMemo.Core.Models;

/// <summary>
/// 便签颜色（§9.2）。
/// </summary>
/// <remarks>
/// 这个枚举有两个序列化出口，两者的字符串形式必须都是小写：
/// <list type="bullet">
///   <item>Markdown 的 Front Matter <c>color</c> 字段，如 <c>color: yellow</c>（§5.3）</item>
///   <item><c>settings.json</c> 的 <c>defaultColor</c>，如 <c>"defaultColor": "yellow"</c>（§8.2）</item>
/// </list>
/// 因此读取 JSON 时必须注册带 camelCase 命名策略的字符串枚举转换器，
/// 否则 System.Text.Json 会把它写成数字 <c>0</c>，与 §8.2 的示例不符。
/// </remarks>
public enum NoteColor
{
    /// <summary>黄色。默认色（§8.2 的 <c>defaultColor</c>）。</summary>
    Yellow,

    Pink,

    Blue,

    Green,

    Purple,

    Orange,

    Gray,
}
