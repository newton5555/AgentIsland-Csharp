using System.Windows;
using AgentIsland.UI.Localization;

namespace AgentIsland.UI;

public partial class NamePromptWindow : Window
{
    private bool _accepted;

    public NamePromptWindow()
    {
        InitializeComponent();
        CancelButton.Label = L10n.Tr("Cancel");
        CancelButton.Clicked += Close;
        ConfirmButton.Clicked += Accept;

        PromptField.TextChanged += (_, _) =>
            PromptPlaceholder.Visibility = PromptField.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter) Accept();
            else if (args.Key == System.Windows.Input.Key.Escape) Close();
        };
        Loaded += (_, _) => PromptField.Focus();
    }

    public NamePromptWindow(
        string title,
        string message,
        string placeholder,
        string confirmLabel,
        int maxLength,
        string? extraLabel = null,
        Action? extraAction = null) : this()
    {
        Title = title;
        PromptTitle.Text = title;
        PromptMessage.Text = message;
        PromptPlaceholder.Text = placeholder;
        ConfirmButton.Label = confirmLabel;
        PromptField.MaxLength = maxLength;

        if (extraLabel is not null && extraAction is not null)
        {
            ExtraButton.Label = extraLabel;
            ExtraButton.Visibility = Visibility.Visible;
            ExtraButton.Clicked += extraAction;
        }
    }

    private void Accept()
    {
        _accepted = true;
        Close();
    }

    /// The typed text, or null when the prompt was dismissed or left blank.
    public static string? Ask(
        Window? owner,
        string title,
        string message,
        string placeholder,
        string? confirmLabel = null,
        int maxLength = 40,
        string? extraLabel = null,
        Action? extraAction = null)
    {
        var prompt = new NamePromptWindow(
            title,
            message,
            placeholder,
            confirmLabel ?? L10n.Tr("Save"),
            maxLength,
            extraLabel,
            extraAction)
        {
            Owner = owner,
        };
        prompt.ShowDialog();
        if (!prompt._accepted) return null;
        var value = prompt.PromptField.Text.Trim();
        return value.Length == 0 ? null : value;
    }
}
