using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrimDeck;

// Shows music.apple.com for the user to sign in, and closes once the site has set the media-user-token cookie, which
// AppleMusicAccount keeps. The page runs only while this window is open.
internal sealed class AppleMusicSignInWindow : Window
{
    private const string Site = "https://music.apple.com";
    private readonly AppleMusicAccount _account;
    private readonly WebView2 _web = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    public AppleMusicSignInWindow(SettingsTheme theme, AppleMusicAccount account)
    {
        _account = account;
        var palette = new SettingsPalette(SettingsPalette.IsDark(theme));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        palette.Apply(Resources); Style = (Style)FindResource(typeof(Window));
        Title = Loc.T("授权 Apple Music", "Authorize Apple Music"); Width = 520; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        FontFamily = UI.PanelFont; Background = UI.Brush(palette.Surface); Foreground = UI.Brush(palette.Primary);
        Native.SettingsChrome.Apply(this);
        SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(this, palette.Dark);
        var hint = UI.Line(Loc.T("请在下方页面右上角登录 Apple 账户。登录完成后，此窗口会自动关闭。",
            "Sign in to your Apple Account at the top right of the page below. This window closes once you are signed in."), 13, palette.Secondary);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(20, 14, 20, 14);
        DockPanel.SetDock(hint, Dock.Top);
        Content = new DockPanel { Children = { hint, _web } };
        _poll.Tick += async (_, _) => await TakeToken();
        Loaded += async (_, _) => await Open(hint);
        Closed += (_, _) => { _poll.Stop(); _web.Dispose(); };
    }

    private async Task Open(TextBlock hint)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _account.WebProfile);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            hint.Text = Loc.T("这台电脑缺少 Microsoft Edge WebView2 运行时，无法打开登录页面。请从 https://developer.microsoft.com/microsoft-edge/webview2/consumer/ 下载安装后重试。",
                "This computer lacks the Microsoft Edge WebView2 Runtime, so the sign-in page cannot open. Install it from https://developer.microsoft.com/microsoft-edge/webview2/consumer/ and try again.");
            _web.Visibility = Visibility.Collapsed;
            return;
        }
        // An earlier token stays in the page's cookies; it is removed so that only a new sign-in is taken.
        var cookies = _web.CoreWebView2.CookieManager;
        foreach (var cookie in await cookies.GetCookiesAsync(Site))
            if (cookie.Name == "media-user-token") cookies.DeleteCookie(cookie);
        _web.CoreWebView2.Navigate(Site);
        _poll.Start();
    }

    private async Task TakeToken()
    {
        if (_web.CoreWebView2 is not { } core) return;
        var cookie = (await core.CookieManager.GetCookiesAsync(Site)).FirstOrDefault(c => c.Name == "media-user-token" && c.Value.Length > 0);
        if (cookie is null || !IsLoaded) return;
        _poll.Stop();
        _account.Authorize(cookie.Value, cookie.IsSession ? null : new DateTimeOffset(cookie.Expires));
        DialogResult = true;
    }
}
