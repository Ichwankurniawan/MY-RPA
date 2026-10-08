using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyRPA.Core.Activities;

namespace MyRPA.Studio.ViewModels;

/// <summary>
/// The Activities pane: every registered activity (built-in and from plugins), grouped by category, filtered by search.
/// Built from <see cref="IActivityCatalog"/>, so new plugins appear without Studio changes.
/// </summary>
public sealed partial class ToolboxViewModel : ObservableObject
{
    private readonly IReadOnlyList<ToolboxItemViewModel> _all;

    /// <summary>Creates the toolbox.</summary>
    /// <param name="catalog">Registered activities.</param>
    public ToolboxViewModel(IActivityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _all = [.. catalog.Descriptors.Select(d => new ToolboxItemViewModel(d))];
        Refresh();
    }

    /// <summary>Visible categories with their items.</summary>
    public ObservableCollection<ToolboxCategoryViewModel> Categories { get; } = [];

    /// <summary>Search text (matches display name, type name, category or description).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Every item, unfiltered.</summary>
    public IReadOnlyList<ToolboxItemViewModel> AllItems => _all;

    partial void OnSearchTextChanged(string value) => Refresh();

    private void Refresh()
    {
        var search = SearchText.Trim();
        var visible = _all.Where(i => search.Length == 0 || i.Matches(search));
        Categories.Clear();
        foreach (var group in visible.GroupBy(i => i.Category).OrderBy(g => CategoryOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            Categories.Add(new ToolboxCategoryViewModel(group.Key, [.. group.OrderBy(i => i.DisplayName, StringComparer.CurrentCulture)]));
        }
    }

    // Built-in categories first, in a sensible order; plugin categories after them, alphabetically.
    private static int CategoryOrder(string category) => category switch
    {
        "Control Flow" => 0,
        "Data" => 1,
        "Workflow" => 2,
        "Diagnostics" => 3,
        _ => 10,
    };
}

/// <summary>A category of the toolbox.</summary>
/// <param name="name">Category name.</param>
/// <param name="items">Items.</param>
public sealed class ToolboxCategoryViewModel(string name, IReadOnlyList<ToolboxItemViewModel> items)
{
    /// <summary>Category name.</summary>
    public string Name { get; } = name;

    /// <summary>Items.</summary>
    public IReadOnlyList<ToolboxItemViewModel> Items { get; } = items;
}

/// <summary>One activity type in the toolbox.</summary>
/// <param name="descriptor">Its metadata.</param>
public sealed class ToolboxItemViewModel(ActivityDescriptor descriptor)
{
    /// <summary>Activity metadata.</summary>
    public ActivityDescriptor Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    /// <summary>Type name.</summary>
    public string TypeName => Descriptor.TypeName.Value;

    /// <summary>Display name.</summary>
    public string DisplayName => Descriptor.DisplayName;

    /// <summary>Category.</summary>
    public string Category => Descriptor.Category;

    /// <summary>Description (tooltip).</summary>
    public string ToolTip => $"{Descriptor.TypeName}\n{Descriptor.Description}".TrimEnd();

    /// <summary>The drag payload.</summary>
    public DragPayload Payload { get; } = new NewActivityPayload(descriptor.TypeName);

    /// <summary>Whether the item matches a search.</summary>
    /// <param name="search">Search text.</param>
    public bool Matches(string search) =>
        DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || TypeName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Category.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || (Descriptor.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
