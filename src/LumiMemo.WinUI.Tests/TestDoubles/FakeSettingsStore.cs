using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>内存版设置的替身：「改了要落盘」的动作在单测里不必碰磁盘。</summary>
/// <remarks>
/// 只记「存了几次」和「最后存的是什么」——本工程的测试关心的是**调用方有没有把值交出来**，
/// 磁盘形态由 <c>LumiMemo.Integration.Tests</c> 的 <c>JsonSettingsStoreTests</c> 负责。
/// </remarks>
public sealed class FakeSettingsStore : ISettingsStore
{
    /// <summary>最近一次保存（也是下一次读取）的那份设置。</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>保存过几次。</summary>
    public int SaveCount { get; private set; }

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Settings = settings;
        SaveCount++;
        return Task.CompletedTask;
    }
}
