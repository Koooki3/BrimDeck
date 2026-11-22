# Clipboard sync (in development)

Under Settings → Shelf → Clipboard sync, this PC can sync what you copy with other devices in three ways. They can be used together.

| Way | Syncs with | Content |
| --- | --- | --- |
| Local network | Another PC with BrimDeck | Text, images |
| WeType | WeType (WeChat's input method) on a phone or PC | Text, images |
| Doubao IME | Phones and PCs signed in to the same Doubao account | Text, images (sending images has a size limit, see below) |

Once connected, "Sync automatically" turns on by itself. While it is on, what you copy here is sent to the other side, and what the other side copies is put on this PC's clipboard; with the history on, it also enters BrimDeck's clipboard history. Only what is copied after syncing is turned on is synced. Files, the history and saved phrases are not synced.

## Local network

1. Connect both PCs to the same network, open Clipboard sync on each, and choose Pair under Local network.
2. Both windows show that PC's six-digit code. Enter on either PC the code the other one shows.

Content travels encrypted between the two PCs, without the internet or an account. One pair of PCs is supported. Images are sent as PNG, up to 8 MiB and 8192 pixels on a side. What is copied while disconnected is not sent later.

Unpairing on one PC also unpairs the other, when it is connected.

A PC that leaves the network and comes back, a laptop for instance, reconnects by itself; a changed network address needs no new pairing. The WeType and Doubao connections also reconnect once the network is back.

## WeType

WeType calls this feature cross-device (跨设备) and the code a match code (匹配码). No WeChat sign-in is needed.

- **Enter a code**: on your phone or another PC, open WeType's settings, go to its cross-device page (跨设备) and choose 查看匹配码; in BrimDeck choose Link a device and enter the code. If that device asks to confirm, allow it.
- **Show this device's code**: switch the BrimDeck window to "Show this device's code"; at the bottom of the other device's cross-device page choose 关联设备 and enter the code BrimDeck shows, then choose Allow in BrimDeck.

Notes:

- The other device syncs what it copies only with 跨设备复制粘贴 turned on in its cross-device page; with it off, BrimDeck receives nothing from it. Its other switches (dictionary, phrases, AI recorder) do not concern BrimDeck.
- WeType puts a received image on the clipboard as an image file, which programs that take only image data may not paste. That is WeType's own behaviour.
- Other devices can rename this PC. The card then shows the name WeType uses for it. This PC's own name can be changed only before linking.
- If another device removes this PC, the status says so. Choose Unlink, then link again.
- Unlinking in BrimDeck also removes this PC from My devices (我的设备) on the other devices. If that fails, for example without a network connection, BrimDeck says so; you can still unlink, then delete this PC on those devices yourself, or linking again will list it twice.
- Images are PNG or JPEG, up to 16 MiB.

### Send a file from the file shelf

After pairing your phone in BrimDeck, right-click a file and choose **Share**. When Windows sharing is also available, hover over **Share** and choose **Send with WeType** or **Share to more apps**. Sending a Windows shortcut transfers the shortcut file itself.

Choose a paired recipient or display a one-time QR code for the other device to scan. When the two devices are not on the same local network, the files go through the WeType server. You can send several files at once; folders are not supported.

### Receive files from a paired device

In WeType on the sending device, select the device running BrimDeck. BrimDeck shows the sender's name. You can also open **Receive files from WeType** at the top of the file shelf to see this computer’s device name and wait for an invitation. Choose **Accept…** to select a destination, or **Decline**. Each verified file is saved in that folder and added to the file shelf. Files with an existing name receive a numbered suffix. When several files arrive at once, the window shows how many there are and the overall progress.

## Doubao IME

Doubao IME calls this feature 超级互传; the devices of one account sync with each other.

1. In BrimDeck choose Sign in and sign in to your Doubao account in the browser window that opens. It is a separate window of the Chrome or Edge installed on this PC and closes when sign-in is done.
2. In Doubao IME on your phone or another PC, sign in to the same account and turn on 超级互传.

On signing in, BrimDeck turns this PC's switch on once. Afterwards you can turn this PC off or on again among the linked devices on Doubao IME's 超级互传 page: off, BrimDeck stops syncing and says sync is turned off for this device; on again, it resumes by itself. Signing out in BrimDeck takes this PC off Doubao's list of linked devices.

A device that was not removed when it signed out (a BrimDeck signed in before a reinstall, for instance) stays on the list, and Doubao IME itself cannot delete a device. Once signed in, open Advanced at the bottom of the Doubao card to remove the account's other devices one at a time. Normally a device should sign out on itself; a removed device that is still in use needs to sign in again.

Text syncs with its formatting (numbered lists, bold and so on); WeType and the local network sync plain text only. Images up to 2 MiB can be sent for now, a larger one shows "Image too large to send"; received images may be up to 10 MiB.

A PC that already has Doubao IME installed with 超级互传 turned on syncs its clipboard by itself; signing in to Doubao in BrimDeck is not needed there.

## Data and privacy

- Connection details are encrypted with Windows data protection for the current user and kept in BrimDeck's data folder. BrimDeck does not read the account files of installed input methods. On another PC you connect again.
- WeType and Doubao sync go through their own servers; local network sync goes through none.
- The clipboard's excluded applications and the privacy marks of password managers apply here too: those copies are not synced.
- The WeType and Doubao connections are not official public interfaces and may stop working after their updates.
- `clipboard-sync\events.log` in the data folder records what the sync did, for finding problems: times, the kind and size of content, the names of devices and source applications, and the services' error codes. It holds no copied content, and its older half is dropped at 256 KB.
