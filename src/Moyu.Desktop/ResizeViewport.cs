namespace Moyu.Desktop;

// While dragging, change the window's viewport without giving FlowDocument a new
// wrapping width. Only the existing visible content is clipped or transformed.
internal sealed class ResizeViewport : Decorator
{
    private Size? frozenSize;

    public void FreezeLayout(Size size) => frozenSize = size;

    public void ResumeLayout()
    {
        frozenSize = null;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (frozenSize is not { } size) return base.MeasureOverride(constraint);
        Child.Measure(size);
        return new Size(Math.Min(size.Width, constraint.Width), Math.Min(size.Height, constraint.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child.Arrange(new Rect(frozenSize ?? arrangeSize));
        return arrangeSize;
    }
}
