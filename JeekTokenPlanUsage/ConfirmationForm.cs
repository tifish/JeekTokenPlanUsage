namespace JeekTokenPlanUsage;

/// Modeless confirmation: the agent can poll while the user decides in the GUI.
internal sealed class ConfirmationForm : Form
{
    public Task<DialogResult> Completion => _completion.Task;
    private readonly TaskCompletionSource<DialogResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmationForm(string title, string message, string yes, string no, string? cancel = null)
    {
        Text = title;
        Name = "UserConfirmation";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.ControlText;
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 18) });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        Button Add(string text, string name, DialogResult result)
        {
            var button = new Button { Text = text, Name = name, AutoSize = true, MinimumSize = new Size(90, 30) };
            button.Click += (_, _) => { DialogResult = result; Close(); };
            buttons.Controls.Add(button);
            return button;
        }
        Add(yes, "Confirm", DialogResult.Yes);
        var decline = Add(no, "Decline", DialogResult.No);
        CancelButton = cancel is null ? decline : Add(cancel, "Cancel", DialogResult.Cancel);
        // Avoid approving migration or restart through an accidental Enter key.
        AcceptButton = CancelButton;
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _completion.TrySetResult(DialogResult == DialogResult.None ? DialogResult.Cancel : DialogResult);
        base.OnFormClosed(e);
    }
}
