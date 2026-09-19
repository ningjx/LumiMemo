using LumiMemo.Core.Models;

namespace LumiMemo.App.Abstractions;

/// <summary>
/// 把一份设置灌进整张对象图（§15.9）。设置窗口保存成功之后立刻调一次，改完即刻生效。
/// </summary>
/// <remarks>
/// <para>
/// <strong>唯一实现是 <c>StartupSequence</c>，它不另起一个类。</strong>「谁是设置与各存储之间的
/// 串联者」这个问题在 §4.3 下只有一个答案，多一个实现类就会多一份「哪边算数」的疑问。
/// 这个接口存在的理由只有一条：<strong>让设置窗口的 ViewModel 能在测试里被构造出来</strong>。
/// 直接依赖 <c>StartupSequence</c> 的话，用例就得先建出它的十二个依赖（其中还包括窗口），
/// 于是「保存之后有没有把设置推下去」这件事永远得不到验证（§18.6）。
/// </para>
/// <para>
/// 接口刻意<strong>只有一个方法、只吃一个参数</strong>。「换笔记目录」那条链路（§8.6）
/// 要 flush 未保存内容、要处理 layout 与回收站的去留、要能在失败时中止，
/// 它不是「赋个值就生效」，不该混进这里。
/// </para>
/// </remarks>
public interface ISettingsApplier
{
    /// <summary>
    /// 把设置里那些「赋值即生效」的值搬到各消费方。
    /// </summary>
    /// <remarks>
    /// <strong>不看笔记目录</strong>：那是 §8.6 一整条流程的事（见接口说明）。
    /// </remarks>
    void Apply(AppSettings settings);
}
