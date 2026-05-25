namespace JeekTokenPlanUsage;

/// Context-menu renderer driven entirely by SystemColors, read fresh on each
/// paint. TrayApplicationContext re-applies Application.SetColorMode on every OS
/// light/dark switch, which re-maps SystemColors, so the menu is correctly themed
/// whenever it next opens — without hardcoding any color.
internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new SystemColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? SystemColors.GrayText
            : e.Item.Selected ? SystemColors.HighlightText
            : SystemColors.ControlText;
        base.OnRenderItemText(e);
    }

    private sealed class SystemColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => SystemColors.Window;
        public override Color ImageMarginGradientBegin => SystemColors.Window;
        public override Color ImageMarginGradientMiddle => SystemColors.Window;
        public override Color ImageMarginGradientEnd => SystemColors.Window;
        public override Color MenuBorder => SystemColors.ControlDark;
        public override Color MenuItemBorder => SystemColors.Highlight;
        public override Color MenuItemSelected => SystemColors.Highlight;
        public override Color MenuItemSelectedGradientBegin => SystemColors.Highlight;
        public override Color MenuItemSelectedGradientEnd => SystemColors.Highlight;
        public override Color MenuItemPressedGradientBegin => SystemColors.Window;
        public override Color MenuItemPressedGradientEnd => SystemColors.Window;
        public override Color SeparatorDark => SystemColors.ControlDark;
        public override Color SeparatorLight => SystemColors.ControlDark;
        public override Color CheckBackground => SystemColors.Highlight;
        public override Color CheckSelectedBackground => SystemColors.Highlight;
        public override Color CheckPressedBackground => SystemColors.Highlight;
    }
}
