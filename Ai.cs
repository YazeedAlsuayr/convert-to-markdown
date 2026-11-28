// The built-in AI: the same Gemma 4 vision model used before with Ollama, run by llama.cpp,
// which lives inside the app's own folder. It only runs while files are being converted
// (it is closed with the app, even if the app crashes) - nothing stays running in the background.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConvertToMarkdown;

static class Ai
{
    /// <summary>The vision models the app can use; both can be installed side by side.</summary>
    public record Model(string Id, string Name, string Url, string File, string Vision, string Size);

    public static readonly Model[] Models =
    {
        new("gemma", "Gemma 4", "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/",
            "gemma-4-E4B-it-Q4_0.gguf", "mmproj-gemma-4-E4B-it-Q8_0.gguf", "about 5.2 GB"),
        new("qwen", "Qwen3-VL 4B", "https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct-GGUF/resolve/main/",
            "Qwen3VL-4B-Instruct-Q4_K_M.gguf", "mmproj-Qwen3VL-4B-Instruct-Q8_0.gguf", "about 3 GB")
    };

    public static Model Find(string id) => Models.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    /// <summary>The chosen model if it is installed, otherwise any installed one.</summary>
    public static Model Current =>
        Find(Settings.Model) is { } m && HasModel(m) ? m : Models.FirstOrDefault(HasModel) ?? Find(Settings.Model) ?? Models[1];
    public static string ModelFile => Current.File;
    public static string VisionFile => Current.Vision;
    public static string ModelName => Current.Name;
    public static bool HasModel(Model m) => File.Exists(Path.Combine(ModelDir, m.File)) && File.Exists(Path.Combine(ModelDir, m.Vision));

    public static string Dir => Path.Combine(Program.DataDir, "ai");
    public static string EngineDir => Path.Combine(Dir, "engine");
    public static string ModelDir => Path.Combine(Dir, "model");
    /// <summary>Where the Setup (non-Store) version keeps its models.</summary>
    static string OldModelDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertToMarkdown", "ai", "model");

    /// <summary>The Store version carries the engine inside its package (Store rules: no downloaded programs).</summary>
    static string PackagedEngineDir => Path.Combine(AppContext.BaseDirectory, "engine");

    public static string ServerExe =>
        Program.IsStore ? (File.Exists(Path.Combine(PackagedEngineDir, "llama-server.exe")) ? Path.Combine(PackagedEngineDir, "llama-server.exe") : null)
        : Directory.Exists(EngineDir) ? Directory.GetFiles(EngineDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    public static bool Installed =>
        ServerExe != null && File.Exists(Path.Combine(ModelDir, ModelFile)) && File.Exists(Path.Combine(ModelDir, VisionFile));

    public static bool Enabled => Installed && Settings.UseAi && !broken;

    static Process server;
    static int port;
    static bool broken;                         // failed once in this run: use Windows OCR for the rest
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(6) };
    static readonly SemaphoreSlim gate = new(1, 1);

    // ---------------- prompts (the ones tuned in the Python version) ----------------
    /// <summary>Small models skip signs drawn as little icons (a plus or minus in a circle next to a word).</summary>
    const string Symbols =
        "Copy every mark at the start of a line exactly as drawn (-, ~, *, x, arrows). " +
        "Signs drawn as small icons next to a word, such as a plus or minus in a circle, are written as (+) and (-) after that word.";
    public const string DescribePrompt =
        "This is one page of a document. In at most 3 short lines describe only how it LOOKS: how the content is laid out " +
        "(title, columns, boxes, table, flowchart, diagram, pictures) and any coloured boxes, banners, borders or shapes " +
        "and what they surround. Do not copy the page's text and do not mention handwriting. Plain text only.";
    /// <summary>With "Describe" ticked: what the picture shows, then its text.</summary>
    public const string PicturePrompt =
        "This picture comes from a document. Answer in exactly this format:\n" +
        "DESCRIPTION: what the picture shows, in 1 to 3 short lines (for example: \"Flowchart of the steps of an order\", " +
        "\"Photo of a building at night\", \"Logo of a company\", \"Bar chart of sales by month\").\n" +
        "TEXT:\n" +
        "every piece of text inside the picture exactly, in English or Arabic, in reading order, one per line, " +
        "keeping arrows as -> and every symbol exactly as drawn. " + Symbols + " Copy only text you can clearly see; never add words or numbers that are not in the picture. " +
        "Write [unclear] for unreadable words. If the picture has no text, write NONE after TEXT:. Plain text only.";

    /// <summary>Without "Describe": only the text inside the picture.</summary>
    public const string PictureTextPrompt =
        "Copy every piece of text inside this picture exactly, in English or Arabic, in reading order, one per line, " +
        "keeping arrows as -> and every symbol exactly as drawn. " + Symbols + " Copy only text you can clearly see; never add words or numbers that are not in the picture. " +
        "Write [unclear] for unreadable words. If the picture has no text, write exactly: NONE. Plain text only.";

    /// <summary>Splits an answer to <see cref="PicturePrompt"/> (or <see cref="PictureTextPrompt"/>) into description and text.</summary>
    public static (string description, string text) SplitPicture(string answer, bool describe)
    {
        string a = (answer ?? "").Trim();
        if (!describe) return ("", IsNone(a) ? "" : a);
        var m = Regex.Match(a, @"DESCRIPTION\s*:\s*(.*?)\s*TEXT\s*:\s*(.*)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        string desc, text;
        if (m.Success) { desc = m.Groups[1].Value; text = m.Groups[2].Value.Trim(); }
        else
        {
            var lines = a.Split('\n');                  // no markers: the first line is the description
            desc = Regex.Replace(lines[0], @"^\s*DESCRIPTION\s*:\s*", "", RegexOptions.IgnoreCase);
            text = string.Join("\n", lines.Skip(1)).Trim();
        }
        return (Regex.Replace(desc, @"\s+", " ").Trim(), IsNone(text) ? "" : text);
    }

    static bool IsNone(string t) => t.Trim().Length == 0 || Regex.IsMatch(t.Trim(), @"^\(?\s*(none|no text)\s*\)?\.?$", RegexOptions.IgnoreCase);

    public const string FullPrompt =
        "Transcribe ALL the text in this image exactly as written, in reading order, one line per line of the image, " +
        "in the language it is written (English or Arabic). " + Symbols + " Keep each question and each answer option on its own line. " +
        "After an answer option that is highlighted, circled, ticked or written in red, add (marked). " +
        "If a word is hard to read write [unclear]; never guess and never add text that is not in the image. Plain text only.";

    // ---------------- starting and stopping ----------------
    /// <summary>Why the AI was not used (empty when it was).</summary>
    public static string Status { get; private set; } = "";
    /// <summary>Where the AI runs: on the graphics card, or on the processor when the card failed.</summary>
    public static string Device { get; private set; } = "";

    static string WhyNot =>
        ServerExe == null ? "the AI is not set up yet - run \"Set up AI\" once"
        : !File.Exists(Path.Combine(ModelDir, ModelFile)) || !File.Exists(Path.Combine(ModelDir, VisionFile))
            ? "the AI model files are missing - run \"Set up AI\" again (it continues where it stopped)"
        : !Settings.UseAi ? "the AI is turned off (turn it on with --ai on)"
        : Status;

    /// <summary>Starts the AI if it is not running yet: on the graphics card, else on the processor.</summary>
    public static async Task<bool> EnsureRunningAsync(IProgress<Step> progress, CancellationToken ct)
    {
        if (!Enabled) { Status = WhyNot; return false; }
        await gate.WaitAsync(ct);
        try
        {
            if (server != null && !server.HasExited) return true;
            string why = "";
            foreach (bool gpu in new[] { true, false })
            {
                progress?.Report(new Step(gpu ? "Starting the AI on the graphics card (takes a moment)" : "Starting the AI on the processor (slower)", 0));
                why = await TryStartAsync(gpu, ct);
                if (why == null)
                {
                    Device = gpu ? "graphics card" : "processor";
                    Status = "";
                    Log.Write("AI running on the " + Device);
                    return true;
                }
                Log.Write($"AI did not start on the {(gpu ? "graphics card" : "processor")}: {why}");
            }
            Status = "the AI did not start: " + (why.Length > 300 ? why[^300..] : why);
            broken = true;
            return false;
        }
        finally { gate.Release(); }
    }

    /// <summary>null when the AI started; otherwise the last lines it printed.</summary>
    static async Task<string> TryStartAsync(bool gpu, CancellationToken ct)
    {
        Stop();
        port = FreePort();
        var psi = new ProcessStartInfo(ServerExe)
        {
            WorkingDirectory = Path.GetDirectoryName(ServerExe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[]
        {
            "-m", Path.Combine(ModelDir, ModelFile),
            "--mmproj", Path.Combine(ModelDir, VisionFile),
            "--host", "127.0.0.1", "--port", port.ToString(),
            "-ngl", gpu ? "99" : "0",          // 99 = the whole model on the graphics card, 0 = processor only
            "-c", "8192"
        }) psi.ArgumentList.Add(a);

        var tail = new Queue<string>();
        void Keep(string line) { if (line == null) return; lock (tail) { tail.Enqueue(line); while (tail.Count > 40) tail.Dequeue(); } }
        try
        {
            server = new Process { StartInfo = psi };
            server.OutputDataReceived += (_, e) => Keep(e.Data);
            server.ErrorDataReceived += (_, e) => Keep(e.Data);
            server.Start();
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            JobObject.Attach(server);
        }
        catch (Exception e) { return "could not run llama-server.exe: " + e.Message; }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(4))
        {
            ct.ThrowIfCancellationRequested();
            if (server.HasExited) break;
            try
            {
                using var r = await http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (r.IsSuccessStatusCode) return null;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, ct);
        }
        string why;
        lock (tail) why = string.Join(" | ", tail.Where(l => l.Trim().Length > 0));
        if (server != null && !server.HasExited) why = "did not get ready in 4 minutes | " + why;
        Stop();
        return why.Length > 0 ? why : "it closed without a message";
    }

    /// <summary>For --check-ai: what is installed and whether it starts.</summary>
    public static async Task<string> CheckAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("AI folder: " + Dir);
        sb.AppendLine("Engine (llama-server.exe): " + (ServerExe ?? "NOT FOUND"));
        foreach (var f in new[] { ModelFile, VisionFile })
        {
            string path = Path.Combine(ModelDir, f);
            sb.AppendLine($"{f}: " + (File.Exists(path) ? Gb(new FileInfo(path).Length) : File.Exists(path + ".part") ? "only partly downloaded" : "NOT FOUND"));
        }
        sb.AppendLine("Turned on: " + (Settings.UseAi ? "yes" : "no"));
        sb.AppendLine("Model in use: " + ModelName + "   (installed: " +
                      string.Join(", ", Models.Where(HasModel).Select(m => m.Name).DefaultIfEmpty("none")) + ")");
        if (!Enabled)
        {
            sb.AppendLine().AppendLine("The AI is not used because " + WhyNot + ".");
            return sb.ToString();
        }
        sb.AppendLine();
        bool ok = await EnsureRunningAsync(null, CancellationToken.None);
        sb.AppendLine(ok ? $"The AI started on the {Device}. It is working." : "Problem: " + Status);
        Stop();
        return sb.ToString();
    }

    public static void Stop()
    {
        try { if (server != null && !server.HasExited) server.Kill(true); } catch { }
        server = null;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ---------------- asking ----------------
    /// <summary>Sends one picture and an instruction; returns the model's cleaned answer.</summary>
    public static async Task<string> AskAsync(string prompt, byte[] png, int maxTokens, bool oneLine, CancellationToken ct)
    {
        var body = new
        {
            model = "local",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } },
                        new { type = "text", text = prompt }
                    }
                }
            },
            temperature = 0,
            repeat_penalty = 1.1,
            max_tokens = maxTokens,
            chat_template_kwargs = new { enable_thinking = false }
        };
        var clock = Stopwatch.StartNew();
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions", content, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"AI error {(int)resp.StatusCode}: {json[..Math.Min(json.Length, 200)]}");
        using var doc = JsonDocument.Parse(json);
        string text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        string usage = doc.RootElement.TryGetProperty("usage", out var u)
            ? $"{u.GetProperty("prompt_tokens").GetInt32()} in / {u.GetProperty("completion_tokens").GetInt32()} out" : "";
        Log.Write($"AI {ModelName}: {clock.Elapsed.TotalSeconds:0.0} s, {png.Length / 1024} KB picture, tokens {usage}");
        return Clean(CollapseRepeats(text), oneLine);
    }

    static readonly Dictionary<string, string> Latex = new()
    {
        ["leftrightarrow"] = "↔", ["Leftrightarrow"] = "⇔", ["rightarrow"] = "→", ["Rightarrow"] = "⇒",
        ["leftarrow"] = "←", ["Leftarrow"] = "⇐", ["uparrow"] = "↑", ["downarrow"] = "↓", ["nearrow"] = "↗",
        ["searrow"] = "↘", ["to"] = "→", ["neq"] = "≠", ["ne"] = "≠", ["geq"] = "≥", ["ge"] = "≥",
        ["leq"] = "≤", ["le"] = "≤", ["mu"] = "μ", ["approx"] = "≈", ["times"] = "×", ["pm"] = "±",
        ["circ"] = "°", ["cdot"] = "·", ["alpha"] = "α", ["beta"] = "β", ["Delta"] = "Δ", ["percent"] = "%"
    };

    static string Clean(string s, bool oneLine)
    {
        s = Regex.Replace(s, @"<think>.*?</think>", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\\(?:text|mathrm|mathbf)\{([^}]*)\}", "$1");
        s = Regex.Replace(s, @"\\([A-Za-z]+)", m => Latex.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Groups[1].Value);
        s = s.Replace("$", "").Replace("**", "").Replace("\\%", "%").Replace("^{", "").Replace("_{", "").Replace("```", "");
        if (oneLine) return Regex.Replace(s, @"\s+", " ").Trim();
        var outp = new List<string>();
        foreach (var raw in s.Split('\n'))
        {
            string ln = Regex.Replace(raw, @"\s+", " ").Trim();
            if (ln.Length > 0 || (outp.Count > 0 && outp[^1].Length > 0)) outp.Add(ln);
        }
        return string.Join("\n", outp).Trim();
    }

    /// <summary>Cuts off a model that got stuck repeating the same few characters.</summary>
    static string CollapseRepeats(string s)
    {
        foreach (Match m in Regex.Matches(s, @"(.{1,30}?)(?:[\s,;.]*\1){7,}", RegexOptions.Singleline))
        {
            if (!Regex.IsMatch(m.Groups[1].Value, @"[0-9A-Za-z\u0600-\u06FF]")) continue;   // dotted or dashed lines are fine
            return s[..m.Index] + m.Groups[1].Value + " [model got stuck repeating - check the page]";
        }
        return s;
    }

    // ---------------- one-time setup: download the engine and the model ----------------
    public static async Task<string> SetupAsync(Model model, IProgress<Step> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);
        using var dl = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        dl.DefaultRequestHeaders.UserAgent.ParseAdd("ConvertToMarkdown/2.0");

        // 1. the engine (llama.cpp for Windows, graphics-card version), about 50 MB
        if (ServerExe == null && Program.IsStore)
            throw new InvalidOperationException("The AI engine is missing from the app. Reinstall Convert to Markdown from the Microsoft Store.");
        if (ServerExe == null)
        {
            progress.Report(new Step("Finding the AI engine", 0.01));
            // llama.cpp publishes many versions a day, and the newest one often has no Windows files yet:
            // take the newest version that already has the Windows graphics-card (Vulkan) build.
            string api = await dl.GetStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15", ct);
            using var rels = JsonDocument.Parse(api);
            string zipUrl = null;
            foreach (var rel in rels.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                foreach (var a in rel.GetProperty("assets").EnumerateArray())
                {
                    string name = (a.GetProperty("name").GetString() ?? "").ToLowerInvariant();
                    if (name.Contains("win-vulkan-x64") && name.EndsWith(".zip"))
                    {
                        zipUrl = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
                if (zipUrl != null) break;
            }
            if (zipUrl == null) throw new InvalidOperationException("The AI engine download was not found. Try again later.");
            string zip = Path.Combine(Dir, "engine.zip");
            await DownloadAsync(dl, zipUrl, zip, "Downloading the AI engine", progress, 0.02, 0.06, ct);
            if (Directory.Exists(EngineDir)) Directory.Delete(EngineDir, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, EngineDir);
            File.Delete(zip);
            if (ServerExe == null) throw new InvalidOperationException("The AI engine download did not contain llama-server.exe.");
        }

        // Store version: reuse a model the Setup version already downloaded (copied, so the old copy keeps working)
        if (Program.IsStore)
            foreach (var f in new[] { model.Vision, model.File })
            {
                string old = Path.Combine(OldModelDir, f), dest = Path.Combine(ModelDir, f);
                if (File.Exists(dest) || !File.Exists(old)) continue;
                progress.Report(new Step($"Copying {model.Name} from the earlier version", f == model.File ? 0.5 : 0.1));
                await Task.Run(() => { File.Copy(old, dest + ".part", true); File.Move(dest + ".part", dest, true); }, ct);
            }

        // 2. the model and its picture reader, about 5.2 GB (resumes if interrupted)
        await DownloadAsync(dl, model.Url + model.Vision, Path.Combine(ModelDir, model.Vision), $"Downloading {model.Name}'s picture reader", progress, 0.06, 0.16, ct);
        await DownloadAsync(dl, model.Url + model.File, Path.Combine(ModelDir, model.File), $"Downloading {model.Name}", progress, 0.16, 0.95, ct);
        Settings.Model = model.Id;                       // switch to the model just set up
        Stop();

        // 3. a quick test run
        Settings.UseAi = true;
        broken = false;
        progress.Report(new Step("Testing the AI on your graphics card", 0.96));
        if (!await EnsureRunningAsync(null, ct))
            throw new InvalidOperationException("The AI was downloaded but did not start: " + Status);
        Stop();
        return "AI";
    }

    static async Task DownloadAsync(HttpClient dl, string url, string dest, string label,
                                    IProgress<Step> progress, double from, double to, CancellationToken ct)
    {
        if (File.Exists(dest)) { progress.Report(new Step(label + "  ·  already done", to)); return; }
        string part = dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
        {
            File.Move(part, dest, true);                // it had already finished
            return;
        }
        resp.EnsureSuccessStatusCode();
        if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0;   // server restarted from zero
        long total = (resp.Content.Headers.ContentLength ?? 0) + have;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = have, lastDone = have;
            var clock = Stopwatch.StartNew();
            double lastTick = 0, speed = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                double now = clock.Elapsed.TotalSeconds;
                if (now - lastTick >= 0.5)
                {
                    double inst = (done - lastDone) / Math.Max(now - lastTick, 0.001);
                    speed = speed == 0 ? inst : 0.8 * speed + 0.2 * inst;
                    lastDone = done;
                    lastTick = now;
                    double f = total > 0 ? (double)done / total : 0;
                    string size = total > 0 ? $"{Gb(done)} of {Gb(total)}" : Gb(done);
                    progress.Report(new Step($"{label}  ·  {size}  ·  {speed / 1e6:0.0} MB/s", from + (to - from) * f));
                }
            }
        }
        File.Move(part, dest, true);
    }

    static string Gb(long b) => b >= 1e9 ? $"{b / 1e9:0.00} GB" : $"{b / 1e6:0} MB";

    public static void RemoveModel(Model m)
    {
        Stop();
        foreach (var f in new[] { m.File, m.Vision })
        {
            string path = Path.Combine(ModelDir, f);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".part")) File.Delete(path + ".part");
        }
        if (Settings.Model == m.Id)
        {
            var other = Models.FirstOrDefault(HasModel);
            if (other != null) Settings.Model = other.Id;
        }
    }

    public static void Remove()
    {
        Stop();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
    }
}

/// <summary>Small settings file (settings.json in the app's data folder).</summary>
static class Settings
{
    static string FilePath => Path.Combine(Program.DataDir, "settings.json");

    /// <summary>False until the first choices are saved (the Store version then shows its welcome screens).</summary>
    public static bool Exists => File.Exists(FilePath);

    class Data
    {
        public bool UseAi { get; set; } = true;
        public string Model { get; set; } = "qwen";
        public bool Describe { get; set; } = false;
        public bool HandwritingAi { get; set; } = true;
        public bool PcSetupDone { get; set; } = false;
        public string PcTest { get; set; } = "";      // last speed test: "device|picture s|note s|date"
    }

    static Data Load()
    {
        try { return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data(); }
        catch { return new Data(); }
    }

    static void Save(Data d)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(d)); } catch { }
    }

    public static bool UseAi
    {
        get => Load().UseAi;
        set { var d = Load(); d.UseAi = value; Save(d); }
    }

    /// <summary>The "Describe layout and pictures" tick.</summary>
    public static bool Describe
    {
        get => Load().Describe;
        set { var d = Load(); d.Describe = value; Save(d); }
    }

    /// <summary>Read pen handwriting with the AI (more accurate, slower) or with Windows handwriting recognition (fast).</summary>
    public static bool HandwritingAi
    {
        get => Load().HandwritingAi;
        set { var d = Load(); d.HandwritingAi = value; Save(d); }
    }

    /// <summary>True once the "Set up for this PC" test has run and a speed was chosen.</summary>
    public static bool PcSetupDone
    {
        get => Load().PcSetupDone;
        set { var d = Load(); d.PcSetupDone = value; Save(d); }
    }

    /// <summary>The last speed test result, kept so the window can show it again without testing.</summary>
    public static string PcTest
    {
        get => Load().PcTest ?? "";
        set { var d = Load(); d.PcTest = value; Save(d); }
    }

    public static string Model
    {
        get => Load().Model;
        set { var d = Load(); d.Model = value; Save(d); }
    }
}

/// <summary>Ties the AI process to this app, so Windows closes it whenever the app closes.</summary>
static class JobObject
{
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong R, W, O, RB, WB, OB; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    static IntPtr job;

    public static void Attach(Process p)
    {
        try
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                var info = new ExtendedLimits();
                info.Basic.LimitFlags = 0x2000;                  // kill the AI when the app closes
                SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<ExtendedLimits>());
            }
            AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception e) { Log.Write("job object: " + e.Message); }
    }
}
