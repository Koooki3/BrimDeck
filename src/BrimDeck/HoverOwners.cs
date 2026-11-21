using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BrimDeck;

// Which controls show their own hover, so that a theme's hover mark (InteractionFx) never draws a second one over them. One
// rule for the whole panel: a control shows its own hover when it fades its own feedback (Feedback.Fade), is a music control
// (MusicControlButton, its round gray hover), has a template part that hover lights ("Hover", "HoverLayer"), has a style (or a
// style it is based on) or template that reacts to the pointer being over it, or was built in code and says so
// (Feedback.OwnHover, for a MouseEnter handler that changes its look). Only a control that shows nothing of its own is marked.
internal static class HoverOwners
{
    private static readonly string[] Parts = ["Hover", "HoverLayer"];
    // A style or template does not change once in use, so what it says is worked out once.
    private static readonly ConditionalWeakTable<object, StrongBox<bool>> Known = new();

    public static bool DrawsOwn(FrameworkElement control)
    {
        if (Feedback.GetFade(control) || Feedback.GetOwnHover(control) || control is MusicControlButton) return true;
        if (control is Control { Template: { } template } owner && (Parts.Any(part => template.FindName(part, owner) is not null) || Reacts(template))) return true;
        for (var style = control.Style; style is not null; style = style.BasedOn)
            if (Reacts(style)) return true;
        return false;
    }

    private static bool Reacts(Style style) => Known.GetValue(style, s => new(Reacts(((Style)s).Triggers))).Value;
    private static bool Reacts(FrameworkTemplate template) => Known.GetValue(template, t => new(t is ControlTemplate control && Reacts(control.Triggers))).Value;

    // A trigger on IsMouseOver (alone or among other conditions, on the control or bound to it) or an animation started when the
    // pointer comes onto the control.
    private static bool Reacts(TriggerCollection triggers) => triggers.Any(trigger => trigger switch
    {
        Trigger one => one.Property == UIElement.IsMouseOverProperty,
        MultiTrigger many => many.Conditions.Any(condition => condition.Property == UIElement.IsMouseOverProperty),
        DataTrigger data => OnMouseOver(data.Binding),
        MultiDataTrigger data => data.Conditions.Any(condition => OnMouseOver(condition.Binding)),
        EventTrigger entered => entered.RoutedEvent == UIElement.MouseEnterEvent,
        _ => false
    });
    // Bound to the control's own IsMouseOver, not to an ancestor's (a scroll bar that shows while its list is hovered).
    private static bool OnMouseOver(BindingBase? binding) => binding is Binding { Path.Path: nameof(UIElement.IsMouseOver), RelativeSource.Mode: RelativeSourceMode.Self or RelativeSourceMode.TemplatedParent };
}
