using LumiMemo.Infrastructure.Storage;
using Xunit;

namespace LumiMemo.Integration.Tests.Storage;

/// <summary>
/// <see cref="NoteFileNameBuilder"/> 的测试（§5.6、§19.4）。
/// </summary>
/// <remarks>
/// 纯函数，不碰文件系统——「文件是否已存在」是通过谓词传进去的。
/// </remarks>
public sealed class NoteFileNameBuilderTests
{
    private static readonly Guid SampleId = Guid.Parse("a3f2b1c8-1111-2222-3333-444455556666");

    private static readonly DateTimeOffset SampleCreated =
        new(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(8));

    private const string NotesRoot = @"C:\笔记";

    [Fact]
    public void 标题为空_用无标题()
    {
        Assert.Equal("无标题", NoteFileNameBuilder.SanitizeSlug(string.Empty));
        Assert.Equal("无标题", NoteFileNameBuilder.SanitizeSlug("   "));
    }

    [Fact]
    public void 短ID是id的前八位小写十六进制()
    {
        Assert.Equal("a3f2b1c8", NoteFileNameBuilder.ShortId(SampleId));
        Assert.All(NoteFileNameBuilder.ShortId(Guid.NewGuid()), c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void 完整文件名符合文档给的形态()
    {
        string path = NoteFileNameBuilder.Build("Docker 常用命令", SampleCreated, SampleId, NotesRoot, NotesRoot);

        Assert.Equal(@"C:\笔记\Docker-常用命令-20260919-a3f2b1c8.md", path);
    }

    [Fact]
    public void 非法字符被删掉()
    {
        // §5.6：删除 < > : " / \ | ? * 与控制字符。剩下的字一个不少地连在一起。
        Assert.Equal("abcdefghij", NoteFileNameBuilder.SanitizeSlug("a<b>c:d\"e/f\\g|h?i*j"));
    }

    [Fact]
    public void 连续的空白与连字符折叠成一个()
    {
        Assert.Equal("a-b", NoteFileNameBuilder.SanitizeSlug("a \t\n- b"));
        Assert.Equal("a-b", NoteFileNameBuilder.SanitizeSlug("a---b"));
        Assert.Equal("a-b", NoteFileNameBuilder.SanitizeSlug("a   b"));
    }

    [Fact]
    public void 首尾的点空格连字符被去掉()
    {
        Assert.Equal("标题", NoteFileNameBuilder.SanitizeSlug("... 标题 ---"));
        Assert.Equal("标题", NoteFileNameBuilder.SanitizeSlug("-标题-"));
    }

    [Fact]
    public void 超长标题截断到四十个字符()
    {
        string slug = NoteFileNameBuilder.SanitizeSlug(new string('字', 100));

        Assert.Equal(40, slug.Length);

        // 截断之后不能再留下上一段的分隔符，否则会得到「标题--20260919-...」这种双划线段。
        string withDash = NoteFileNameBuilder.SanitizeSlug(new string('a', 39) + "-尾巴");
        Assert.DoesNotContain("--", withDash, StringComparison.Ordinal);
    }

    [Fact]
    public void 保留字追加下划线()
    {
        Assert.Equal("CON_", NoteFileNameBuilder.SanitizeSlug("CON"));
        Assert.Equal("con_", NoteFileNameBuilder.SanitizeSlug("con"));
        Assert.True(NoteFileNameBuilder.IsReservedName("NUL"));
        Assert.True(NoteFileNameBuilder.IsReservedName("com9"));
    }

    [Fact]
    public void 带扩展名的保留字同样算命中()
    {
        Assert.True(NoteFileNameBuilder.IsReservedName("CON.md"));
        Assert.False(NoteFileNameBuilder.IsReservedName("CONSOLE"));
    }

    [Fact]
    public void 重名时依次追加序号()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\笔记\标题-20260919-a3f2b1c8.md",
            @"C:\笔记\标题-20260919-a3f2b1c8-2.md",
        };

        string path = NoteFileNameBuilder.Build("标题", SampleCreated, SampleId, NotesRoot, NotesRoot, taken.Contains);

        Assert.Equal(@"C:\笔记\标题-20260919-a3f2b1c8-3.md", path);
    }

    [Fact]
    public void 标题是点点时_文件名仍然落在笔记目录内()
    {
        // ".." 不含任何非法字符，光靠洗字符是挡不住的；这里确认它到不了文件名里（§19.4）。
        string path = NoteFileNameBuilder.Build("..", SampleCreated, SampleId, NotesRoot, NotesRoot);

        Assert.StartsWith(NotesRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void 路径校验在规范化之后比较_点名要挡住点点穿越()
    {
        Assert.False(NoteFileNameBuilder.IsInsideNotesRoot(
            @"C:\笔记\..\Windows\System32\坏东西.md",
            @"C:\笔记"));

        Assert.True(NoteFileNameBuilder.IsInsideNotesRoot(
            @"C:\笔记\工作\周报.md",
            @"C:\笔记\工作"));

        // 前缀相同但其实是兄弟目录，不算在里面。
        Assert.False(NoteFileNameBuilder.IsInsideNotesRoot(
            @"C:\笔记备份\周报.md",
            @"C:\笔记"));
    }

    [Fact]
    public void 兜底名不含任何用户输入()
    {
        // 走到兜底名意味着派生出来的路径不可信，因此这个名字必须完全由程序决定。
        string path = NoteFileNameBuilder.Build("正常标题", SampleCreated, SampleId, NotesRoot, @"D:\别的目录");

        Assert.Equal(@"C:\笔记\untitled-a3f2b1c8.md", path);
    }
}
