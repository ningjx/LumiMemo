using LumiText.Core.Documents;

namespace LumiText.Core.Editing.Commands;

/// <summary>
/// 图片拖动改锚点命令（Phase 3 设计 §5/§6.3）：把 blockIndex 处浮动图片的锚点改为
/// <paramref name="Anchor"/>、浮动侧改为 <paramref name="Side"/>，并按
/// <paramref name="AnchorToChar"/> 决定横向落位（true = 紧跟锚字符之后，拖放落点语义）。
/// 拖动期间的实时预览走排版层的 Rect 直给路径（不进模型）；本命令是松手时的唯一提交，
/// 因此拖动全程只产生一条撤销记录。
/// </summary>
public sealed record MoveImageAnchorCommand(int BlockIndex, FloatAnchor Anchor, FloatSide Side,
    bool AnchorToChar = false) : IEditCommand
{
    public string Kind => "move-image-anchor";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        if (BlockIndex < 0 || BlockIndex >= blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockIndex));
        }
        if (blocks[BlockIndex] is not ImageBlock image)
        {
            return state; // 目标已不是图片（拖动瞬间文档已变）：不产生变化，不进历史
        }

        // 紧跟锚字符的图片不留外边距：4dip 的排除区外扩会把锚字符所在行的尾巴挤出
        // （末字被绕到图片右侧——A1 探针实测）。贴缘浮动（粘贴默认）保留原有边距。
        float margin = AnchorToChar ? 0f : image.Float?.Margin ?? 0f;

        if (image.Float is { } old
            && old.Anchor == Anchor && old.Side == Side
            && old.AnchorToChar == AnchorToChar && old.Margin == margin)
        {
            return state; // 锚点/侧/横向落位/边距都没变：无变化不进历史
        }

        var placement = (image.Float ?? new FloatPlacement(Side)) with
        {
            Side = Side,
            Anchor = Anchor,
            AnchorToChar = AnchorToChar,
            Position = null, // 锚定与直给二选一：锚定生效时清掉直给坐标
            Margin = margin,
        };

        var newBlocks = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = i == BlockIndex
                ? image with { Float = placement }
                : blocks[i];
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }
}

/// <summary>
/// 图片显示尺寸命令（Phase 3 设计 §5/§6.3）：改 <see cref="ImageBlock"/> 的显示尺寸（dip）。
/// 拖动左/上侧手柄时左上角会移动，<paramref name="Anchor"/> 非空即同时把锚点改到新位置——
/// 尺寸与锚点一次提交 = 一条撤销记录，且重锚后图片紧跟锚字符、不留外边距（与拖动落点同语义）。
/// 缩放拖动期间覆盖层只动预览几何，松手提交本命令后一次重排——与旧产品的提交语义一致。
/// </summary>
public sealed record ResizeImageCommand(int BlockIndex, float Width, float Height,
    FloatAnchor? Anchor = null) : IEditCommand
{
    public string Kind => "resize-image";

    public EditorState Apply(EditorState state)
    {
        var blocks = state.Document.Blocks;
        if (BlockIndex < 0 || BlockIndex >= blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockIndex));
        }
        if (Width <= 0f || Height <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "图片尺寸必须为正。");
        }
        if (blocks[BlockIndex] is not ImageBlock image)
        {
            return state;
        }

        var resized = Anchor is { } anchor
            ? image with
            {
                Width = Width,
                Height = Height,
                Float = (image.Float ?? new FloatPlacement(FloatSide.Right)) with
                {
                    Anchor = anchor,
                    AnchorToChar = true, // 重锚语义：X 随锚字符走
                    Position = null,     // 锚定与直给二选一
                    Margin = 0f,         // 紧跟锚字符的图片不留边距（见 MoveImageAnchorCommand）
                },
            }
            : image with { Width = Width, Height = Height };

        if (Math.Abs(resized.Width - image.Width) < 0.01f
            && Math.Abs(resized.Height - image.Height) < 0.01f
            && resized.Float == image.Float)
        {
            return state; // 尺寸与锚点都没变：不进历史
        }

        var newBlocks = new Block[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            newBlocks[i] = i == BlockIndex ? resized : blocks[i];
        }
        return new EditorState(
            state.Document with { Blocks = newBlocks },
            state.Selection);
    }
}
