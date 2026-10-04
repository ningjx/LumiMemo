using System.Globalization;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using Microsoft.Extensions.Logging;

namespace LumiMemo.WinUI.Services;

/// <summary>
/// 工具栏上那两个「当前值」的全局状态（2026-10-04）：**文字底色的颜色**与**常用标题级别**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是全局</b>：这两个值是「我平时习惯怎么排」，不是某张便签的属性——所以跟着应用走，
/// 落在 <c>settings.json</c>（<see cref="AppSettings"/>），新建便签、重开程序都接着上次用。
/// </para>
/// <para>
/// <b>为什么做成单例服务</b>：多张便签窗口共用同一份值。谁改了，其余窗口通过
/// <see cref="HighlightColorChanged"/> / <see cref="HeadingLevelChanged"/> 即时跟上，
/// 不用关掉重开。
/// </para>
/// <para>
/// <b>写盘时机</b>：改一次存一次（这两个动作一天也没几次），异步、失败只记日志——
/// 改个颜色不该被一个写盘异常打断。
/// </para>
/// </remarks>
public sealed class ToolbarPreferences
{
    /// <summary>没设置过底色时的兜底：便签黄纸色（与合并前的默认色一致，见 <c>NoteColorPalette.Paper</c>）。</summary>
    private static readonly Windows.UI.Color FallbackHighlight =
        Windows.UI.Color.FromArgb(255, 0xFD, 0xF3, 0xC4);

    /// <summary>「常用标题」没设置过时的兜底级别（2 = H2）。</summary>
    private const int FallbackHeadingLevel = 2;

    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly ILogger<ToolbarPreferences> _logger;

    public ToolbarPreferences(AppSettings settings, ISettingsStore store, ILogger<ToolbarPreferences> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _store = store;
        _logger = logger;
    }

    /// <summary>文字底色的当前颜色（工具栏上那个圆角方块的颜色）。</summary>
    public Windows.UI.Color HighlightColor =>
        TryParseColor(_settings.TextHighlightColor) ?? FallbackHighlight;

    /// <summary>常用标题级别（1–3）：左键点 H 按钮时套用到光标所在块的那一档。</summary>
    public int HeadingLevel =>
        _settings.DefaultHeadingLevel is >= 1 and <= 3 ? _settings.DefaultHeadingLevel : FallbackHeadingLevel;

    /// <summary>底色变了（可能是**别的**便签窗口改的）。</summary>
    public event EventHandler? HighlightColorChanged;

    /// <summary>常用标题级别变了（同上）。</summary>
    public event EventHandler? HeadingLevelChanged;

    /// <summary>改文字底色并落盘；颜色没变就什么都不做。</summary>
    public void SetHighlightColor(Windows.UI.Color color)
    {
        string text = ToHex(color);
        if (string.Equals(_settings.TextHighlightColor, text, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.TextHighlightColor = text;
        HighlightColorChanged?.Invoke(this, EventArgs.Empty);
        _ = SaveAsync();
    }

    /// <summary>改常用标题级别并落盘；级别没变就什么都不做。</summary>
    /// <remarks>只接受 1–3：「常用标题」是「平时用几级标题」，正文不是其中的一档（正文＝不再点它）。</remarks>
    public void SetHeadingLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 3);

        if (_settings.DefaultHeadingLevel == level)
        {
            return;
        }

        _settings.DefaultHeadingLevel = level;
        HeadingLevelChanged?.Invoke(this, EventArgs.Empty);
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "工具栏偏好写盘失败（{ExceptionType}），本次只作用于当前会话。",
                exception.GetType().Name);
        }
    }

    // ---- 颜色 ↔ "#AARRGGBB" ----

    /// <summary>认不出来（用户手改坏了、或来自别的版本）就返回 <see langword="null"/>，由调用方兜底。</summary>
    private static Windows.UI.Color? TryParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string hex = text.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }

        return hex.Length == 6
            ? Windows.UI.Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : Windows.UI.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static string ToHex(Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
