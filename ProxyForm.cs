using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Small modal dialog for the custom-proxy fields (protocol, host, port).
/// Edits a copy of the values and only writes them back to the caller on OK.
/// Colors come from SystemColors so it follows the OS light/dark theme that
/// Program.Main applies via Application.SetColorMode.
internal sealed class ProxyForm : Form
{
    private static readonly string[] Protocols = { "socks5", "http" };

    private readonly ComboBox _protocol;
    private readonly TextBox _host;
    private readonly NumericUpDown _port;

    public string Protocol => (_protocol.SelectedItem as string) ?? "http";
    public string Host => _host.Text.Trim();
    public int Port => (int)_port.Value;

    public ProxyForm(string protocol, string host, int port)
    {
        Text = Strings.Proxy_DialogTitle;
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
            ColumnCount = 2,
            RowCount = 4,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _protocol = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 160,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 8),
        };
        _protocol.Items.AddRange(Protocols);
        _protocol.SelectedItem = Array.IndexOf(Protocols, protocol?.ToLowerInvariant()) >= 0
            ? protocol!.ToLowerInvariant()
            : "http";

        _host = new TextBox
        {
            Text = host,
            Width = 160,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 8),
        };

        _port = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(port, 1, 65535),
            Width = 160,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 12),
        };

        layout.Controls.Add(MakeLabel(Strings.Proxy_Protocol), 0, 0);
        layout.Controls.Add(_protocol, 1, 0);
        layout.Controls.Add(MakeLabel(Strings.Proxy_Host), 0, 1);
        layout.Controls.Add(_host, 1, 1);
        layout.Controls.Add(MakeLabel(Strings.Proxy_Port), 0, 2);
        layout.Controls.Add(_port, 1, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Margin = new Padding(6, 0, 0, 0),
        };
        var cancel = new Button
        {
            Text = Strings.Proxy_Cancel,
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 3);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static Label MakeLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 4, 12, 8),
    };
}
