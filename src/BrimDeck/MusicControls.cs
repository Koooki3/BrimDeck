using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BrimDeck;

public sealed class MusicControlButton : Button
{
    private bool _animationsEnabled;
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set { if (_animationsEnabled == value) return; _animationsEnabled = value; UpdateState(); }
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty) UpdateState();
    }
    public override void OnApplyTemplate() { base.OnApplyTemplate(); UpdateState(); }
    private void UpdateState()
    {
        if (Template?.FindName("Hover", this) is Ellipse hover)
        {
            double opacity = !IsEnabled ? 0 : IsPressed ? .16 : (bool)GetValue(IsMouseOverProperty) ? .10 : 0;
            Soft.Glide(hover, OpacityProperty, opacity, AnimationsEnabled);
        }
        if (Template?.FindName("ControlScale", this) is ScaleTransform scale)
        {
            double value = IsEnabled && IsPressed ? .94 : 1;
            Soft.Glide(scale, ScaleTransform.ScaleXProperty, value, false);
            Soft.Glide(scale, ScaleTransform.ScaleYProperty, value, false);
        }
    }
    // Kept for MainWindow.CoverFocus until it calls Soft.Glide itself.
    internal static void Animate(Animatable target, DependencyProperty property, double value, bool animate, int milliseconds)
        => Soft.Glide(target, property, value, animate, milliseconds);
}

public sealed class MusicSeekSlider : Slider
{
    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(nameof(BarHeight), typeof(double), typeof(MusicSeekSlider), new PropertyMetadata(4d));
    public static readonly DependencyProperty BubbleXProperty = DependencyProperty.Register(nameof(BubbleX), typeof(double), typeof(MusicSeekSlider), new PropertyMetadata(0d));
    public static readonly DependencyProperty BubbleTextProperty = DependencyProperty.Register(nameof(BubbleText), typeof(string), typeof(MusicSeekSlider), new PropertyMetadata(""));
    public static readonly DependencyProperty BubbleVisibleProperty = DependencyProperty.Register(nameof(BubbleVisible), typeof(Visibility), typeof(MusicSeekSlider), new PropertyMetadata(Visibility.Collapsed));
    public static readonly DependencyProperty SeekingProperty = DependencyProperty.Register(nameof(Seeking), typeof(bool), typeof(MusicSeekSlider), new PropertyMetadata(false, StateChanged));
    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }
    public double BubbleX { get => (double)GetValue(BubbleXProperty); set => SetValue(BubbleXProperty, value); }
    public string BubbleText { get => (string)GetValue(BubbleTextProperty); set => SetValue(BubbleTextProperty, value); }
    public Visibility BubbleVisible { get => (Visibility)GetValue(BubbleVisibleProperty); set => SetValue(BubbleVisibleProperty, value); }
    public bool Seeking { get => (bool)GetValue(SeekingProperty); set => SetValue(SeekingProperty, value); }
    public double RestHeight { get; init; } = 4;
    public double HoverHeight { get; init; } = 8;
    public double DurationSeconds { get; init; }
    public bool AnimationsEnabled { get; init; }
    public Color Accent { get; init; }
    private readonly SolidColorBrush _fill = new();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_fill is not null && (e.Property == IsMouseOverProperty || e.Property == IsEnabledProperty)) UpdateState();
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); Foreground = _fill;
        // A layout rebuild must start in its final color, not animate from the
        // new brush's transparent white. Animate only subsequent interaction.
        UpdateState(animate: false);
    }
    private static void StateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MusicSeekSlider)d).UpdateState();
    private void UpdateState(bool animate = true)
    {
        animate &= AnimationsEnabled && IsLoaded;
        bool active = IsEnabled && ((bool)GetValue(IsMouseOverProperty) || Seeking);
        Soft.Glide(this, BarHeightProperty, active ? HoverHeight : RestHeight, animate);
        // On a dark surface the hover color is lighter, on a light one darker, so hovering only ever adds contrast.
        bool dark = PanelTheme.Current.IsDark;
        byte Shift(byte value) => (byte)Math.Round(dark ? value + (255 - value) * .12 : value * .88);
        var color = active ? Color.FromArgb(Accent.A, Shift(Accent.R), Shift(Accent.G), Shift(Accent.B)) : Accent;
        Soft.Glide(_fill, color, animate);
        Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow;
        BubbleVisible = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateBubble();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Seeking && IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Pressed)
            Value = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        UpdateBubble();
    }
    protected override void OnValueChanged(double oldValue, double newValue) { base.OnValueChanged(oldValue, newValue); UpdateBubble(); }
    private void UpdateBubble()
    {
        double x = Math.Clamp(Mouse.GetPosition(this).X, 0, Math.Max(0, ActualWidth));
        double fraction = Seeking ? Value : x / Math.Max(1, ActualWidth);
        if (Seeking) x = ActualWidth * fraction;
        var time = TimeSpan.FromSeconds(Math.Floor(DurationSeconds * fraction));
        BubbleText = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        BubbleX = Math.Clamp(x - 26, 0, Math.Max(0, ActualWidth - 52));
    }
}
