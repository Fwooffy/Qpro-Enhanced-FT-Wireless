# Developer tongue training

`developer_tongue_training.py` is an offline tool in the **public source
checkout** for preparing and evaluating future developer model candidates. It
fine tunes separate copies of the supplied baseline visibility and direction
checkpoints from manually captured stereo stills, then compares the pair on
**different capture sessions**. The archived original v8 pair is the tool's
baseline for the workflow below; supply that pair explicitly. Running the tool
does not replace a release asset or change the Hub's selected model.

The current Hub uses **Tongue training/refinement** on **Personalize**:

| Capture mode | Current curriculum | Result |
| --- | --- | --- |
| Quick refinement | **16 tongue cards** | A personal copy of the selected tongue model. |
| Focused diagonals + facial hair | **22 tongue cards** | A focused personal copy of the selected tongue model. |
| Full dataset | **58 tongue cards** | A new personal tongue model, trained from scratch. |

Full cards cover hidden mouth expressions, extension strengths, cardinal and
diagonal directions, intermediate X/Y combinations, and matched hidden/visible
fit and mouth conditions. Follow each card's recommended count and minimum;
every minimum is enforced. Some hidden-tongue cards include cheek or lip poses
as negative tongue examples. They do not train cheek outputs. Quick refinement
does not satisfy the developer tool's complete Full curriculum gate.

New captures and training are tongue-only. Individual cheek puff and suck use
the streaming app's native signals through optional **1/0** switches. They do
not use the tongue camera model, cheek capture cards or a trained cheek head.

## Current bundled models and validation

The two public bundled choices are **Developer tongue model** and the opt-in
**Mustachio · highly experimental**. Personal imports and refinements remain
available for their tongue outputs; they are not extra bundled choices.

Developer tongue model incorporates the new clean-shaven visibility training
while retaining the original developer tongue direction outputs. On a separate
155-still recording after reseating the same wearer's headset, native TongueOut
fusion reduced missed visible-tongue stills from 75 to 1, with no hidden-tongue
false detections. One small tongue tip during a smile was still missed.
Camera-only visibility produced four false detections in that recording. Some
older bearded poses worsened; broader facial-hair and independent-wearer checks
remain pending. This evidence supports the reported visibility result on that
wearer; it does not establish improved direction accuracy or completion of the
independent-wearer release gate described below.

Use **Tongue visibility > Weighted camera + native** as the starting mode.
Mustachio retains its separate experimental classification and needs broader
facial-hair and clean-shaven evaluation. Updates keep personal/imported
selections; verified obsolete bundled choices move to Developer tongue model.
Local model numbers can change during imports or updates, so use the friendly
model name.

## Prepare eligible captures

Collect Full and Focused sessions from multiple wearers. Both the training and
holdout groups must include at least one complete **58-card tongue-only Full**
capture and one complete **current 22-card Focused
diagonal/facial-hair** capture. Focused recommends 132 usable stills and requires
at least 88, with each card's minimum enforced.
Every card must have its minimum number of usable stills; skipped cards and
legacy 10-card Focused sessions do not satisfy this release gate. Every holdout
wearer must have contributed no training session. Keep all holdout sessions
completely out of training and model selection. A complete historical
79-card combined capture can satisfy Full coverage through its 58 tongue cards;
the compatibility audit still requires that historical journal to be complete.
No new combined capture is offered. In the guided capture,
inspect both lower camera panels. Exclude a still when the tip is obscured, the
pose differs from its card, or the headset moved too far. Prompt targets are
instructions to the wearer, not measured ground truth.

A fresh session from a training wearer is useful for private regression and
live responsiveness checks, but it does not satisfy the independent-wearer
release gate. Capturing more stills from one developer cannot establish
performance on clean-shaven wearers, facial-hair groups or other headset fits.

Use the same image size for every prepared cache. Run this from `src` in a
source checkout with the required modules. PC runtime setup provides Python;
the runnable ZIP does **not** contain this
offline developer tool or a separately selectable original v8 model. Keep an
approved archived baseline pair outside the release package and set
`$qproBaselineModels` to its folder. Example preparation commands:

```powershell
$qproPython = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\.venv\Scripts\python.exe'
$qproBaselineModels = 'C:\path\to\approved\baseline-models' # archived original baseline pair
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

For current Full, preparation retains the **58 tongue cards** using
`tongue-stereo-stills-v1`. Current Focused uses `tongue-stereo-arc-v3`.
Raw pixels, timestamps and stored factory expressions are reconstructed for
every row. The 35 ms native-label alignment requirement applies to all tongue
rows, including excluded ones.

Historical `lower-face-stills-v1` captures retain their original 79-card
journals, including the old 21-card cheek portion and any cheek cache arrays.
The audit verifies those historical arrays and their hashes for provenance;
only usable stills from the 58 tongue cards enter tongue training, checkpoint
selection or tongue holdout metrics. Cheek-only cards never become hidden-tongue
examples. An inactive native label feed during that historical cheek portion
does not invalidate its original cheek pose labels. These checks preserve old
data compatibility; they do not start cheek-model training.

Keep older datasets intact. If an old cache lacks the current
`source-flags-v2` native TongueOut mapping or points to moved source files, prepare
a **new output directory** from its original capture and sidecars. Supply
`--session` and `--labels` explicitly when their paths differ from the capture.
Use `--tracking-source` for an ambiguous legacy sidecar only when the actual
recording app is known. Re-preparation updates the cache contract; it does not
establish pose review, consent, missing cards or an independent wearer.

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
  --baseline-gate (Join-Path $qproBaselineModels 'qpro-stereo-tongue-v8-gate.pt') `
  --baseline-direction (Join-Path $qproBaselineModels 'qpro-stereo-tongue-v8-direction.pt') `
  --output-dir .\training\developer-run-1 --epochs 24 --batch-size 16 --stage
```

The trainer starts each checkpoint from its matching **original v8** gate or
direction weights, with a lower fine-tuning learning rate. It preserves each
parent's image input size (224 px gate, 192 px direction in the archived v8 pair)
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
alongside **Developer tongue model**. It extends copies of the earlier
developer weights with a beard/moustache capture; its separate exploratory
results do not validate it across wearers. Store it as a complete ordinary versioned
gate/direction pair, plus matching metadata with `displayName: "Mustachio"`,
`modelKind: "mustachio-experimental"`, and `isExperimental: true`. The Hub shows
a moustache icon and retains the experimental warning when it is renamed,
exported, or imported. It is selected explicitly rather than made the default.
Training on one bearded wearer is exploratory; independent beard and
clean-shaven validation remain required before promotion to a developer
baseline.

To refine Mustachio, select it under **Facial tracking > Lower-face tracking > Tongue model** before
training a Quick or Focused dataset in **Personalize**. The resulting personal
copy retains its experimental classification. Command-line refinement without
an explicit base version chooses an ordinary model; Full dataset training
does not inherit a parent checkpoint.

Keep approved baseline weights and comparison reports privately while testing
future candidates. Do not add archived v8 or combined cheek checkpoints as
extra bundled model choices. For a later approved developer update, choose a new version number
and place the two staged checkpoint files under `models/` using that version's
`qpro-stereo-tongue-vN-gate.pt` and `qpro-stereo-tongue-vN-direction.pt` names.
Update `release-manifest.json`, the bundled model inventory in `build-release.ps1`,
and the Hub's bundled-model display/default/protection logic together. Keep
Quick and Focused refinement tied to the selected base, including personal
models; make the new developer pair the fresh-setup default. Validate the runnable ZIP contains that
pair, loads it, and excludes all raw captures, caches, and personal models.
Keep the report and contributor consent records privately for release review.

Do not label a candidate as the new developer model merely because training
finished. The offline gate and live review determine that step. Externally
trained checkpoint pairs may be compared diagnostically with `--candidate-gate`
and `--candidate-direction`; their training split cannot be verified by this
tool, so those pairs cannot be staged through `--stage`.

## Historical combined models and captures

Earlier builds appended 21 camera-cheek cards to Quick or Full tongue captures,
using `lower-face-refinement-v1` and `lower-face-stills-v1`. Some resulting
direction checkpoints used a `cheek-augmented-` architecture with
`cheekPuffLeft` and `cheekPuffRight` outputs. Their cheek labels described
requested poses rather than measured strengths. One wearer's held-out stills
and fresh holds did not establish accuracy across other wearers or fits.

Keep those original checkpoints, journals and recordings intact. Imported
combined models remain usable for their tongue outputs, and compatibility
readers can inspect their original cheek data without relabelling it as tongue
supervision. The current Quick, Focused and Full workflows do not record camera
cheek cards, train a cheek head or publish camera-cheek outputs. No standalone
combined developer model is bundled.

Release assets include only the approved Developer tongue model and Mustachio
bundled pairs. They must omit camera recordings, journals, caches, personal
models and absolute training paths. Keep contributor consent and evaluation
evidence private for release review.
