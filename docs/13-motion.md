# Motion detection

## What it does

VisionMesh compares each frame with the one before it and measures how much of the picture
changed. If enough changed, for long enough, that is motion.

## What it is not

**It detects movement, not people.** VisionMesh will not tell you a person was at the door,
because it does not know. There is no recognition model.

Anything that claims to identify people without a model behind it is guessing, and a surveillance
system that guesses is worse than one that is honest about what it saw.

## Turning it on

Open a camera → **Settings** → Recording → **Record when something moves**.

A sensitivity slider appears. The server-wide default is under Settings → Advanced, and each
camera can override it.

## Tuning it

Start in the middle and adjust from what actually happens.

**Too many recordings?** Lower the sensitivity.

**Missing things?** Raise it.

The usual causes of false alarms are rain, snow, moving shadows, headlights at night, and insects
near the lens. Insects are by far the worst: one moth close to the lens fills the frame.

Aiming the camera slightly downward, so less sky and fewer trees are in shot, helps more than any
setting. So does moving it away from a light that attracts insects.

## What gets recorded

A motion recording includes a few seconds from **before** the trigger, so the clip starts before
the interesting thing entered the frame rather than after.

Recording continues while motion is present, plus about twenty seconds after it stops. A person
walking past does not produce three separate one-second clips.

## Events

Every motion detection writes an event, visible on the **Events** page and on the recording
timeline as an orange mark. Events are kept even when the camera is not recording, so a camera set
to record continuously still gives you motion marks to jump to.

## Common problems

**Motion never triggers.** Check the camera is set to **Record when something moves** — detection
does not run on cameras that are not using it. Then check the camera is actually online and
producing video.

**Motion triggers constantly.** Lower the sensitivity. If it is already low, something in the
frame is always moving: a tree, a road, a flag, a television. Consider what is in shot.

**It triggers when the lights come on.** It should not: a whole-frame brightness change is
subtracted before comparison. If it still does, the change was not uniform — a lamp lighting one
corner is genuinely a local change, and there is no way to distinguish that from a person walking
into that corner.

**Nothing is detected on one particular camera.** Its frames may not be decodable. VisionMesh logs
a warning after about sixty consecutive undecodable frames. The usual cause is a camera producing
progressive JPEG, which the fast decoder deliberately refuses rather than mis-decoding.

**Motion works but nothing is recorded.** Recording needs ffmpeg. Press **Fix camera**, which
checks that explicitly.

## Advanced

### How it works

Detection runs on a one-eighth scale greyscale image recovered from each JPEG **without fully
decoding it**. The DC coefficient of each 8×8 JPEG block is the average brightness of those 64
pixels — exactly the downscale a detector would compute anyway — so it can be read out by
Huffman-decoding one coefficient per block and skipping the rest.

That is what makes it cheap enough to run on every camera at once, and it keeps a third-party image
decoder out of the path that handles bytes arriving from cameras on the network.

### The two guards

**Global shift rejection.** The mean signed difference across the frame is computed and subtracted
before anything is counted. A light switching on, or a camera auto-exposing, changes the whole
frame at once, and without this it would read as motion everywhere.

**Consecutive frames.** Change has to persist across more than one frame. Sensor noise in a single
frame, which is common on a cheap camera at night, cannot trigger a recording on its own.

### What sensitivity actually changes

Two things at once:

- The per-cell brightness change that counts as "this part moved", from about 39 levels at the
  lowest sensitivity down to 8 at the highest
- The fraction of the frame that has to change, from 8% down to 0.5%

So low sensitivity means "a large part of the picture changed a lot", and high sensitivity means
"a small part changed a little".

### Cameras this does not work on

Progressive JPEG is refused rather than mis-decoded — decoding it as baseline would produce a
plausible-looking but wrong picture, which would corrupt detection silently. Almost no webcam or
ffmpeg output is progressive, so this is rare in practice.

### What it costs

Roughly a tenth of the work of a full decode, on a frame that is already in memory. A dozen
cameras with motion detection on a modest mini PC is not a problem.
