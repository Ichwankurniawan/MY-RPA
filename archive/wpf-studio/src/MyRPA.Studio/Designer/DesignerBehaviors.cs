using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio.Designer;

/// <summary>
/// Attached behaviors that connect the designer's visuals to <see cref="StudioViewModel"/>: blocks and toolbox items
/// are drag sources, drop zones accept what <see cref="StudioViewModel.CanDrop"/> allows, and clicking a block selects
/// it. All decisions are made by the view model; these only translate WPF input.
/// </summary>
internal static class DesignerBehaviors
{
    /// <summary>The clipboard/drag format of a <see cref="DragPayload"/>.</summary>
    public const string DragFormat = "MyRPA.DragPayload";

    /// <summary>Makes an element a drag source for this payload.</summary>
    public static readonly DependencyProperty DragPayloadProperty = DependencyProperty.RegisterAttached(
        "DragPayload", typeof(DragPayload), typeof(DesignerBehaviors), new PropertyMetadata(null, OnDragPayloadChanged));

    /// <summary>Selects this block when the element is clicked.</summary>
    public static readonly DependencyProperty SelectsProperty = DependencyProperty.RegisterAttached(
        "Selects", typeof(NodeViewModel), typeof(DesignerBehaviors), new PropertyMetadata(null, OnSelectsChanged));

    /// <summary>Makes an element a drop target for this zone.</summary>
    public static readonly DependencyProperty DropZoneProperty = DependencyProperty.RegisterAttached(
        "DropZone", typeof(DropZoneViewModel), typeof(DesignerBehaviors), new PropertyMetadata(null, OnDropZoneChanged));

    private static readonly DependencyProperty _dragStartProperty = DependencyProperty.RegisterAttached(
        "DragStart", typeof(Point?), typeof(DesignerBehaviors), new PropertyMetadata(null));

    /// <summary>Gets the drag payload.</summary>
    public static DragPayload? GetDragPayload(DependencyObject element) => (DragPayload?)element.GetValue(DragPayloadProperty);

    /// <summary>Sets the drag payload.</summary>
    public static void SetDragPayload(DependencyObject element, DragPayload? value) => element.SetValue(DragPayloadProperty, value);

    /// <summary>Gets the block selected on click.</summary>
    public static NodeViewModel? GetSelects(DependencyObject element) => (NodeViewModel?)element.GetValue(SelectsProperty);

    /// <summary>Sets the block selected on click.</summary>
    public static void SetSelects(DependencyObject element, NodeViewModel? value) => element.SetValue(SelectsProperty, value);

    /// <summary>Gets the drop zone.</summary>
    public static DropZoneViewModel? GetDropZone(DependencyObject element) => (DropZoneViewModel?)element.GetValue(DropZoneProperty);

    /// <summary>Sets the drop zone.</summary>
    public static void SetDropZone(DependencyObject element, DropZoneViewModel? value) => element.SetValue(DropZoneProperty, value);

    /// <summary>The Studio view model of the window that contains <paramref name="element"/>.</summary>
    public static StudioViewModel? Studio(DependencyObject element) => Window.GetWindow(element)?.DataContext as StudioViewModel;

    /// <summary>Reads the payload of a drag, if it is one of Studio's.</summary>
    public static DragPayload? Payload(IDataObject data) => data.GetDataPresent(DragFormat) ? data.GetData(DragFormat) as DragPayload : null;

    private static void OnDragPayloadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.OldValue is null && e.NewValue is not null)
        {
            element.PreviewMouseLeftButtonDown += (_, args) => element.SetValue(_dragStartProperty, args.GetPosition(element));
            element.PreviewMouseLeftButtonUp += (_, _) => element.ClearValue(_dragStartProperty);
            element.MouseMove += OnDragSourceMouseMove;
        }
    }

    private static void OnDragSourceMouseMove(object sender, MouseEventArgs e)
    {
        var element = (UIElement)sender;
        if (e.LeftButton != MouseButtonState.Pressed || element.GetValue(_dragStartProperty) is not Point start || GetDragPayload(element) is not { } payload)
        {
            return;
        }

        var delta = e.GetPosition(element) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // The innermost block starts the drag; its ancestors must not start another one.
        e.Handled = true;
        element.ClearValue(_dragStartProperty);
        DragDrop.DoDragDrop(element, new DataObject(DragFormat, payload), payload is MoveNodePayload ? DragDropEffects.Move : DragDropEffects.Copy);
    }

    private static void OnSelectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.OldValue is null && e.NewValue is not null)
        {
            element.MouseLeftButtonDown += (_, args) =>
            {
                if (GetSelects(element) is { } node && Studio(element) is { } studio)
                {
                    studio.Select(node);
                    element.Focus();
                    args.Handled = true; // the innermost block wins
                }
            };
        }
    }

    private static void OnDropZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.OldValue is not null || e.NewValue is null)
        {
            return;
        }

        element.AllowDrop = true;
        element.DragEnter += OnDragOver;
        element.DragOver += OnDragOver;
        element.DragLeave += (_, _) => Highlight(element, false);
        element.Drop += (_, args) =>
        {
            args.Handled = true;
            Highlight(element, false);
            if (GetDropZone(element) is { } zone && Payload(args.Data) is { } payload && Studio(element) is { } studio)
            {
                // Outside the drag-and-drop loop, so the drop may show a dialog (e.g. the case value).
                element.Dispatcher.BeginInvoke(() => studio.Drop(payload, zone), DispatcherPriority.Input);
            }
        };
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var element = (UIElement)sender;
        var accepted = GetDropZone(element) is { } zone && Payload(e.Data) is { } payload && Studio(element) is { } studio && studio.CanDrop(payload, zone);
        e.Effects = !accepted ? DragDropEffects.None : Payload(e.Data) is MoveNodePayload ? DragDropEffects.Move : DragDropEffects.Copy;
        Highlight(element, accepted);
        e.Handled = true;
    }

    private static void Highlight(UIElement element, bool on)
    {
        if (GetDropZone(element) is { } zone)
        {
            zone.IsHighlighted = on;
        }
    }
}

/// <summary>
/// Commits an editor's value (runs <see cref="CommandProperty"/>) when Enter is pressed, when keyboard focus leaves
/// the editor, or when a value is picked from a combo box list. Each commit is one undoable edit.
/// </summary>
internal static class Commit
{
    /// <summary>The command to run.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(Commit), new PropertyMetadata(null, OnCommandChanged));

    /// <summary>Gets the command.</summary>
    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    /// <summary>Sets the command.</summary>
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control || e.OldValue is not null || e.NewValue is null)
        {
            return;
        }

        control.IsKeyboardFocusWithinChanged += (_, args) =>
        {
            if (args.NewValue is false)
            {
                Run(control);
            }
        };
        control.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && !(control is TextBox { AcceptsReturn: true }))
            {
                Run(control);
            }
        };
        if (control is ComboBox combo)
        {
            // After the picked item has been copied into the bound text.
            combo.DropDownClosed += (_, _) => combo.Dispatcher.BeginInvoke(() => Run(combo), DispatcherPriority.Input);
        }
    }

    private static void Run(Control control)
    {
        var binding = control switch
        {
            TextBox => control.GetBindingExpression(TextBox.TextProperty),
            ComboBox => control.GetBindingExpression(ComboBox.TextProperty),
            _ => null,
        };
        binding?.UpdateSource();
        if (GetCommand(control) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
