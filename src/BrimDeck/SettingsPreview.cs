using System.Windows;
using System.Windows.Threading;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // Inherit through control templates, including slider thumbs and number editors.
    private static readonly DependencyProperty PreviewControlProperty = DependencyProperty.RegisterAttached(
        "PreviewControl", typeof(bool), typeof(SettingsWindow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static T KeepPreview<T>(T control) where T : DependencyObject
    { control.SetValue(PreviewControlProperty, true); return control; }

    private void DismissPreviewOutsideControl(object? source)
    {
        if (_page is 1 or 2 && (source is not DependencyObject control || !(bool)control.GetValue(PreviewControlProperty)))
            QueuePreviewDismissal();
    }

    private void QueuePreviewDismissal()
    {
        // A number editor commits on focus loss and may start preview again.
        // Finish that input event before releasing the forced expansion.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_preview || _app.Exiting) return;
            _preview = false;
            _app.Deck.ResumeAfterPreview();
        });
    }

    // Ends the preview at once, for callers outside this window: clears this window's flag and resumes the main panel.
    internal void EndPreview() { if (!_preview) return; _preview = false; _app.Deck.ResumeAfterPreview(); }
}
