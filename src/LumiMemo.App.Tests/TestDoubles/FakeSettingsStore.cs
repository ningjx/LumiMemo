using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="ISettingsStore"/> 替身。
/// </summary>
/// <remarks>
/// <para>
/// 它<strong>刻意不做钳制</strong>：钳制边界的真实行为（读的时候兜底钳一次、
/// 写的时候按同一套边界）由集成测试用真实文件覆盖。这里要验的是设置窗口
/// 「保存前自己先钳一遍」这条策略有没有生效——若替身也钳一遍，
/// 就算设置窗口什么都不做，用例照样是绿的。
/// </para>
/// <para>
/// <see cref="Current"/> 是<strong>同一个实例反复交出去</strong>，与真实实现一致
/// （真实实现每次反序列化一份新的，但调用方拿到的始终是"当前磁盘内容"）。
/// 恢复默认值时换一个新对象，不要原地改字段——原地改会让「保存时有没有把
/// 没显示的字段一起带上」这件事看不出来。
/// </para>
/// </remarks>
public sealed class FakeSettingsStore : ISettingsStore
{
    /// <summary>当前"磁盘上"的那一份设置。</summary>
    public AppSettings Current { get; set; } = new();

    /// <summary><see cref="LoadAsync"/> 被调了几次。保存前补读那条分支靠它验。</summary>
    public int LoadCallCount { get; private set; }

    /// <summary>所有交给 <see cref="SaveAsync"/> 的设置，按发生顺序。</summary>
    public List<AppSettings> Saved { get; } = [];

    /// <inheritdoc />
    public Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        LoadCallCount++;

        return Task.FromResult(Current);
    }

    /// <inheritdoc />
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Saved.Add(settings);
        Current = settings;

        return Task.CompletedTask;
    }
}
