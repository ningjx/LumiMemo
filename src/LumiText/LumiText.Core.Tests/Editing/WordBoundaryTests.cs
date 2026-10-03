using LumiText.Core.Editing;
using Xunit;

namespace LumiText.Core.Tests.Editing;

/// <summary>
/// 词边界单测（双击选词的底层展开规则）：英文连续成词、CJK 单字成词、
/// 标点/空格选中自身、边界钳制。
/// </summary>
public sealed class WordBoundaryTests
{
    [Fact]
    public void Expand_EnglishWord_ExpandsToWholeWord()
    {
        var (start, end) = WordBoundary.Expand("hello world", 7); // "world" 内
        Assert.Equal(6, start);
        Assert.Equal(11, end);
    }

    [Fact]
    public void Expand_WordStart_StaysAtWordBoundary()
    {
        var (start, end) = WordBoundary.Expand("hello world", 0);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void Expand_MixedRun_StopsAtScriptChange()
    {
        // 字母与 CJK 都是词字符：现状语义为连续扩展成一段（不含逐语言切词）
        var (start, end) = WordBoundary.Expand("foo中文bar", 4);
        Assert.Equal(0, start);
        Assert.Equal(8, end);
    }

    [Fact]
    public void Expand_Punctuation_SelectsItself()
    {
        var (start, end) = WordBoundary.Expand("a, b", 1);
        Assert.Equal(1, start);
        Assert.Equal(2, end);
    }

    [Fact]
    public void Expand_Space_SelectsItself()
    {
        var (start, end) = WordBoundary.Expand("a b", 1);
        Assert.Equal(1, start);
        Assert.Equal(2, end);
    }

    [Fact]
    public void Expand_CjkSingleChar_ExpandsAcrossRun()
    {
        var (start, end) = WordBoundary.Expand("中文测试", 2);
        Assert.Equal(0, start);
        Assert.Equal(4, end);
    }

    [Fact]
    public void Expand_DigitsAndLetters_OneWord()
    {
        var (start, end) = WordBoundary.Expand("abc123 def", 4);
        Assert.Equal(0, start);
        Assert.Equal(6, end);
    }

    [Fact]
    public void Expand_PositionAtTextEnd_ClampsToLastChar()
    {
        // 越界钳到末字符（词字符）→ 扩成完整词 "ab"
        var (start, end) = WordBoundary.Expand("ab", 2);
        Assert.Equal(0, start);
        Assert.Equal(2, end);
    }

    [Fact]
    public void Expand_EmptyText_ReturnsZero()
    {
        Assert.Equal((0, 0), WordBoundary.Expand("", 0));
    }
}
