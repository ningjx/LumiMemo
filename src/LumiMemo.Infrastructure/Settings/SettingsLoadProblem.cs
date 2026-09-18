namespace LumiMemo.Infrastructure.Settings;

/// <summary>
/// <c>settings.json</c> 载入时出了问题的种类（§8.4、§17.1 第 4 步）。
/// </summary>
/// <remarks>
/// 三种情况的处理动作相同（退回默认值继续启动），<strong>但对用户说的话完全不同</strong>：
/// 「文件损坏」要告诉他备份在哪，「来自更新版本」要告诉他换个版本的程序，
/// 「读不出来」则要提醒他可能有别的程序占着这个文件。
/// 所以这里必须区分，不能糊成一句「配置读不出来」。
/// </remarks>
public enum SettingsLoadProblemKind
{
    /// <summary>文件内容不是合法 JSON，或字段类型不符。已改名备份，本次用默认值。</summary>
    Corrupt,

    /// <summary>文件来自更新版本的程序。已改名备份，本次用默认值（§8.4）。</summary>
    NewerVersion,

    /// <summary>
    /// 文件存在但读不出来（被占用、无权限）。
    /// <strong>没有</strong>备份文件——原文件完好，只是这一刻读不到。
    /// </summary>
    Unreadable,
}

/// <summary>
/// 一次 <see cref="JsonSettingsStore.LoadAsync"/> 出了什么问题，以及现场留在哪。
/// </summary>
/// <param name="Kind">问题种类。</param>
/// <param name="BackupPath">被挪走的原文件路径；没备份时为 <see langword="null"/>。</param>
/// <param name="Message">可以直接呈现给用户的一句中文说明。</param>
/// <remarks>
/// 做成一个可空的只读属性而不是往 <c>ISettingsStore</c> 上加成员：这是实现层的细节，
/// 而 <c>ISettingsStore</c> 的消费者（<c>StartupSequence</c>）本来就认识具体类型
/// （它还要往 <c>AppPaths</c> 里灌笔记目录，那是另一个只有具体类型才有的成员）。
/// </remarks>
public sealed record SettingsLoadProblem(
    SettingsLoadProblemKind Kind,
    string? BackupPath,
    string Message);
