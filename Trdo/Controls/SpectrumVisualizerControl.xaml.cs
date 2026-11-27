using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NAudio.Dsp;
using NAudio.Wave;
using System;
using System.Diagnostics;
using Trdo.Services;

namespace Trdo.Controls;

/// <summary>
/// Renders an animated frequency-spectrum bar graph using WASAPI loopback capture and FFT.
/// Each bar uses spring physics to produce a bouncy rise/fall animation.
/// </summary>
public sealed partial class SpectrumVisualizerControl : UserControl
{
    private const int BandCount = 32;
    private const int FFTSize = 2048;
    private const double BarGap = 4.0;
    private const double CanvasHeight = 60.0;

    // Spring physics constants — underdamped (ζ ≈ 0.28) for a bouncy feel.
    private const double SpringK = 500;
    private const double DampingD = 11.0;

    private readonly double[] _targetLevels = new double[BandCount];
    private readonly double[] _currentLevels = new double[BandCount];
    private readonly double[] _velocities = new double[BandCount];
    private float[] _latestBands = new float[BandCount]; // written on capture thread, read on UI thread

    private Rectangle[] _bars = [];
    // WasapiLoopbackCapture is obsolete in favor of WasapiRecorderBuilder, but that API
    // reshapes capture setup/teardown significantly; deferred until it can be verified on device.
#pragma warning disable CS0618
    private WasapiLoopbackCapture? _capture;
#pragma warning restore CS0618
    private int _sampleRate = 44100;
    private int _channels = 2;

    // FFT accumulation buffer — only written on the single NAudio capture thread.
    private readonly Complex[] _fftAccumBuffer = new Complex[FFTSize];
    private int _fftWritePos;

    private DispatcherQueueTimer? _animTimer;
    private DateTime _lastFrameTime = DateTime.UtcNow;

    public SpectrumVisualizerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SpectrumCanvas.SizeChanged += OnCanvasSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateBars();
        StartCapture();
        StartAnimationTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

    /// <summary>
    /// Stops the frame timer and the loopback capture. Runs on Unloaded, and is also called by
    /// the hosting window's Closed handler, since Unloaded is not guaranteed to arrive before the
    /// app's final dispatcher drain. Safe to call more than once.
    /// </summary>
    public void Stop()
    {
        StopAnimationTimer();
        StopCapture();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => LayoutBars();

    private void CreateBars()
    {
        SpectrumCanvas.Children.Clear();
        _bars = new Rectangle[BandCount];

        SolidColorBrush accentBrush = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        for (int i = 0; i < BandCount; i++)
        {
            Rectangle rect = new()
            {
                RadiusX = 2,
                RadiusY = 2,
                Fill = accentBrush,
                Height = 2,
            };
            _bars[i] = rect;
            SpectrumCanvas.Children.Add(rect);
        }

        LayoutBars();
    }

    private void LayoutBars()
    {
        if (_bars.Length == 0 || SpectrumCanvas.ActualWidth == 0) return;

        double totalGap = BarGap * (BandCount - 1);
        double barWidth = Math.Max(2.0, (SpectrumCanvas.ActualWidth - totalGap) / BandCount);

        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Width = barWidth;
            Canvas.SetLeft(_bars[i], i * (barWidth + BarGap));
            Canvas.SetTop(_bars[i], CanvasHeight - _bars[i].Height);
        }
    }

    private void StartAnimationTimer()
    {
        StopAnimationTimer();
        _animTimer = DispatcherQueue.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(16);
        _animTimer.IsRepeating = true;
        _animTimer.Tick += OnAnimationTick;
        _animTimer.Start();
        _lastFrameTime = DateTime.UtcNow;
    }

    private void StopAnimationTimer()
    {
        if (_animTimer is null) return;
        _animTimer.Stop();
        _animTimer.Tick -= OnAnimationTick;
        _animTimer = null;
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        if (AppLifetime.StopIfShuttingDown(sender))
            return;

        try
        {
            AdvanceFrame();
        }
        catch (Exception ex)
        {
            AppLifetime.ReportTickFailure(sender, nameof(SpectrumVisualizerControl), ex);
        }
    }

    private void AdvanceFrame()
    {
        DateTime now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastFrameTime).TotalSeconds, 0.05);
        _lastFrameTime = now;

        float[] latestBands = _latestBands;

        double totalGap = BarGap * (BandCount - 1);
        double barWidth = SpectrumCanvas.ActualWidth > 0
            ? Math.Max(2.0, (SpectrumCanvas.ActualWidth - totalGap) / BandCount)
            : 8.0;

        for (int i = 0; i < BandCount; i++)
        {
            _targetLevels[i] = latestBands[i] * CanvasHeight;

            // Spring: F = SpringK*(target - pos) - DampingD*vel
            double acceleration = (SpringK * (_targetLevels[i] - _currentLevels[i])) - (DampingD * _velocities[i]);
            _velocities[i] += acceleration * dt;
            _currentLevels[i] = Math.Clamp(_currentLevels[i] + (_velocities[i] * dt), 0, CanvasHeight);

            double barHeight = Math.Max(2.0, _currentLevels[i]);
            _bars[i].Height = barHeight;
            _bars[i].Width = barWidth;
            Canvas.SetLeft(_bars[i], i * (barWidth + BarGap));
            Canvas.SetTop(_bars[i], CanvasHeight - barHeight);
        }
    }

    private void StartCapture()
    {
        try
        {
#pragma warning disable CS0618
            _capture = new WasapiLoopbackCapture();
#pragma warning restore CS0618
            _sampleRate = _capture.WaveFormat.SampleRate;
            _channels = _capture.WaveFormat.Channels;
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpectrumVisualizer] Failed to start capture: {ex.Message}");
        }
    }

    private void StopCapture()
    {
        if (_capture == null) return;
        _capture.DataAvailable -= OnDataAvailable;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        const int bytesPerSample = 4; // float32
        int bytesPerFrame = bytesPerSample * _channels;

        for (int i = 0; i + bytesPerFrame <= e.BytesRecorded; i += bytesPerFrame)
        {
            // Mix all channels to mono.
            float mono = 0;
            for (int ch = 0; ch < _channels; ch++)
                mono += BitConverter.ToSingle(e.Buffer, i + (ch * bytesPerSample));
            mono /= _channels;

            // Hanning window to reduce spectral leakage.
            float window = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * _fftWritePos / (FFTSize - 1)));
            _fftAccumBuffer[_fftWritePos].X = mono * window;
            _fftAccumBuffer[_fftWritePos].Y = 0;
            _fftWritePos++;

            if (_fftWritePos >= FFTSize)
            {
                _fftWritePos = 0;
                ComputeSpectrum();
            }
        }
    }

    private void ComputeSpectrum()
    {
        Complex[] fftData = (Complex[])_fftAccumBuffer.Clone();
        int m = (int)Math.Log2(FFTSize); // 10 for 1024-point FFT
        FastFourierTransform.FFT(true, m, fftData);

        float freqPerBin = (float)_sampleRate / FFTSize;
        int usableBins = FFTSize / 2;
        const float minFreq = 60f;
        const float maxFreq = 16000f;

        float[] bands = new float[BandCount];

        for (int band = 0; band < BandCount; band++)
        {
            float freqLow = minFreq * MathF.Pow(maxFreq / minFreq, (float)band / BandCount);
            float freqHigh = minFreq * MathF.Pow(maxFreq / minFreq, (float)(band + 1) / BandCount);

            int binLow = Math.Max(1, (int)(freqLow / freqPerBin));
            int binHigh = Math.Min(usableBins - 1, (int)(freqHigh / freqPerBin));
            if (binLow > binHigh) binHigh = binLow;

            float sum = 0;
            for (int bin = binLow; bin <= binHigh; bin++)
            {
                float re = fftData[bin].X;
                float im = fftData[bin].Y;
                sum += MathF.Sqrt((re * re) + (im * im));
            }

            float avg = sum / (binHigh - binLow + 1);

            // Map to [0, 1] using dB scale: range [-80, 0] dB → [0, 1].
            float dB = 20f * MathF.Log10(avg + 1e-9f);
            bands[band] = Math.Clamp((dB + 80f) / 80f, 0f, 1f);
        }

        // Atomic reference swap — safe for single producer / single consumer.
        _latestBands = bands;
    }
}
