using System.Windows;
using System.Windows.Controls;
using MyRPA.Studio.Services;

namespace MyRPA.Studio.Dialogs;

/// <summary>Asks for one line of text (e.g. the value of a new Switch case).</summary>
internal sealed class TextPromptDialog : Window
{
    private readonly TextBox _input;

    public TextPromptDialog(string title, string prompt, string initialValue)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _input = new TextBox { Text = initialValue, Margin = new Thickness(0, 6, 0, 12) };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_input);
        panel.Children.Add(DialogButtons.Create(this));
        Content = panel;
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>The entered text.</summary>
    public string Value => _input.Text;
}

/// <summary>Asks for the values of a workflow's input arguments before a run (blank keeps the default).</summary>
internal sealed class ArgumentsDialog : Window
{
    private readonly Dictionary<string, TextBox> _inputs = new(StringComparer.Ordinal);

    public ArgumentsDialog(IReadOnlyList<ArgumentPrompt> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Title = "Run workflow";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var grid = new Grid { Margin = new Thickness(0, 8, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var argument in arguments)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;
            var label = new TextBlock
            {
                Text = $"{argument.Name}{(argument.Required ? " *" : string.Empty)}  ({argument.Type})",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 10, 3),
            };
            var input = new TextBox
            {
                Margin = new Thickness(0, 3, 0, 3),
                ToolTip = argument.DefaultJson is null ? "No default" : $"Default: {argument.DefaultJson} (leave blank to use it)",
            };
            AutomationName(input, argument.Name);
            Grid.SetRow(label, row);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);
            grid.Children.Add(label);
            grid.Children.Add(input);
            _inputs[argument.Name] = input;
        }

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "Values are text: 42, 3.5, true, 2026-01-31, or JSON for lists and dictionaries. Blank uses the default.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        panel.Children.Add(grid);
        panel.Children.Add(DialogButtons.Create(this, "Run"));
        Content = panel;
        Loaded += (_, _) => _inputs.Values.FirstOrDefault()?.Focus();
    }

    /// <summary>Entered values by argument name.</summary>
    public IReadOnlyDictionary<string, string> Values => _inputs.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal);

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}

/// <summary>OK/Cancel buttons for the small dialogs.</summary>
internal static class DialogButtons
{
    public static StackPanel Create(Window window, string okText = "OK")
    {
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        return buttons;
    }
}
