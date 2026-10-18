# Read Aloud

Highlight text in **any Windows program** and press a shortcut to hear it read out loud in a natural-sounding voice.

Read Aloud runs quietly in the system tray. It works in web browsers, PDFs, Word, Outlook emails, Notepad and anything else that lets you select and copy text.

---

## Features

- **Works everywhere.** Reads the highlighted text in any app, using one shortcut.
- **Natural neural voices.** These are Microsoft's lifelike voices, the same ones Microsoft Edge uses for "Read aloud". There are UK, US and Australian accents, both male and female.
- **Offline fallback.** If there's no internet connection, it carries on automatically with the built-in Windows voices.
- **Starts quickly on long text.** Long passages are split into sentences, and the first sentence plays while the rest are prepared.
- **Pause, resume and stop.** Pressing the read shortcut while it's speaking stops it.
- **Adjustable speed.** Make it faster or slower with a shortcut. The speed you choose is remembered.
- **Avoids shortcut clashes.** If another program already uses the read shortcut, it picks a free one and tells you which. You can also change it from the tray menu.
- **Keeps your clipboard.** Whatever you had copied before is put back after reading.
- **Optional start with Windows.** Turn it on with one click in the tray menu.
- **Nothing else to install.** It uses what's already built into Windows 10 and 11. There's no Python and no extra runtime.

---

## Requirements

- Windows 10 or Windows 11
- An internet connection for the natural voices. The Windows voices work offline.

---

## Installation

1. Download the latest release zip, or clone this repository.
2. If you downloaded a zip, right-click it and choose **Properties**. If there's an **Unblock** tick box at the bottom, tick it and click **OK**. Then right-click the zip and choose **Extract All**.
3. Open the extracted folder and double-click **`Install.cmd`**.
   - If Windows shows *"Windows protected your PC"*, click **More info**, then **Run anyway**.
4. That's it. Read Aloud is installed to `Documents\Read Aloud`, a **Read Aloud** shortcut is added to your Desktop, and the speaker icon appears in the system tray.

To have it start automatically, right-click the tray icon and tick **Run at startup**.

**Upgrading:** run `Install.cmd` from the new version. It closes the running copy and replaces it. Your settings are kept.

---

## How to use

1. Highlight some text in any program.
2. Press **Ctrl+Alt+R**.
3. Press **Ctrl+Alt+R** again to stop.

If the tray icon is hidden, click the small **^** arrow in the bottom-right of the taskbar.

---

## Controls

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| **Ctrl+Alt+R** | Read highlighted text, or stop if it's already reading |
| **Ctrl+Alt+P** | Pause / resume |
| **Ctrl+Alt+=** | Speak faster |
| **Ctrl+Alt+-** | Speak slower |
| **Ctrl+Alt+V** | Switch to the next voice |

If Ctrl+Alt+R is already taken by another program, Read Aloud switches to the first free option from **Change read shortcut**: Ctrl+Alt+Space, Ctrl+Shift+Space, Ctrl+Alt+F12 or Pause/Break.

### Tray menu (right-click the speaker icon)

| Menu item | What it does |
|---|---|
| **Shortcut keys** | Lists every shortcut. Click one to run it. |
| **Change read shortcut** | Choose a different key combination for reading |
| **Voice** | Pick a natural voice (online) or a Windows voice (offline). It says hello so you can hear it. |
| **Run at startup** | Start Read Aloud automatically when you sign in |
| **Speed** | Shows the current speed |
| **Quit Read Aloud** | Close the app |

Left-click the tray icon to see a reminder of the read shortcut.

---

## Voices

**Natural voices (need internet)**

| Voice | Accent | Gender |
|---|---|---|
| Ryan *(default)* | UK | Male |
| Sonia | UK | Female |
| Libby | UK | Female |
| Thomas | UK | Male |
| Andrew | US | Male |
| Emma | US | Female |
| Aria | US | Female |
| Guy | US | Male |
| Natasha | Australia | Female |
| William | Australia | Male |

**Windows voices (work offline)**

These are the voices installed on your PC, for example George, Hazel, Susan, David, Mark and Zira. The list depends on which Windows language packs you have installed.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| "These shortcuts are already used by another program" | Another app (often graphics or gaming software) owns that key combination. Pick a different one from **Change read shortcut** in the tray menu. |
| Nothing happens when I press the shortcut | Make sure the text is highlighted and the window you're reading from is in front. Apps running as administrator only receive the shortcut if Read Aloud is also run as administrator. |
| A pop-up says it's using the Windows voice | The natural voices couldn't be reached. Check your internet connection. Reading carries on with a Windows voice. |
| "Read Aloud couldn't start" | The error details are saved to `ReadAloud-error.txt` in the Read Aloud folder. Please include them when you open an issue. |
| "Read Aloud is already running" | It's already in the system tray. Click the **^** arrow to find it. |

**Diagnostic log:** create an empty file called `ReadAloud.log` in the Read Aloud folder. The app will then record each read attempt and any voice errors there.

---

## Privacy

- With a **natural voice**, the text you highlight is sent to Microsoft's online speech service to be turned into audio. This is the same service Microsoft Edge's "Read aloud" uses.
- With a **Windows voice**, everything stays on your computer.
- Read Aloud doesn't store your text. Temporary audio files are deleted after they play.

---

## How it works

| File | Purpose |
|---|---|
| `ReadAloud.cs` | The app: tray icon, global shortcuts, text capture, voices and playback |
| `ReadAloud.ps1` | Launcher. It compiles `ReadAloud.cs` with the C# compiler built into Windows PowerShell and starts the app. |
| `Setup.ps1` | Copies the files to `Documents\Read Aloud`, creates the Desktop shortcut and starts the app |
| `Install.cmd` | Double-click wrapper for `Setup.ps1` |
| `ReadAloud.ico` | App icon |

When you press the shortcut, Read Aloud copies the current selection by sending Ctrl+C to the active window, reads the text and then restores your clipboard.

- **Natural voices** are fetched as MP3 audio over a secure WebSocket connection and played with the Windows media player built into the system.
- **Windows voices** use the Windows Speech API (SAPI).

Settings (voice, speed and read shortcut) are stored in the registry under `HKEY_CURRENT_USER\Software\ReadAloud`.

**Uninstall:** quit the app from the tray menu, untick **Run at startup** first if it's on, then delete the `Documents\Read Aloud` folder and the Desktop shortcut.

---

## Disclaimer

Read Aloud is an independent project and isn't affiliated with or endorsed by Microsoft. The natural voices rely on Microsoft's online speech service, which may change or stop working without notice. If that happens, the app falls back to the Windows voices.
