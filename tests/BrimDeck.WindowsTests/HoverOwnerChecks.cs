using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck;

// Which controls a theme's hover mark leaves alone because they show their own hover (HoverOwners.DrawsOwn, which the mark's
// layer asks before marking a control). Real controls with real styles and templates, the music controls' own style from
// MusicResources.xaml among them; templates are applied, nothing is shown on screen.
internal static class HoverOwnerChecks
{
    public static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var music = (ResourceDictionary)Application.LoadComponent(new Uri("/BrimDeck.WindowsTests;component/MusicResources.xaml", UriKind.Relative));
        static T Applied<T>(T control) where T : Control { control.ApplyTemplate(); return control; }
        static ControlTemplate Bare(Type type, FrameworkElementFactory? part = null)
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            if (part is not null) root.AppendChild(part);
            root.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            return new ControlTemplate(type) { VisualTree = root };
        }
        static FrameworkElementFactory Named(Type type, string name) { var part = new FrameworkElementFactory(type); part.Name = name; return part; }
        static Style Hovering(Type type)
        {
            var style = new Style(type);
            style.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, .8) } });
            return style;
        }

        // A control that shows nothing of its own on hover is what a mark is for.
        check("hover owners: a button whose template and style show no hover may be marked", !HoverOwners.DrawsOwn(Applied(new Button { Template = Bare(typeof(Button)), Content = "x" })));
        check("hover owners: a list row with a plain template may be marked", !HoverOwners.DrawsOwn(Applied(new ListBoxItem { Template = Bare(typeof(ListBoxItem)) })));

        // The music controls: the play, previous and next buttons draw a round gray hover of their own.
        var play = Applied(new MusicControlButton { Style = (Style)music["MusicControl"], Content = "play" });
        check("hover owners: the music controls, in their own style, show their own hover", HoverOwners.DrawsOwn(play));
        check("hover owners: a music control is its own hover whatever template it has", HoverOwners.DrawsOwn(Applied(new MusicControlButton { Template = Bare(typeof(Button)) })));
        check("hover owners: any button in the music controls' style shows that style's hover", HoverOwners.DrawsOwn(Applied(new Button { Style = (Style)music["MusicControl"] })));

        // Buttons that fade their own feedback, or whose template has the layer that hover lights.
        var fading = new Button { Template = Bare(typeof(Button)) }; Feedback.SetFade(fading, true);
        check("hover owners: a button that fades its own feedback", HoverOwners.DrawsOwn(Applied(fading)));
        check("hover owners: a template with a hover layer", HoverOwners.DrawsOwn(Applied(new Button { Template = Bare(typeof(Button), Named(typeof(Border), "HoverLayer")) })));
        check("hover owners: a template with a hover shape", HoverOwners.DrawsOwn(Applied(new Button { Template = Bare(typeof(Button), Named(typeof(Ellipse), "Hover")) })));

        // Hover written as triggers: in the style, in a style it is based on, or in the template.
        check("hover owners: a style with a trigger on the pointer being over", HoverOwners.DrawsOwn(Applied(new Button { Style = Hovering(typeof(Button)), Template = Bare(typeof(Button)) })));
        var based = new Style(typeof(Button), Hovering(typeof(Button)));
        check("hover owners: a style based on one with that trigger", HoverOwners.DrawsOwn(Applied(new Button { Style = based, Template = Bare(typeof(Button)) })));
        var triggered = Bare(typeof(Button));
        triggered.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, .8) } });
        check("hover owners: a template with a trigger on the pointer being over", HoverOwners.DrawsOwn(Applied(new Button { Template = triggered })));
        var both = Bare(typeof(Button));
        var multi = new MultiTrigger { Setters = { new Setter(UIElement.OpacityProperty, .8) } };
        multi.Conditions.Add(new Condition(UIElement.IsEnabledProperty, true)); multi.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
        both.Triggers.Add(multi);
        check("hover owners: a trigger that needs the pointer over among other conditions", HoverOwners.DrawsOwn(Applied(new Button { Template = both })));
        var entering = Bare(typeof(Button));
        entering.Triggers.Add(new EventTrigger(UIElement.MouseEnterEvent) { Actions = { new BeginStoryboard { Storyboard = new Storyboard() } } });
        check("hover owners: a template that starts an animation when the pointer comes onto it", HoverOwners.DrawsOwn(Applied(new Button { Template = entering })));
        static Style Bound(System.Windows.Data.RelativeSource source)
        {
            var style = new Style(typeof(Button));
            style.Triggers.Add(new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(UIElement.IsMouseOver)) { RelativeSource = source }, Value = true,
                Setters = { new Setter(UIElement.OpacityProperty, .8) } });
            return style;
        }
        check("hover owners: a style bound to the control's own IsMouseOver", HoverOwners.DrawsOwn(Applied(new Button { Style = Bound(System.Windows.Data.RelativeSource.Self), Template = Bare(typeof(Button)) })));
        check("hover owners: a style bound to an ancestor's IsMouseOver is not the control's own hover", !HoverOwners.DrawsOwn(Applied(new Button
        {
            Style = Bound(new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(ScrollViewer) }), Template = Bare(typeof(Button))
        })));
        var pressedOnly = Bare(typeof(Button));
        pressedOnly.Triggers.Add(new Trigger { Property = ButtonBase.IsPressedProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, .8) } });
        check("hover owners: a trigger on a press only is no hover of its own", !HoverOwners.DrawsOwn(Applied(new Button { Template = pressedOnly })));

        // Built in code: a button that changes its look in a MouseEnter handler says so.
        var pill = new Button { Template = Bare(typeof(Button)) }; Feedback.SetOwnHover(pill, true);
        check("hover owners: a button built in code that says it shows its own hover", HoverOwners.DrawsOwn(Applied(pill)));
    }
}
