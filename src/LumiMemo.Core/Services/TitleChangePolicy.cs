namespace LumiMemo.Core.Services;

/// <summary>识别首次成文或正文被整体替换，避免逐字编辑时反复调用模型。</summary>
public static class TitleChangePolicy
{
    public static bool ShouldGenerate(string baseline, string current, bool hasGeneratedTitle)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        string before = baseline.Trim();
        string after = current.Trim();
        if (after.EnumerateRunes().Count() < 12 || string.Equals(before, after, StringComparison.Ordinal))
        {
            return false;
        }

        if (before.Length == 0)
        {
            return !hasGeneratedTitle;
        }

        if (before.EnumerateRunes().Count() < 12)
        {
            return !hasGeneratedTitle;
        }

        int prefix = 0;
        while (prefix < System.Math.Min(before.Length, after.Length) && before[prefix] == after[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < System.Math.Min(before.Length, after.Length) - prefix
               && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }

        return prefix + suffix < before.Length * 0.4;
    }
}
