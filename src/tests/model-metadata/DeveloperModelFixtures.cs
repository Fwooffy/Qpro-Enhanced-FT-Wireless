using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

internal static class DeveloperModelFixtures
{
    internal static void RunActualMetadata(string path, Action<bool, string> check)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        int version = metadata["version"]!.GetValue<int>();
        check(HubModelMetadata.IsDeveloper(metadata) && HubModelMetadata.IsBundledDeveloper(version, metadata),
            "actual developer sidecar is not recognized as a protected bundle");
        bool tongueOnly = metadata["modelKind"]?.GetValue<string>() == "developer-tongue";
        check(HubModelMetadata.IsExperimental(metadata) && HubModelMetadata.HasCameraCheeks(metadata) != tongueOnly,
            "actual developer sidecar does not match its tongue-only/legacy capabilities");
        var portable = new JsonObject();
        HubModelMetadata.CopyPortableDetails(metadata, portable);
        foreach (string key in new[] { "lineage", "validationScope" })
            foreach (var field in metadata[key]!.AsObject())
                check(JsonNode.DeepEquals(field.Value, portable[key]?[field.Key]),
                    $"actual developer field {key}.{field.Key} was lost or changed in export");
        check(portable["bundledDeveloperModel"] is null, "actual bundle ownership was exported");
        var renamed = HubModelMetadata.Create(version, "Local renamed developer", null,
            "bundled developer model", metadata);
        check(HubModelMetadata.IsBundledDeveloper(version, renamed), "actual developer rename lost protection");
        var choices = new[] { new HubModelOption("v8-gate.pt", 8, null),
            new HubModelOption("new-developer-gate.pt", version, metadata) };
        check(HubModelSelection.ChooseGatePath(choices, null) == "new-developer-gate.pt",
            "actual developer sidecar was not the clean-install default");
        check(HubModelSelection.ChooseGatePath(choices, "v8-gate.pt") == "v8-gate.pt",
            "actual developer sidecar replaced a saved fallback choice");
    }

    internal static void Run(Action<bool, string> check)
    {
        var developer = new JsonObject
        {
            ["version"] = 15, ["displayName"] = "Developer lower-face model",
            ["modelKind"] = "developer-lower-face", ["origin"] = "bundled developer model",
            ["bundledDeveloperModel"] = true, ["isExperimental"] = true,
            ["hasCameraCheeks"] = true, ["baseDeveloperVersion"] = 8,
            ["lineage"] = new JsonObject
            {
                ["baseDeveloperVersion"] = 8, ["baseGateFile"] = "C:\\private\\models\\v8-gate.pt",
                ["baseGateSha256"] = new string('A', 64), ["trainedVisibilitySha256"] = new string('b', 64),
                ["visibilityEncoderUnchanged"] = true, ["visibilityHeadConvertedWithoutFitting"] = true,
                ["directionSourceVersion"] = 13, ["directionSourceFile"] = "D:/private/models/v13-direction.pt",
                ["directionSourceSha256"] = new string('c', 64), ["cameraCheekStateUnchanged"] = true,
                ["originalTongueDirectionSha256"] = new string('d', 64), ["cameraWeight"] = .6, ["threshold"] = .5,
                ["capturePath"] = "C:\\private\\capture.qpcap"
            },
            ["validationScope"] = new JsonObject
            {
                ["trainingWearers"] = 1, ["appearance"] = "clean-shaven, user declared",
                ["reviewType"] = "representative contact-sheet pose-intent",
                ["freshFrames"] = 155, ["freshHiddenFrames"] = 32, ["freshVisibleFrames"] = 123,
                ["independentSessionFromTraining"] = true, ["independentWearerValidation"] = false,
                ["publicPromotionValidated"] = false, ["strictPoseRetentionPassed"] = false,
                ["directionAccuracyCeilingPassed"] = false, ["cameraOnlyFalsePositiveGatePassed"] = false,
                ["nativeFusionRequired"] = true, ["tunedFromFreshCapture"] = false,
                ["baselineMisses"] = 75, ["candidateMisses"] = 1, ["baselineFalsePositives"] = 0,
                ["candidateFalsePositives"] = 0, ["baselineF1"] = 96.0 / 171, ["candidateF1"] = 244.0 / 245,
                ["newSmileTipMisses"] = 1, ["directionOutputsUnchanged"] = true, ["cameraCheekOutputsUnchanged"] = true,
                ["capturePath"] = "C:\\private\\capture.qpcap", ["wearerId"] = "private-wearer"
            },
            ["capturePath"] = "C:\\private\\capture.qpcap"
        };
        check(HubModelMetadata.IsBundledDeveloper(15, developer), "developer bundle lacks deletion protection");
        check(HubModelMetadata.IsBundledDeveloper(16, developer), "relocated developer bundle lost protection");
        check(!HubModelMetadata.IsBundledDeveloper(8, null), "version 8 alone was mistaken for bundled ownership");
        check(!HubModelMetadata.IsBundledDeveloper(15, null), "personal v15 was mistaken for the bundled developer");
        var renamed = HubModelMetadata.Create(15, "Renamed developer", null, "bundled developer model", developer);
        check(HubModelMetadata.IsBundledDeveloper(15, renamed), "renaming removed bundled ownership");
        var manifest = new JsonObject { ["format"] = "qpro-tongue-model-package-v1" };
        HubModelMetadata.CopyPortableDetails(renamed, manifest);
        var imported = HubModelMetadata.Create(40, "Imported developer copy", null, "imported package", null, manifest);
        foreach (JsonObject value in new[] { renamed, manifest, imported })
        {
            check(HubModelMetadata.IsDeveloper(value), "developer classification was not portable");
            check(HubModelMetadata.IsExperimental(value) && HubModelMetadata.HasCameraCheeks(value), "developer capabilities/warning were lost");
            check(value["baseDeveloperVersion"]!.GetValue<int>() == 8, "base developer version was lost");
            check(value["lineage"]?["baseGateFile"]?.GetValue<string>() == "v8-gate.pt", "gate filename leaked its private path");
            check(value["lineage"]?["directionSourceFile"]?.GetValue<string>() == "v13-direction.pt", "direction filename leaked its private path");
            check(value["lineage"]?["baseGateSha256"]?.GetValue<string>() == new string('a', 64), "exact gate lineage hash was lost");
            check(value["lineage"]?["cameraCheekStateUnchanged"]?.GetValue<bool>() == true, "unchanged cheek lineage was lost");
            check(value["validationScope"]?["strictPoseRetentionPassed"]?.GetValue<bool>() == false, "failed retention became a false acceptance claim");
            check(value["validationScope"]?["independentWearerValidation"]?.GetValue<bool>() == false, "same-wearer limitation was lost");
            check(value["validationScope"]?["publicPromotionValidated"]?.GetValue<bool>() == false, "public pending scope was lost");
            check(value["validationScope"]?["cameraOnlyFalsePositiveGatePassed"]?.GetValue<bool>() == false, "camera-only failure was lost");
            check(value["validationScope"]?["nativeFusionRequired"]?.GetValue<bool>() == true, "native-fusion scope was lost");
            check(value["validationScope"]?["candidateF1"]?.GetValue<double>() == 244.0 / 245, "observed validation metric was lost");
            check(value["capturePath"] is null && value["lineage"]?["capturePath"] is null &&
                  value["validationScope"]?["capturePath"] is null && value["validationScope"]?["wearerId"] is null,
                "private source details leaked into portable metadata");
        }
        check(manifest["bundledDeveloperModel"] is null && imported["bundledDeveloperModel"] is null,
            "bundled ownership escaped through export/import");
        check(!HubModelMetadata.IsBundledDeveloper(40, imported), "imported copy gained deletion protection");
        var child = new JsonObject { ["modelKind"] = "personal-refinement", ["parentModelKind"] = "developer-lower-face" };
        HubModelMetadata.CopyDeveloperParentDetails(developer, child);
        check(child["validationScope"] is null, "parent validation was presented as the new refinement's validation");
        check(child["parentValidationScope"]?["candidateMisses"]?.GetValue<int>() == 1, "parent observed scope was lost from refinement");
        check(child["lineage"]?["directionSourceVersion"]?.GetValue<int>() == 13, "refinement lost developer lineage");
        check(child["bundledDeveloperModel"] is null && !HubModelMetadata.IsBundledDeveloper(41, child), "refinement gained bundled ownership");
        var childExport = new JsonObject();
        HubModelMetadata.CopyPortableDetails(child, childExport);
        check(HubModelMetadata.HasDeveloperLineage(childExport) && childExport["parentValidationScope"] is JsonObject,
            "refinement export lost developer parent classification/scope");
        child["parentValidationScope"]!["candidateMisses"] = 2;
        check(developer["validationScope"]!["candidateMisses"]!.GetValue<int>() == 1 &&
              childExport["parentValidationScope"]!["candidateMisses"]!.GetValue<int>() == 1, "portable scopes alias their originals");
        var missingResult = HubModelMetadata.CreateRefinementClassification(null, developer)!;
        check(!HubModelMetadata.HasCameraCheeks(missingResult), "new tongue refinement inherited retired camera cheek outputs");
        check(missingResult["modelKind"]?.GetValue<string>() == "personal-refinement" &&
              missingResult["validationScope"] is null && missingResult["bundledDeveloperModel"] is null,
            "missing trainer metadata inherited developer identity or its own validation claim");
        HubModelMetadata.CopyDeveloperParentDetails(developer, missingResult);
        check(missingResult["parentValidationScope"]?["candidateMisses"]?.GetValue<int>() == 1,
            "missing trainer metadata lost the parent validation scope");
        var newResult = new JsonObject { ["modelKind"] = "personal-refinement",
            ["validationScope"] = new JsonObject { ["freshFrames"] = 20 } };
        var resultDetails = HubModelMetadata.CreateRefinementClassification(newResult, developer)!;
        HubModelMetadata.CopyDeveloperParentDetails(developer, resultDetails);
        check(resultDetails["validationScope"]?["freshFrames"]?.GetValue<int>() == 20 &&
              resultDetails["parentValidationScope"]?["freshFrames"]?.GetValue<int>() == 155,
            "the trainer's actual validation scope was replaced by the parent's scope");
        var multigeneration = developer.DeepClone().AsObject();
        multigeneration["parentValidationScope"] = new JsonObject { ["freshFrames"] = 12 };
        var nextChild = new JsonObject();
        HubModelMetadata.CopyDeveloperParentDetails(multigeneration, nextChild);
        check(nextChild["parentValidationScope"]?["freshFrames"]?.GetValue<int>() == 155,
            "ancestor validation overwrote the immediate parent's observed scope");
        var hostile = new JsonObject
        {
            ["lineage"] = new JsonObject { ["baseGateSha256"] = "bad-hash", ["threshold"] = 2.0, ["privatePath"] = "C:\\private" },
            ["validationScope"] = new JsonObject { ["appearance"] = "C:\\private\\wearer.json", ["reviewType"] = "D:/private/review", ["freshFrames"] = -1 }
        };
        var clean = new JsonObject();
        HubModelMetadata.CopyPortableDetails(hostile, clean);
        check(clean["lineage"]?["baseGateSha256"] is null && clean["lineage"]?["threshold"] is null &&
              clean["lineage"]?["privatePath"] is null, "untyped lineage was copied");
        check(clean["validationScope"]?["appearance"] is null && clean["validationScope"]?["reviewType"] is null &&
              clean["validationScope"]?["freshFrames"] is null, "invalid/path-bearing validation details were copied");

        var choices = new[]
        {
            new HubModelOption("personal-v50-gate.pt", 50, null),
            new HubModelOption("original-v8-gate.pt", 8, null),
            new HubModelOption("developer-v15-gate.pt", 15, developer)
        };
        check(HubModelSelection.ChooseGatePath(choices, null) == "developer-v15-gate.pt", "clean install did not default to developer model");
        check(HubModelSelection.ChooseGatePath(choices, "personal-v50-gate.pt") == "personal-v50-gate.pt", "saved personal selection was replaced");
        check(HubModelSelection.ChooseGatePath(choices, "ORIGINAL-V8-GATE.PT") == "original-v8-gate.pt", "saved fallback selection was replaced");
        check(HubModelSelection.ChooseGatePath(choices, "missing.pt") == "developer-v15-gate.pt", "missing remembered model prevented valid default");
        check(HubModelSelection.ChooseGatePath(choices.Take(2), null) == "personal-v50-gate.pt", "missing bundle incorrectly forced a legacy v8 fallback");
        var relocated = new[] { choices[0], new HubModelOption("personal-v15-gate.pt", 15, null),
            new HubModelOption("developer-v16-gate.pt", 16, developer), choices[1] };
        check(HubModelSelection.ChooseGatePath(relocated, null) == "developer-v16-gate.pt", "relocated bundle was not recognized as default");
        check(HubModelSelection.ChooseGatePath(relocated, "personal-v15-gate.pt") == "personal-v15-gate.pt", "collision relocation changed personal selection");
        var personalCheeks = new JsonObject { ["modelKind"] = "camera-cheeks-experimental", ["hasCameraCheeks"] = true };
        var withPersonal13 = new[] { choices[1], choices[2], new HubModelOption("personal-v13-gate.pt", 13, personalCheeks) };
        check(HubModelSelection.ChooseGatePath(withPersonal13, null) == "developer-v15-gate.pt",
            "a separate camera-cheek model replaced the combined developer default");
        check(HubModelSelection.ChooseGatePath(withPersonal13, "personal-v13-gate.pt") == "personal-v13-gate.pt",
            "retiring a bundle must not change an available personal v13 selection");
        check(HubModelSelection.ChooseGatePath(choices, "removed-bundled-v13-gate.pt") == "developer-v15-gate.pt",
            "removing the old combined bundle did not fall back to the current developer model");
        var withImported13 = new[] { choices[1], choices[2], new HubModelOption("imported-v13-gate.pt", 13, imported) };
        check(HubModelSelection.ChooseGatePath(withImported13, "imported-v13-gate.pt") == "imported-v13-gate.pt",
            "retiring a bundle changed an imported developer copy selection");
        var tongueDeveloper = developer.DeepClone().AsObject();
        tongueDeveloper["modelKind"] = "developer-tongue";
        tongueDeveloper["displayName"] = "Developer tongue model";
        tongueDeveloper["hasCameraCheeks"] = false;
        check(HubModelMetadata.IsDeveloper(tongueDeveloper) && HubModelMetadata.IsBundledDeveloper(15, tongueDeveloper) &&
              !HubModelMetadata.HasCameraCheeks(tongueDeveloper), "new tongue-only developer metadata is not recognized");
        var tongueChoices = new[] { new HubModelOption("developer-tongue-v15-gate.pt", 15, tongueDeveloper),
            new HubModelOption("mustachio-v12-gate.pt", 12, new JsonObject { ["modelKind"] = "mustachio-experimental" }) };
        check(HubModelSelection.ChooseGatePath(tongueChoices, null) == "developer-tongue-v15-gate.pt",
            "two-model bundle did not default to the developer tongue model");
        check(HubModelSelection.ChooseGatePath(tongueChoices, "mustachio-v12-gate.pt") == "mustachio-v12-gate.pt",
            "two-model bundle replaced a saved Mustachio selection");
        check(HubModelSelection.ChooseGatePath([], null) is null, "empty model list returned a false selection");
    }
}
