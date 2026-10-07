using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qpro.GazeBridge;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

internal static class CheekSessionModuleChecks
{
    internal static void Run(Action<float, float, string> near)
    {
        foreach (bool steamLink in new[] { false, true })
        {
            string source = steamLink ? "Steam Link" : "Virtual Desktop";
            using var fixture = new SessionFixture(steamLink);
            float[] weights = Weights(.09f, .09f, .18f, .02f);
            fixture.Frame(weights, 0);
            fixture.Frame(weights, 100);
            Check(.09f, .09f, .18f, .02f, "closed Companion keeps saved Strong native");

            fixture.Active = true;
            fixture.Frame(weights, 149);
            Check(.09f, .09f, .18f, .02f, "session is not polled early");
            fixture.Frame(weights, 1);
            fixture.Frame(weights, 100);
            Check(1, 1, 1, 0, "active session applies saved Strong puff and suck");

            fixture.Active = false;
            fixture.Frame(weights, 150);
            Check(.09f, .09f, .18f, .02f, "Stop restores native at next session poll");
            near(fixture.SavedPuffModeIsStrong ? 1 : 0, 1, source + ": Stop preserves saved puff preference");
            near(fixture.SavedSuckModeIsStrong ? 1 : 0, 1, source + ": Stop preserves saved suck preference");

            fixture.Camera(.81f, .21f);
            fixture.Frame(weights, 10);
            Check(.09f, .09f, .18f, .02f, "orphan camera packets cannot override inactive session");

            fixture.Active = true;
            fixture.Frame(weights, 240);
            fixture.Frame(weights, 100);
            Check(1, 1, 1, 0, "new session reuses saved preference");
            fixture.Camera(.73f, .31f);
            fixture.Frame(weights, 10);
            Check(.73f, .31f, 1, 0, "camera packet works in active session");

            fixture.Active = false;
            fixture.Frame(weights, 140);
            Check(.09f, .09f, .18f, .02f, "Stop discards a still-fresh camera override");
            fixture.Active = true;
            fixture.Frame(weights, 250);
            fixture.Frame(weights, 100);
            Check(1, 1, 1, 0, "restart cannot replay pre-Stop camera values");

            fixture.Camera(.88f, .26f);
            fixture.Frame(weights, 10);
            Check(.88f, .26f, 1, 0, "camera override acquired before native feed loss");
            fixture.Active = false;
            fixture.Frame(weights, 140, nativeAvailable: false);
            near(Shape(UnifiedExpressions.CheekPuffLeft), 0, source + ": missing native source clears last camera left");
            near(Shape(UnifiedExpressions.CheekPuffRight), 0, source + ": missing native source clears last camera right");
            near(Shape(UnifiedExpressions.CheekSuckLeft), 0, source + ": missing native source clears adjusted suck left");
            near(Shape(UnifiedExpressions.CheekSuckRight), 0, source + ": missing native source clears adjusted suck right");
            fixture.Camera(.99f, .99f);
            fixture.Frame(weights, 25, nativeAvailable: false);
            near(Shape(UnifiedExpressions.CheekPuffLeft), 0, source + ": orphan camera cannot refill cleared left");
            near(Shape(UnifiedExpressions.CheekPuffRight), 0, source + ": orphan camera cannot refill cleared right");
            fixture.Frame(weights, 10);
            Check(.09f, .09f, .18f, .02f, "native source recovery remains unmodified");

            // Adjusted native cheeks also need releasing when their source
            // disappears; this case has never received a camera packet.
            using var nativeOnly = new SessionFixture(steamLink) { Active = true };
            nativeOnly.Frame(weights, 0);
            nativeOnly.Frame(weights, 100);
            Check(1, 1, 1, 0, "native-only Strong adjustment is active");
            nativeOnly.Active = false;
            nativeOnly.Frame(weights, 150, nativeAvailable: false);
            Check(0, 0, 0, 0, "Stop with unavailable native feed releases all adjusted cheeks");

            foreach ((bool active, string style) in new[] { (false, "Strong"), (true, "Off"), (true, "Balanced"), (true, "Strong") })
            {
                using var loss = new SessionFixture(steamLink, style) { Active = active };
                loss.Frame(Weights(.37f, .12f, .6f, .1f), 0);
                near(Shape(UnifiedExpressions.CheekSuckLeft) > 0 ? 1 : 0, 1, source + $": {active}/{style} produces native suck before loss");
                loss.Frame(weights, 501, nativeAvailable: false, expireSteamFeed: true);
                near(Shape(UnifiedExpressions.CheekSuckLeft), 0, source + $": {active}/{style} native suck cannot latch after source expires");
                Check(0, 0, 0, 0, $"{active}/{style} clears prior native writes when source expires");
                UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.CheekPuffLeft].Weight = .123f;
                UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.CheekSuckLeft].Weight = .234f;
                loss.Frame(weights, 10, nativeAvailable: false, expireSteamFeed: true);
                near(Shape(UnifiedExpressions.CheekPuffLeft), .123f, source + ": released native puff slot is not repeatedly overwritten");
                near(Shape(UnifiedExpressions.CheekSuckLeft), .234f, source + ": released native suck slot is not repeatedly overwritten");
            }

            using var queued = new SessionFixture(steamLink);
            float[] unilateral = Weights(.38f, .27f, .6f, .1f);
            queued.Frame(unilateral, 0);
            queued.Camera(.81f, .49f);
            queued.Active = true;
            queued.Frame(unilateral, 250);
            near(Shape(UnifiedExpressions.CheekPuffLeft), 1, source + ": queued inactive camera left cannot activate on Start");
            near(Shape(UnifiedExpressions.CheekPuffRight), 0, source + ": queued inactive camera right cannot activate on Start");
            queued.Active = false;
            queued.Frame(unilateral, 250);
            for (int packet = 0; packet < 600; packet++) queued.Camera(.83f, .57f);
            queued.Active = true;
            queued.Frame(unilateral, 250);
            near(Shape(UnifiedExpressions.CheekPuffRight), 0, source + ": bounded transition drain withholds backlog");
            queued.Frame(unilateral, 10);
            near(Shape(UnifiedExpressions.CheekPuffRight), 0, source + ": remaining backlog stays discarded");
            queued.Camera(.72f, .22f);
            queued.Frame(unilateral, 10);
            near(Shape(UnifiedExpressions.CheekPuffLeft), .72f, source + ": fresh camera left accepted after draining");
            near(Shape(UnifiedExpressions.CheekPuffRight), .22f, source + ": fresh camera right accepted after draining");

            void Check(float puffLeft, float puffRight, float suckLeft, float suckRight, string label)
            {
                near(Shape(UnifiedExpressions.CheekPuffLeft), puffLeft, source + ": " + label + " (puff left)");
                near(Shape(UnifiedExpressions.CheekPuffRight), puffRight, source + ": " + label + " (puff right)");
                near(Shape(UnifiedExpressions.CheekSuckLeft), suckLeft, source + ": " + label + " (suck left)");
                near(Shape(UnifiedExpressions.CheekSuckRight), suckRight, source + ": " + label + " (suck right)");
            }
        }
    }

    private static float Shape(UnifiedExpressions shape) => UnifiedTracking.Data.Shapes[(int)shape].Weight;
    private static float[] Weights(float puffLeft, float puffRight, float suckLeft, float suckRight)
    {
        var weights = new float[70];
        weights[2] = puffLeft;
        weights[3] = puffRight;
        weights[6] = suckLeft;
        weights[7] = suckRight;
        weights[40] = .8f;
        weights[41] = .8f;
        return weights;
    }

    private sealed class SessionFixture : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly TrackingModule _module;
        private readonly MemoryMappedViewAccessor _view;
        private readonly bool _steamLink;
        private readonly ParsePacket? _steamParser;
        private readonly UdpClient _cameraInput;
        private readonly UdpClient _cameraSender = new(AddressFamily.InterNetwork);
        private readonly object _strongPuff;
        private readonly object _strongSuck;
        private long _time = Environment.TickCount64;
        internal bool Active { get; set; }
        internal bool SavedPuffModeIsStrong => Get("_cheekPuffMode")!.Equals(_strongPuff);
        internal bool SavedSuckModeIsStrong => Get("_cheekSuckMode")!.Equals(_strongSuck);

        internal SessionFixture(bool steamLink, string style = "Strong")
        {
            _steamLink = steamLink;
            _module = (TrackingModule)Activator.CreateInstance(typeof(TrackingModule), Private,
                binder: null, args: [new Func<long>(() => _time), "QproCheekSessionTest." + Guid.NewGuid().ToString("N")], culture: null)!;
            for (Type? type = _module.GetType(); type is not null; type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (typeof(ILogger).IsAssignableFrom(field.FieldType)) field.SetValue(_module, NullLogger.Instance);
                foreach (PropertyInfo property in type.GetProperties(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (property.CanWrite && typeof(ILogger).IsAssignableFrom(property.PropertyType)) property.SetValue(_module, NullLogger.Instance);
            }
            MemoryMappedFile map = MemoryMappedFile.CreateNew(null, 360);
            _view = map.CreateViewAccessor();
            Set("_map", map);
            Set("_view", _view);
            Set("_wasActive", true);
            Set("_needsExpression", true);
            Set("_useSteamLink", steamLink);
            Set("_cheekSessionProbe", new Func<bool>(() => Active));
            Set("_nextEyebrowSettingsCheckTick", long.MaxValue);
            Assembly assembly = typeof(TrackingModule).Assembly;
            _strongPuff = Enum.Parse(assembly.GetType("Qpro.GazeBridge.CheekPuffMode")!, style);
            _strongSuck = Enum.Parse(assembly.GetType("Qpro.GazeBridge.CheekSuckMode")!, style);
            if (steamLink)
            {
                Type sourceType = assembly.GetType("Qpro.GazeBridge.SteamOscSource")!;
                object source = Activator.CreateInstance(sourceType, Private, binder: null, args: [0], culture: null)!;
                Set("_steamSource", source);
                _steamParser = sourceType.GetMethod("ParseDatagram", Private)!.CreateDelegate<ParsePacket>(source);
            }
            _cameraInput = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _cameraInput.Client.Blocking = false;
            _cameraInput.Client.ReceiveBufferSize = 1024 * 1024;
            Set("_cheekCameraSocket", _cameraInput);
            _module.Status = ModuleState.Active;
        }

        internal void Frame(float[] weights, long elapsedMs, bool nativeAvailable = true, bool expireSteamFeed = false)
        {
            _time += elapsedMs;
            // Exercise the real session refresh while freezing only preference
            // polling. Tests must never read or overwrite installed settings.
            typeof(TrackingModule).GetMethod("RefreshCheekSession", Private)!.Invoke(_module, null);
            Set("_cheekPuffMode", _strongPuff);
            Set("_cheekSuckMode", _strongSuck);
            Set("_nextCheekPuffModeCheckTick", long.MaxValue);
            Set("_nextCheekSuckModeCheckTick", long.MaxValue);
            if (_steamLink && !expireSteamFeed)
            {
                foreach ((string name, int index) in new[] { ("CheekPuffL", 2), ("CheekPuffR", 3), ("CheekSuckL", 6), ("CheekSuckR", 7), ("LipPuckerL", 40), ("LipPuckerR", 41) })
                    if (!_steamParser!(Osc("/sl/xrfb/facew/" + name, weights[index]), _time)) throw new InvalidOperationException("Synthetic Steam expression was rejected.");
                if (!_steamParser!(Osc("/sl/xrfb/facec/LowerFace", nativeAvailable ? 1 : 0), _time)) throw new InvalidOperationException("Synthetic Steam capability was rejected.");
            }
            else if (!_steamLink)
            {
                byte[] bytes = new byte[360];
                bytes[0] = nativeAvailable ? (byte)1 : (byte)0;
                for (int i = 0; i < weights.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4 + i * 4, 4), weights[i]);
                _view.WriteArray(0, bytes, 0, bytes.Length);
            }
            _module.Update();
        }

        internal void Camera(float left, float right)
        {
            byte[] packet = new byte[16];
            "QPCO"u8.CopyTo(packet);
            packet[4] = 1;
            packet[5] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8, 4), left);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), right);
            _cameraSender.Send(packet, (IPEndPoint)_cameraInput.Client.LocalEndPoint!);
            if (!_cameraInput.Client.Poll(1_000_000, SelectMode.SelectRead)) throw new TimeoutException("Synthetic camera packet did not arrive.");
        }

        private static byte[] Osc(string address, float value)
        {
            byte[] text = Encoding.ASCII.GetBytes(address);
            int addressBytes = (text.Length + 4) & ~3;
            byte[] packet = new byte[addressBytes + 8];
            text.CopyTo(packet, 0);
            packet[addressBytes] = (byte)',';
            packet[addressBytes + 1] = (byte)'f';
            BinaryPrimitives.WriteSingleBigEndian(packet.AsSpan(addressBytes + 4, 4), value);
            return packet;
        }

        private object? Get(string name) => typeof(TrackingModule).GetField(name, Private)!.GetValue(_module);
        private void Set(string name, object? value) => typeof(TrackingModule).GetField(name, Private)!.SetValue(_module, value);
        public void Dispose() { _module.Teardown(); _cameraSender.Dispose(); }
        private delegate bool ParsePacket(ReadOnlySpan<byte> packet, long tick);
    }
}
