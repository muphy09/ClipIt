using System.Security.Cryptography;
using System.Text.Json;

namespace ClipIt;

internal static class MediaChecks
{
    // Integration checks use newly generated video in their own unique subdirectory.
    internal static async Task<int> Run(string root)
    {
        string folder = Path.Combine(Path.GetFullPath(root), "media-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("CLIPIT_DATA", Path.Combine(folder, "app-data"));
        var checks = new List<string>();
        string original = Path.Combine(folder, "original.mp4"), exported = Path.Combine(folder, "export.mp4");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void Require(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        try
        {
            var generated = await MediaTools.Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=60:duration=8", "-f", "lavfi", "-i", "sine=frequency=440:duration=8", "-f", "lavfi", "-i", "sine=frequency=880:duration=8", "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-c:a", "aac", "-shortest", "-y", original], CancellationToken.None);
            Require(generated.Exit == 0, "Generated isolated test video with two audio tracks");
            string before = Hash(original); var stamp = ClipStamp.Read(original);
            string thumbnail = await MediaTools.Thumbnail(original, stamp, CancellationToken.None);
            var previewInfo = await MediaTools.PreviewInfo(original, thumbnail, CancellationToken.None);
            Require(File.Exists(thumbnail) && previewInfo.Width == 320 && previewInfo.Height == 180 && Math.Abs(previewInfo.Duration - 8) < .1,
                "A fresh thumbnail and its list details can be cached");
            string offline = original + ".offline";
            File.Move(original, offline);
            try
            {
                Require(await MediaTools.Thumbnail(original, stamp, CancellationToken.None) == thumbnail
                    && await MediaTools.PreviewInfo(original, thumbnail, CancellationToken.None) == previewInfo,
                    "Cached thumbnails and list details are reusable without opening the source video");
            }
            finally { File.Move(offline, original); }
            bool rejected = false;
            try { await MediaTools.Trim(original, original, 3, 2, stamp, CancellationToken.None); } catch (ArgumentException) { rejected = true; }
            Require(rejected && Hash(original) == before, "Invalid range preserves original bytes");
            rejected = false;
            try { await MediaTools.Trim(original, original, 1, 3, stamp with { Size = stamp.Size + 1 }, CancellationToken.None); } catch (IOException) { rejected = true; }
            Require(rejected && Hash(original) == before, "Changed-file guard preserves original bytes");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            rejected = false;
            try { await MediaTools.Trim(original, original, 1, 3, stamp, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
            Require(rejected && Hash(original) == before, "Canceled operation preserves original bytes");
            var copy = await MediaTools.Trim(original, exported, 1.25, 4.75, stamp, CancellationToken.None);
            Require(Math.Abs(copy.Duration - 3.5) < .1 && copy.AudioTracks == 2 && Hash(original) == before, "Export has precise duration and both audio tracks; source unchanged");
            string exportHash = Hash(exported);
            rejected = false;
            try { await MediaTools.Trim(original, exported, 1, 3, stamp, CancellationToken.None); } catch (IOException) { rejected = true; }
            Require(rejected && Hash(exported) == exportHash && Hash(original) == before, "Existing export is never overwritten");
            var replaced = await MediaTools.Trim(original, original, 2, 5, stamp, CancellationToken.None);
            Require(Math.Abs(replaced.Duration - 3) < .1 && replaced.AudioTracks == 2 && Hash(original) != before, "Atomic overwrite produces selected duration and preserves audio tracks");
            var decode = await MediaTools.Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-xerror", "-i", original, "-map", "0", "-f", "null", "-"], CancellationToken.None);
            Require(decode.Exit == 0, "Entire overwritten clip decodes without errors");
            string separate = Path.Combine(folder, "separate.mp4");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=6" };
            foreach (int frequency in new[] { 440, 440, 880, 1320 }) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=6"]);
            args.AddRange(["-map", "0:v"]);
            for (int i = 0; i < 4; i++) args.AddRange(["-map", $"{i + 1}:a", $"-metadata:s:a:{i}", "handler_name=" + new[] { "ClipIt Mix", "Game", "Discord", "Microphone" }[i]]);
            args.AddRange(["-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-c:a", "aac", "-y", separate]);
            Require((await MediaTools.Run("ffmpeg", args, CancellationToken.None)).Exit == 0 && (await MediaTools.Probe(separate, CancellationToken.None)).HasStems, "Four named sources are recognized as a ClipIt recording");
            var mix = new MixSettings(Game: .5, DiscordMuted: true, Microphone: .75);
            var mixed = await MediaTools.Trim(separate, separate, 1, 5, ClipStamp.Read(separate), CancellationToken.None, mix);
            var reread = await MediaTools.Probe(separate, CancellationToken.None);
            Require(mixed.HasStems && reread.HasStems && reread.Mix == mix && Math.Abs(reread.Duration - 4) < .1, "Trim retains all named stems and persists mixer settings");
            using (var preview = await MediaTools.ExtractStems(separate, CancellationToken.None))
                Require(preview.Files.All(f => new FileInfo(f).Length > 1000), "All three separate preview sources decode");
            // Measure distinct tones in decoded PCM, including the muted source's retained raw track.
            async Task<double[]> Tones(int track)
            {
                string wav = Path.Combine(folder, $"measure-{track}.wav");
                var result = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", separate, "-map", $"0:a:{track}", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", "-y", wav], CancellationToken.None);
                if (result.Exit != 0) throw new Exception("PCM measurement failed");
                using var reader = new BinaryReader(File.OpenRead(wav)); reader.BaseStream.Position = 12;
                byte[] data = [];
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length) { string id = new(reader.ReadChars(4)); int count = reader.ReadInt32(); if (id == "data") { data = reader.ReadBytes(count); break; } reader.BaseStream.Position += count + count % 2; }
                return new[] { 440, 880, 1320 }.Select(f => { double real = 0, imag = 0; int n = Math.Min(48000, data.Length / 2 - 4800); for (int i = 0; i < n; i++) { double sample = BitConverter.ToInt16(data, (i + 4800) * 2) / 32768.0; double phase = 2 * Math.PI * f * i / 48000; real += sample * Math.Cos(phase); imag += sample * Math.Sin(phase); } return 2 * Math.Sqrt(real * real + imag * imag) / n; }).ToArray();
            }
            var levels = await Tones(0); var raw = await Tones(2);
            Require(levels[0] > .03 && levels[2] > .05 && levels[1] < levels[0] * .01 && raw[1] > .08, "Discord mute removes its tone from playback mix while retaining its raw source");
            var restored = await MediaTools.Trim(separate, separate, 0, 3, ClipStamp.Read(separate), CancellationToken.None, new MixSettings());
            Require(restored.HasStems && (await Tones(0))[1] > .08, "Muted Discord can be restored by a subsequent edit");
            Require(AppTargets.FolderName("CON") == "_CON" && AppTargets.FolderName("a:b/c") == "a-b-c", "Application folder names are safe Windows names");
            Require(!Directory.EnumerateFiles(folder, ".clipit-render-*").Any(), "No temporary renders remain");
            File.WriteAllText(Path.Combine(root, "media-check-results.json"), JsonSerializer.Serialize(new { Success = true, Folder = folder, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "media-check-results.json"), JsonSerializer.Serialize(new { Success = false, Error = ex.ToString(), Folder = folder, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
