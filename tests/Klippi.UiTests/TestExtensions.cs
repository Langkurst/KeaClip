using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Klippi.UiTests;

internal static class TestExtensions
{
    // Pages are separate UserControls with their own name scopes, so look up named
    // controls through the logical tree instead of the window's name scope.
    internal static T? FindNamed<T>(this Control root, string name) where T : Control
        => root.Name == name && root is T self ? self : root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
}
