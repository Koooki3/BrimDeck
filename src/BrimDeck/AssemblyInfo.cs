using System.Windows;

// The glyph preview (tests/BrimDeck.GlyphPreview) draws the notice glyphs with the app's own elements.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BrimDeck.GlyphPreview")]
// The interaction preview (tests/BrimDeck.FxPreview) drives the pointer answers on a panel snippet and checks them.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BrimDeck.FxPreview")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The glyph preview (tests/BrimDeck.GlyphPreview) draws the notice glyphs with the app's own elements and checks the liquid motion's boundary.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BrimDeck.GlyphPreview")]
