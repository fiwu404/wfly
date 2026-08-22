using System.Text.Json;
using WFly.UI.Controls;

namespace WFly.UI.Dialogs;

/// <summary>
/// Small shared helpers for the editor dialogs. They deliberately do not know
/// about stores or persistence so a caller can decide when to save a result.
/// </summary>
internal static class DialogControls
{
    public static RoundedButton CreatePrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = UiPalette.Accent,
        ForeColor = Color.White,
        HoverBackColor = Color.FromArgb(54, 98, 207),
        PressedBackColor = Color.FromArgb(43, 83, 178),
        CornerRadius = 9,
        Padding = new Padding(13, 5, 13, 5),
    };

    public static RoundedButton CreateSecondaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = UiPalette.Hover,
        ForeColor = UiPalette.Ink,
        HoverBackColor = UiPalette.AccentSoft,
        PressedBackColor = Color.FromArgb(214, 227, 251),
        BorderColor = UiPalette.CardBorder,
        BorderThickness = 1,
        CornerRadius = 9,
        Padding = new Padding(13, 5, 13, 5),
    };

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
        var textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            AcceptsReturn = multiline,
            WordWrap = !multiline,
            Margin = new Padding(0, 4, 0, 4)
        };
        UiControlTheme.ApplyTextBox(textBox);
        return textBox;
    }

    public static ComboBox CreateComboBox()
    {
        var comboBox = UiControlTheme.CreateComboBox();
        comboBox.Dock = DockStyle.Fill;
        comboBox.Margin = new Padding(0, 4, 0, 4);
        return comboBox;
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
