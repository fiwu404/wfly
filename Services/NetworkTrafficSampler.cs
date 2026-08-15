using System.Net.NetworkInformation;

namespace WFly.Services;

/// <summary>
/// Samples host interface counters in memory. It deliberately exposes these as
/// aggregate traffic only; the dashboard combines them with a core's
/// loopback Clash API counters before labelling proxy and direct curves.
/// </summary>
internal sealed class NetworkTrafficSampler
{
    private (long Sent, long Received, DateTimeOffset CapturedAt)? _previous;

    public NetworkTrafficRate Sample()
    {
        var now = DateTimeOffset.UtcNow;
        long sent = 0;
        long received = 0;
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var statistics = networkInterface.GetIPv4Statistics();
                sent += statistics.BytesSent;
                received += statistics.BytesReceived;
            }
            catch (NetworkInformationException)
            {
                // A virtual interface may disappear during sampling.
            }
        }

        var current = (sent, received, now);
        if (_previous is not { } previous)
        {
            _previous = current;
            return new NetworkTrafficRate(now, 0, 0);
        }

        _previous = current;
        var seconds = Math.Max(0.25, (now - previous.CapturedAt).TotalSeconds);
        return new NetworkTrafficRate(
            now,
            Math.Max(0, (long)((sent - previous.Sent) / seconds)),
            Math.Max(0, (long)((received - previous.Received) / seconds)));
    }
}

internal sealed record NetworkTrafficRate(DateTimeOffset CapturedAt, long UploadBytesPerSecond, long DownloadBytesPerSecond);
