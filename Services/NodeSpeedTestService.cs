using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using WFly.Models;

namespace WFly.Services;

internal enum NodeTestKind
{
    Ping,
    Tcping,
    RealConnection,
    Udp,
}

internal sealed record NodeTestUpdate(string NodeId, NodeTestKind Kind, string Display, bool Completed);

/// <summary>
/// Node diagnostics follow v2rayN's split: ICMP/TCP connect directly to the
/// server endpoint, while real HTTP and UDP probes run through isolated local
/// SOCKS inbounds backed by a temporary sing-box profile.
/// </summary>
internal sealed class NodeSpeedTestService
{
    private static readonly Uri RealTestUri = new("https://www.google.com/generate_204");
    private readonly AppPaths _paths;
    private readonly InstalledCoreStore _installedCoreStore;

    public NodeSpeedTestService(AppPaths paths, InstalledCoreStore installedCoreStore)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _installedCoreStore = installedCoreStore ?? throw new ArgumentNullException(nameof(installedCoreStore));
    }

    public async Task TestAsync(
        NodeTestKind kind,
        IReadOnlyCollection<ProxyNode> nodes,
        IProgress<NodeTestUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var targets = new List<TestTarget>(nodes.Count);
        foreach (var node in nodes)
        {
            progress?.Report(new NodeTestUpdate(node.Id, kind, "测试中…", false));
            try
            {
                targets.Add(ParseTarget(node));
            }
            catch (InvalidDataException)
            {
                progress?.Report(new NodeTestUpdate(node.Id, kind, "配置无效", true));
            }
        }

        if (targets.Count == 0) return;
        if (kind is NodeTestKind.Ping or NodeTestKind.Tcping)
        {
            await Parallel.ForEachAsync(
                targets,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 16 },
                async (target, token) =>
                {
                    var display = kind == NodeTestKind.Ping
                        ? await MeasurePingAsync(target.Server, token)
                        : await MeasureTcpingAsync(target.Server, target.Port, token);
                    progress?.Report(new NodeTestUpdate(target.Node.Id, kind, display, true));
                });
            return;
        }

        await TestThroughCoreAsync(kind, targets, progress, cancellationToken);
    }

    private async Task TestThroughCoreAsync(
        NodeTestKind kind,
        IReadOnlyList<TestTarget> targets,
        IProgress<NodeTestUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var installed = await _installedCoreStore.GetLatestAsync("sing-box", cancellationToken);
        if (installed is null || !File.Exists(installed.ExecutablePath))
        {
            foreach (var target in targets)
                progress?.Report(new NodeTestUpdate(target.Node.Id, kind, "需安装 sing-box", true));
            return;
        }

        _paths.EnsureDirectories();
        var testId = Guid.NewGuid().ToString("N");
        var configPath = Path.Combine(_paths.TempDirectory, $"node-test-{testId}.json");
        Process? process = null;
        try
        {
            var boundTargets = targets.Select((target, index) => target with
            {
                LocalPort = ReserveLoopbackPort(),
                InboundTag = $"test-in-{index}",
                OutboundTag = $"test-node-{index}",
            }).ToArray();
            var config = BuildSpeedTestProfile(boundTargets);
            await File.WriteAllTextAsync(configPath, config.ToJsonString(JsonStore.IndentedOptions), cancellationToken);

            var checkError = await CheckConfigAsync(installed.ExecutablePath, configPath, cancellationToken);
            if (checkError is not null)
            {
                foreach (var target in boundTargets)
                    progress?.Report(new NodeTestUpdate(target.Node.Id, kind, "配置不兼容", true));
                return;
            }

            process = StartCore(installed.ExecutablePath, configPath);
            if (!await WaitUntilReadyAsync(process, boundTargets.Select(static target => target.LocalPort), cancellationToken))
            {
                foreach (var target in boundTargets)
                    progress?.Report(new NodeTestUpdate(target.Node.Id, kind, "内核启动失败", true));
                return;
            }

            await Parallel.ForEachAsync(
                boundTargets,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
                async (target, token) =>
                {
                    var display = kind == NodeTestKind.RealConnection
                        ? await MeasureRealConnectionAsync(target.LocalPort, token)
                        : await MeasureUdpAsync(target.LocalPort, token);
                    progress?.Report(new NodeTestUpdate(target.Node.Id, kind, display, true));
                });
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The speed-test process already exited.
                }
                finally
                {
                    process.Dispose();
                }
            }

            try
            {
                if (File.Exists(configPath)) File.Delete(configPath);
            }
            catch (IOException)
            {
                // The next startup can safely clean a stale file in data/temp.
            }
        }
    }

    private static TestTarget ParseTarget(ProxyNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigurationJson)) throw new InvalidDataException();
        try
        {
            if (JsonNode.Parse(node.ConfigurationJson) is not JsonObject outbound) throw new InvalidDataException();
            var server = outbound["server"]?.GetValue<string>()?.Trim();
            var port = outbound["server_port"]?.GetValue<int>() ?? 0;
            if (string.IsNullOrWhiteSpace(server) || port is <= 0 or >= 65536) throw new InvalidDataException();
            return new TestTarget(node, server, port, outbound);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("节点配置缺少服务器或端口。", exception);
        }
    }

    private static JsonObject BuildSpeedTestProfile(IEnumerable<TestTarget> targets)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
        var rules = new JsonArray();
        foreach (var target in targets)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = target.InboundTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = target.LocalPort,
            });
            var outbound = JsonNode.Parse(target.Outbound.ToJsonString())!.AsObject();
            outbound["tag"] = target.OutboundTag;
            outbounds.Add(outbound);
            rules.Add(new JsonObject
            {
                ["inbound"] = target.InboundTag,
                ["action"] = "route",
                ["outbound"] = target.OutboundTag,
            });
        }

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error", ["timestamp"] = false },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = "direct",
                ["auto_detect_interface"] = true,
            },
        };
    }

    private static async Task<string> MeasurePingAsync(string server, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var task = ping.SendPingAsync(server, 5_000);
            var reply = await task.WaitAsync(cancellationToken);
            return reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms" : "不可达";
        }
        catch (Exception exception) when (exception is PingException or SocketException or TimeoutException)
        {
            return "不可达";
        }
    }

    private static async Task<string> MeasureTcpingAsync(string server, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(server, port, timeout.Token);
            stopwatch.Stop();
            return $"{Math.Max(1, stopwatch.ElapsedMilliseconds)} ms";
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return "失败";
        }
    }

    private static async Task<string> MeasureRealConnectionAsync(int localPort, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{localPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(6),
                AutomaticDecompression = DecompressionMethods.None,
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(RealTestUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();
            return response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 500
                ? $"{Math.Max(1, stopwatch.ElapsedMilliseconds)} ms"
                : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return "失败";
        }
    }

    private static async Task<string> MeasureUdpAsync(int localPort, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var stopwatch = Stopwatch.StartNew();
            await SendDnsThroughSocks5UdpAsync(localPort, timeout.Token);
            stopwatch.Stop();
            return $"{Math.Max(1, stopwatch.ElapsedMilliseconds)} ms";
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidDataException)
        {
            return "失败";
        }
    }

    private static async Task SendDnsThroughSocks5UdpAsync(int localPort, CancellationToken cancellationToken)
    {
        using var control = new TcpClient(AddressFamily.InterNetwork);
        await control.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken);
        await using var stream = control.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
        var greeting = new byte[2];
        await ReadExactAsync(stream, greeting, cancellationToken);
        if (greeting[0] != 5 || greeting[1] != 0) throw new InvalidDataException("SOCKS5 无认证协商失败。");

        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken);
        var header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken);
        if (header[0] != 5 || header[1] != 0) throw new InvalidDataException("SOCKS5 UDP Associate 失败。");
        var relayAddress = await ReadSocksAddressAsync(stream, header[3], cancellationToken);
        var portBytes = new byte[2];
        await ReadExactAsync(stream, portBytes, cancellationToken);
        var relayPort = (portBytes[0] << 8) | portBytes[1];
        if (relayPort <= 0) throw new InvalidDataException("SOCKS5 UDP 端口无效。");
        if (relayAddress.Equals(IPAddress.Any)) relayAddress = IPAddress.Loopback;

        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var dnsQuery = BuildDnsQuery(transactionId, "one.one.one.one");
        var packet = new byte[10 + dnsQuery.Length];
        packet[3] = 1;
        IPAddress.Parse("1.1.1.1").GetAddressBytes().CopyTo(packet, 4);
        packet[8] = 0;
        packet[9] = 53;
        dnsQuery.CopyTo(packet, 10);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var relay = new IPEndPoint(relayAddress, relayPort);
        await udp.SendAsync(packet.AsMemory(), relay, cancellationToken);
        var response = await udp.ReceiveAsync(cancellationToken);
        if (response.Buffer.Length < 12 || response.Buffer[3] != 1) throw new InvalidDataException("SOCKS5 UDP 响应无效。");
        var offset = 10;
        if (response.Buffer.Length < offset + 2 || response.Buffer[offset] != (byte)(transactionId >> 8) || response.Buffer[offset + 1] != (byte)transactionId)
            throw new InvalidDataException("DNS 响应不匹配。");
    }

    private static byte[] BuildDnsQuery(ushort id, string host)
    {
        var bytes = new List<byte>
        {
            (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
        };
        foreach (var label in host.Split('.'))
        {
            var encoded = System.Text.Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.AddRange([0, 0, 1, 0, 1]);
        return bytes.ToArray();
    }

    private static async Task<IPAddress> ReadSocksAddressAsync(NetworkStream stream, byte addressType, CancellationToken cancellationToken)
    {
        switch (addressType)
        {
            case 1:
                var ipv4 = new byte[4];
                await ReadExactAsync(stream, ipv4, cancellationToken);
                return new IPAddress(ipv4);
            case 4:
                var ipv6 = new byte[16];
                await ReadExactAsync(stream, ipv6, cancellationToken);
                return new IPAddress(ipv6);
            case 3:
                var length = stream.ReadByte();
                if (length <= 0) throw new InvalidDataException("SOCKS5 域名无效。");
                var domain = new byte[length];
                await ReadExactAsync(stream, domain, cancellationToken);
                return (await Dns.GetHostAddressesAsync(System.Text.Encoding.ASCII.GetString(domain), cancellationToken)).First();
            default:
                throw new InvalidDataException("SOCKS5 地址类型无效。");
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new IOException("SOCKS5 连接提前关闭。");
            offset += read;
        }
    }

    private static async Task<string?> CheckConfigAsync(string executablePath, string configPath, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(executablePath, ["check", "-c", configPath]);
        if (!process.Start()) return "无法启动 sing-box check。";
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        return process.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(error) ? "配置检查失败。" : error;
    }

    private static Process StartCore(string executablePath, string configPath)
    {
        var process = CreateProcess(executablePath, ["run", "-c", configPath]);
        if (!process.Start()) throw new InvalidOperationException("无法启动测速内核。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static Process CreateProcess(string executablePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static async Task<bool> WaitUntilReadyAsync(Process process, IEnumerable<int> ports, CancellationToken cancellationToken)
    {
        var pending = ports.ToHashSet();
        for (var attempt = 0; attempt < 30 && pending.Count > 0; attempt++)
        {
            if (process.HasExited) return false;
            foreach (var port in pending.ToArray())
            {
                try
                {
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(150);
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    pending.Remove(port);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    // Keep polling until the shared five-second startup window expires.
                }
            }
            if (pending.Count > 0) await Task.Delay(150, cancellationToken);
        }
        return pending.Count == 0;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed record TestTarget(ProxyNode Node, string Server, int Port, JsonObject Outbound)
    {
        public int LocalPort { get; init; }
        public string InboundTag { get; init; } = string.Empty;
        public string OutboundTag { get; init; } = string.Empty;
    }
}
