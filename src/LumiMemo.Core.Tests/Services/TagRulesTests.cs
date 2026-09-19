using LumiMemo.Core.Services;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

/// <summary>
/// <see cref="TagRules"/> 的单元测试（§5.8）。
/// </summary>
/// <remarks>
/// 这些规则有两处调用者（解析 Front Matter 与管理器的标签编辑框），而且它们对
/// 「同一个输入会变成什么」必须给出同一个答案——否则「编辑完写回、再读回来标签变了」
/// 这种缺陷只会在特定输入下出现。所以这里逐条钉住规则表本身。
/// </remarks>
public sealed class TagRulesTests
{
    // ================= 规范化 =================

    [Theory]
    [InlineData("  work  ", "work")]          // 首尾空白
    [InlineData("#work", "work")]             // 开头的 #
    [InlineData("# work", "work")]            // 剥完 # 还要再 Trim 一次
    [InlineData("##work", "#work")]           // 只剥一个：第二个 # 是标签自己的内容
    [InlineData("to read", "to-read")]        // 内部空格换 -
    [InlineData("a - b", "a-b")]              // 空白段后面跟着 - 时不再补一个
    [InlineData("a  b", "a-b")]               // 连着几段空白只出一个 -
    [InlineData("a\tb", "a-b")]               // 制表符也算内部空白
    [InlineData("a--b", "a--b")]              // 用户自己写的连续 - 是标签内容，原样保留
    [InlineData("工作 计划", "工作-计划")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("#", "")]
    public void 规范化按规则表变换(string raw, string expected)
    {
        Assert.Equal(expected, TagRules.Normalize(raw));
    }

    [Fact]
    public void 规范化不改动没有空白的标签()
    {
        // 走的是「直接返回」那条短路。若它顺手做了什么（比如统一小写），
        // 用户写的 Work 会在写盘时被改掉，而他改天打开文件会发现对不上。
        Assert.Equal("Work-Plan", TagRules.Normalize("Work-Plan"));
    }

    // ================= 加入列表 =================

    [Fact]
    public void 加入标签_空标签被丢掉()
    {
        var tags = new List<string>();

        Assert.False(TagRules.TryAdd(tags, "   "));
        Assert.False(TagRules.TryAdd(tags, "#"));
        Assert.Empty(tags);
    }

    [Fact]
    public void 加入标签_忽略大小写去重且保留首次出现的写法()
    {
        // §5.8：Work 与 work 视为同一个标签，存储时以首次出现的写法为准。
        // 这条正是「不能交给 Distinct()」的理由——那个会按字节序挑一个留下。
        var tags = new List<string>();

        Assert.True(TagRules.TryAdd(tags, "Work"));
        Assert.False(TagRules.TryAdd(tags, "work"));
        Assert.False(TagRules.TryAdd(tags, "WORK"));

        Assert.Equal("Work", Assert.Single(tags));
    }

    [Fact]
    public void 加入标签_规范化之后才比重复()
    {
        // "  #work " 与 "work" 是同一个标签。若拿原始输入比，它会以两个身份进列表。
        var tags = new List<string>();

        Assert.True(TagRules.TryAdd(tags, "work"));
        Assert.False(TagRules.TryAdd(tags, "  #work "));

        Assert.Equal("work", Assert.Single(tags));
    }

    // ================= 拆分 =================

    [Theory]
    [InlineData("工作,紧急", "工作", "紧急")]            // 半角逗号
    [InlineData("工作，紧急", "工作", "紧急")]            // 全角逗号（中文输入法的默认行为）
    [InlineData("工作、紧急", "工作", "紧急")]            // 顿号
    [InlineData("工作;紧急", "工作", "紧急")]
    [InlineData("工作；紧急", "工作", "紧急")]
    [InlineData("工作\n紧急", "工作", "紧急")]
    [InlineData("工作,,紧急", "工作", "紧急")]            // 连续分隔符
    [InlineData(" 工作 , 紧急 ", "工作", "紧急")]         // 每段自带空白
    public void 拆分成多个标签(string input, string first, string second)
    {
        Assert.Equal([first, second], TagRules.Split(input));
    }

    [Fact]
    public void 拆分_去重且保留首次出现的写法()
    {
        // 写法的归属在整行范围内也一样：先到的是 Work，那留下的就是 Work。
        Assert.Equal(["Work"], TagRules.Split("Work, work, WORK"));
    }

    [Fact]
    public void 拆分_空输入得到空列表()
    {
        // 「留空即清空全部标签」就是走这一条：它必须返回一个真的空列表，
        // 而不是「什么都没拆出来」这种要靠调用方猜的状态。
        Assert.Empty(TagRules.Split("   "));
        Assert.Empty(TagRules.Split(",,"));
    }

    [Fact]
    public void 拆分_不按空格拆()
    {
        // §5.8 要求标签内部的空格换成 -。若这里按空格拆，「to read」会变成两个标签，
        // 与那条规则直接打架。
        Assert.Equal(["to-read"], TagRules.Split("to read"));
    }

    [Fact]
    public void 拆分_不执行长度上限()
    {
        // 长度上限是输入校验（拒绝并提示），不是这里的职责。读文件时套用它
        // 等于把用户已有的长标签直接丢掉。超过上限的标签必须原样交出去，
        // 由调用方决定怎么拒绝。
        string longTag = new('x', TagRules.MaxLength + 10);

        string only = Assert.Single(TagRules.Split(longTag));

        Assert.Equal(longTag, only);
    }
}
