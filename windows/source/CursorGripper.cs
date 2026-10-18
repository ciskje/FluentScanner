using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FluentScanner;

/// Column-resize gripper: the boundary handle between two header cells.
/// WinUI 3 has no public per-element cursor API and `Rectangle`/`Border` are
/// sealed, so this is a `Grid` subclass that can set the protected cursor:
/// hovering the column boundary shows the west-east resize arrow.
/// (The former route — `Window.Current.CoreWindow.PointerCursor` — silently
/// did nothing: WinUI 3 desktop apps have no CoreWindow, so it was null.)
public sealed class CursorGripper : Grid
{
    public CursorGripper()
    {
        ApplyCursor();
        // The cursor only sticks once the element is in the visual tree;
        // setting it in the constructor alone can be applied too early.
        Loaded += (_, _) => ApplyCursor();
    }

    private void ApplyCursor() =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}
