using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Verification codes in notices (N5): what is recognised and what is not (Chinese and English), where the code stands, the mask,
// a text built to make the patterns slow, the setting, what the history keeps, and when the clipboard is emptied. Fixed clocks and
// a clipboard that is a variable.
internal static class NoticeCodeTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Recognised(check);
            Rejected(check);
            Spans(check);
            Words(check);
            Adversarial(check);
            Setting(root, check);
            Guard(check);
        }
        finally { Loc.Use(language); }
    }

    // (title, body, the code that must be found).
    private static readonly (string Title, string Body, string Code)[] Positive =
    [
        ("", "您的验证码是：834921，10分钟内有效。", "834921"),
        ("【某某】验证码", "您的验证码是：834921，10分钟内有效。", "834921"),
        ("", "动态密码：482913（5分钟内有效）", "482913"),
        ("", "验证码 906213", "906213"),
        ("", "安全码：4021", "4021"),
        ("", "验证码\n735164\n请勿泄露", "735164"),
        ("", "校验码 582134，请勿告诉他人", "582134"),
        ("", "【微信】登录验证码：382910", "382910"),
        ("", "【银行】您的短信验证码为 276145，工作人员不会索取。", "276145"),
        ("", "激活码：90817265", "90817265"),
        ("", "验证码：A1B2C3", "A1B2C3"),
        ("验证码 K7Q2-9XMP", "10 分钟内有效", "K7Q2-9XMP"),
        ("", "您的验证码是：８３４９２１，10分钟内有效", "834921"),
        ("", "验证码：83​4921", "834921"),
        ("", "验证码：834921，如非本人操作请忽略。订单号 987654", "834921"),
        ("", "您的验证码为 123 456，有效期5分钟", "123 456"),
        ("", "您的驗證碼為 431872，請勿分享。", "431872"),
        ("", "您的動態密碼：482913，5 分鐘內有效。", "482913"),
        ("登入驗證碼", "您的一次性驗證碼為：4318729", "4318729"),
        ("Your verification code", "Your verification code is 552310. It expires in 10 minutes.", "552310"),
        ("", "Your security code is 293847", "293847"),
        ("", "Your one-time passcode: 847362", "847362"),
        ("", "Use code 123456 to sign in", "123456"),
        ("", "OTP: 4821", "4821"),
        ("", "Verification code: 4829", "4829"),
        ("", "Microsoft account security code: 4318", "4318"),
        ("", "[Apple] Your Apple ID Code is: 482 913. Don't share it with anyone.", "482 913"),
        ("", "Your PIN code is 739104", "739104"),
        ("", "PIN: 7391", "7391"),
        ("", "Your verification code is QXTRPA.", "QXTRPA"),
        ("", "验证码为：AbCdEf。", "AbCdEf"),
        ("Sign-in", "One-time code:\nQXTRPA\nExpires in 5 minutes.", "QXTRPA"),
        ("", "Verification code\n\nQXTRPA\n", "QXTRPA"),
        ("Login code 640218", "", "640218"),
    ];

    // (title, body): a number or word that is not a code.
    private static readonly (string Title, string Body)[] Negative =
    [
        ("", "您的订单号 12345678 已发货"),
        ("", "订单 482913，金额 ￥1,299.00"),
        ("", "会议号 123 456 789"),
        ("", "2026年10月30日 13:20 开始"),
        ("", "客服电话 400-800-8888"),
        ("", "优惠券码 SAVE20，立减 20 元"),
        ("", "https://example.com/verify/482913"),
        ("", "卡尾号 8891"),
        ("", "邮编 200120"),
        ("", "验证码有效期至 2026-10-30"),
        ("", "产品型号 SKU-8891 已出库"),
        ("", "验证码请见图片"),
        ("", "您的快递 773456789012 已到驿站"),
        ("", "验证码：请在邮箱中查看"),
        ("", "你的快递已到，取件码 6-3-2088"),
        ("", "【抢购】满100减50优惠券，验证码 888888"),
        ("", "账户余额 ¥123456"),
        ("", "成交价 123456 元"),
        ("", "构建编号 482913 已完成"),
        ("", "Your order 482913 has shipped"),
        ("", "Meeting ID 123 456 789"),
        ("", "Meeting at 10:30 in room 4821"),
        ("", "Call us at 555-123-4567"),
        ("", "Order #123456 shipped"),
        ("", "Tracking number 1Z999AA10123456784"),
        ("", "Total: $1,299.00"),
        ("", "Happy 2026!"),
        ("", "Your build number is 482913"),
        ("", "The code is in the repo at 2024"),
        ("", "Your verification code will arrive soon."),
        ("", "Your verification code is valid."),
        ("", "Verification code: Action Required"),
        ("", "Verification code: https://example.com/QXTRPA"),
        ("", "Verification code: QXTRPA@example.com"),
        ("", "Your promo code: SUMMER"),
        ("", "Please verify your email and click CONTINUE."),
        // Below the threshold on purpose: a four-digit number said only with "is" is not shown as a code.
        ("", "Your PIN is 7391"),
        ("", ""),
        ("", "Hello, how are you today?"),
    ];

    private static void Recognised(Action<string, bool> check)
    {
        static bool Cjk(string text) => text.Any(c => c >= '一');
        static bool Ascii(string text) => text.Length > 0 && text.All(c => c < 'Ā');
        check("The table holds at least forty cases, in both languages", Positive.Length + Negative.Length >= 40
            && Positive.Any(c => Cjk(c.Body)) && Positive.Any(c => Ascii(c.Body)) && Negative.Any(c => Cjk(c.Body)) && Negative.Any(c => Ascii(c.Body)));
        var missed = Positive.Where(c => VerificationCode.Find(c.Title, c.Body)?.Code != c.Code).Select(c => c.Title + " | " + c.Body).ToList();
        check("Every code in the positive table is found" + (missed.Count > 0 ? ": " + string.Join(" || ", missed) : ""), missed.Count == 0);
        check("A code written in groups is pasted together, one with letters keeps its hyphen",
            VerificationCode.Find("", "您的验证码为 123 456")?.CopyText == "123456" && VerificationCode.Find("验证码 K7Q2-9XMP", "")?.CopyText == "K7Q2-9XMP"
            && VerificationCode.Find("", "验证码：834921")?.CopyText == "834921");
        var again = VerificationCode.Find("", "您的验证码是：834921，请勿泄露");
        check("Reading the same words twice gives the same answer", again == VerificationCode.Find("", "您的验证码是：834921，请勿泄露") && again is not null);
    }

    private static void Rejected(Action<string, bool> check)
    {
        var wrong = Negative.Select(c => (c.Title, c.Body, Hit: VerificationCode.Find(c.Title, c.Body))).Where(c => c.Hit is not null).Select(c => c.Title + " | " + c.Body + " => " + c.Hit!.Code).ToList();
        check("Nothing in the negative table is taken for a code" + (wrong.Count > 0 ? ": " + string.Join(" || ", wrong) : ""), wrong.Count == 0);
        check("Null words are no words", VerificationCode.Find(null, null) is null);
        check("Two different letter codes are not guessed between", VerificationCode.Find("", "Verification code: QXTRPA\nSecurity code: JZKWRT") is null);
        check("The same letter code twice is that code", VerificationCode.Find("", "Verification code: QXTRPA\nSecurity code: QXTRPA")?.Code == "QXTRPA");
    }

    private static void Spans(Action<string, bool> check)
    {
        string body = "您的验证码是：834921，请勿泄露";
        var hit = VerificationCode.Find("", body)!;
        check("The span is where the code stands in the body", !hit.InTitle && body.Substring(hit.Start, hit.Length) == "834921");
        check("Masking puts dots of the same length in its place", VerificationCode.Mask(body, hit.Start, hit.Length) == "您的验证码是：••••••，请勿泄露");
        var title = VerificationCode.Find("验证码 K7Q2-9XMP", "")!;
        check("A code in the title says so", title.InTitle && "验证码 K7Q2-9XMP".Substring(title.Start, title.Length) == "K7Q2-9XMP");
        string wide = "验证码：８３４９２１";
        var full = VerificationCode.Find("", wide)!;
        check("Full-width digits are masked where they were written", full.Code == "834921" && VerificationCode.Mask(wide, full.Start, full.Length) == "验证码：••••••");
        string zero = "验证码：83​4921";
        var hidden = VerificationCode.Find("", zero)!;
        check("A zero-width character inside a code is masked with it", VerificationCode.Mask(zero, hidden.Start, hidden.Length) == "验证码：•••••••");
        check("A span outside the text changes nothing", VerificationCode.Mask("abc", 2, 5) == "abc" && VerificationCode.Mask("abc", -1, 1) == "abc" && VerificationCode.Mask("abc", 1, 0) == "abc");
    }

    private static Notice Note(string title, string body) => new() { AppKey = "windows:Chat", AppName = "Chat", Title = title, Body = body };

    private static void Words(Action<string, bool> check)
    {
        var notice = Note("验证码 482913", "您的验证码是：482913，请勿泄露。");
        var on = NoticeCodes.Words(notice, true);
        check("With codes on, a code is hidden in the title and wherever else the body repeats it", on.Title == "验证码 ••••••" && on.Body == "您的验证码是：••••••，请勿泄露。" && on.Hit?.Code == "482913");
        var off = NoticeCodes.Words(notice, false);
        check("With codes off, the words are as they came", off.Title == notice.Title && off.Body == notice.Body && !off.HasCode);
        check("A screen reader hears that a code is hidden, never the dots or the digits",
            !on.SpokenBody.Contains('•') && !on.SpokenBody.Contains("482913") && on.SpokenBody.Contains("验证码已隐藏") && !on.SpokenTitle.Contains("482913"));
        var two = NoticeCodes.Words(Note("", "验证码 111111。备用验证码 222222"), true);
        check("A second code is hidden too", two.Body == "验证码 ••••••。备用验证码 ••••••");
        var plain = Note("你好", "今晚吃什么");
        check("Words without a code are returned as they are", NoticeCodes.Words(plain, true) is { HasCode: false } w && w.Title == "你好" && w.Body == "今晚吃什么");
        notice.Body = "没有了";
        check("A changed notice is read again", NoticeCodes.Words(notice, true) is { } changed && changed.Body == "没有了" && changed.Title == "验证码 ••••••");
        var live = Note("", "验证码 654321");
        var saved = NoticeCodes.ForHistory(live, true);
        check("The history keeps the words with the code hidden; the notice in memory keeps the code",
            saved.Body == "验证码 ••••••" && live.Body == "验证码 654321" && !ReferenceEquals(saved, live) && saved.Id == live.Id);
        check("With codes off, the history keeps the notice itself", ReferenceEquals(NoticeCodes.ForHistory(live, false), live) && ReferenceEquals(NoticeCodes.ForHistory(plain, true), plain));
        check("A notice read back from the history has no code left to copy", NoticeCodes.Words(saved, true) is { HasCode: false });

        var window = Note("银行", "您的验证码是：482913");
        check("The island line hides the code at every level that shows words",
            NoticeText.Presented(window, NoticePrivacy.Full, CodeMask.Dots).Summary == "您的验证码是：••••••"
            && NoticeText.Presented(window, NoticePrivacy.Full).Summary == "您的验证码是：482913"
            && NoticeText.Presented(Note("验证码 482913", ""), NoticePrivacy.TitleOnly, CodeMask.Dots).Title == "验证码 ••••••"
            && NoticeText.Presented(window, NoticePrivacy.AppOnly, CodeMask.Dots) == NoticeText.Presented(window, NoticePrivacy.AppOnly));
        check("The spoken line has no dots", !NoticeText.Presented(window, NoticePrivacy.Full, CodeMask.Spoken).Summary.Contains('•') && !NoticeText.Presented(window, NoticePrivacy.Full, CodeMask.Spoken).Summary.Contains("482913"));
    }

    private static void Adversarial(Action<string, bool> check)
    {
        string unit = "验证码 1 ";
        var texts = new List<(string Name, string Title, string Body)>
        {
            ("digits", "", new string('9', 1_000_000)),
            ("keyword and spaces", "", string.Concat(Enumerable.Repeat(unit, 400_000))),
            ("letters", "", "code " + new string('A', 1_000_000)),
            ("hyphens", "", string.Concat(Enumerable.Repeat("a-", 500_000))),
            ("groups", "验证码", string.Concat(Enumerable.Repeat("123 456 ", 500_000))),
            ("newlines", "", string.Concat(Enumerable.Repeat("1\n", 500_000))),
            ("full body of codes", "", string.Concat(Enumerable.Repeat("A1B2C3 ", 570)) + "verification code"),
            ("one long line", "otp", string.Concat(Enumerable.Repeat("1234 ", 800)) + "code: 123456"),
            ("long title", new string('验', 1_000_000), "")
        };
        var slow = new List<string>();
        foreach (var (name, title, body) in texts)
        {
            var clock = Stopwatch.StartNew();
            VerificationCode.Find(title, body);
            NoticeCodes.Words(title, body);
            clock.Stop();
            // Cut to 4000 characters before it is read, each takes a few milliseconds; the bound is far above that, so that a slow
            // machine does not fail it, and far below what a pattern that backtracks would take.
            if (clock.ElapsedMilliseconds > 2000) slow.Add(name + " " + clock.ElapsedMilliseconds + " ms");
        }
        check("Texts built to make the patterns slow are read within the bound" + (slow.Count > 0 ? ": " + string.Join(", ", slow) : ""), slow.Count == 0);
        string far = new string(' ', VerificationCode.MaxBody) + "验证码 834921";
        check("Words past what a notice may hold are not read", VerificationCode.Find("", far) is null);
    }

    private static void Setting(string root, Action<string, bool> check)
    {
        check("Recognising codes is off by default", !new DeckSettings().NoticeCodes);
        var store = new SettingsStore(Path.Combine(root, "notice-code-settings"), "1.2.0");
        check("An older settings file reads with codes off", !store.Load().NoticeCodes);
        store.Save(new DeckSettings { NoticesEnabled = true, NoticeCodes = true });
        var back = store.Load();
        check("The setting survives saving and loading and copying", back.NoticeCodes && back.Copy().NoticeCodes && System.Text.Json.JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement.GetProperty("NoticeCodes").GetBoolean());
        store.Save(new DeckSettings { NoticesEnabled = true, NoticeCodes = false });
        check("Switched off again, it stays off", !store.Load().NoticeCodes);
    }

    // The clipboard as a variable.
    private sealed class FakeClipboard : ICodeClipboard
    {
        public string? Text; public int BusyReads, BusyClears, Clears;
        public bool TryRead(out string? text) { text = Text; if (BusyReads > 0) { BusyReads--; return false; } return true; }
        public bool TryClear() { if (BusyClears > 0) { BusyClears--; return false; } Text = null; Clears++; return true; }
    }

    private static void Guard(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var board = new FakeClipboard();
        var guard = new CodeClipboardGuard(() => now, board);
        check("The wait is sixty seconds", CodeClipboardGuard.ClearAfter == TimeSpan.FromSeconds(60));
        check("Before anything was copied the guard does nothing", guard.Tick() == CodeClearResult.Idle && !guard.Armed);
        board.Text = "834921"; guard.Arm("834921");
        check("It waits out the time", guard.Tick() == CodeClearResult.Waiting && guard.Due == now.AddSeconds(60));
        now = now.AddMilliseconds(59_900);
        check("Not a moment early", guard.Tick() == CodeClearResult.Waiting && board.Text == "834921");
        now = now.AddMilliseconds(100);
        check("At sixty seconds a clipboard still holding the code is emptied", guard.Tick() == CodeClearResult.Cleared && board.Text is null && board.Clears == 1 && !guard.Armed);
        check("Then it is done", guard.Tick() == CodeClearResult.Idle && board.Clears == 1);

        board.Text = "834921"; guard.Arm("834921"); board.Text = "something else the user copied"; now = now.AddSeconds(61);
        check("A clipboard holding anything else is left alone", guard.Tick() == CodeClearResult.Kept && board.Text == "something else the user copied" && board.Clears == 1 && !guard.Armed);

        board.Text = "834921"; guard.Arm("834921"); now = now.AddSeconds(30); guard.Arm("111222"); board.Text = "111222"; now = now.AddSeconds(31);
        check("A second copy restarts the time and replaces the first code", guard.Tick() == CodeClearResult.Waiting && board.Text == "111222");
        now = now.AddSeconds(30);
        check("The second code is the one cleared", guard.Tick() == CodeClearResult.Cleared && board.Text is null);

        board.Text = "834921\n"; guard.Arm("834921"); now = now.AddSeconds(60);
        check("The text must match exactly, not nearly", guard.Tick() == CodeClearResult.Kept && board.Text == "834921\n");
        board.Text = null; guard.Arm("834921"); now = now.AddSeconds(60);
        check("An empty clipboard is left as it is", guard.Tick() == CodeClearResult.Kept && board.Clears == 2);

        board.Text = "834921"; board.BusyReads = 2; guard.Arm("834921"); now = now.AddSeconds(60);
        check("A busy clipboard is asked again, and then cleared", guard.Tick() == CodeClearResult.Retry && guard.Tick() == CodeClearResult.Retry && guard.Tick() == CodeClearResult.Cleared && board.Text is null);
        board.Text = "834921"; board.BusyClears = 1; guard.Arm("834921"); now = now.AddSeconds(60);
        check("A clipboard that refuses to be emptied is asked again", guard.Tick() == CodeClearResult.Retry && guard.Tick() == CodeClearResult.Cleared);
        board.Text = "834921"; board.BusyReads = 100; guard.Arm("834921"); now = now.AddSeconds(60);
        int tries = 0; CodeClearResult last;
        do { last = guard.Tick(); tries++; } while (last == CodeClearResult.Retry && tries < 50);
        check("A clipboard that stays busy is given up after a few tries, the code left where it is", last == CodeClearResult.Kept && tries == CodeClipboardGuard.MaxBusy && board.Text == "834921" && !guard.Armed);
        board.Text = "834921"; board.BusyReads = 0; guard.Arm("834921"); guard.Disarm(); now = now.AddSeconds(120);
        check("A disarmed guard never clears", guard.Tick() == CodeClearResult.Idle && board.Text == "834921");
        guard.Arm(""); check("An empty code arms nothing", !guard.Armed);
    }
}
