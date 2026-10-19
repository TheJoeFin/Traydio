using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Trdo.Services.Audio;

/// <summary>
/// Gives the native, LibVLC, and generated-audio sessions one Windows mixer control.
/// </summary>
internal sealed class AudioSessionGroupingService : IDisposable, IAudioSessionEventsHandler
{
    private static readonly Guid GroupingId = new("2e88002a-27aa-4f56-9e38-10418c3964f8");
    private const string DisplayName = "Traydio";
    private const string LogComponent = "AudioSessionGrouping";

    private readonly Lock _requestGate = new();
    private readonly AutoResetEvent _refreshRequested = new(true);
    private readonly Dictionary<string, MMDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioSessionControl> _sessions = new(StringComparer.Ordinal);
    private readonly Thread _worker;
    private volatile bool _isStopping;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _deviceNotifications;

    public AudioSessionGroupingService()
    {
        // Core Audio session notifications require MTA. All COM work stays off both the
        // XAML dispatcher and the audio callbacks; the worker sleeps until a notification.
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Traydio audio session grouping"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    private void Run()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _deviceNotifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _deviceNotifications.DeviceAdded += OnDevicesChanged;
            _deviceNotifications.DeviceRemoved += OnDevicesChanged;
            _deviceNotifications.DeviceStateChanged += OnDevicesChanged;
            _deviceNotifications.DefaultDeviceChanged += OnDevicesChanged;

            while (_refreshRequested.WaitOne())
            {
                if (_isStopping)
                    break;

                try
                {
                    RefreshSessions();
                }
                catch (Exception ex)
                {
                    LogService.Warn(LogComponent, $"Could not refresh audio sessions: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error(LogComponent, "Audio session grouping stopped", ex);
        }
        finally
        {
            lock (_requestGate)
            {
                _isStopping = true;
            }

            foreach (AudioSessionControl session in _sessions.Values)
                ReleaseSession(session);

            foreach (MMDevice device in _devices.Values)
                ReleaseDevice(device);

            _sessions.Clear();
            _devices.Clear();
            try
            {
                _deviceNotifications?.Dispose();
                _enumerator?.Dispose();
            }
            catch (Exception ex)
            {
                LogService.Warn(LogComponent, $"Could not release audio device notifications: {ex.Message}");
            }
            finally
            {
                _refreshRequested.Dispose();
            }
        }
    }

    private void RefreshSessions()
    {
        HashSet<string> deviceIds = new(StringComparer.Ordinal);
        HashSet<string> sessionIds = new(StringComparer.Ordinal);
        bool refreshSucceeded = true;

        foreach (MMDevice candidate in _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (candidate)
            {
                try
                {
                    string deviceId = candidate.ID;
                    deviceIds.Add(deviceId);

                    if (!_devices.TryGetValue(deviceId, out MMDevice? device))
                    {
                        device = _enumerator.GetDevice(deviceId);
                        try
                        {
                            device.AudioSessionManager.OnSessionCreated += OnSessionCreated;
                            _devices.Add(deviceId, device);
                        }
                        catch
                        {
                            ReleaseDevice(device);
                            throw;
                        }
                    }

                    AudioSessionManager manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    SessionCollection sessions = manager.Sessions;
                    int count = sessions.Count;

                    for (int i = 0; i < count; i++)
                    {
                        AudioSessionControl session = sessions[i];
                        bool retained = false;
                        try
                        {
                            if (session.GetProcessID != Environment.ProcessId ||
                                session.State == AudioSessionState.AudioSessionStateExpired)
                                continue;

                            string sessionId = session.GetSessionInstanceIdentifier;
                            sessionIds.Add(sessionId);

                            if (!_sessions.TryGetValue(sessionId, out AudioSessionControl? tracked))
                            {
                                session.RegisterEventClient(this);
                                _sessions.Add(sessionId, session);
                                tracked = session;
                                retained = true;
                            }

                            ApplyIdentity(tracked);
                        }
                        catch (Exception ex)
                        {
                            refreshSucceeded = false;
                            LogService.Warn(LogComponent, $"Could not group an audio session: {ex.Message}");
                        }
                        finally
                        {
                            if (!retained)
                                ReleaseSession(session);
                        }
                    }
                }
                catch (Exception ex)
                {
                    refreshSucceeded = false;
                    LogService.Warn(LogComponent, $"Could not group sessions on an audio output: {ex.Message}");
                }
            }
        }

        // A transient endpoint error must not discard subscriptions to sessions we could
        // not enumerate. A later device/session notification will retry the refresh.
        if (refreshSucceeded)
        {
            foreach (string id in _sessions.Keys.Where(id => !sessionIds.Contains(id)).ToArray())
            {
                ReleaseSession(_sessions[id]);
                _sessions.Remove(id);
            }
        }

        foreach (string id in _devices.Keys.Where(id => !deviceIds.Contains(id)).ToArray())
        {
            ReleaseDevice(_devices[id]);
            _devices.Remove(id);
        }
    }

    private static void ApplyIdentity(AudioSessionControl session)
    {
        if (session.GetGroupingParam() != GroupingId)
            session.SetGroupingParam(GroupingId, GroupingId);

        if (session.DisplayName != DisplayName)
            session.DisplayName = DisplayName;
    }

    private void RequestRefresh()
    {
        lock (_requestGate)
        {
            if (!_isStopping)
                _refreshRequested.Set();
        }
    }

    private void OnDevicesChanged(object? sender, DeviceNotificationEventArgs e) => RequestRefresh();

    private void OnSessionCreated(object sender, AudioSessionControl newSession)
    {
        // The callback lends us this wrapper. Enumerate an owned control on the worker
        // instead of keeping the borrowed interface after the callback returns.
        newSession.Dispose();
        RequestRefresh();
    }

    public void OnGroupingParamChanged(ref Guid groupingId)
    {
        // Playback engines can assign their own identity after creating the session.
        if (groupingId != GroupingId)
            RequestRefresh();
    }

    public void OnDisplayNameChanged(string displayName)
    {
        if (displayName != DisplayName)
            RequestRefresh();
    }

    public void OnStateChanged(AudioSessionState state)
    {
        if (state == AudioSessionState.AudioSessionStateExpired)
            RequestRefresh();
    }

    public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => RequestRefresh();
    public void OnVolumeChanged(float volume, bool isMuted) { }
    public void OnIconPathChanged(string iconPath) { }
    public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }

    private static void ReleaseSession(AudioSessionControl session)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Warn(LogComponent, $"Could not release an audio session: {ex.Message}");
        }
    }

    private void ReleaseDevice(MMDevice device)
    {
        try
        {
            try
            {
                device.AudioSessionManager.OnSessionCreated -= OnSessionCreated;
            }
            finally
            {
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(LogComponent, $"Could not release an audio output: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_requestGate)
        {
            if (_isStopping)
                return;

            _isStopping = true;
            _refreshRequested.Set();
        }

        if (!_worker.Join(TimeSpan.FromSeconds(2)))
            LogService.Warn(LogComponent, "Audio session grouping did not stop within the shutdown timeout");
    }
}
