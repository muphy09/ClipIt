using System.Media;
using System.Text;

namespace ClipIt;

internal sealed class CaptureChime : IDisposable
{
    private readonly MemoryStream audio;
    private readonly SoundPlayer player;
    internal CaptureChime()
    {
        // A short, soft two-note confirmation, independent of Windows notification settings.
        const int rate = 24000, samples = rate / 4;
        audio = new MemoryStream();
        using (var writer = new BinaryWriter(audio, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / rate, noteTime = t < .1 ? t : t - .1;
                double envelope = Math.Min(1, noteTime / .008) * Math.Max(0, 1 - noteTime / (t < .1 ? .1 : .15));
                writer.Write((short)(Math.Sin(2 * Math.PI * (t < .1 ? 880 : 1174.66) * noteTime) * envelope * 5500));
            }
        }
        audio.Position = 0; player = new SoundPlayer(audio); player.Load();
    }
    internal void Play() { try { player.Play(); } catch (Exception ex) { Log.Write("Capture chime: " + ex.Message); } }
    public void Dispose() { player.Stop(); player.Dispose(); audio.Dispose(); }
}
