using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.ViewModels;

namespace Trdo.Services.Playback;

/// <summary>
/// After a crash or an unclean exit a Sonos speaker keeps playing the stream this app gave it.
/// At startup this looks for such a speaker among the ones used on this network before, and
/// if one is playing a station from the list, selects that station and takes control of the
/// speaker again instead of leaving it running unmanaged.
/// </summary>
internal static class SonosRecoveryService
{
    private const string Component = "SonosRecovery";
    private static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Runs on the UI thread; every wait is bounded and failures are logged, never thrown.</summary>
    public static async Task TryRecoverAsync(DispatcherQueue queue)
    {
        try
        {
            IReadOnlyList<CastHistoryDevice> remembered = CastingContextService.Instance.RememberedDevices;
            LogService.Info(Component, $"Starting: {remembered.Count} remembered device(s) on this network " +
                $"({remembered.Count(device => device.Protocol == CastProtocol.Sonos)} Sonos), " +
                $"{PlayerViewModel.Shared.Stations.Count} station(s) loaded, " +
                $"networkKey={(CastingContextService.Instance.NetworkKey is null ? "none" : "set")}");

            // Only people who have cast on this network get discovery started on their behalf.
            if (!remembered.Any(device => device.Protocol == CastProtocol.Sonos))
            {
                LogService.Info(Component, "Skipped: no remembered Sonos speaker for this network");
                return;
            }

            RadioPlayerService player = RadioPlayerService.Instance;
            if (player.IsCasting || player.IsPlaying || player.IsBuffering)
            {
                LogService.Info(Component, $"Skipped: player already active (casting={player.IsCasting}, playing={player.IsPlaying}, buffering={player.IsBuffering})");
                return;
            }

            using CancellationTokenSource timeout = new(SearchWindow);
            CastPickerSession session = CastPickerSession.Acquire(queue);
            try
            {
                using SonosController controller = new();
                HashSet<string> checkedUuids = new(StringComparer.OrdinalIgnoreCase);
                while (!timeout.IsCancellationRequested)
                {
                    foreach (SonosDevice device in session.Entries
                                 .Select(entry => entry.LiveDevice?.Sonos)
                                 .OfType<SonosDevice>()
                                 .ToList())
                    {
                        if (!checkedUuids.Add(device.Uuid))
                        {
                            continue;
                        }

                        LogService.Info(Component, $"Checking live speaker '{device.Name}' ({device.Address}:{device.Port}, {device.Uuid})");
                        if (await TryAdoptAsync(device, controller, timeout.Token))
                        {
                            return;
                        }
                    }

                    await Task.Delay(PollInterval, timeout.Token);
                }
            }
            finally
            {
                LogService.Info(Component, $"Finished search; {session.Entries.Count} entr(ies), " +
                    $"{session.Entries.Count(entry => entry.LiveDevice?.Sonos is not null)} live Sonos");
                session.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            LogService.Info(Component, $"Gave up after {SearchWindow.TotalSeconds:0}s: no speaker was found playing one of this app's stations");
        }
        catch (Exception ex)
        {
            LogService.Warn(Component, $"Recovering a running Sonos cast failed: {ex.Message}");
        }
    }

    private static async Task<bool> TryAdoptAsync(SonosDevice device, SonosController controller, CancellationToken cancellationToken)
    {
        string? transportUri;
        try
        {
            using CancellationTokenSource query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            query.CancelAfter(QueryTimeout);

            SonosTransportState state = await controller.GetTransportStateAsync(device, query.Token);
            LogService.Info(Component, $"'{device.Name}' transport state: {state}");
            if (state != SonosTransportState.Playing)
            {
                return false;
            }

            transportUri = (await controller.GetPositionInfoAsync(device, query.Token)).TrackUri;
            LogService.Info(Component, $"'{device.Name}' track URI: {LogService.Redact(transportUri ?? "(none)")}");
        }
        catch (Exception ex) when ((ex is HttpRequestException or SonosActionException or System.Xml.XmlException
                                        or OperationCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            LogService.Warn(Component, $"Couldn't read what Sonos '{device.Name}' is playing: {ex.Message}");
            return false;
        }

        if (SonosPolicy.StreamUrlFromTransportUri(transportUri) is not { } streamUrl)
        {
            LogService.Info(Component, $"'{device.Name}' is not playing an internet radio URI; skipping");
            return false;
        }

        PlayerViewModel viewModel = PlayerViewModel.Shared;
        RadioStation? station = viewModel.Stations.FirstOrDefault(candidate =>
            candidate.SourceKind == AudioSourceKind.Radio && SonosPolicy.IsSameStream(candidate.StreamUrl, streamUrl));
        if (station is null)
        {
            LogService.Info(Component, $"Sonos '{device.Name}' is playing {LogService.Redact(streamUrl)}, which matches no station in the list");
            return false;
        }

        // The user may have started something while the speaker was being asked.
        RadioPlayerService player = RadioPlayerService.Instance;
        LogService.Info(Component, $"Matched station '{station.Name}' on '{device.Name}'");
        if (player.IsCasting || player.IsPlaying || player.IsBuffering)
        {
            LogService.Info(Component, "Not adopting: playback started while the speaker was being checked");
            return true;
        }

        // Selecting without playing: with the player idle this only points it at the station.
        if (!ReferenceEquals(viewModel.SelectedStation, station))
        {
            viewModel.SelectStationToBrowse(station);
        }

        bool adopted = player.AdoptRunningSonos(device, transportUri!);
        LogService.Info(Component, adopted
            ? $"Adopted '{device.Name}' playing '{station.Name}'"
            : $"AdoptRunningSonos refused (sourceKind={player.ActiveSourceKind}, casting={player.IsCasting}, playing={player.IsPlaying})");
        return true;
    }
}
