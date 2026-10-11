# Developer tongue model candidates

`developer_tongue_training.py` is an offline tool in the **public source
checkout** for preparing a future bundled developer model. It fine tunes
separate copies of the v8 visibility and direction checkpoints from manually
captured stereo stills, then compares the pair with
the bundled v8 demonstration pair on **different capture sessions**. It does
not change the original v8 files, the release package, or the Hub's selected model.

The tongue portion of Full dataset has 58 fixed-pose cards: hidden mouth expressions,
extension strengths, cardinal and diagonal directions, intermediate X/Y
combinations, and matched hidden/visible fit and mouth conditions. Plan about
60–120 minutes for a careful capture. The compact Quick refinement remains
available in the Hub for each wearer after a developer model is bundled.

## Prepare eligible captures

Collect Full and Focused sessions from multiple wearers. Both the training and
holdout groups must include at least one complete **current 58-card Full**
capture and one complete **current Focused diagonal/facial-hair** capture.
Every card must have its minimum number of usable stills; skipped cards and
legacy 10-card Focused sessions do not satisfy this release gate. Every holdout
wearer must have contributed no training session. Keep all holdout sessions
completely out of training and model selection. In the guided capture,
inspect both lower camera panels. Exclude a still when the tip is obscured, the
pose differs from its card, or the headset moved too far. Prompt targets are
instructions to the wearer, not measured ground truth.

Use the same image size for every prepared cache. Run this from `src` in a
source checkout with the required modules. The extracted Qpro release provides
the v8 model pair, and PC runtime setup provides Python; the runnable ZIP does
**not** contain this offline developer tool. Set `$qproRuntime` to the
`QproRuntime` folder in your extracted release. Example preparation commands:

```powershell
$qproPython = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\.venv\Scripts\python.exe'
$qproRuntime = 'C:\path\to\extracted\QproRuntime' # change this to your extracted release folder
& $qproPython .\prepare_tongue_stills.py .\captures\full-a.qpcap --output .\training\full-a --size 224
& $qproPython .\prepare_tongue_stills.py .\captures\focused-a.qpcap --output .\training\focused-a --size 224
& $qproPython .\prepare_tongue_stills.py .\captures\full-b.qpcap --output .\training\full-b --size 224
& $qproPython .\prepare_tongue_stills.py .\captures\focused-b.qpcap --output .\training\focused-b --size 224
```

The prepared cache records the source journal and capture paths. Keep the
`.qpcap`, `.qpsession.json`, and `.qplabel.jsonl` files locally so provenance
checks can rebuild every prepared stereo frame and native TongueOut label.
This detects a stale or modified cache; it cannot prove that a wearer followed
each pose or that a consent declaration is authentic. Never commit camera
captures, caches, personal calibration, or checkpoints without the wearer's
explicit release permission. The project
ignores these generated formats by default.

## Record consent and run the audit

Save `training/developer-manifest.json` locally. Each training wearer must
approve both training and redistribution of the resulting checkpoint. Each
holdout wearer must approve evaluation. `manualPoseReview` records that a human
checked pose labels and camera visibility. Use stable pseudonyms rather than
names or email addresses.

```json
{
  "schemaVersion": 1,
  "purpose": "developer-tongue-model",
  "sessions": [
    {
      "cache": "full-a",
      "role": "train",
      "sessionId": "full-a-1",
      "wearerId": "wearer-a",
      "consent": {
        "training": true,
        "redistributeCheckpoint": true,
        "manualPoseReview": true
      }
    },
    {
      "cache": "focused-a",
      "role": "train",
      "sessionId": "focused-a-1",
      "wearerId": "wearer-a",
      "consent": {
        "training": true,
        "redistributeCheckpoint": true,
        "manualPoseReview": true
      }
    },
    {
      "cache": "full-b",
      "role": "holdout",
      "sessionId": "full-b-1",
      "wearerId": "wearer-b",
      "consent": {
        "evaluation": true,
        "manualPoseReview": true
      }
    },
    {
      "cache": "focused-b",
      "role": "holdout",
      "sessionId": "focused-b-1",
      "wearerId": "wearer-b",
      "consent": {
        "evaluation": true,
        "manualPoseReview": true
      }
    }
  ]
}
```

Add the complete Full and Focused training and holdout sessions to the
manifest. The cache paths are relative to the manifest. The tool checks
session completion, exact-still format, target schema, per-frame journal
labels, exclusions, camera order, consent flags, raw-frame reconstruction,
factory-label alignment, and source hashes. A repeated capture cannot occur
on both sides of the split. Use a fresh `--output-dir` for every run; the tool
refuses to overwrite an earlier checkpoint or report.

```powershell
& $qproPython .\developer_tongue_training.py .\training\developer-manifest.json --check-only
& $qproPython .\developer_tongue_training.py .\training\developer-manifest.json `
  --baseline-gate (Join-Path $qproRuntime 'models\qpro-stereo-tongue-v8-gate.pt') `
  --baseline-direction (Join-Path $qproRuntime 'models\qpro-stereo-tongue-v8-direction.pt') `
  --output-dir .\training\developer-run-1 --epochs 24 --batch-size 16 --stage
```

The trainer starts each checkpoint from its matching **original v8** gate or
direction weights, with a lower fine-tuning learning rate. It preserves each
parent's image input size (224 px gate, 192 px direction in the current v8 pair)
and records the parent filename and SHA-256 digest in the candidate without
embedding a private filesystem path. It balances prompt cards within the
training cache and uses its ordinary per-card repetition split to select
checkpoints. The reserved
sessions are evaluated afterward with each checkpoint's **frozen** visibility
weight and threshold. The report includes overall visibility precision,
recall, F1, hidden false-positive rate, visible miss rate, separate extension,
horizontal and vertical errors, results by wearer, recording and card, and all four diagonal corners. The
offline gate requires every holdout wearer to be unseen, complete Full and
Focused coverage on both sides, minimum absolute
quality, no regressions beyond a 0.000001 floating-point allowance, and an improvement in visibility or X/Y
direction. At least one branch must improve beyond its epoch-zero v8 copy.
A passing run writes an `offline-gate-pair` folder for review. A failed
run keeps its diagnostics but does not stage a pair.

The comparison checks each axis independently in every recording and wearer,
not just the average across the dataset. It also checks individual pose cards
with at least four stills and each required diagonal corner. Extra hidden
false positives or visible misses block promotion even if the total F1 improves.
Missing, mismatched or non-finite comparison evidence blocks promotion too.
These checks prevent improvements on easy poses from hiding a worse direction
or recording. They do not turn intended pose labels into measured ground truth.

Include clean-shaven and facial-hair wearers, multiple headset fits, gentle
extension strengths and ordinary hidden-tongue mouth expressions in independent
evaluation. Record appearance and fit in private review notes rather than
guessing from the images. If a group has no independent recordings, report that
coverage as missing and avoid claiming that the model was validated for it.

The comparison measures agreement with reviewed **prompted poses**. It is not
proof of accuracy for every wearer. The still-frame audit does not reproduce
live EMA smoothing, visibility hysteresis, or VRChat output, and never marks
live validation as passed. Before bundling, replay the candidate on
additional approved captures, inspect hidden false positives and diagonals,
and verify live VRCFaceTracking output on volunteers with different face fit.

## Optional contrast candidate

For a separately evaluated candidate, add `--input-preprocessing clahe-v1` to
the developer training command and use a fresh `--output-dir`. This applies
bounded local contrast adjustment to each stereo view after resizing, during
both training and inference. It can make existing low-contrast detail easier
to distinguish; it cannot recover a tongue physically blocked by facial hair.
It does not remove or synthesize hair or tongue pixels, and it never edits the
raw capture or prepared cache.

The checkpoint records its versioned `inputPreprocessing` tag. The default
remains `raw-v1`; older checkpoints with no tag also use their original raw
input pipeline. Each gate and direction checkpoint selects its own processing,
including mixed pairs. An unknown tag is rejected rather than guessed. Do not
add a contrast tag to existing weights: the candidate must be trained and
evaluated with that processing.

Before bundling, review the candidate against raw-v1 on independent wearers
with beards or moustaches **and** independent clean-shaven wearers. Check both
visible tongue poses and hidden mouth expressions for misses and false
positives. Improved results on one bearded contributor do not establish that
clean-shaven performance is preserved. The existing complete-session offline
gate and live VRCFaceTracking review still apply.

## Bundling a passing candidate

A separate opt-in **Mustachio · highly experimental** model may be provided
for testing alongside v8. It extends copies of the existing developer v8
weights with a beard/moustache capture; it is not a replacement for the
validated developer baseline. Store it as a complete ordinary versioned
gate/direction pair, plus matching metadata with `displayName: "Mustachio"`,
`modelKind: "mustachio-experimental"`, and `isExperimental: true`. The Hub shows
a moustache icon and retains the experimental warning when it is renamed,
exported, or imported. It is selected explicitly rather than made the default.
Training on one bearded wearer is exploratory; independent beard and
clean-shaven validation remain required before promotion to a developer
baseline.

To refine Mustachio, select it under **Live tracking → Tongue model** before
training a Quick or Focused dataset in **Personalize**. The resulting personal
copy retains its experimental classification. Command-line refinement without
an explicit base version chooses an ordinary model; Full dataset training
does not inherit a parent checkpoint.

Keep v8 as the known working release demo until the independent report passes
and live review is complete. For the next release, choose a new version number
and place the two staged checkpoint files under `models/` using that version's
`qpro-stereo-tongue-vN-gate.pt` and `qpro-stereo-tongue-vN-direction.pt` names.
Update `release-manifest.json`, the two model entries in `build-release.ps1`,
and the Hub's bundled-model display/default/protection logic together. Make
the Hub's Quick refinement start from the newly bundled pair while retaining
its existing personal-copy behavior. Validate the runnable ZIP contains that
pair, loads it, and excludes all raw captures, caches, and personal models.
Keep the report and contributor consent records privately for release review.

Do not label a candidate as the new developer model merely because training
finished. The offline gate and live review determine that step. Externally
trained checkpoint pairs may be compared diagnostically with `--candidate-gate`
and `--candidate-direction`; their training split cannot be verified by this
tool, so those pairs cannot be staged through `--stage`.

## Experimental camera cheek head

The Hub now records 21 cheek cards after Quick refinement or Full dataset's tongue cards. Those sessions use `lower-face-refinement-v1` and `lower-face-stills-v1`; historical tongue journals remain supported. Cheek cards provide approximate left/right puff fractions from the requested pose. They are excluded from tongue supervision, including the last three cheek cards where the tongue is visible.

`prepare_cheek_stills.py` validates the complete cheek curriculum and prepares its camera examples. `train_cheek_model.py` freezes the existing tongue branch, normalizes only its shared features for the new cheek head and learns independent left/right strengths. The resulting direction checkpoint uses `cheek-augmented-` architecture and appends `cheekPuffLeft` and `cheekPuffRight`; the visibility gate keeps the original tongue target schema. CPU and GPU parity tests protect the parent tongue outputs.

`train-latest-cheeks.ps1` can add these cheek outputs to a separate paired copy of an existing tongue model. Quick/Full helpers stage their tongue results privately, rebuild the cheek head from the same new capture and publish only the complete pair. Refining a combined model first extracts its original tongue parent; an old cheek head is not reused after the tongue backbone changes.

The bundled experimental developer cheek candidate uses one wearer's recordings. Held-out repetitions and fresh holds from that wearer are useful checks, but do not establish accuracy on another wearer, fit, beard or streaming app. Preserve the original developer model and require separate live evaluation before promoting a new baseline. Release assets must omit camera recordings, journals, caches and absolute training paths.
