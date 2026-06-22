using System.Diagnostics;
using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Small modal "About" dialog: app name, build identity, and a clickable link
/// to the project home page. Colors come from SystemColors so it follows the
/// OS light/dark theme that Program.Main applies via Application.SetColorMode.
internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = Strings.Menu_About;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.ControlText;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
        };

        var name = new Label
        {
            AutoSize = true,
            Text = "JeekTokenPlanUsage",
            Font = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };

        int build = AutoUpdate.LocalBuild;
        var version = new Label
        {
            AutoSize = true,
            Text = build > 0
                ? string.Format(Strings.About_VersionFormat, build)
                : Strings.About_DevBuild,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10),
        };

        var link = new LinkLabel
        {
            AutoSize = true,
            Text = Strings.About_ProjectLink,
            Margin = new Padding(0, 0, 0, 12),
        };
        link.LinkClicked += (_, _) => OpenUrl(AutoUpdate.RepoUrl);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };

        layout.Controls.Add(name);
        layout.Controls.Add(version);
        layout.Controls.Add(link);
        layout.Controls.Add(ok);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = ok;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "JeekTokenPlanUsage",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
