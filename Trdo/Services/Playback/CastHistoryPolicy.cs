using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Trdo.Services.Playback;

public static class CastHistoryPolicy
{
    public static CastHistoryDocument CreateDocument() => new() { Salt = RandomNumberGenerator.GetHexString(32) };

    public static string NetworkKey(string ssid, string authentication, string encryption, string salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(ssid);
        ArgumentException.ThrowIfNullOrEmpty(salt);
        string identity = $"{salt.Length}:{salt}{ssid.Length}:{ssid}{authentication.Length}:{authentication}{encryption.Length}:{encryption}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static NetworkCastHistory? FindNetwork(CastHistoryDocument document, string? key) =>
        key is null ? null : document.Networks.Find(network => network.NetworkKey == key);

    public static bool ShouldShowShortcut(NetworkCastHistory? network) =>
        network is { ShortcutHidden: false, Devices.Count: > 0 };

    public static bool CanRemember(CastAttempt attempt, string? currentKey, long currentGeneration, bool isContextCurrent = true) =>
        isContextCurrent && currentKey is not null &&
        attempt.NetworkKey == currentKey &&
        attempt.NetworkGeneration == currentGeneration;

    public static bool Matches(CastHistoryDevice left, CastHistoryDevice right) =>
        left.Protocol == right.Protocol &&
        (left.Protocol == CastProtocol.Sonos
            ? !string.IsNullOrWhiteSpace(left.Uuid) &&
              string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase)
            : CastDevicePolicy.IsSameDevice(left.Name, left.RendererType, right.Name, right.RendererType));

    public static void RecordSuccess(CastHistoryDocument document, CastAttempt attempt, DateTimeOffset now)
    {
        NetworkCastHistory? network = FindNetwork(document, attempt.NetworkKey);
        if (network is null)
        {
            network = new NetworkCastHistory { NetworkKey = attempt.NetworkKey };
            document.Networks.Add(network);
        }

        CastHistoryDevice device = attempt.Device with { LastUsedUtc = now };
        int index = network.Devices.FindIndex(existing => Matches(existing, device));
        if (index < 0)
        {
            network.Devices.Add(device);
        }
        else
        {
            network.Devices[index] = device;
        }
    }

    public static List<CastHistoryRow> Merge(IReadOnlyList<CastHistoryDevice> remembered, IReadOnlyList<CastHistoryDevice> live)
    {
        List<CastHistoryRow> rows = [];
        foreach (CastHistoryDevice device in remembered)
        {
            rows.Add(new CastHistoryRow(device, IsRemembered: true, IsAvailable: false));
        }

        foreach (CastHistoryDevice device in live)
        {
            int index = rows.FindIndex(row => Matches(row.Device, device));
            if (index < 0)
            {
                rows.Add(new CastHistoryRow(device, IsRemembered: false, IsAvailable: true));
            }
            else
            {
                CastHistoryRow previous = rows[index];
                rows[index] = previous with
                {
                    Device = device with { LastUsedUtc = previous.Device.LastUsedUtc },
                    IsAvailable = true,
                };
            }
        }

        rows.Sort((left, right) =>
        {
            int rememberedOrder = right.IsRemembered.CompareTo(left.IsRemembered);
            if (rememberedOrder != 0)
                return rememberedOrder;

            int recentOrder = right.Device.LastUsedUtc.CompareTo(left.Device.LastUsedUtc);
            return recentOrder != 0 ? recentOrder : CastDevicePolicy.CompareForDisplay(left.Device.Name, right.Device.Name);
        });
        return rows;
    }

    public static void Validate(CastHistoryDocument document)
    {
        if (document.Version != 1)
            throw new NotSupportedException($"Unsupported casting history version: {document.Version}.");

        if (string.IsNullOrEmpty(document.Salt) || document.Networks is null)
            throw new FormatException("Casting history has no salt or network list.");

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (NetworkCastHistory network in document.Networks)
        {
            if (network is null || string.IsNullOrWhiteSpace(network.NetworkKey) ||
                !keys.Add(network.NetworkKey) || network.Devices is null)
                throw new FormatException("Casting history contains an invalid or duplicate network.");

            List<CastHistoryDevice> devices = [];
            foreach (CastHistoryDevice device in network.Devices)
            {
                if (device is null || string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Kind) ||
                    !Enum.IsDefined(device.Protocol) || devices.Exists(existing => Matches(existing, device)))
                    throw new FormatException("Casting history contains an invalid or duplicate device.");

                if (device.Protocol == CastProtocol.Sonos &&
                    (string.IsNullOrWhiteSpace(device.Uuid) || !IPAddress.TryParse(device.Address, out IPAddress? address) ||
                     IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || device.Port is < 1 or > 65535))
                    throw new FormatException("Casting history contains an invalid Sonos endpoint.");

                devices.Add(device);
            }
        }
    }
}
