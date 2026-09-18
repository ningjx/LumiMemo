using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// <c>settings.json</c> 的读写（§8.2、§8.4）。实现在 Infrastructure 层，用 System.Text.Json。
/// </summary>
/// <remarks>
/// 实现必须处理三种容错：文件不存在（用默认值）、文件损坏（改名保留后用默认值）、
/// <c>version</c> 高于当前版本（不要尝试解析，改名保留后提示用户，§8.4）。
/// 读取后还要钳制 <c>autoSaveDelayMs</c>（300–800）与 <c>searchDebounceMs</c>（100–500）。
/// </remarks>
public interface ISettingsStore
{
    /// <summary>读取设置。任何异常都必须降级为默认值，不能抛出去阻断启动（§17.1）。</summary>
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
