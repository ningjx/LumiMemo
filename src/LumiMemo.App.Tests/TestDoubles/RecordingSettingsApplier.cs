using LumiMemo.App.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="ISettingsApplier"/> 的记录型替身。
/// </summary>
/// <remarks>
/// 生产的那个实现是 <c>StartupSequence</c>，它的构造函数有十二个依赖（其中还包括窗口），
/// 在无头测试里建不起来——这个替身正是为此存在的（§18.6）。
/// 它只回答一个问题：<strong>设置窗口保存完之后，有没有把新的那份推下去</strong>。
/// </remarks>
public sealed class RecordingSettingsApplier : ISettingsApplier
{
    /// <summary>所有被推下来的设置，按发生顺序。</summary>
    public List<AppSettings> Applied { get; } = [];

    /// <inheritdoc />
    public void Apply(AppSettings settings) => Applied.Add(settings);
}
