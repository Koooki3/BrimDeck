using BrimDeck.Core;

// What the devices layer must not do to hardware or to the vendors' own software, through the real protocol and matching code:
// the HID++ software id, which devices a request goes to, and which vendor programs make a brand stand aside.
static class DeviceSafetyTests
{
    public static void Run(Action<string, bool> check)
    {
        SoftwareId(check);
    }

    // A receiver sends every reply to every program that has it open, and only the software id tells a program its own reply.
    // The ids other programs use (Solaar's lib/logitech_receiver/base.py): OpenRGB 0x07, LGSTrayEx 0x0A, Solaar 0x0B, G HUB
    // 0x0D, the firmware 0x0F.
    private static void SoftwareId(Action<string, bool> check)
    {
        byte[] taken = [0x07, 0x0A, 0x0B, 0x0D, 0x0F];
        byte ours = Hidpp.SoftwareId;
        check("HID++: the software id is nobody else's, and a nibble", !taken.Contains(ours) && ours is > 0 and <= 0x0F);
        check("HID++: a request carries it in the low nibble of byte 3", (Hidpp.Request(1, 0x08, 1)[3] & 0x0F) == ours && (Hidpp.Request(1, 0x08, 1)[3] >> 4) == 1);

        byte[] Reply(byte id, bool error = false)
        {
            var report = new byte[20];
            report[0] = 0x11; report[1] = 1;
            if (error) { report[2] = 0xFF; report[3] = 0x08; report[4] = (byte)((1 << 4) | id); report[5] = 0x02; }
            else { report[2] = 0x08; report[3] = (byte)((1 << 4) | id); report[4] = 55; }
            return report;
        }
        check("HID++: a reply with our id is the reply, with an error as well", Hidpp.Match(Reply(ours), 1, 0x08, 1).Reply is [55, ..] && Hidpp.Match(Reply(ours, error: true), 1, 0x08, 1).Error == 2);
        // G HUB asking the same device the same function at the same moment: its reply is not ours, and ours is not its.
        check("HID++: G HUB's reply (its id 0x0D) is not taken for ours, nor are the other programs'",
            taken.All(id => Hidpp.Match(Reply(id), 1, 0x08, 1) == (null, null) && Hidpp.Match(Reply(id, error: true), 1, 0x08, 1) == (null, null)));
    }
}
