using BrimDeck.Native;

// The Windows side of bringing WeChat forward: the registry reader on schemes that cannot be registered, and the launcher's own
// allow-list. Nothing is launched: the launcher is only given addresses it must refuse, never weixin://.
internal static class NoticeChatRestoreWindowsTests
{
    public static void Run(Action<string, bool> check)
    {
        var registry = new ProtocolRegistry();
        string unknown = "brimdeck-" + Guid.NewGuid().ToString("N");
        bool ok;
        try { ok = !registry.IsRegistered(unknown) && !registry.IsRegistered("") && !registry.IsRegistered(@"x\y") && !registry.IsRegistered("a b"); }
        catch (Exception ex) { Console.WriteLine(ex); ok = false; }
        check("chat restore: a scheme nobody registered reads as not registered, without throwing", ok);
        var launcher = new ProtocolLauncher();
        check("chat restore: the launcher refuses every address but the constant", !launcher.Launch("") && !launcher.Launch("weixin://x") && !launcher.Launch("WEIXIN://")
            && !launcher.Launch(@"C:\Windows\System32\cmd.exe") && !launcher.Launch("ms-settings:"));
    }
}
