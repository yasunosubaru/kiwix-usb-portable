using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace KiwixApple.Views;

public enum AlertKind { Info, Success, Warning, Error }

/// <summary>
/// The ContentDialog equivalent for this app: a centred sheet with a glyph, a
/// title, a message and one or two tinted text actions, over a scrim.
///
/// Deliberately not a MessageBox. A MessageBox is a system chrome dialog with
/// its own title bar, its own button order and a Windows look; it cannot carry
/// an icon, it cannot follow the dark appearance, and it cannot be styled at
/// all. The product's own contract is that every consequence of pressing start
/// is explained calmly, in the same visual language as the rest of the window.
/// </summary>
public partial class AppleAlert : UserControl
{
    /// <summary>
    /// Geometries rather than glyphs. U+2713 and friends are missing from some
    /// font stacks and turn into tofu; a stroked path is always the same shape.
    /// </summary>
    private const string InfoGlyph = "M 12,6.6 L 12,6.7 M 12,12 L 12,25";
    private const string SuccessGlyph = "M 2.5,13.5 L 9.5,20.5 L 21.5,5";
    private const string WarningGlyph = "M 12,3.5 L 12,16 M 12,21.4 L 12,22";
    private const string ErrorGlyph = "M 3.5,3.5 L 20.5,20.5 M 20.5,3.5 L 3.5,20.5";

    private TaskCompletionSource<bool>? _pending;

    public AppleAlert()
    {
        InitializeComponent();
        Scrim.PointerPressed += OnScrimPressed;
        SecondaryAction.Click += (_, _) => Settle(false);
        PrimaryAction.Click += (_, _) => Settle(true);
    }

    public bool IsShowing => _pending is not null;

    /// <summary>Two-action sheet. True when the confirming action was taken.</summary>
    public Task<bool> ConfirmAsync(AlertKind kind, string title, string message,
                                   string confirmText, string cancelText = "取消",
                                   bool destructive = false)
    {
        Present(kind, title, message, confirmText, cancelText, destructive);
        return _pending!.Task;
    }

    /// <summary>Acknowledgement only.</summary>
    public async Task NotifyAsync(AlertKind kind, string title, string message, string okText = "好")
    {
        await ConfirmAsync(kind, title, message, okText, string.Empty).ConfigureAwait(true);
    }

    private void Present(AlertKind kind, string title, string message,
                         string confirmText, string cancelText, bool destructive)
    {
        // Only one decision is ever outstanding; a second request would leave
        // the first one hanging forever.
        Settle(false);

        Glyph.Data = Geometry.Parse(kind switch
        {
            AlertKind.Success => SuccessGlyph,
            AlertKind.Warning => WarningGlyph,
            AlertKind.Error => ErrorGlyph,
            _ => InfoGlyph
        });
        Glyph.Classes.Remove("info");
        Glyph.Classes.Remove("success");
        Glyph.Classes.Remove("warning");
        Glyph.Classes.Remove("error");
        Glyph.Classes.Add(kind.ToString().ToLowerInvariant());

        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(message);

        PrimaryAction.Content = confirmText;
        PrimaryAction.Classes.Add("strong");
        PrimaryAction.Classes.Remove("destructive");
        if (destructive) PrimaryAction.Classes.Add("destructive");

        SecondaryAction.Content = cancelText;
        var hasCancel = !string.IsNullOrEmpty(cancelText);
        SecondaryAction.IsVisible = hasCancel;
        SecondarySeparator.IsVisible = hasCancel;

        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsVisible = true;
    }

    private void Settle(bool result)
    {
        var pending = _pending;
        if (pending is null) return;
        _pending = null;
        IsVisible = false;
        pending.TrySetResult(result);
    }

    /// <summary>Tapping the scrim is a cancel, which is what iOS does for a destructive confirm.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SecondaryAction.IsVisible) Settle(false);
        e.Handled = true;
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Window closed with a decision pending: never leave a caller awaiting
        // a TaskCompletionSource that nothing will ever complete.
        Settle(false);
    }
}
