using BrimDeck.Core;

internal static class FullscreenExceptionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var none = Array.Empty<FullscreenException>();
        check("Without exceptions every app follows the window rules",
            !ScreenPolicy.IsException("Sahhpire", @"F:\Apps\Sahhpire\Sahhpire.exe", none) && !ScreenPolicy.IsException(null, null, null));

        var byName = new[] { new FullscreenException("Sahhpire") };
        check("An exception matches its process name regardless of case",
            ScreenPolicy.IsException("sahhpire", "", byName) && ScreenPolicy.IsException("SAHHPIRE", null, byName));
        check("An exception ignores the .exe suffix on either side",
            ScreenPolicy.IsException("Sahhpire.exe", "", byName)
            && ScreenPolicy.IsException("Sahhpire", "", [new FullscreenException("Sahhpire.exe")]));
        check("Another app is not exempt", !ScreenPolicy.IsException("Sahhpire2", "", byName));

        var byPath = new[] { new FullscreenException("Other", @"F:\Apps\Sahhpire\Sahhpire.exe") };
        check("An exception matches its executable path whichever slash direction is reported",
            ScreenPolicy.IsException("unrelated", @"f:\apps\sahhpire\SAHHPIRE.EXE", byPath)
            && ScreenPolicy.IsException("unrelated", "F:/Apps/Sahhpire/Sahhpire.exe", byPath));
        check("An exception still matches by name when the executable path cannot be read",
            ScreenPolicy.IsException("Other", "", byPath) && ScreenPolicy.IsException("Other", null, byPath));
        check("An exception does not match a different executable",
            !ScreenPolicy.IsException("unrelated", @"F:\Other\Sahhpire.exe", byPath));

        var settings = new DeckSettings
        {
            FullscreenExceptions =
            [
                new(" Sahhpire ", @"F:\Apps\Sahhpire\Sahhpire.EXE"),
                new("sahhpire", @"f:\apps\sahhpire\sahhpire.exe"),
                new("", ""),
                new("", @"F:\Apps\Other\Other.exe")
            ]
        };
        settings.Normalize();
        check("Duplicate and empty entries are cleaned up and a path-only entry takes its file name",
            settings.FullscreenExceptions.Count == 2
            && settings.FullscreenExceptions[0] == new FullscreenException("Sahhpire", @"F:\Apps\Sahhpire\Sahhpire.EXE")
            && settings.FullscreenExceptions[1] == new FullscreenException("Other", @"F:\Apps\Other\Other.exe"));
        var copy = settings.Copy();
        copy.FullscreenExceptions.Add(new FullscreenException("Extra"));
        check("A settings copy keeps its own exception list",
            settings.FullscreenExceptions.Count == 2 && copy.FullscreenExceptions.Count == 3);

        var old = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("{}")!;
        check("Settings saved before the list existed read as no exceptions", old.FullscreenExceptions.Count == 0);
        var saved = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("""{"FullscreenExceptions":[{"Name":"Sahhpire","Path":""}]}""")!;
        check("The exception list is saved and read back", saved.FullscreenExceptions is [{ Name: "Sahhpire", Path: "" }]);

        var many = new DeckSettings
        {
            FullscreenExceptions = Enumerable.Range(0, 40).Select(index => new FullscreenException("App" + index)).ToList()
        };
        many.Normalize();
        check("The exception list is capped", many.FullscreenExceptions.Count == AppPresets.MaximumFullscreenExceptions);
    }
}
