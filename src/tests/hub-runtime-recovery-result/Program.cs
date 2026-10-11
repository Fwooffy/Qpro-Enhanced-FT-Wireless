using QproFaceTracking.Hub;

var backup = "runtime-backup-" + new string('a', 32);
var other = "runtime-backup-" + new string('b', 32);
var checks = 0;
void Check(bool condition, string context) { if (!condition) throw new InvalidOperationException(context); checks++; }
HubSetupResult New() => HubSetupResult.Create("runtime-recovery.ps1", ["-Action", "Restore", "-BackupName", backup])!;
var valid = "QPRO_RUNTIME_RECOVERY {\"format\":\"qpro-runtime-recovery-result-v1\",\"restored\":\"" + backup + "\",\"verified\":true}";
Check(HubSetupResult.Create("runtime-recovery.ps1", ["-Action", "List"]) is null, "Metadata scans do not imply a restore");
Check(New().Failure() is not null, "No completion report cannot certify restore");
var result = New(); result.Observe(valid); Check(result.Failure() is null, "One verified matching restore accepted");
result.Observe(valid); Check(result.Failure() is not null, "Duplicate completion is ambiguous");
foreach (var line in new[] { valid.Replace(backup, other), valid.Replace("true", "false"), valid.Replace(backup, "../foreign"),
    "QPRO_RUNTIME_RECOVERY {}", "QPRO_RUNTIME_RECOVERY {bad", valid.Replace("\"verified\":true", "\"verified\":true,\"verified\":true") })
{
    result = New(); result.Observe(line); Check(result.Failure() is not null, "Invalid restore cannot be reported successful");
}
Console.WriteLine($"{checks} runtime recovery completion checks passed.");
