using System.Globalization;
using LumiMemo.Core.Services;
using Xunit;

namespace LumiMemo.Core.Tests.Services;

/// <summary>
/// <see cref="TitleDeriver"/> 的单元测试，逐条对应 §21.2 的「[标题派生]」清单。
/// </summary>
/// <remarks>
/// <para>
/// 标题派生是纯函数，是整个 Core 里性价比最高的测试对象：没有 IO、没有时钟、没有线程，
/// 输入输出一一对应，跑一次不到一毫秒。
/// </para>
/// <para>
/// 文档里占位标题有两种写法——§5.4/§5.6 写「无标题」，§21.2 的测试清单写「无标题便签」。
/// 取多数意见定为「无标题」，并把这个字面量固化在断言里：将来要改，这里会立刻变红，
/// 而不是悄悄换掉用户看到的文案。
/// </para>
/// </remarks>
public sealed class TitleDeriverTests
{
    private const string Untitled = "无标题";

    // ---- 兜底 ----

    [Fact]
    public void 空串_返回占位标题() => Assert.Equal(Untitled, TitleDeriver.Derive(string.Empty));

    [Fact]
    public void null_返回占位标题() => Assert.Equal(Untitled, TitleDeriver.Derive(null));

    [Fact]
    public void 只有空白字符_返回占位标题() =>
        Assert.Equal(Untitled, TitleDeriver.Derive("  \r\n\t\r\n   "));

    [Fact]
    public void 正文为空即只有FrontMatter被剥掉_返回占位标题() =>
        Assert.Equal(Untitled, TitleDeriver.Derive(string.Empty));

    // ---- 基本取行 ----

    [Fact]
    public void 一级标题_剥掉井号() => Assert.Equal("标题", TitleDeriver.Derive("# 标题"));

    [Fact]
    public void 二级标题_层级不影响结果() => Assert.Equal("标题", TitleDeriver.Derive("## 标题"));

    [Fact]
    public void 六级标题_同样剥掉() => Assert.Equal("标题", TitleDeriver.Derive("###### 标题"));

    [Fact]
    public void 前置空行_跳到第一行有内容的行() => Assert.Equal("内容", TitleDeriver.Derive("\r\n\r\n  内容\r\n第二行"));

    [Fact]
    public void 前置空格_被去掉() => Assert.Equal("内容", TitleDeriver.Derive("     内容"));

    [Fact]
    public void 普通段落_原样取第一行() => Assert.Equal("今天要买的东西", TitleDeriver.Derive("今天要买的东西\r\n- 牛奶"));

    // ---- 需要跳过的行 ----

    [Fact]
    public void 整行是HTML注释_跳过() =>
        Assert.Equal("真标题", TitleDeriver.Derive("<!-- 这是备注 -->\r\n真标题"));

    [Fact]
    public void 单独成行的图片_跳过() =>
        Assert.Equal("真标题", TitleDeriver.Derive("![截图](attachments/a.png)\r\n真标题"));

    [Fact]
    public void 表格分隔行_跳过() =>
        Assert.Equal("姓名", TitleDeriver.Derive("|---|:--:|---|\r\n姓名"));

    [Fact]
    public void 水平分隔线_跳过() =>
        Assert.Equal("真标题", TitleDeriver.Derive("---\r\n真标题"));

    [Fact]
    public void 内容只有一行井号_返回占位标题() => Assert.Equal(Untitled, TitleDeriver.Derive("#"));

    [Fact]
    public void 全部行都被跳过_返回占位标题() =>
        Assert.Equal(Untitled, TitleDeriver.Derive("<!-- 一 -->\r\n![图](a.png)\r\n---"));

    // ---- 代码围栏要跳过整块 ----

    [Fact]
    public void 以代码围栏开头_跳到围栏块之后() =>
        Assert.Equal("真标题", TitleDeriver.Derive("```\r\nvar x = 1;\r\n```\r\n# 真标题"));

    [Fact]
    public void 代码围栏带语言标记_同样跳过整块() =>
        Assert.Equal("真标题", TitleDeriver.Derive("```csharp\r\nvar x = 1;\r\n```\r\n真标题"));

    [Fact]
    public void 波浪号围栏_同样跳过整块() =>
        Assert.Equal("真标题", TitleDeriver.Derive("~~~\r\ncode\r\n~~~\r\n真标题"));

    [Fact]
    public void 围栏收尾必须同种字符_否则不结束() =>
        Assert.Equal("真标题", TitleDeriver.Derive("```\r\n~~~\r\nstill\r\n```\r\n真标题"));

    // ---- 行内标记剥离 ----

    [Fact]
    public void 引用块标记_剥掉() => Assert.Equal("引用", TitleDeriver.Derive("> 引用"));

    [Fact]
    public void 嵌套引用块_全部剥掉() => Assert.Equal("引用", TitleDeriver.Derive("> > > 引用"));

    [Fact]
    public void 无序列表标记_剥掉() => Assert.Equal("买牛奶", TitleDeriver.Derive("- 买牛奶"));

    [Fact]
    public void 星号列表标记_剥掉() => Assert.Equal("买牛奶", TitleDeriver.Derive("* 买牛奶"));

    [Fact]
    public void 有序列表标记_剥掉() => Assert.Equal("第一步", TitleDeriver.Derive("1. 第一步"));

    [Fact]
    public void 任务列表_剥掉复选框() => Assert.Equal("待办", TitleDeriver.Derive("- [ ] 待办"));

    [Fact]
    public void 已完成任务列表_剥掉复选框() => Assert.Equal("待办", TitleDeriver.Derive("- [x] 待办"));

    [Fact]
    public void 引用加列表加复选框_全剥掉() => Assert.Equal("待办", TitleDeriver.Derive("> - [ ] 待办"));

    [Fact]
    public void 行内链接_保留显示文字() =>
        Assert.Equal("文档", TitleDeriver.Derive("[文档](https://example.com/a.md)"));

    [Fact]
    public void 行内代码_去掉反引号() => Assert.Equal("Console.WriteLine", TitleDeriver.Derive("`Console.WriteLine`"));

    [Fact]
    public void 粗体_去掉星号() => Assert.Equal("重点", TitleDeriver.Derive("**重点**"));

    [Fact]
    public void 下划线粗体_去掉下划线() => Assert.Equal("重点", TitleDeriver.Derive("__重点__"));

    [Fact]
    public void 删除线_去掉波浪号() => Assert.Equal("作废", TitleDeriver.Derive("~~作废~~"));

    [Fact]
    public void 斜体_去掉星号() => Assert.Equal("强调", TitleDeriver.Derive("*强调*"));

    [Fact]
    public void 行内HTML标签_去掉标签保留文字() =>
        Assert.Equal("加粗", TitleDeriver.Derive("<b>加粗</b>"));

    [Fact]
    public void 连续空白_折叠成单个空格() =>
        Assert.Equal("a b", TitleDeriver.Derive("a      b"));

    // ---- 截断 ----

    [Fact]
    public void 超长标题_截断到60个字符()
    {
        var result = TitleDeriver.Derive(new string('a', 200));

        Assert.Equal(TitleDeriver.MaxLength, result.Length);
        Assert.Equal(new string('a', 60), result);
    }

    [Fact]
    public void 刚好60个字符_不截断()
    {
        var source = new string('a', 60);

        Assert.Equal(source, TitleDeriver.Derive(source));
    }

    [Fact]
    public void 超长标题_按文本元素截断_不切出半个emoji()
    {
        // 每个 emoji 由两个 char（代理对）组成。按 char 截断会切出半个代理对，
        // 结果是乱码方块——这正是 MaxLength 要按文本元素计数的原因（§5.4 第 4 步）。
        var result = TitleDeriver.Derive(string.Concat(Enumerable.Repeat("😀", 100)));

        Assert.Equal(TitleDeriver.MaxLength, new StringInfo(result).LengthInTextElements);
        Assert.All(
            result.EnumerateRunes(),
            rune => Assert.NotEqual(0xFFFD, rune.Value));

        // 没有落单的代理项
        for (var i = 0; i < result.Length; i++)
        {
            if (char.IsHighSurrogate(result[i]))
            {
                Assert.True(i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(result[i]));
            }
        }
    }

    [Fact]
    public void 中文字符串_按字符计数()
    {
        var source = new string('中', 100);

        Assert.Equal(60, TitleDeriver.Derive(source).Length);
    }
}
