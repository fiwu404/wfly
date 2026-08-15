using System.Text.Json;

namespace WFly.UI.Dialogs;

/// <summary>
/// Small shared helpers for the editor dialogs. They deliberately do not know
/// about stores or persistence so a caller can decide when to save a result.
/// </summary>
internal static class DialogControls
{
    public static Label CreateLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 12, 7)
        };
    }

    public static TextBox CreateTextBox(bool multiline = false)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            AcceptsReturn = multiline,
            WordWrap = !multiline,
            Margin = new Padding(0, 4, 0, 4)
        };
    }

    public static ComboBox CreateComboBox()
    {
        return new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 4, 0, 4)
        };
    }

    public static FlowLayoutPanel CreateButtonPanel(Button okButton, Button cancelButton)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 12, 12),
            WrapContents = false
        };

        cancelButton.AutoSize = true;
        cancelButton.DialogResult = DialogResult.Cancel;
        okButton.AutoSize = true;
        panel.Controls.Add(cancelButton);
        panel.Controls.Add(okButton);
        return panel;
    }

    public static void AddLabeledControl(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.RowStyles.Add(new RowStyle(control is TextBox { Multiline: true } ? SizeType.Percent : SizeType.AutoSize,
            control is TextBox { Multiline: true } ? 100 : 0));
        layout.Controls.Add(CreateLabel(label), 0, row);
        layout.Controls.Add(control, 1, row);
    }

    public static string? NullIfWhiteSpace(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static bool IsValidJson(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static void ShowValidationError(IWin32Window owner, string message)
    {
        MessageBox.Show(owner, message, "WFly", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
