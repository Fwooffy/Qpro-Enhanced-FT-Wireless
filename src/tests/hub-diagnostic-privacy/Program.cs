using QproFaceTracking.Hub;
using System.Diagnostics;
using System.Text.Json;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
void Equal(string input, string expected, string description)
    => Check(HubDiagnosticPrivacy.Sanitize(input) == expected, description);

try
{
    Equal(@"At C:\Users\Tester\Downloads\Qpro\setup-runtime.ps1:163 char:5",
        @"At %USERPROFILE%\Downloads\Qpro\setup-runtime.ps1:163 char:5", "Keep script and line after hiding another user's profile.");
    Equal(@"File D:/Users/Another Person/Documents/refine.py, line 42",
        @"File %USERPROFILE%/Documents/refine.py, line 42", "Handle spaces and forward slashes.");
    Equal("{\"recovery\":\"C:\\\\Users\\\\Tester\\\\AppData\\\\Local\\\\Qpro\\\\backup\"}",
        "{\"recovery\":\"%USERPROFILE%\\\\AppData\\\\Local\\\\Qpro\\\\backup\"}", "Handle JSON-escaped profile paths.");
    Check(HubDiagnosticPrivacy.Sanitize(@"At E:\Profiles\Owner\Qpro\worker.py", @"E:\Profiles\Owner") == @"At %USERPROFILE%\Qpro\worker.py",
        "Handle a nonstandard current profile root.");
    Check(HubDiagnosticPrivacy.Sanitize(@"At E:\Profiles\OwnerTwo\Qpro", @"E:\Profiles\Owner") == @"At E:\Profiles\OwnerTwo\Qpro",
        "A profile prefix is not another user's entire root.");
    Equal("ADB target: 230TESTSERIAL1\nDriver SHA-256: abc123", "ADB target: [redacted]\nDriver SHA-256: abc123", "Keep following diagnostic line.");
    Equal("serial=230TESTSERIAL1 state=offline exit=4", "serial=[redacted] state=offline exit=4", "Keep fields after unquoted serial.");
    Equal("serial='230TESTSERIAL1' error=unauthorized", "serial=[redacted] error=unauthorized", "Handle quoted serial values.");
    Equal("ANDROID_SERIAL=230TESTSERIAL1 QPRO_ADB_TARGET=230SECONDTEST1", "ANDROID_SERIAL=[redacted] QPRO_ADB_TARGET=[redacted]", "Handle diagnostic environment identifiers.");
    Equal("adb.exe -s 230TESTSERIAL1 get-state: offline", "adb.exe -s [redacted] get-state: offline", "Keep ADB operation and result.");
    Equal("230TESTSERIAL1\tdevice product:seacliff model:Quest_Pro", "[redacted]\tdevice product:seacliff model:Quest_Pro", "Keep useful device row identity.");
    Equal("230TESTSERIAL1 unauthorized", "[redacted] unauthorized", "Mask unauthorized devices too.");
    Equal("unexpected device stopped", "unexpected device stopped", "Do not erase an ordinary error phrase.");
    var json = HubDiagnosticPrivacy.Sanitize("{\"serial\":\"230TESTSERIAL1\",\"adbTarget\":\"192.168.1.23:5555\",\"compatible\":false,\"exit\":4}");
    using (var doc = JsonDocument.Parse(json))
    {
        Check(doc.RootElement.GetProperty("serial").GetString() == "[redacted]", "JSON serial is masked.");
        Check(doc.RootElement.GetProperty("adbTarget").GetString() == "[redacted]", "JSON ADB target is masked.");
        Check(!doc.RootElement.GetProperty("compatible").GetBoolean() && doc.RootElement.GetProperty("exit").GetInt32() == 4, "JSON evidence remains parseable.");
    }
    Equal("{\"deviceSerial\":\"230TESTSERIAL1\",\"headsetSerial\":\"230SECONDTEST1\"}",
        "{\"deviceSerial\":\"[redacted]\",\"headsetSerial\":\"[redacted]\"}", "Handle alternate explicit identifier fields.");
    Check(HubDiagnosticPrivacy.Sanitize("Device 230TESTSERIAL1 disconnected", null, "230TESTSERIAL1") == "Device [redacted] disconnected", "Known target is removed from free-form errors.");
    Check(HubDiagnosticPrivacy.Sanitize("ADB target: 230TESTSERIAL1 state=offline", null, "230TESTSERIAL1") == "ADB target: [redacted] state=offline", "Known and field redaction do not corrupt markers.");
    Check(HubDiagnosticPrivacy.Sanitize("hash=aa230TESTSERIAL1bb", null, "230TESTSERIAL1") == "hash=aa230TESTSERIAL1bb", "Do not remove a substring of a hash.");
    Check(HubDiagnosticPrivacy.Sanitize("CUDA: False", null, " ", "", "a") == "CUDA: False", "Ignore invalid tiny configuration values.");
    Equal("Wireless Quest ready: 192.168.1.168:5555", "Wireless Quest ready: [redacted]:5555", "Keep service port, hide LAN endpoint.");
    Equal("http://10.20.30.40:8081/camera0.mjpg", "http://[redacted]:8081/camera0.mjpg", "Preserve camera route and port.");
    Equal("address=10.20.30.40", "address=[redacted]", "Mask bare address fields.");
    Equal("connected to 172.20.1.4", "connected to [redacted]", "Mask network connection messages.");
    Equal("{\"host\":\"172.31.5.6\",\"port\":5555}", "{\"host\":\"[redacted]\",\"port\":5555}", "Mask JSON network fields.");
    Equal("Camera 169.254.2.3 unavailable", "Camera [redacted] unavailable", "Mask link-local IPv4.");
    Equal("http://[fd12:3456::1234]:8081/preview", "http://[redacted]:8081/preview", "Mask unique-local IPv6.");
    Equal("address=fe80::1234%7", "address=[redacted]", "Mask scoped link-local IPv6.");
    Equal("address=::ffff:192.168.1.5", "address=[redacted]", "Mask IPv4-mapped local IPv6.");
    foreach (var evidence in new[]
    {
        "ROCm version=10.1.0.0 PyTorch=2.14.0+rocm10.1.0", "Windows build: 26200", "VD version: 1.34.25.0",
        "Quest build 51503870024400340 · 207.0.0.218.1234.1051346098", "address=127.0.0.1 port=27273",
        "http://127.0.0.1:8081/selected.mjpg", "IPv6 loopback ::1", "Download https://download.pytorch.org/whl/cu128",
        "Driver SHA-256 9fa2b70005d4e0aea02a891ae48a5caa0314d0f30e1190001d7ea979ec10d5c1",
        "INFERENCE_BACKEND feature=pupil backend=amd-rocm device=cuda:0 name=AMD Radeon RX 7900 XTX",
        "192.168.999.1 is invalid", "version 172.20.0.0", "https://192.169.1.2/help", "20:11:42 exit 4",
        @"C:\Program Files\Virtual Desktop Streamer\driver_virtualdesktop.dll", "serializable=False"
    }) Equal(evidence, evidence, "Preserve useful support evidence: " + evidence);
    var sample = "At C:\\Users\\Tester\\app.ps1\nADB target: 230TESTSERIAL1\naddress=192.168.1.20:5555\nserial=230TESTSERIAL1 status=offline";
    var once = HubDiagnosticPrivacy.Sanitize(sample);
    Check(HubDiagnosticPrivacy.Sanitize(once) == once, "Redacting shared diagnostics again is idempotent.");
    var large = string.Join('\n', Enumerable.Repeat("GPU AMD Radeon RX 7900 XTX; status=ready; callback=0; version=10.1.0.0", 2800));
    var watch = Stopwatch.StartNew();
    Check(HubDiagnosticPrivacy.Sanitize(large) == large, "Handle a full bounded Activity history without losing evidence.");
    Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Sanitizing bounded history completes promptly.");
    Console.WriteLine($"PASS: {checks} diagnostic privacy checks.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    return 1;
}
