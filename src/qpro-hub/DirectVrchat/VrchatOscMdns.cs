using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace QproFaceTracking.Hub;

internal static class VrchatOscMdns
{
    private const string ServiceType = "_oscjson._tcp.local";
    private const ushort AddressRecord = 1;
    private const ushort PointerRecord = 12;
    private const ushort ServiceRecord = 33;
    private const int MaxNames = 32;
    private const int MaxServices = 8;
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly Question Browse = new(ServiceType, PointerRecord);

    internal static Task<IReadOnlyList<VrchatOscService>> BrowseAsync(CancellationToken token) =>
        BrowseAsync(Multicast, GetLocalAddresses(), token);

    internal static async Task<IReadOnlyList<VrchatOscService>> BrowseAsync(
        IPEndPoint destination, HashSet<IPAddress> localAddresses, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cache = new ResponseCache(localAddresses);
        try
        {
            // RFC 6762 section 5.1 allows a short-lived client to receive replies on an ephemeral port.
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.MulticastLoopback = true;
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            var ids = new HashSet<ushort>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var round = CancellationTokenSource.CreateLinkedTokenSource(token);
                round.CancelAfter(TimeSpan.FromMilliseconds(1050));
                var sent = new HashSet<Question>();
                try
                {
                    while (true)
                    {
                        foreach (Question question in cache.GetQuestions())
                        {
                            if (sent.Add(question))
                            {
                                ushort id;
                                do { id = (ushort)Random.Shared.Next(1, 65536); } while (!ids.Add(id));
                                await SendAsync(udp, destination, localAddresses, CreateQuery(question, id), round.Token)
                                    .ConfigureAwait(false);
                            }
                        }

                        var response = await udp.ReceiveAsync(round.Token).ConfigureAwait(false);
                        if (IsLocal(response.RemoteEndPoint.Address, localAddresses))
                            cache.AddResponse(response.Buffer, ids);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

                var services = cache.GetServices();
                if (services.Count > 0)
                    return services;
            }
        }
        catch (SocketException) { }

        token.ThrowIfCancellationRequested();
        return cache.GetServices();
    }

    private static async Task SendAsync(UdpClient udp, IPEndPoint destination, HashSet<IPAddress> localAddresses,
        ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        if (IPAddress.IsLoopback(destination.Address))
        {
            await udp.SendAsync(packet, destination, token).ConfigureAwait(false);
            return;
        }

        foreach (IPAddress address in localAddresses)
        {
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                await udp.SendAsync(packet, destination, token).ConfigureAwait(false);
            }
            catch (SocketException) { } // An adapter can disappear between enumeration and send.
        }
    }

    private static HashSet<IPAddress> GetLocalAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(address.Address);
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    private static bool IsLocal(IPAddress address, HashSet<IPAddress> localAddresses) =>
        IPAddress.IsLoopback(address) || localAddresses.Contains(address);

    private static byte[] CreateQuery(Question question, ushort id)
    {
        var packet = new byte[12 + question.Name.Length + 2 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        packet[5] = 1;
        int offset = 12;
        foreach (string label in question.Name.Split('.'))
        {
            packet[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, packet.AsSpan(offset));
        }
        offset++; // Root label.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), question.Type);
        packet[offset + 3] = 1; // IN class.
        return packet;
    }

    private sealed class ResponseCache(HashSet<IPAddress> localAddresses)
    {
        private readonly HashSet<string> instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Host, ushort Port)> serviceRecords = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> addresses = new(StringComparer.OrdinalIgnoreCase);

        internal IEnumerable<Question> GetQuestions()
        {
            yield return Browse;
            foreach (string instance in instances)
            {
                if (!serviceRecords.TryGetValue(instance, out var service))
                    yield return new Question(instance, ServiceRecord);
                else if (!addresses.ContainsKey(service.Host))
                    yield return new Question(service.Host, AddressRecord);
            }
        }

        internal IReadOnlyList<VrchatOscService> GetServices()
        {
            var result = new List<VrchatOscService>();
            foreach (string instance in instances.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!serviceRecords.TryGetValue(instance, out var service)
                    || !addresses.TryGetValue(service.Host, out var address))
                    continue;
                // VRChat's OSCQuery HTTP listener is bound to loopback, even if a local NIC was advertised.
                var queryAddress = IPAddress.IsLoopback(address) ? address : IPAddress.Loopback;
                result.Add(new VrchatOscService(instance[..instance.IndexOf('.')], new IPEndPoint(queryAddress, service.Port)));
                if (result.Count == MaxServices)
                    break;
            }
            return result;
        }

        internal void AddResponse(ReadOnlySpan<byte> packet, HashSet<ushort> ids)
        {
            if (packet.Length is < 12 or > 9000 || !ids.Contains(Read16(packet, 0))
                || (Read16(packet, 2) & 0xF80F) != 0x8000)
                return;

            int questionCount = Read16(packet, 4);
            int recordCount = Read16(packet, 6) + Read16(packet, 8) + Read16(packet, 10);
            if (questionCount is < 1 or > 16 || recordCount is < 1 or > 128)
                return;

            try
            {
                int offset = 12;
                for (int i = 0; i < questionCount; i++)
                {
                    ReadName(packet, ref offset);
                    offset += 4;
                }

                // Parse all records before committing them, so malformed packets cannot publish a partial service.
                var records = new List<Record>();
                for (int i = 0; i < recordCount; i++)
                {
                    string name = ReadName(packet, ref offset);
                    if (offset + 10 > packet.Length)
                        return;
                    ushort type = Read16(packet, offset);
                    ushort recordClass = Read16(packet, offset + 2);
                    bool goodbye = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]) == 0;
                    int length = Read16(packet, offset + 8);
                    offset += 10;
                    if (offset + length > packet.Length)
                        return;
                    if ((recordClass & 0x7FFF) == 1)
                    {
                        switch (type)
                        {
                            case PointerRecord when string.Equals(name, ServiceType, StringComparison.OrdinalIgnoreCase):
                                records.Add(new Record(type, name, ReadTarget(packet, offset, offset + length), 0, null, goodbye));
                                break;
                            case ServiceRecord when IsVrchatInstance(name) && length > 6:
                                records.Add(new Record(type, name, ReadTarget(packet, offset + 6, offset + length),
                                    Read16(packet, offset + 4), null, goodbye));
                                break;
                            case AddressRecord when length == 4 && IsLocalHostName(name):
                                records.Add(new Record(type, name, null, 0, new IPAddress(packet.Slice(offset, length)), goodbye));
                                break;
                        }
                    }
                    offset += length;
                }

                foreach (Record record in records)
                    AddRecord(record);
            }
            catch (FormatException) { }
        }

        private void AddRecord(Record record)
        {
            switch (record.Type)
            {
                case PointerRecord when record.Target is { } instance && IsVrchatInstance(instance):
                    if (record.Goodbye)
                        instances.Remove(instance);
                    else if (instances.Count < MaxNames)
                        instances.Add(instance);
                    break;
                case ServiceRecord when record.Target is { } host && record.Port != 0 && IsLocalHostName(host):
                    if (record.Goodbye)
                        serviceRecords.Remove(record.Name);
                    else if (serviceRecords.Count < MaxNames || serviceRecords.ContainsKey(record.Name))
                        serviceRecords[record.Name] = (host, record.Port);
                    break;
                case AddressRecord when record.Address is { } address && IsLocal(address, localAddresses):
                    if (record.Goodbye)
                        addresses.Remove(record.Name);
                    else if (addresses.Count < MaxNames || addresses.ContainsKey(record.Name))
                        addresses[record.Name] = address;
                    break;
            }
        }
    }

    private static bool IsVrchatInstance(string name)
    {
        int suffix = name.IndexOf('.');
        return suffix > 0 && name[(suffix + 1)..].Equals(ServiceType, StringComparison.OrdinalIgnoreCase)
            && VrchatOscQuery.IsVrchatClientName(name[..suffix]);
    }

    private static bool IsLocalHostName(string name) =>
        name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static string ReadTarget(ReadOnlySpan<byte> packet, int offset, int end)
    {
        string name = ReadName(packet, ref offset);
        return offset == end ? name : throw new FormatException("Invalid DNS target record.");
    }

    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        int next = offset;
        int expandedLength = 1;
        bool compressed = false;
        var name = new StringBuilder();
        while (next < packet.Length)
        {
            int length = packet[next++];
            if (length == 0)
            {
                if (!compressed)
                    offset = next;
                return name.ToString();
            }

            if ((length & 0xC0) == 0xC0 && next < packet.Length)
            {
                int target = ((length & 0x3F) << 8) | packet[next++];
                if (target >= next - 2 || target < 12)
                    break; // A valid compression pointer refers to a prior name occurrence.
                if (!compressed)
                    offset = next;
                next = target;
                compressed = true;
                continue;
            }

            expandedLength += length + 1;
            if (length > 63 || next + length > packet.Length || expandedLength > 255)
                break;
            if (name.Length > 0)
                name.Append('.');
            // Escape label punctuation so a single wire label cannot impersonate a multi-label DNS name.
            name.Append(Encoding.UTF8.GetString(packet.Slice(next, length)).Replace("\\", "\\\\").Replace(".", "\\."));
            next += length;
        }

        throw new FormatException("Invalid DNS name.");
    }

    private static ushort Read16(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);

    private readonly record struct Question(string Name, ushort Type);
    private readonly record struct Record(ushort Type, string Name, string? Target, ushort Port, IPAddress? Address, bool Goodbye);
}
