namespace BluetoothSwitcher.UI;

/// <summary>Dunkles Design für das Tray-Kontextmenü.</summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColors())
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prepare(g);
        var r = e.ImageRectangle;
        r.Inflate(2, 2);
        Theme.FillRounded(g, r, 4, Theme.Accent);
        using var font = Theme.PixelFont(Theme.IconFont, r.Height * 0.6f);
        Theme.DrawTextCentered(g, Theme.GlyphCheck, font, Color.White, r);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.TextSecondary;
        base.OnRenderArrow(e);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(32, 32, 38);
        private static readonly Color Hover = Color.FromArgb(52, 58, 78);
        private static readonly Color Border = Color.FromArgb(58, 58, 68);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Theme.Accent;
        public override Color CheckSelectedBackground => Theme.Accent;
        public override Color CheckPressedBackground => Theme.Accent;
    }
}
