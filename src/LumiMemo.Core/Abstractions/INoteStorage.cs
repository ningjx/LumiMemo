using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 便笺的持久化存取（私有 <c>.lumi</c> 格式，2026-10 决策）。
/// </summary>
/// <remarks>
/// <para>
/// 与旧的 <see cref="INoteRepository"/> 的本质区别是<strong>没有外部编辑</strong>：
/// <c>.lumi</c> 只由本程序读写，因此不需要「重读单个文件、冲突检测、编码/行尾保留」
/// 这些为「用户会用别的编辑器打开它」而生的机制——整条链路连同文件监听一起退役。
/// </para>
/// <para>
/// 实现必须走 <c>AtomicFileWriter</c>：直接的 <c>File.WriteAllBytes</c> 先把文件截断为
/// 0 字节再写，断电或强杀会在磁盘上留下空文件——这是这类程序最常见的丢数据方式。
/// </para>
/// </remarks>
public interface INoteStorage
{
    /// <summary>扫描笔记目录，加载全部便笺。</summary>
    /// <remarks>
    /// 单个文件解析失败<strong>不能</strong>让整体失败：跳过它并记日志，
    /// 一份坏文件不该拖垮整个启动。返回的列表按创建时间排序，供窗口恢复流程直接使用。
    /// </remarks>
    Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>读取单个便笺文件；坏文件返回 <see langword="null"/> 并记日志。</summary>
    /// <remarks>
    /// 给「单文件场景」用——回收站恢复之后要把恢复出来的那个文件读成便笺，
    /// 不必为此重扫整个目录。
    /// </remarks>
    Task<Note?> TryLoadAsync(string path, CancellationToken ct = default);

    /// <summary>把便笺整份写回磁盘（原子写入）。</summary>
    Task SaveAsync(Note note, CancellationToken ct = default);

    /// <summary>建一张新的空白便笺，并立刻落盘。</summary>
    /// <param name="color">颜色；<see langword="null"/> 时用设置里的默认色。</param>
    /// <remarks>
    /// 笔记目录尚未配置、或配置的路径已经不存在时，实现应当抛
    /// <see cref="InvalidOperationException"/>，<strong>而不是默默重建目录</strong>——
    /// 后者会在被拔掉的移动盘上凭空造一个空目录，而用户的便笺都在别处（§11.5）。
    /// </remarks>
    Task<Note> CreateAsync(NoteColor? color = null, CancellationToken ct = default);
}
