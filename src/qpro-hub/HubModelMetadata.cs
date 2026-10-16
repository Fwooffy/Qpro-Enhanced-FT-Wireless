using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

// Export only portable model details. Private recording paths and prepared
// image caches belong to the local dataset, not to a transferable model.
internal static class HubModelMetadata
{
    internal static JsonObject Create(int version, string displayName, string? datasetName,
        string origin, JsonObject? existing, JsonObject? classification = null)
    {
        var payload = new JsonObject
        {
            ["format"] = "qpro-tongue-model-metadata-v1", ["displayName"] = displayName,
            ["version"] = version, ["origin"] = origin,
            ["datasetSession"] = datasetName is null ? existing?["datasetSession"]?.DeepClone() : datasetName,
            ["createdUtc"] = existing?["createdUtc"]?.DeepClone() ?? JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
        };
        CopyPortableDetails(classification ?? existing, payload);
        // Bundle ownership is local state: keep it on rename, never transfer
        // it through an exported package or a newly trained refinement.
        if (classification is null && IsBundledDeveloper(version, existing))
            payload["bundledDeveloperModel"] = true;
        return payload;
    }

    internal static bool IsDeveloper(JsonObject? metadata) => Text(metadata, "modelKind") is "developer-tongue" or "developer-lower-face";

    internal static bool HasDeveloperLineage(JsonObject? metadata) => IsDeveloper(metadata) ||
        Text(metadata, "parentModelKind") is "developer-tongue" or "developer-lower-face";

    internal static bool IsBundledDeveloper(int version, JsonObject? metadata) =>
        IsDeveloper(metadata) && Boolean(metadata, "bundledDeveloperModel") == true &&
        Text(metadata, "origin") == "bundled developer model";

    internal static void CopyDeveloperParentDetails(JsonObject? source, JsonObject destination)
    {
        if (!HasDeveloperLineage(source)) return;
        CopyDeveloperDetails(source!, destination, parentScope: true);
    }

    internal static JsonObject? CreateRefinementClassification(JsonObject? result, JsonObject? parent)
    {
        var classification = (result ?? parent)?.DeepClone()?.AsObject();
        // New captures and refinements train tongue outputs only. Preserve
        // old combined model metadata on import/export, not on a new child.
        classification?.Remove("hasCameraCheeks");
        classification?.Remove("cheekTraining");
        if (result is null && classification is not null && HasDeveloperLineage(parent))
        {
            // A missing friendly sidecar does not make the new weights the
            // bundled developer model, or validate them with the parent's data.
            classification["modelKind"] = "personal-refinement";
            classification.Remove("validationScope");
            classification.Remove("bundledDeveloperModel");
        }
        return classification;
    }

    internal static bool IsMustachio(JsonObject? metadata) =>
        Text(metadata, "modelKind") == "mustachio-experimental" ||
        Text(metadata, "parentModelKind") == "mustachio-experimental";

    internal static bool HasCameraCheeks(JsonObject? metadata) =>
        metadata?["hasCameraCheeks"] is JsonValue value &&
        value.TryGetValue<bool>(out bool enabled) && enabled;

    internal static bool IsExperimental(JsonObject? metadata) =>
        IsMustachio(metadata) || HasCameraCheeks(metadata) ||
        Text(metadata, "modelKind") == "camera-cheeks-experimental" ||
        (metadata?["isExperimental"] is JsonValue value &&
         value.TryGetValue<bool>(out bool experimental) && experimental);

    internal static void CopyPortableDetails(JsonObject? source, JsonObject destination)
    {
        if (source is null) return;
        foreach (string key in new[] { "modelKind", "parentModelKind", "parentDisplayName", "trackingSource",
                     "tongueParentSha256", "trainingOrigin", "trainedUtc" })
            if (Text(source, key) is string text && !string.IsNullOrWhiteSpace(text))
                destination[key] = text;
        if (IsExperimental(source)) destination["isExperimental"] = true;
        if (HasCameraCheeks(source)) destination["hasCameraCheeks"] = true;
        foreach (string key in new[] { "parentVersion", "sourceVersion" })
            if (source[key] is JsonValue number && number.TryGetValue<int>(out int version) && version > 0)
                destination[key] = version;
        CopyDeveloperDetails(source, destination, parentScope: false);
        if (Text(source, "tongueParent") is string parent)
            destination["tongueParent"] = parent.Replace('\\', '/').Split('/')[^1];
        if (destination["trainingOrigin"] is null && Text(source, "origin") is string origin)
            destination["trainingOrigin"] = origin;
        if (destination["trainedUtc"] is null && Text(source, "createdUtc") is string created)
            destination["trainedUtc"] = created;
        if (HasCameraCheeks(source) && source["cheekTraining"] is JsonObject training)
        {
            var portableTraining = new JsonObject();
            foreach (string key in new[] { "targetSource", "frozenTongueParent", "trainSamples", "validationSamples",
                         "checkpointEpoch", "validation", "featureNormalization", "loss", "validationGrade",
                         "independentWearerValidation", "developerPromotionApproved", "trainingWearers",
                         "independentCaptureValidation", "liveValidationComplete", "freshLivePoseCheck" })
                if (training[key] is JsonNode detail) portableTraining[key] = detail.DeepClone();
            destination["cheekTraining"] = portableTraining;
        }
    }

    private static void CopyDeveloperDetails(JsonObject source, JsonObject destination, bool parentScope)
    {
        if (source["baseDeveloperVersion"] is JsonValue baseVersion &&
            baseVersion.TryGetValue<int>(out int version) && version > 0)
            destination["baseDeveloperVersion"] = version;
        if (source["lineage"] is JsonObject lineage)
        {
            var portable = new JsonObject();
            CopyIntegers(lineage, portable, ["baseDeveloperVersion", "directionSourceVersion"], minimum: 1);
            CopyBooleans(lineage, portable, ["visibilityEncoderUnchanged", "visibilityHeadConvertedWithoutFitting", "cameraCheekStateUnchanged"]);
            CopyFractions(lineage, portable, ["cameraWeight", "threshold"]);
            foreach (string key in new[] { "baseGateFile", "directionSourceFile" })
                if (Text(lineage, key) is string file && !string.IsNullOrWhiteSpace(file))
                    portable[key] = file.Replace('\\', '/').Split('/')[^1];
            foreach (string key in new[] { "baseGateSha256", "trainedVisibilitySha256", "directionSourceSha256", "originalTongueDirectionSha256" })
                if (Text(lineage, key) is string hash && hash.Length == 64 && hash.All(Uri.IsHexDigit))
                    portable[key] = hash.ToLowerInvariant();
            destination["lineage"] = portable;
        }
        foreach (string key in new[] { "parentValidationScope", "validationScope" })
        {
            if (source[key] is not JsonObject scope) continue;
            var portable = new JsonObject();
            CopyIntegers(scope, portable, ["trainingWearers", "freshFrames", "freshHiddenFrames", "freshVisibleFrames",
                "baselineMisses", "candidateMisses", "baselineFalsePositives", "candidateFalsePositives", "newSmileTipMisses"], minimum: 0);
            CopyBooleans(scope, portable, ["independentSessionFromTraining", "independentWearerValidation", "publicPromotionValidated",
                "strictPoseRetentionPassed", "directionAccuracyCeilingPassed", "cameraOnlyFalsePositiveGatePassed", "nativeFusionRequired",
                "tunedFromFreshCapture", "directionOutputsUnchanged", "cameraCheekOutputsUnchanged"]);
            CopyFractions(scope, portable, ["baselineF1", "candidateF1"]);
            foreach (string textKey in new[] { "appearance", "reviewType" })
                if (Text(scope, textKey) is string text && text.Length <= 256 &&
                    !text.Any(char.IsControl) && text.IndexOfAny(['\\', '/', ':']) < 0)
                    portable[textKey] = text;
            destination[parentScope && key == "validationScope" ? "parentValidationScope" : key] = portable;
        }
    }

    private static void CopyIntegers(JsonObject source, JsonObject destination, string[] keys, int minimum)
    {
        foreach (string key in keys)
            if (source[key] is JsonValue value && value.TryGetValue<int>(out int number) && number >= minimum)
                destination[key] = number;
    }

    private static void CopyBooleans(JsonObject source, JsonObject destination, string[] keys)
    {
        foreach (string key in keys)
            if (Boolean(source, key) is bool value) destination[key] = value;
    }

    private static void CopyFractions(JsonObject source, JsonObject destination, string[] keys)
    {
        foreach (string key in keys)
            if (source[key] is JsonValue value && value.TryGetValue<double>(out double number) &&
                double.IsFinite(number) && number is >= 0 and <= 1)
                destination[key] = number;
    }

    private static bool? Boolean(JsonObject? source, string key) =>
        source?[key] is JsonValue value && value.TryGetValue<bool>(out bool result) ? result : null;

    private static string? Text(JsonObject? metadata, string key) =>
        metadata?[key] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
}
