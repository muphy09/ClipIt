using System.Diagnostics;
using System.Text.Json;
using C = System.Windows.Controls;
using Wpf = System.Windows;

namespace ClipIt;

public partial class ClipWindow
{
    private MicTest? micTest;
    private static readonly System.Windows.Media.Brush ClipRed = Brush("#FF8E8E");

    private sealed class MicTest(bool forRecording, Process worker)
    {
        public bool ForRecording { get; } = forRecording;
        public Process Worker { get; } = worker;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public long LastSample { get; set; } = -1;
        public double PeakDb { get; set; } = -120;
        public long ClipUntil { get; set; }
    }

    private (C.Button Button, C.StackPanel Panel, C.ProgressBar Level, C.TextBlock Status, C.Slider Boost) MicControls(bool forRecording) =>
        forRecording ? (RecordingTestMicButton, RecordingMicTestPanel, RecordingMicTestLevel, RecordingMicTestStatus, RecordingMicBoost)
            : (TestMicButton, MicTestPanel, MicTestLevel, MicTestStatus, MicrophoneBoost);

    private void TestMic_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (micTest != null) StopMicTest();
        else StartMicTest(ReferenceEquals(sender, RecordingTestMicButton));
    }

    private async void StartMicTest(bool forRecording)
    {
        StopMicTest();
        var controls = MicControls(forRecording);
        var selected = (forRecording ? RecordingMicDevice : MicrophoneBox).SelectedItem as AudioDevice;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--mic-test");
        start.ArgumentList.Add(selected?.Id ?? "default");
        controls.Panel.Visibility = Wpf.Visibility.Visible;
        controls.Level.Value = controls.Level.Minimum;
        controls.Status.Text = "Connecting to microphone…";
        MicTest? test = null;
        try
        {
            test = new(forRecording, Process.Start(start) ?? throw new IOException("Could not start the microphone test."));
            micTest = test;
            controls.Button.Content = "Stop Test";
            controls.Panel.BringIntoView();
            while (await test.Worker.StandardOutput.ReadLineAsync() is { } line)
            {
                if (micTest != test) break;
                var message = JsonSerializer.Deserialize<WorkerMessage>(line);
                switch (message?.Type)
                {
                    case "microphones":
                        UpdateMicrophoneDevices(JsonSerializer.Deserialize<List<AudioDevice>>(message.Text) ?? []);
                        break;
                    case "mic-test-level":
                        if (message.MicLevel != int.MinValue)
                        {
                            test.LastSample = test.Clock.ElapsedMilliseconds;
                            test.PeakDb = message.MicLevel / 10.0;
                        }
                        UpdateMicTestMeter();
                        break;
                    case "error":
                        throw new IOException(message.Text);
                }
            }
            if (micTest == test) throw new IOException("The microphone test stopped unexpectedly.");
        }
        catch (Exception ex)
        {
            if (test == null || micTest == test)
            {
                StopMicTest();
                controls.Level.Value = controls.Level.Minimum;
                controls.Status.Text = "Could not test the microphone. " + ex.Message;
                controls.Panel.Visibility = Wpf.Visibility.Visible;
            }
        }
    }

    private void UpdateMicTestMeter()
    {
        if (micTest is not { } test) return;
        var controls = MicControls(test.ForRecording);
        long now = test.Clock.ElapsedMilliseconds;
        bool hasSignal = test.LastSample >= 0 && now - test.LastSample < 400;
        double peak = hasSignal && test.PeakDb > -120 ? test.PeakDb + controls.Boost.Value : -120;
        if (peak >= 0) test.ClipUntil = now + 1000;
        controls.Level.Value = Math.Max(peak, controls.Level.Minimum);
        controls.Level.Foreground = now < test.ClipUntil ? ClipRed : peak >= -6 ? Amber : Green;
        controls.Status.Text = now < test.ClipUntil ? "Clipping · lower Boost Mic"
            : hasSignal ? $"Peak {Math.Max(peak, -60):0} dBFS · keep loud peaks out of the red"
            : now < 2000 ? "Connecting to microphone…"
            : "No audio received. Check the selected microphone and Windows microphone access.";
    }

    private void StopMicTest()
    {
        MicTestPanel.Visibility = RecordingMicTestPanel.Visibility = Wpf.Visibility.Collapsed;
        if (micTest is not { } test) return;
        micTest = null;
        MicControls(test.ForRecording).Button.Content = "Test Mic";
        _ = Task.Run(() =>
        {
            using var worker = test.Worker;
            try { worker.StandardInput.Close(); }
            catch (IOException) { }
            try { if (!worker.WaitForExit(2000)) worker.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex) { Log.Write("Could not stop microphone test: " + ex.Message); }
        });
    }
}
