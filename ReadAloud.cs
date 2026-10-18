// Read Aloud - highlight text in any app and press a shortcut to hear it.
// Version 2: natural Microsoft neural voices (online), with the Windows voices as an offline fallback.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Read Aloud")]
[assembly: System.Reflection.AssemblyProduct("Read Aloud")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

static class Program {
    [STAThread]
    static void Main() {
        bool created;
        using (var mutex = new Mutex(true, "Local\\ReadAloudHotkey", out created)) {
            if (!created) {
                MessageBox.Show("Read Aloud is already running.\nLook for the speaker icon in the system tray.", "Read Aloud");
                return;
            }
            Application.EnableVisualStyles();
            Application.Run(new ReadAloudApp());
        }
    }
}

// ---------------------------------------------------------------------------
// Microsoft Edge "Read aloud" neural voices, spoken to over a secure WebSocket.
// Returns MP3 audio for a piece of text.
// ---------------------------------------------------------------------------
public static class EdgeVoices {
    const string Host = "speech.platform.bing.com";
    const string Token = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    const string ChromiumVersion = "143.0.3650.75";
    const string ChromiumMajor = "143";
    static double clockSkewSeconds;

    static string SecMsGec() {
        // Windows file time (100 ns ticks since 1601), rounded down to 5 minutes
        long ticks = DateTime.UtcNow.AddSeconds(clockSkewSeconds).ToFileTimeUtc();
        ticks -= ticks % 3000000000L;
        using (var sha = SHA256.Create()) {
            byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(ticks.ToString(CultureInfo.InvariantCulture) + Token));
            return BitConverter.ToString(hash).Replace("-", "");
        }
    }

    static string NewId() { return Guid.NewGuid().ToString("N"); }

    static string JsDate() {
        return DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture)
               + " GMT+0000 (Coordinated Universal Time)";
    }

    static string EscapeXml(string s) {
        var sb = new StringBuilder(s.Length + 16);
        foreach (char c in s) {
            if (c == '&') sb.Append("&amp;");
            else if (c == '<') sb.Append("&lt;");
            else if (c == '>') sb.Append("&gt;");
            else if (c < 32 && c != '\n' && c != '\r' && c != '\t') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // voiceId like "en-GB-RyanNeural"; ratePercent like +20 or -10
    public static byte[] Synthesize(string text, string voiceId, int ratePercent) {
        try {
            return SynthesizeOnce(text, voiceId, ratePercent);
        } catch (ClockSkewException) {
            return SynthesizeOnce(text, voiceId, ratePercent);   // retry once with the corrected clock
        }
    }

    class ClockSkewException : Exception { }

    static byte[] SynthesizeOnce(string text, string voiceId, int ratePercent) {
        using (var ws = new MiniWebSocket()) {
            string path = "/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=" + Token
                        + "&ConnectionId=" + NewId()
                        + "&Sec-MS-GEC=" + SecMsGec()
                        + "&Sec-MS-GEC-Version=1-" + ChromiumVersion;
            var headers = new List<string> {
                "Pragma: no-cache",
                "Cache-Control: no-cache",
                "Origin: chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold",
                "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/"
                    + ChromiumMajor + ".0.0.0 Safari/537.36 Edg/" + ChromiumMajor + ".0.0.0",
                "Accept-Encoding: gzip, deflate, br, zstd",
                "Accept-Language: en-US,en;q=0.9",
                "Cookie: muid=" + NewId().ToUpperInvariant() + ";"
            };

            int status; DateTime? serverDate;
            ws.Connect(Host, path, headers, out status, out serverDate);
            if (status == 403 && serverDate.HasValue) {
                double skew = (serverDate.Value - DateTime.UtcNow).TotalSeconds;
                if (Math.Abs(skew) > 2) { clockSkewSeconds += skew; throw new ClockSkewException(); }
            }
            if (status != 101) throw new Exception("Voice service refused the connection (HTTP " + status + ")");

            ws.SendText(
                "X-Timestamp:" + JsDate() + "\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
                "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
                "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n");

            string fullName = VoiceFullName(voiceId);
            string rate = (ratePercent >= 0 ? "+" : "") + ratePercent + "%";
            string ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                "<voice name='" + fullName + "'>" +
                "<prosody pitch='+0Hz' rate='" + rate + "' volume='+0%'>" +
                EscapeXml(text) +
                "</prosody></voice></speak>";
            ws.SendText(
                "X-RequestId:" + NewId() + "\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                "X-Timestamp:" + JsDate() + "Z\r\n" +
                "Path:ssml\r\n\r\n" + ssml);

            var audio = new MemoryStream();
            while (true) {
                bool isText; byte[] msg;
                if (!ws.Receive(out isText, out msg)) break;   // connection closed
                if (isText) {
                    string s = Encoding.UTF8.GetString(msg);
                    if (s.Contains("Path:turn.end")) break;
                } else if (msg.Length >= 2) {
                    int headerLen = (msg[0] << 8) | msg[1];
                    if (2 + headerLen > msg.Length) continue;
                    string header = Encoding.ASCII.GetString(msg, 2, headerLen);
                    if (header.Contains("Path:audio"))
                        audio.Write(msg, 2 + headerLen, msg.Length - 2 - headerLen);
                }
            }
            if (audio.Length == 0) throw new Exception("Voice service returned no audio");
            return audio.ToArray();
        }
    }

    // "en-GB-RyanNeural" -> "Microsoft Server Speech Text to Speech Voice (en-GB, RyanNeural)"
    static string VoiceFullName(string id) {
        int dash = id.IndexOf('-');
        int dash2 = dash < 0 ? -1 : id.IndexOf('-', dash + 1);
        if (dash2 < 0) return id;
        return "Microsoft Server Speech Text to Speech Voice (" + id.Substring(0, dash2) + ", " + id.Substring(dash2 + 1) + ")";
    }
}

// Minimal WebSocket client over TLS (gives full control of the request headers).
public class MiniWebSocket : IDisposable {
    TcpClient tcp;
    Stream stream;
    readonly Random rng = new Random();

    public void Connect(string host, string pathAndQuery, List<string> extraHeaders, out int status, out DateTime? serverDate) {
        status = 0; serverDate = null;
        var target = new Uri("https://" + host + "/");
        Uri proxyUri = null;
        try {
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            if (proxy != null && !proxy.IsBypassed(target)) {
                Uri p = proxy.GetProxy(target);
                if (p != null && !string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase)) proxyUri = p;
            }
        } catch { }

        tcp = new TcpClient();
        string connectHost = proxyUri != null ? proxyUri.Host : host;
        int connectPort = proxyUri != null ? proxyUri.Port : 443;
        IAsyncResult ar = tcp.BeginConnect(connectHost, connectPort, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(10000)) throw new Exception("Timed out connecting to the voice service");
        tcp.EndConnect(ar);
        Stream raw = tcp.GetStream();
        raw.ReadTimeout = 15000; raw.WriteTimeout = 15000;

        if (proxyUri != null) {
            byte[] req = Encoding.ASCII.GetBytes("CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n\r\n");
            raw.Write(req, 0, req.Length);
            string resp = ReadHeaders(raw);
            if (ParseStatus(resp) != 200) throw new Exception("Proxy refused the connection");
        }

        var ssl = new SslStream(raw, false);
        ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
        ssl.ReadTimeout = 15000; ssl.WriteTimeout = 15000;
        stream = ssl;

        var keyBytes = new byte[16];
        rng.NextBytes(keyBytes);
        var sb = new StringBuilder();
        sb.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(host).Append("\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Key: ").Append(Convert.ToBase64String(keyBytes)).Append("\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        foreach (string h in extraHeaders) sb.Append(h).Append("\r\n");
        sb.Append("\r\n");
        byte[] hs = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(hs, 0, hs.Length);
        stream.Flush();

        string response = ReadHeaders(stream);
        status = ParseStatus(response);
        Match m = Regex.Match(response, @"\r\nDate:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        if (m.Success) {
            DateTime d;
            if (DateTime.TryParseExact(m.Groups[1].Value.Trim(), "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                serverDate = d;
        }
    }

    static int ParseStatus(string response) {
        Match m = Regex.Match(response, @"^HTTP/\d\.\d\s+(\d{3})");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    static string ReadHeaders(Stream s) {
        var buf = new List<byte>();
        while (buf.Count < 65536) {
            int b = s.ReadByte();
            if (b < 0) break;
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) break;
        }
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    void ReadExact(byte[] buf, int count) {
        int got = 0;
        while (got < count) {
            int n = stream.Read(buf, got, count - got);
            if (n <= 0) throw new IOException("Connection closed");
            got += n;
        }
    }

    void SendFrame(int opcode, byte[] payload) {
        var frame = new MemoryStream();
        frame.WriteByte((byte)(0x80 | opcode));
        int len = payload.Length;
        if (len < 126) frame.WriteByte((byte)(0x80 | len));
        else if (len < 65536) { frame.WriteByte(0x80 | 126); frame.WriteByte((byte)(len >> 8)); frame.WriteByte((byte)len); }
        else {
            frame.WriteByte(0x80 | 127);
            for (int i = 7; i >= 0; i--) frame.WriteByte((byte)(((long)len >> (8 * i)) & 0xFF));
        }
        var mask = new byte[4];
        rng.NextBytes(mask);
        frame.Write(mask, 0, 4);
        for (int i = 0; i < len; i++) frame.WriteByte((byte)(payload[i] ^ mask[i % 4]));
        byte[] data = frame.ToArray();
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    public void SendText(string text) { SendFrame(1, Encoding.UTF8.GetBytes(text)); }

    // Returns false when the server closes the connection.
    public bool Receive(out bool isText, out byte[] message) {
        isText = false; message = null;
        var assembled = new MemoryStream();
        int messageOpcode = -1;
        var head = new byte[2];
        while (true) {
            ReadExact(head, 2);
            bool fin = (head[0] & 0x80) != 0;
            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long len = head[1] & 0x7F;
            if (len == 126) { var b = new byte[2]; ReadExact(b, 2); len = (b[0] << 8) | b[1]; }
            else if (len == 127) { var b = new byte[8]; ReadExact(b, 8); len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | b[i]; }
            byte[] mask = null;
            if (masked) { mask = new byte[4]; ReadExact(mask, 4); }
            if (len > 50000000) throw new IOException("Frame too large");
            var payload = new byte[len];
            ReadExact(payload, (int)len);
            if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

            if (opcode == 8) return false;                          // close
            if (opcode == 9) { SendFrame(10, payload); continue; }  // ping -> pong
            if (opcode == 10) continue;                             // pong
            if (opcode != 0) messageOpcode = opcode;
            assembled.Write(payload, 0, payload.Length);
            if (fin) {
                isText = messageOpcode == 1;
                message = assembled.ToArray();
                return true;
            }
        }
    }

    public void Dispose() {
        try { if (stream != null) { SendFrame(8, new byte[0]); } } catch { }
        try { if (stream != null) stream.Dispose(); } catch { }
        try { if (tcp != null) tcp.Close(); } catch { }
    }
}

// ---------------------------------------------------------------------------
// Speaker: plays natural voices (MP3 via Windows' built-in MCI player) or the
// classic Windows SAPI voices.
// ---------------------------------------------------------------------------
public class Speaker {
    const int ASYNC = 1, PURGE = 2, NOT_XML = 16;
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

    readonly dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
    readonly Control ui;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly ConcurrentQueue<string> clips = new ConcurrentQueue<string>();
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "ReadAloud");
    int generation;          // bumped on every Stop so stale background work is ignored
    volatile bool synthesizing;
    string playingAlias, playingFile;
    int playStarted, aliasCounter, rate;
    bool paused;

    public Action<string> Notice;   // shown as a tray balloon
    public Action<string> Log;

    public class VoiceChoice {
        public string Name, Label, EdgeId; public object Token;
        public bool Natural { get { return EdgeId != null; } }
    }
    public readonly List<VoiceChoice> Voices = new List<VoiceChoice>();
    public int Current;

    static readonly string[][] NaturalVoices = {
        new[] { "en-GB-RyanNeural",    "Ryan",    "UK", "male" },
        new[] { "en-GB-SoniaNeural",   "Sonia",   "UK", "female" },
        new[] { "en-GB-LibbyNeural",   "Libby",   "UK", "female" },
        new[] { "en-GB-ThomasNeural",  "Thomas",  "UK", "male" },
        new[] { "en-US-AndrewNeural",  "Andrew",  "US", "male" },
        new[] { "en-US-EmmaNeural",    "Emma",    "US", "female" },
        new[] { "en-US-AriaNeural",    "Aria",    "US", "female" },
        new[] { "en-US-GuyNeural",     "Guy",     "US", "male" },
        new[] { "en-AU-NatashaNeural", "Natasha", "AU", "female" },
        new[] { "en-AU-WilliamNeural", "William", "AU", "male" },
    };

    public Speaker(Control ui) {
        this.ui = ui;
        try { Directory.CreateDirectory(tempDir); } catch { }
        CleanTemp();

        foreach (var v in NaturalVoices)
            Voices.Add(new VoiceChoice { EdgeId = v[0], Name = v[1] + " (natural)", Label = v[1] + " (" + v[2] + ", " + v[3] + ")" });

        // Windows voices: modern OneCore voices first; older "Desktop" duplicates skipped
        foreach (var category in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices",
                                         @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices" }) {
            try {
                dynamic cat = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory"));
                cat.SetId(category, false);
                foreach (dynamic tok in cat.EnumerateTokens()) {
                    string name = ((string)tok.GetAttribute("Name")).Replace("Microsoft ", "").Replace(" Desktop", "");
                    if (Voices.Exists(v => v.Name == name)) continue;
                    string desc = tok.GetDescription();
                    string region = desc.Contains("United Kingdom") ? "UK" : desc.Contains("United States") ? "US"
                                  : desc.Contains("Australia") ? "AU" : desc.Contains("India") ? "IN" : "";
                    string gender = ((string)tok.GetAttribute("Gender")).ToLower();
                    string label = name + " (" + (region != "" ? region + ", " : "") + gender + ")";
                    Voices.Add(new VoiceChoice { Name = name, Label = label, Token = tok });
                }
            } catch { }
        }

        timer.Interval = 80;
        timer.Tick += (o, e) => Pump();
        timer.Start();
    }

    bool SapiRunning { get { try { return (int)voice.Status.RunningState == 2; } catch { return false; } } }

    public bool Busy {
        get { return paused || SapiRunning || playingAlias != null || !clips.IsEmpty || synthesizing; }
    }

    public int Rate {
        get { return rate; }
        set { rate = Math.Max(-10, Math.Min(10, value)); try { voice.Rate = rate; } catch { } }
    }

    // Natural voices: each speed step is 10% faster, or 5% slower
    int NaturalRatePercent { get { return rate >= 0 ? rate * 10 : rate * 5; } }

    public void SelectVoice(int i) {
        if (i < 0 || i >= Voices.Count) return;
        Stop();
        Current = i;
        if (!Voices[i].Natural) { try { voice.Voice = Voices[i].Token; } catch { } }
    }

    public void Speak(string text) {
        Stop();
        VoiceChoice v = Voices[Current];
        if (!v.Natural) { voice.Speak(text, ASYNC | PURGE | NOT_XML); return; }

        int gen = generation;
        string edgeId = v.EdgeId;
        int ratePct = NaturalRatePercent;
        List<string> chunks = SplitText(text);
        synthesizing = true;
        var worker = new Thread(() => {
            try {
                for (int i = 0; i < chunks.Count; i++) {
                    if (gen != generation) return;
                    byte[] mp3;
                    try {
                        mp3 = EdgeVoices.Synthesize(chunks[i], edgeId, ratePct);
                    } catch (Exception ex) {
                        if (Log != null) Log("natural voice failed: " + ex.Message);
                        if (gen != generation) return;
                        string rest = string.Join(" ", chunks.GetRange(i, chunks.Count - i).ToArray());
                        ui.BeginInvoke((Action)(() => FallBackToWindowsVoice(gen, rest, v)));
                        return;
                    }
                    if (gen != generation) return;
                    string file = Path.Combine(tempDir, "clip_" + Guid.NewGuid().ToString("N") + ".mp3");
                    File.WriteAllBytes(file, mp3);
                    clips.Enqueue(file);
                }
            } catch (Exception ex) {
                if (Log != null) Log("speech worker failed: " + ex);
            } finally {
                if (gen == generation) synthesizing = false;
            }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    void FallBackToWindowsVoice(int gen, string text, VoiceChoice wanted) {
        if (gen != generation) return;
        // Wait for any clips already made to finish, then continue with a Windows voice
        if (playingAlias != null || !clips.IsEmpty) {
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            t.Tick += (o, e) => {
                if (gen != generation) { t.Dispose(); return; }
                if (playingAlias == null && clips.IsEmpty) { t.Dispose(); FallBackToWindowsVoice(gen, text, wanted); }
            };
            t.Start();
            return;
        }
        // Pick a Windows voice with the same accent and gender if there is one
        string accent = wanted.Label.Contains("(UK") ? "(UK" : wanted.Label.Contains("(US") ? "(US" : wanted.Label.Contains("(AU") ? "(AU" : "";
        string gender = wanted.Label.Contains("female") ? "female" : "male";
        VoiceChoice best = Voices.Find(x => !x.Natural && accent != "" && x.Label.Contains(accent) && x.Label.Contains(gender + ")"))
                        ?? Voices.Find(x => !x.Natural && accent != "" && x.Label.Contains(accent))
                        ?? Voices.Find(x => !x.Natural);
        try { if (best != null) voice.Voice = best.Token; } catch { }
        synthesizing = false;
        if (Notice != null) Notice("Couldn't reach the natural voices (are you online?). Using the Windows voice instead.");
        voice.Speak(text, ASYNC | PURGE | NOT_XML);
    }

    // Plays queued clips one after another (runs on the UI thread)
    void Pump() {
        if (paused) return;
        if (playingAlias != null) {
            string mode = Mci("status " + playingAlias + " mode");
            bool justStarted = unchecked(Environment.TickCount - playStarted) < 400;
            if (mode == "playing" || justStarted) return;
            CloseClip();
        }
        string file;
        if (clips.TryDequeue(out file)) {
            string alias = "readaloud" + (++aliasCounter);
            int err = mciSendString("open \"" + file + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero);
            if (err != 0 && Log != null) Log("could not open audio clip (MCI error " + err + ")");
            Mci("play " + alias);
            playingAlias = alias; playingFile = file;
            playStarted = Environment.TickCount;
        }
    }

    void CloseClip() {
        if (playingAlias != null) {
            Mci("stop " + playingAlias);
            Mci("close " + playingAlias);
        }
        TryDelete(playingFile);
        playingAlias = null; playingFile = null;
    }

    static string Mci(string command) {
        var sb = new StringBuilder(128);
        mciSendString(command, sb, sb.Capacity, IntPtr.Zero);
        return sb.ToString();
    }

    static void TryDelete(string file) { try { if (file != null) File.Delete(file); } catch { } }

    void CleanTemp() {
        try { foreach (string f in Directory.GetFiles(tempDir, "clip_*.mp3")) TryDelete(f); } catch { }
    }

    public void Stop() {
        generation++;
        synthesizing = false;
        string f;
        while (clips.TryDequeue(out f)) TryDelete(f);
        CloseClip();
        try {
            if (paused) voice.Resume();
            voice.Speak("", ASYNC | PURGE);
        } catch { }
        paused = false;
    }

    public void PauseOrResume() {
        if (paused) {
            paused = false;
            if (playingAlias != null) Mci("play " + playingAlias);
            try { voice.Resume(); } catch { }
        } else if (Busy) {
            paused = true;
            if (playingAlias != null) Mci("pause " + playingAlias);
            if (SapiRunning) { try { voice.Pause(); } catch { } }
        }
    }

    // Sentence-sized pieces: a short first piece so speech starts quickly
    static List<string> SplitText(string text) {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var parts = Regex.Split(text, @"(?<=[.!?;:])\s+");
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (string raw in parts) {
            string s = raw;
            int limit = chunks.Count == 0 ? 250 : 1200;
            while (s.Length > 1200) {                // a giant "sentence" with no full stops
                int cut = s.LastIndexOf(' ', 1000);
                if (cut < 200) cut = 1000;
                if (current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); }
                chunks.Add(s.Substring(0, cut));
                s = s.Substring(cut).TrimStart();
            }
            if (current.Length > 0 && current.Length + s.Length + 1 > limit) {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(s);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }
}

// ---------------------------------------------------------------------------
// The tray app
// ---------------------------------------------------------------------------
public class ReadAloudApp : Form {
    // Set by the launcher: the command Windows should run at sign-in, and where the log lives
    public static string StartupCommand;
    public static string LogFile;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000, KEYUP = 2;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "ReadAloud";
    const string SettingsKey = @"Software\ReadAloud";

    class Shortcut {
        public int Id; public uint Mods; public Keys Key; public string Label; public Action Run; public bool Registered;
    }

    // Choices offered for the main "read" shortcut (first free one is used if none is saved)
    static readonly uint[] ReadMods = { MOD_CONTROL | MOD_ALT, MOD_CONTROL | MOD_ALT, MOD_CONTROL | MOD_SHIFT, MOD_CONTROL | MOD_ALT, 0 };
    static readonly Keys[] ReadKeys = { Keys.R, Keys.Space, Keys.Space, Keys.F12, Keys.Pause };

    readonly Speaker synth;
    readonly NotifyIcon tray = new NotifyIcon();
    ToolStripMenuItem voiceMenu, readKeyMenu;
    readonly List<ToolStripMenuItem> voiceItems = new List<ToolStripMenuItem>();
    Shortcut[] shortcuts;
    ToolStripMenuItem[] shortcutItems;

    public ReadAloudApp() {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;

        synth = new Speaker(this);
        synth.Log = Log;
        synth.Notice = msg => tray.ShowBalloonTip(5000, "Read Aloud", msg, ToolTipIcon.Info);

        shortcuts = new[] {
            new Shortcut { Id = 1, Mods = MOD_CONTROL | MOD_ALT, Key = Keys.R,  Label = "Read highlighted text / Stop", Run = ReadOrStop },
            new Shortcut { Id = 2, Mods = MOD_CONTROL | MOD_ALT, Key = Keys.P,  Label = "Pause / Resume",               Run = () => synth.PauseOrResume() },
            new Shortcut { Id = 3, Mods = MOD_CONTROL | MOD_ALT, Key = Keys.Oemplus,  Label = "Speak faster",           Run = () => ChangeRate(+1) },
            new Shortcut { Id = 4, Mods = MOD_CONTROL | MOD_ALT, Key = Keys.OemMinus, Label = "Speak slower",           Run = () => ChangeRate(-1) },
            new Shortcut { Id = 5, Mods = MOD_CONTROL | MOD_ALT, Key = Keys.V,  Label = "Next voice",                   Run = NextVoice },
        };

        CreateHandle();
        LoadSettings();

        // Main shortcut: the saved one, else the first free choice
        Shortcut read = shortcuts[0];
        string fallbackNote = null;
        if (!Register(read)) {
            string wanted = KeyText(read.Mods, read.Key);
            for (int i = 0; i < ReadKeys.Length && !read.Registered; i++) {
                read.Mods = ReadMods[i]; read.Key = ReadKeys[i];
                if (Register(read)) {
                    fallbackNote = wanted + " is used by another program, so the read shortcut is now " + KeyText(read.Mods, read.Key) + ".";
                    SaveSettings();
                }
            }
        }
        string failed = "";
        for (int i = 1; i < shortcuts.Length; i++)
            if (!Register(shortcuts[i])) failed += "\n  " + KeyText(shortcuts[i].Mods, shortcuts[i].Key) + "  (" + shortcuts[i].Label + ")";
        if (!read.Registered) failed = "\n  Read highlighted text (no free shortcut found)" + failed;

        tray.Icon = MakeIcon();
        tray.ContextMenuStrip = BuildMenu();
        tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowBalloon(); };
        tray.Visible = true;
        UpdateTrayText();

        if (failed != "")
            MessageBox.Show("These shortcuts are already used by another program and won't work:" + failed
                + "\n\nYou can pick a different read shortcut from the tray icon menu.", "Read Aloud");
        else if (fallbackNote != null)
            tray.ShowBalloonTip(6000, "Read Aloud", fallbackNote, ToolTipIcon.Info);
        else
            ShowBalloon();
    }

    bool Register(Shortcut s) {
        if (s.Registered) { UnregisterHotKey(Handle, s.Id); s.Registered = false; }
        s.Registered = RegisterHotKey(Handle, s.Id, s.Mods | MOD_NOREPEAT, (uint)s.Key);
        return s.Registered;
    }

    static string KeyText(uint mods, Keys k) {
        string name = k == Keys.Oemplus ? "=" : k == Keys.OemMinus ? "-" : k == Keys.Pause ? "Pause/Break" : k.ToString();
        string prefix = "";
        if ((mods & MOD_CONTROL) != 0) prefix += "Ctrl+";
        if ((mods & MOD_ALT) != 0) prefix += "Alt+";
        if ((mods & MOD_SHIFT) != 0) prefix += "Shift+";
        return prefix + name;
    }

    string ReadKeyText { get { return KeyText(shortcuts[0].Mods, shortcuts[0].Key); } }

    void UpdateTrayText() {
        string t = "Read Aloud - " + ReadKeyText;
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
    }

    void ShowBalloon() {
        tray.ShowBalloonTip(4000, "Read Aloud is running",
            "Highlight text and press " + ReadKeyText + ".\nRight-click this icon for voices and shortcuts.", ToolTipIcon.Info);
    }

    ContextMenuStrip BuildMenu() {
        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem("Shortcut keys") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        menu.Items.Add(header);
        shortcutItems = new ToolStripMenuItem[shortcuts.Length];
        for (int i = 0; i < shortcuts.Length; i++) {
            var sc = shortcuts[i];
            var item = new ToolStripMenuItem(sc.Label);
            item.Click += (o, e) => { if (sc.Id != 1) sc.Run(); else if (synth.Busy) synth.Stop(); };
            shortcutItems[i] = item;
            menu.Items.Add(item);
        }

        readKeyMenu = new ToolStripMenuItem("Change read shortcut");
        for (int i = 0; i < ReadKeys.Length; i++) {
            int idx = i;
            var item = new ToolStripMenuItem(KeyText(ReadMods[i], ReadKeys[i]));
            item.Click += (o, e) => ChangeReadShortcut(ReadMods[idx], ReadKeys[idx]);
            readKeyMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(readKeyMenu);
        menu.Items.Add(new ToolStripSeparator());

        voiceMenu = new ToolStripMenuItem("Voice");
        bool naturalHeader = false, windowsHeader = false;
        for (int i = 0; i < synth.Voices.Count; i++) {
            int idx = i;
            var v = synth.Voices[i];
            if (v.Natural && !naturalHeader) {
                naturalHeader = true;
                var h = new ToolStripMenuItem("Natural voices (need internet)") { Enabled = false };
                h.Font = new Font(h.Font, FontStyle.Bold);
                voiceMenu.DropDownItems.Add(h);
            }
            if (!v.Natural && !windowsHeader) {
                windowsHeader = true;
                voiceMenu.DropDownItems.Add(new ToolStripSeparator());
                var h = new ToolStripMenuItem("Windows voices (work offline)") { Enabled = false };
                h.Font = new Font(h.Font, FontStyle.Bold);
                voiceMenu.DropDownItems.Add(h);
            }
            var item = new ToolStripMenuItem(v.Label);
            item.Click += (o, e) => ChooseVoice(idx);
            voiceItems.Add(item);
            voiceMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(voiceMenu);

        var startup = new ToolStripMenuItem("Run at startup") { CheckOnClick = true, Checked = IsStartupEnabled() };
        startup.Click += (o, e) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        var speed = new ToolStripMenuItem("Speed: normal") { Enabled = false };
        menu.Items.Add(speed);

        menu.Opening += (o, e) => {
            speed.Text = "Speed: " + (synth.Rate == 0 ? "normal" : (synth.Rate > 0 ? "+" : "") + synth.Rate);
            startup.Checked = IsStartupEnabled();
            voiceMenu.Text = "Voice: " + synth.Voices[synth.Current].Name;
            for (int i = 0; i < voiceItems.Count; i++) voiceItems[i].Checked = i == synth.Current;
            for (int i = 0; i < shortcuts.Length; i++)
                shortcutItems[i].ShortcutKeyDisplayString = shortcuts[i].Registered ? KeyText(shortcuts[i].Mods, shortcuts[i].Key) : "(unavailable)";
            string current = ReadKeyText;
            foreach (ToolStripMenuItem it in readKeyMenu.DropDownItems) it.Checked = it.Text == current;
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Read Aloud", null, (o, e) => Close());
        return menu;
    }

    void ChangeReadShortcut(uint mods, Keys key) {
        Shortcut read = shortcuts[0];
        uint oldMods = read.Mods; Keys oldKey = read.Key;
        read.Mods = mods; read.Key = key;
        if (Register(read)) {
            SaveSettings();
            UpdateTrayText();
            tray.ShowBalloonTip(4000, "Read Aloud", "Read shortcut is now " + ReadKeyText + ".", ToolTipIcon.Info);
        } else {
            MessageBox.Show(KeyText(mods, key) + " is already used by another program. Please pick a different one.", "Read Aloud");
            read.Mods = oldMods; read.Key = oldKey;
            Register(read);
        }
    }

    static bool IsStartupEnabled() {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(AppName) != null;
    }

    static void SetStartup(bool on) {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) {
            if (on) key.SetValue(AppName, StartupCommand ?? ("\"" + Application.ExecutablePath + "\""));
            else key.DeleteValue(AppName, false);
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_HOTKEY) {
            int id = m.WParam.ToInt32();
            foreach (var s in shortcuts)
                if (s.Id == id) { try { s.Run(); } catch (Exception ex) { Log(s.Label + " failed: " + ex); } }
        }
        base.WndProc(ref m);
    }

    void ChangeRate(int delta) {
        synth.Rate += delta;
        SaveSettings();
    }

    void ChooseVoice(int i) {
        synth.SelectVoice(i);
        SaveSettings();
        string name = synth.Voices[i].Name.Replace(" (natural)", "");
        synth.Speak("Hello, I'm " + name + ".");
    }

    void NextVoice() {
        if (synth.Voices.Count > 0) ChooseVoice((synth.Current + 1) % synth.Voices.Count);
    }

    // Remembers the chosen voice, speed and read shortcut between sessions
    void LoadSettings() {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey)) {
            // "Voice2" is new in version 2, so everyone starts on a natural voice
            string name = key == null ? null : key.GetValue("Voice2") as string;
            int idx = synth.Voices.FindIndex(v => v.Name == name);
            synth.SelectVoice(idx >= 0 ? idx : 0);
            if (key != null) {
                try { synth.Rate = Convert.ToInt32(key.GetValue("Rate", 0)); } catch { }
                try {
                    object mods = key.GetValue("ReadMods"), k = key.GetValue("ReadKey");
                    if (mods != null && k != null) { shortcuts[0].Mods = (uint)Convert.ToInt32(mods); shortcuts[0].Key = (Keys)Convert.ToInt32(k); }
                } catch { }
            }
        }
    }

    void SaveSettings() {
        using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey)) {
            key.SetValue("Voice2", synth.Voices[synth.Current].Name);
            key.SetValue("Rate", synth.Rate);
            key.SetValue("ReadMods", (int)shortcuts[0].Mods);
            key.SetValue("ReadKey", (int)shortcuts[0].Key);
        }
    }

    // Diagnostics: create an empty ReadAloud.log next to the app to turn on logging
    static string LogPath { get { return LogFile ?? Path.ChangeExtension(Application.ExecutablePath, ".log"); } }
    static void Log(string s) {
        try { if (File.Exists(LogPath)) File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }

    static bool IsHeld(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    static void SendCtrlC() {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);      // Ctrl down
        Thread.Sleep(30);
        keybd_event(0x43, 0, 0, UIntPtr.Zero);      // C down
        Thread.Sleep(30);
        keybd_event(0x43, 0, KEYUP, UIntPtr.Zero);  // C up
        Thread.Sleep(30);
        keybd_event(0x11, 0, KEYUP, UIntPtr.Zero);  // Ctrl up
    }

    void ReadOrStop() {
        if (synth.Busy) { synth.Stop(); return; }

        // Wait for Ctrl/Alt/Shift to be released so the copy isn't Ctrl+Alt+C
        for (int waited = 0; waited < 1500 && (IsHeld(0x11) || IsHeld(0x12) || IsHeld(0x10)); waited += 20)
            Thread.Sleep(20);

        string savedText = null;
        try { if (Clipboard.ContainsText()) savedText = Clipboard.GetText(); } catch { }

        uint seq = GetClipboardSequenceNumber();
        // Office sometimes ignores a copy that arrives too fast, so try up to 3 times, slowly
        for (int attempt = 0; attempt < 3 && GetClipboardSequenceNumber() == seq; attempt++) {
            SendCtrlC();
            for (int t = 0; t < 700 && GetClipboardSequenceNumber() == seq; t += 20)
                Thread.Sleep(20);
        }
        Log("hotkey: foreground copy " + (GetClipboardSequenceNumber() == seq ? "produced nothing" : "ok"));
        if (GetClipboardSequenceNumber() == seq) return; // nothing was selected

        string text = null;
        for (int i = 0; i < 10 && text == null; i++) {
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (text == null) Thread.Sleep(50);
        }
        if (savedText != null)
            for (int i = 0; i < 5; i++) { try { Clipboard.SetText(savedText); break; } catch { Thread.Sleep(50); } }

        Log("speaking " + (text == null ? 0 : text.Length) + " chars with " + synth.Voices[synth.Current].Name);
        if (!string.IsNullOrWhiteSpace(text)) synth.Speak(text);
    }

    // Speaker icon drawn at runtime so the app is fully self-contained
    public static Bitmap DrawSpeaker(int size) {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = size / 32f;
            using (var bg = new SolidBrush(Color.FromArgb(0, 120, 212)))
                g.FillEllipse(bg, 0, 0, size - 1, size - 1);
            var body = new PointF[] {
                new PointF(7*u, 12*u), new PointF(12*u, 12*u), new PointF(18*u, 7*u),
                new PointF(18*u, 25*u), new PointF(12*u, 20*u), new PointF(7*u, 20*u) };
            g.FillPolygon(Brushes.White, body);
            using (var pen = new Pen(Color.White, 2.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawArc(pen, 15*u, 11*u, 8*u, 10*u, -50, 100);
                g.DrawArc(pen, 14*u, 7*u, 13*u, 18*u, -50, 100);
            }
        }
        return bmp;
    }

    static Icon MakeIcon() {
        using (var bmp = DrawSpeaker(32)) return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        foreach (var s in shortcuts) if (s.Registered) UnregisterHotKey(Handle, s.Id);
        tray.Visible = false;
        tray.Dispose();
        synth.Stop();
        base.OnFormClosed(e);
    }
}
